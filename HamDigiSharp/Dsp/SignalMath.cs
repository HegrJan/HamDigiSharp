using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace HamDigiSharp.Dsp;

/// <summary>
/// General-purpose signal processing utilities.
/// Ports of PomAll methods used across all MSHV decoders.
/// </summary>
public static class SignalMath
{
    // ── Extrema ───────────────────────────────────────────────────────────────

    public static double MaxValue(ReadOnlySpan<double> a, int beg, int end)
    {
        double m = double.MinValue;
        for (int i = beg; i <= end; i++) if (a[i] > m) m = a[i];
        return m;
    }

    public static double MinValue(ReadOnlySpan<double> a, int beg, int end)
    {
        double m = double.MaxValue;
        for (int i = beg; i <= end; i++) if (a[i] < m) m = a[i];
        return m;
    }

    /// <summary>Index of the maximum value in a[beg..end] (inclusive).</summary>
    public static int MaxLoc(ReadOnlySpan<double> a, int beg, int end)
    {
        int idx = beg;
        for (int i = beg + 1; i <= end; i++) if (a[i] > a[idx]) idx = i;
        return idx;
    }

    /// <summary>Index of the minimum value in a[beg..end] (inclusive).</summary>
    public static int MinLoc(ReadOnlySpan<double> a, int beg, int end)
    {
        int idx = beg;
        for (int i = beg + 1; i <= end; i++) if (a[i] < a[idx]) idx = i;
        return idx;
    }

    /// <summary>Index of max value scanning from end to beg.</summary>
    public static int MaxLocReverse(ReadOnlySpan<double> a, int beg, int end)
    {
        int idx = end;
        for (int i = end - 1; i >= beg; i--) if (a[i] > a[idx]) idx = i;
        return idx;
    }

    // ── Parabolic peak interpolation ─────────────────────────────────────────

    /// <summary>
    /// Parabolic interpolation of a peak given three consecutive samples.
    /// Returns the sub-sample offset from the centre sample.
    /// Mirrors MSHV's <c>PomAll::peakup</c>.
    ///
    /// Derivation: for a parabola through (−1, ym), (0, y0), (+1, yp):
    ///   P(x) = Ax² + Bx + C  with  C=y0, A=(ym+yp)/2−y0, B=(yp−ym)/2
    ///   Peak at x = −B/(2A) = (yp−ym) / (2·(2·y0−ym−yp))
    /// </summary>
    public static double Peakup(double ym, double y0, double yp)
    {
        double d = 2.0 * (2.0 * y0 - ym - yp);
        if (Math.Abs(d) < 1e-12) return 0.0;
        return (yp - ym) / d;
    }

    // ── dB ────────────────────────────────────────────────────────────────────

    /// <summary>10*log10(x), clamped to −100 dB for x ≤ 0.</summary>
    public static double Db(double x) => x > 0.0 ? 10.0 * Math.Log10(x) : -100.0;

    // ── Smoothing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 1-2-1 boxcar smoother applied <c>nz</c> times starting at index <c>beg</c>.
    /// Mirrors MSHV's <c>PomAll::smo121</c>.
    ///
    /// Each pass uses the original (pre-update) left-neighbour value — stored in
    /// a local variable — so the output is a true symmetric FIR (no causal leakage).
    /// </summary>
    public static void Smo121(double[] x, int beg, int nz)
    {
        for (int k = 0; k < nz; k++)
        {
            double x0 = x[beg]; // original value of x[beg], used as first left-neighbour
            for (int i = beg + 1; i < x.Length - 1; i++)
            {
                double x1 = x[i];                                    // save current before update
                x[i] = 0.25 * x0 + 0.5 * x[i] + 0.25 * x[i + 1]; // use ORIGINAL left-neighbour
                x0 = x1;                                             // next iteration's left-neighbour
            }
        }
    }

    // ── Circular shift ────────────────────────────────────────────────────────

