namespace HamDigiSharp.Dsp;

/// <summary>
/// Window functions used by MSHV decoders.
/// Ports of nuttal_window and related routines in PomFt / individual decoder files.
/// </summary>
public static class Windowing
{
    /// <summary>
    /// Nuttall 4-term window (used by FT8, FT4, FT2, Q65 decoders).
    /// Uses symmetric normalization <c>i/(n-1)</c> so that <c>win[0] == win[n-1]</c>
    /// (standard textbook definition; MSHV uses DFT-even <c>i/n</c>, but the difference
    /// is negligible for practical FFT sizes ≥ 256).
    /// </summary>
    public static void Nuttall(Span<double> win)
    {
        int n = win.Length;
        double a0 = 0.3635819, a1 = 0.4891775, a2 = 0.1365995, a3 = 0.0106411;
        double twopi = 2.0 * Math.PI;
        for (int i = 0; i < n; i++)
        {
            double x = twopi * i / (n - 1);
            win[i] = a0 - a1 * Math.Cos(x) + a2 * Math.Cos(2 * x) - a3 * Math.Cos(3 * x);
        }
    }

    /// <summary>Nuttall window as a new array of length <paramref name="n"/>.</summary>
    public static double[] Nuttall(int n)
    {
        var win = new double[n];
        Nuttall(win);
        return win;
    }

    /// <summary>
    /// Hann (raised-cosine) window.
    /// </summary>
    public static void Hann(Span<double> win)
    {
        int n = win.Length;
        double twopi = 2.0 * Math.PI;
        for (int i = 0; i < n; i++)
            win[i] = 0.5 * (1.0 - Math.Cos(twopi * i / (n - 1)));
    }

    public static double[] Hann(int n)
    {
        var win = new double[n];
        Hann(win);
        return win;
    }

    /// <summary>
    /// Blackman-Harris 4-term window.
    /// </summary>
    public static void BlackmanHarris(Span<double> win)
    {
        int n = win.Length;
        double a0 = 0.35875, a1 = 0.48829, a2 = 0.14128, a3 = 0.01168;
        double twopi = 2.0 * Math.PI;
        for (int i = 0; i < n; i++)
        {
            double x = twopi * i / (n - 1);
            win[i] = a0 - a1 * Math.Cos(x) + a2 * Math.Cos(2 * x) - a3 * Math.Cos(3 * x);
        }
    }

    public static double[] BlackmanHarris(int n)
    {
        var win = new double[n];
        BlackmanHarris(win);
        return win;
    }

    /// <summary>Apply window coefficients in-place to <paramref name="signal"/>.</summary>
    public static void Apply(Span<double> signal, ReadOnlySpan<double> win)
    {
        int n = Math.Min(signal.Length, win.Length);
        for (int i = 0; i < n; i++) signal[i] *= win[i];
    }
}
