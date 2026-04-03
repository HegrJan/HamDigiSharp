using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace HamDigiSharp.Dsp;

/// <summary>
/// Thin wrapper around MathNet.Numerics FFT, mirroring the MSHV four2a_c2c / four2a_d2c API.
/// Sign convention: isign = -1 → forward DFT, isign = +1 → inverse DFT (scaled by 1/N).
/// </summary>
public static class Fft
{
    // ── Pre-computed twiddle factors for the 32-point fast-path ──────────────
    // tw32[k] = e^{-j·2π·k/32} for k = 0..31 (AsymmetricScaling, forward sign)
    private static readonly (double re, double im)[] _tw32 = BuildTwiddle(32);

    private static (double re, double im)[] BuildTwiddle(int n)
    {
        var tw = new (double re, double im)[n];
        for (int k = 0; k < n; k++)
        {
            double theta = -2.0 * Math.PI * k / n;
            tw[k] = (Math.Cos(theta), Math.Sin(theta));
        }
        return tw;
    }

    /// <summary>
    /// High-performance in-place forward FFT for exactly 32 complex samples.
    /// Uses pre-computed twiddle factors and a direct radix-2 DIT butterfly.
    /// ~4× faster than the MathNet generic path for this size — critical since
    /// FT4/FT2 call this ~7 000 times per candidate (pilot scoring + symbol LLR)
    /// and FT8 calls it ~1 100 times per candidate.
    /// Semantics identical to <see cref="ForwardInPlace"/> (AsymmetricScaling,
    /// i.e. no normalization on the forward transform).
    /// </summary>
    public static void ForwardInPlace32(Complex[] buf)
    {
        // ── Bit-reversal permutation for n=32 (5-bit reversed index) ─────────
        const int n = 32;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
            j ^= bit;
            if (i < j) (buf[i], buf[j]) = (buf[j], buf[i]);
        }

        // ── 5 radix-2 DIT butterfly stages ───────────────────────────────────
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            int step = n / len;          // index step through _tw32 for this stage
            for (int i = 0; i < n; i += len)
            {
                for (int j = 0; j < half; j++)
                {
                    var (twRe, twIm) = _tw32[j * step];
                    Complex u = buf[i + j];
                    Complex v = buf[i + j + half];
                    double vRe = v.Real * twRe - v.Imaginary * twIm;
                    double vIm = v.Real * twIm + v.Imaginary * twRe;
                    buf[i + j]        = new Complex(u.Real + vRe, u.Imaginary + vIm);
                    buf[i + j + half] = new Complex(u.Real - vRe, u.Imaginary - vIm);
                }
            }
        }
    }

    /// <summary>
    /// In-place complex-to-complex FFT.
    /// <paramref name="isign"/> = -1 forward, +1 inverse.
    /// </summary>
    public static void C2C(Complex[] data, int isign)
    {
        if (isign == -1)
            Fourier.Forward(data, FourierOptions.AsymmetricScaling);
        else
            Fourier.Inverse(data, FourierOptions.AsymmetricScaling);
    }

    /// <inheritdoc cref="C2C(Complex[], int)"/>
    public static void C2C(Span<Complex> data, int isign)
    {
        var arr = data.ToArray();
        C2C(arr, isign);
        arr.AsSpan().CopyTo(data);
    }

    /// <summary>
    /// Real-to-complex forward FFT.
    /// Returns a complex array of length <c>n/2 + 1</c> with the positive-frequency half.
    /// </summary>
    public static Complex[] R2C(ReadOnlySpan<double> real)
    {
        int n = real.Length;
        var c = new Complex[n];
        for (int i = 0; i < n; i++) c[i] = new Complex(real[i], 0.0);
        Fourier.Forward(c, FourierOptions.AsymmetricScaling);
        // return positive half only
        var half = new Complex[n / 2 + 1];
        c.AsSpan(0, half.Length).CopyTo(half);
        return half;
    }

    /// <summary>
    /// Real-to-complex forward FFT from a float array (convenience overload).
    /// </summary>
    public static Complex[] R2C(ReadOnlySpan<float> real)
    {
        int n = real.Length;
        var c = new Complex[n];
        for (int i = 0; i < n; i++) c[i] = new Complex(real[i], 0.0);
        Fourier.Forward(c, FourierOptions.AsymmetricScaling);
        var half = new Complex[n / 2 + 1];
        c.AsSpan(0, half.Length).CopyTo(half);
        return half;
    }

    /// <summary>
    /// Full complex FFT from a real double array (result has N complex values).
    /// Used by waterfall and mode decoders that need the full spectrum.
    /// </summary>
    public static Complex[] R2CFull(ReadOnlySpan<double> real)
    {
        int n = real.Length;
        var c = new Complex[n];
        for (int i = 0; i < n; i++) c[i] = new Complex(real[i], 0.0);
        Fourier.Forward(c, FourierOptions.AsymmetricScaling);
        return c;
    }

    /// <summary>
    /// Full complex FFT from a float array.
    /// </summary>
    public static Complex[] R2CFull(ReadOnlySpan<float> real)
    {
        int n = real.Length;
        var c = new Complex[n];
        for (int i = 0; i < n; i++) c[i] = new Complex(real[i], 0.0);
        Fourier.Forward(c, FourierOptions.AsymmetricScaling);
        return c;
    }

    /// <summary>
    /// In-place complex FFT on a pre-allocated array (avoids allocation on hot paths).
    /// Automatically uses the optimised 32-point fast-path when <paramref name="buffer"/>
    /// has exactly 32 elements.
    /// </summary>
    public static void ForwardInPlace(Complex[] buffer)
    {
        if (buffer.Length == 32)
            ForwardInPlace32(buffer);
        else
            Fourier.Forward(buffer, FourierOptions.AsymmetricScaling);
    }

    /// <summary>
    /// In-place inverse complex FFT (scaled 1/N).
    /// </summary>
    public static void InverseInPlace(Complex[] buffer)
        => Fourier.Inverse(buffer, FourierOptions.AsymmetricScaling);

    /// <summary>
    /// Compute power spectrum |X[k]|² for each frequency bin, from a real signal.
    /// </summary>
    public static double[] PowerSpectrum(ReadOnlySpan<double> real)
    {
        int n = real.Length;
        var c = new Complex[n];
        for (int i = 0; i < n; i++) c[i] = new Complex(real[i], 0.0);
        Fourier.Forward(c, FourierOptions.AsymmetricScaling);
        var ps = new double[n / 2 + 1];
        for (int i = 0; i < ps.Length; i++)
            ps[i] = c[i].Real * c[i].Real + c[i].Imaginary * c[i].Imaginary;
        return ps;
    }
}