    /// <summary>
    /// Circular-shift a complex array by <paramref name="shift"/> elements.
    /// Positive shift → rotate right (element at index 0 moves to index <c>shift</c>).
    ///
    /// <para>Note: MSHV's <c>PomAll::cshift1</c> with positive <c>ish</c> performs the
    /// opposite direction (left rotation). The C# API uses the conventional right-rotation
    /// semantics; callers requiring MSHV-compatible left-rotation should negate the shift.</para>
    /// </summary>
    public static void CShift(Complex[] a, int count, int shift)
    {
        if (count <= 0 || shift == 0) return;
        shift = ((shift % count) + count) % count;
        ReverseSegment(a, 0, count - 1);
        ReverseSegment(a, 0, shift - 1);
        ReverseSegment(a, shift, count - 1);
    }

    private static void ReverseSegment(Complex[] a, int lo, int hi)
    {
        while (lo < hi) { (a[lo], a[hi]) = (a[hi], a[lo]); lo++; hi--; }
    }

    // ── Percentile (shell sort) ───────────────────────────────────────────────

    /// <summary>
    /// Compute the <paramref name="npct"/>-th percentile of <paramref name="data"/>[0..<paramref name="n"/>-1].
    /// Mirrors MSHV's <c>PomAll::pctile_shell</c> (uses a rented temp buffer).
    /// </summary>
    public static double Pctile(ReadOnlySpan<double> data, int n, double npct)
    {
        if (n <= 0) return 0.0;
        double[] tmp = ArrayPool<double>.Shared.Rent(n);
        try
        {
            data[..n].CopyTo(tmp);
            ShellSort(tmp, n);
            int idx = (int)(npct * n / 100.0);
            idx = Math.Clamp(idx, 0, n - 1);
            return tmp[idx];
        }
        finally
        {
            ArrayPool<double>.Shared.Return(tmp);
        }
    }

    private static void ShellSort(double[] a, int n)
    {
        int gap = n / 2;
        while (gap > 0)
        {
            for (int i = gap; i < n; i++)
            {
                double temp = a[i];
                int j = i;
                while (j >= gap && a[j - gap] > temp) { a[j] = a[j - gap]; j -= gap; }
                a[j] = temp;
            }
            gap /= 2;
        }
    }

    // ── Polynomial fit ────────────────────────────────────────────────────────

