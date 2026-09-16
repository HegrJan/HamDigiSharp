using System.Collections.Concurrent;
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace HamDigiSharp.Dsp;

/// <summary>
/// FFT front-end over the built-in KISS FFT port (<see cref="KissFftPlan"/>), mirroring the
/// MSHV four2a_c2c / four2a_d2c API.
/// Sign convention: isign = -1 → forward DFT X[k] = Σ x[n]·e^{-j2πkn/N} (unscaled),
/// isign = +1 → inverse DFT (scaled by 1/N). Any length N is supported; lengths whose
/// prime factors are 2, 3 and 5 are fastest.
/// </summary>
public static class Fft
{
    // Plans are immutable and shared by all threads; one per (length, direction).
    // Small lengths — the per-symbol FFTs called thousands of times per candidate — are
    // looked up by direct index; a racing duplicate build is harmless (plans are equal).
    private const int SmallPlanLimit = 4096;
    private static readonly KissFftPlan?[] _smallForward = new KissFftPlan?[SmallPlanLimit + 1];
    private static readonly KissFftPlan?[] _smallInverse = new KissFftPlan?[SmallPlanLimit + 1];
    private static readonly ConcurrentDictionary<(int N, bool Inverse), KissFftPlan> _plans = new();

    private static KissFftPlan GetPlan(int n, bool inverse)
    {
        if (n <= SmallPlanLimit)
        {
            var table = inverse ? _smallInverse : _smallForward;
            return table[n] ??= new KissFftPlan(n, inverse);
        }
        return _plans.GetOrAdd((n, inverse), static key => new KissFftPlan(key.N, key.Inverse));
    }

    /// <summary>
    /// In-place forward FFT for exactly 32 complex samples.
    /// Kept for API compatibility; identical to <see cref="ForwardInPlace(Span{Complex})"/>.
    /// </summary>
    public static void ForwardInPlace32(Complex[] buf)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(buf.Length, 32);
        ForwardInPlace(buf.AsSpan());
    }

    /// <summary>
    /// In-place complex-to-complex FFT.
    /// <paramref name="isign"/> = -1 forward, +1 inverse.
    /// </summary>
    public static void C2C(Complex[] data, int isign) => C2C(data.AsSpan(), isign);

    /// <inheritdoc cref="C2C(Complex[], int)"/>
    public static void C2C(Span<Complex> data, int isign)
    {
        if (isign == -1)
            ForwardInPlace(data);
        else
            InverseInPlace(data);
    }

    /// <summary>
    /// Real-to-complex forward FFT.
    /// Returns a complex array of length <c>n/2 + 1</c> with the positive-frequency half.
    /// </summary>
    public static Complex[] R2C(ReadOnlySpan<double> real)
        => R2CFull(real).AsSpan(0, real.Length / 2 + 1).ToArray();

    /// <summary>
    /// Real-to-complex forward FFT from a float array (convenience overload).
    /// </summary>
    public static Complex[] R2C(ReadOnlySpan<float> real)
        => R2CFull(real).AsSpan(0, real.Length / 2 + 1).ToArray();

    /// <summary>
    /// Full complex FFT from a real double array (result has N complex values).
    /// Used by waterfall and mode decoders that need the full spectrum.
    /// </summary>
    public static Complex[] R2CFull(ReadOnlySpan<double> real)
    {
        var c = new Complex[real.Length];
        for (int i = 0; i < c.Length; i++) c[i] = new Complex(real[i], 0.0);
        ForwardInPlace(c.AsSpan());
        return c;
    }

    /// <summary>
    /// Full complex FFT from a float array.
    /// </summary>
    public static Complex[] R2CFull(ReadOnlySpan<float> real)
    {
        var c = new Complex[real.Length];
        for (int i = 0; i < c.Length; i++) c[i] = new Complex(real[i], 0.0);
        ForwardInPlace(c.AsSpan());
        return c;
    }

    /// <summary>
    /// In-place forward complex FFT on a pre-allocated array (avoids allocation on hot paths).
    /// </summary>
    public static void ForwardInPlace(Complex[] buffer) => ForwardInPlace(buffer.AsSpan());

    /// <summary>
    /// In-place forward complex FFT over exactly <paramref name="buffer"/>.Length samples
    /// (a slice of a larger pooled array is fine).
    /// </summary>
    public static void ForwardInPlace(Span<Complex> buffer)
    {
        if (buffer.IsEmpty) return;
        GetPlan(buffer.Length, inverse: false).Transform(buffer, buffer);
    }

    /// <summary>
    /// In-place inverse complex FFT (scaled 1/N).
    /// </summary>
    public static void InverseInPlace(Complex[] buffer) => InverseInPlace(buffer.AsSpan());

    /// <inheritdoc cref="InverseInPlace(Complex[])"/>
    public static void InverseInPlace(Span<Complex> buffer)
    {
        if (buffer.IsEmpty) return;
        GetPlan(buffer.Length, inverse: true).Transform(buffer, buffer);
        var d = MemoryMarshal.Cast<Complex, double>(buffer);
        TensorPrimitives.Multiply(d, 1.0 / buffer.Length, d);
    }

    /// <summary>
    /// Compute power spectrum |X[k]|² for each frequency bin, from a real signal.
    /// </summary>
    public static double[] PowerSpectrum(ReadOnlySpan<double> real)
    {
        var c = R2CFull(real);
        var ps = new double[real.Length / 2 + 1];
        for (int i = 0; i < ps.Length; i++)
            ps[i] = c[i].MagnitudeSquared;
        return ps;
    }
}
