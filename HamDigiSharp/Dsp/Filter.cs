using System.Numerics;

namespace HamDigiSharp.Dsp;

/// <summary>
/// Simple biquad IIR filter bank, mirroring MSHV's HvRawFilter (bandpass).
/// Used to band-limit the audio to ~65–4750 Hz before the decoder.
/// </summary>
public sealed class BandpassFilter
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="centerHz">Centre frequency in Hz.</param>
    /// <param name="bandwidthHz">−3 dB bandwidth in Hz.</param>
    public BandpassFilter(double sampleRate, double centerHz, double bandwidthHz)
    {
        // BPF via bilinear transform of 2nd-order analogue prototype
        double w0 = 2.0 * Math.PI * centerHz / sampleRate;
        double bw = 2.0 * Math.PI * bandwidthHz / sampleRate;
        double q = w0 / bw;
        double alpha = Math.Sin(w0) / (2.0 * q);

        double a0 = 1.0 + alpha;
        _b0 = alpha / a0;
        _b1 = 0.0;
        _b2 = -alpha / a0;
        _a1 = -2.0 * Math.Cos(w0) / a0;
        _a2 = (1.0 - alpha) / a0;
        _z1 = _z2 = 0.0;
    }

    /// <summary>Process a single sample (direct form II transposed).</summary>
    public double Process(double x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    /// <summary>Process a buffer in-place.</summary>
    public void ProcessInPlace(Span<double> buf)
    {
        for (int i = 0; i < buf.Length; i++) buf[i] = Process(buf[i]);
    }

    /// <summary>Reset internal state.</summary>
    public void Reset() => _z1 = _z2 = 0.0;
}

/// <summary>
/// Analytic signal (real → complex) via the Hilbert transform.
/// The imaginary part is the Hilbert transform of the real input.
/// Used for baseband conversion in MSK144 and SFOX decoders.
/// </summary>
public static class AnalyticSignal
{
    /// <summary>
    /// Compute the analytic signal via FFT: zero negative frequencies, IFFT.
    /// </summary>
    public static Complex[] Compute(ReadOnlySpan<double> real)
    {
        int n = real.Length;
        var c = new Complex[n];
        for (int i = 0; i < n; i++) c[i] = new Complex(real[i], 0.0);
        Fft.ForwardInPlace(c);

        // Zero negative frequencies (k = n/2+1 .. n-1), double positive (k = 1 .. n/2-1)
        c[0] = new Complex(c[0].Real, 0); // DC
        if (n % 2 == 0) c[n / 2] = new Complex(c[n / 2].Real, 0); // Nyquist
        for (int k = 1; k < n / 2; k++) c[k] *= 2.0;
        for (int k = n / 2 + 1; k < n; k++) c[k] = Complex.Zero;

        Fft.InverseInPlace(c);
        return c;
    }
}