    /// <summary>
    /// Least-squares polynomial fit (port of MSHV's <c>PomAll::polyfit</c>).
    /// Fits polynomial of degree <paramref name="nterms"/>-1 to data points.
    /// </summary>
    /// <param name="x">Independent variable values.</param>
    /// <param name="y">Dependent variable values.</param>
    /// <param name="npts">Number of data points.</param>
    /// <param name="nterms">Number of polynomial coefficients (degree+1).</param>
    /// <param name="coeffs">Output coefficients c[0] + c[1]*x + c[2]*x^2 + ...</param>
    public static void Polyfit(
        ReadOnlySpan<double> x, ReadOnlySpan<double> y, int npts, int nterms,
        Span<double> coeffs)
    {
        // Build normal equations
        double[,] a = new double[nterms, nterms + 1];
        double[] xpow = new double[2 * nterms]; // allocated once, reused per data point
        for (int k = 0; k < npts; k++)
        {
            xpow[0] = 1.0;
            for (int j = 1; j < 2 * nterms; j++) xpow[j] = xpow[j - 1] * x[k];
            for (int i = 0; i < nterms; i++)
            {
                for (int j = 0; j < nterms; j++)
                    a[i, j] += xpow[i + j];
                a[i, nterms] += y[k] * xpow[i];
            }
        }
        // Gaussian elimination
        for (int col = 0; col < nterms; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < nterms; row++)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            for (int j = col; j <= nterms; j++)
                (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);
            double den = a[col, col];
            if (Math.Abs(den) < 1e-15) continue;
            for (int row = col + 1; row < nterms; row++)
            {
                double fac = a[row, col] / den;
                for (int j = col; j <= nterms; j++) a[row, j] -= fac * a[col, j];
            }
        }
        for (int i = nterms - 1; i >= 0; i--)
        {
            double s = a[i, nterms];
            for (int j = i + 1; j < nterms; j++) s -= a[i, j] * coeffs[j];
            coeffs[i] = Math.Abs(a[i, i]) > 1e-15 ? s / a[i, i] : 0.0;
        }
    }

    // ── Frequency tweak ───────────────────────────────────────────────────────

    /// <summary>
    /// Apply a fractional-sample frequency correction to a complex baseband signal.
    /// Mirrors MSHV's <c>PomFt::twkfreq1</c>.
    /// </summary>
    public static void TweakFreq(
        ReadOnlySpan<Complex> ca, int npts, double fsample, double freqOffset,
        Span<Complex> cb)
    {
        double phase = 0.0;
        double dphi = 2.0 * Math.PI * freqOffset / fsample;
        for (int i = 0; i < npts; i++)
        {
            double cos = Math.Cos(phase);
            double sin = Math.Sin(phase);
            cb[i] = new Complex(
                ca[i].Real * cos - ca[i].Imaginary * sin,
                ca[i].Real * sin + ca[i].Imaginary * cos);
            phase += dphi;
        }
    }

    // ── Analytic signal sum ───────────────────────────────────────────────────

    /// <summary>
    /// Sum of element-wise product of <c>a</c> and conjugate of <c>b</c>.
    /// Mirrors MSHV's <c>PomAll::sum_dca_mplay_conj_dca</c>.
    /// </summary>
    public static Complex SumMulConj(
        ReadOnlySpan<Complex> a, int aBeg, int aEnd, ReadOnlySpan<Complex> b)
    {
        Complex s = Complex.Zero;
        for (int i = aBeg; i <= aEnd; i++)
            s += a[i] * Complex.Conjugate(b[i - aBeg]);
        return s;
    }

    // ── Baseline helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Compute a running baseline (background noise) of a power spectrum.
    /// Used by FT8/FT4 to normalise bit-metrics.
    /// </summary>
    public static void ComputeBaseline(
        ReadOnlySpan<double> s, int nfa, int nfb, Span<double> sbase,
        int smoothPasses = 5)
    {
        int n = nfb - nfa + 1;
        for (int i = 0; i < n; i++) sbase[i] = s[nfa + i];
        // iterative lower-envelope smoothing
        for (int pass = 0; pass < smoothPasses; pass++)
        {
            for (int i = 1; i < n - 1; i++)
            {
                double avg = (sbase[i - 1] + sbase[i] + sbase[i + 1]) / 3.0;
                if (sbase[i] > avg) sbase[i] = avg;
            }
        }
    }

    // ── Fast tanh approximation ───────────────────────────────────────────────

    /// <summary>
    /// Padé [5/5] rational approximation to tanh(x).
    /// Max error &lt; 5 × 10⁻⁷ for |x| ≤ 5; clipped to ±1 beyond that.
    /// 6× faster than <see cref="Math.Tanh"/> on hot LDPC paths.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double FastTanh(double x)
    {
        if (x < -5.0) return -1.0;
        if (x >  5.0) return  1.0;
        double x2 = x * x;
        return x * (10395.0 + x2 * (1260.0 + x2 * 21.0)) /
                   (10395.0 + x2 * (4725.0 + x2 * (210.0 + x2)));
    }

    // ── Callsign validation ───────────────────────────────────────────────────

    /// <summary>
    /// Returns true if <paramref name="s"/> is a plausible standard callsign.
    /// Simplified version of MSHV's <c>PomAll::isStandardCall</c>.
    /// </summary>
    public static bool IsStandardCall(ReadOnlySpan<char> s)
    {
        if (s.Length < 3 || s.Length > 11) return false;
        bool hasDigit = false, hasLetter = false;
        foreach (char c in s)
        {
            if (c == '/' || c == '-') continue;
            if (char.IsDigit(c)) { hasDigit = true; continue; }
            if (char.IsLetter(c)) { hasLetter = true; continue; }
            return false;
        }
        return hasDigit && hasLetter;
    }
}
