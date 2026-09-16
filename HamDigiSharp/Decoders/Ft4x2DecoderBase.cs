using System.Buffers;
using System.Numerics;
using System.Numerics.Tensors;
using HamDigiSharp.Codecs;
using HamDigiSharp.Dsp;
using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders;

/// <summary>
/// Abstract base class shared by <see cref="Ft4.Ft4Decoder"/> and
/// <see cref="Ft2.Ft2Decoder"/>.
///
/// Both modes use an identical 103-symbol, 4-FSK frame with four Costas pilot
/// groups, Gray-coded 2-bit symbols, and LDPC(174,91). They differ only in
/// symbol duration, downsample factor, buffer size, and two mode-specific
/// features: FT4 applies a 77-bit XOR scramble after LDPC decode; FT2 supports
/// coherent averaging across periods.  Extracting the shared algorithm here
/// eliminates duplication and guarantees that improvements — pilot-based SNR,
/// timing-offset refinement, and the half-tone frequency sub-bin pass — apply
/// equally to both modes.
/// </summary>
public abstract class Ft4x2DecoderBase : BaseDecoder
{
    // ── Shared frame constants ────────────────────────────────────────────────
    protected const int NSymbols    = 103;
    protected const int NBins       = 4;
    protected const int SampleRate  = 12_000;

    // LLR scale factor applied before LDPC belief-propagation.
    // MSHV (decoderft4.cpp / decoderft2.cpp) uses 2.83.
    // Decodium Raptor Engine (2026) uses 3.2 for FT2, yielding +0.5 dB sensitivity.
    // We use 3.2 for both FT4 and FT2: same LDPC(174,91) codec, same benefit.
    protected const double LlrScaleFactor = 3.2;

    // Costas pilot sequences — identical for FT4 and FT2.
    protected static readonly int[] CostasA = { 0, 1, 3, 2 };
    protected static readonly int[] CostasB = { 1, 0, 2, 3 };
    protected static readonly int[] CostasC = { 2, 3, 1, 0 };
    protected static readonly int[] CostasD = { 3, 2, 0, 1 };

    // Gray map for 4-FSK: natural-binary index → channel tone index.
    protected static readonly int[] GrayMap = { 0, 1, 3, 2 };

    // Pilot / data positions (computed once at class load).
    protected static readonly int[] PilotPositions;
    protected static readonly int[] DataPositions;

    private static readonly int[][] AllCostas;
    private static readonly int[]   CostasOffsets = { 0, 33, 66, 99 };

    static Ft4x2DecoderBase()
    {
        var pilots = new HashSet<int>();
        for (int i = 0; i < 4; i++)
        {
            pilots.Add(i);
            pilots.Add(i + 33);
            pilots.Add(i + 66);
            pilots.Add(i + 99);
        }
        PilotPositions = pilots.OrderBy(x => x).ToArray();
        DataPositions  = Enumerable.Range(0, NSymbols).Where(i => !pilots.Contains(i)).ToArray();
        AllCostas      = [CostasA, CostasB, CostasC, CostasD];
    }

    // ── XOR scramble mask shared by FT4 and FT2 ─────────────────────────────
    // Applied by the encoder before LDPC; must be undone after LDPC decoding.
    protected static readonly bool[] Rvec =
    {
        false,true ,false,false,true ,false,true ,false,false,true ,
        false,true ,true ,true ,true ,false,true ,false,false,false,
        true ,false,false,true ,true ,false,true ,true ,false,true ,
        false,false,true ,false,true ,true ,false,false,false,false,
        true ,false,false,false,true ,false,true ,false,false,true ,
        true ,true ,true ,false,false,true ,false,true ,false,true ,
        false,true ,false,true ,true ,false,true ,true ,true ,true ,
        true ,false,false,false,true ,false,true
    };

    // ── Instance constants (injected by concrete subclass) ────────────────────
    protected readonly int _nsps;   // samples per symbol at 12 kHz
    protected readonly int _nDown;  // frequency-domain downsample factor
    protected readonly int _nMax;   // recording buffer length (samples)
    protected readonly int _nfft1;  // candidate-search FFT size

    // Nuttall window cached once per instance (size = _nfft1, same for all decode calls).
    private readonly double[] _window;

    /// <summary>Samples per symbol after downsampling (Nsps / NDown = 32 for both FT4 and FT2).</summary>
    protected int    Nss         => _nsps / _nDown;
    private   double ToneSpacing => (double)SampleRate / _nsps;

    /// <summary>
    /// Half-step used for ±¼-symbol timing diversity in <see cref="CombineTimingChannels"/>.
    /// At ±Nss/4 = ±8 samples, ISI from adjacent symbols is only ~12.5% — small enough that
    /// LLR signs are almost always correct, yet the three channels see different multipath delays.
    /// </summary>
    private int TimingHalfStep => Nss / 4;

    protected Ft4x2DecoderBase(int nsps, int nDown, int nMax, int nfft1)
    {
        _nsps = nsps; _nDown = nDown; _nMax = nMax; _nfft1 = nfft1;
        _window = Windowing.Nuttall(_nfft1);
    }

    // ── Buffer preparation ────────────────────────────────────────────────────

    protected double[] PrepareBuffer(ReadOnlySpan<float> samples)
    {
        // Use the actual input length (at least _nMax) so larger guard bands from
        // RealTimeDecoder are fully processed rather than silently truncated.
        int n  = Math.Max(samples.Length, _nMax);
        var dd = new double[n];
        TensorPrimitives.ConvertTruncating<float, double>(samples, dd);
        return dd;
    }

    /// <summary>
    /// Applies WSJT-X 3.0 multi-cycle time-domain smoothing to the sample buffer
    /// (same algorithm as <c>Ft8Decoder.ApplyCycleSmoothing</c>).
    ///
    /// <list type="bullet">
    ///   <item>Cycle 0: identity — returns the original buffer.</item>
    ///   <item>Cycle 1: forward average — <c>dd[i] = (orig[i] + orig[i+1]) / 2</c>.
    ///     Shifts the signal forward by half a sample in time, giving the timing
    ///     search a slightly different alignment.</item>
    ///   <item>Cycle 2: backward average — <c>dd[i] = (orig[i-1] + orig[i]) / 2</c>.
    ///     Shifts the signal backward by half a sample.</item>
    /// </list>
    /// Each cycle runs the full candidate search + LDPC path on a fresh FFT so
    /// that previously missed signals (due to sub-sample timing misalignment) are found.
    /// </summary>
    protected static double[] ApplyCycleSmoothing(double[] original, int cycle)
    {
        if (cycle == 0) return original; // original buffer; no copy needed (only reads)

        var s = new double[original.Length];
        if (cycle == 1)
        {
            for (int i = 0; i < original.Length - 1; i++)
                s[i] = (original[i] + original[i + 1]) * 0.5;
            s[original.Length - 1] = original[original.Length - 1];
        }
        else // cycle == 2: backward average
        {
            s[0] = original[0];
            for (int i = 1; i < original.Length; i++)
                s[i] = (original[i - 1] + original[i]) * 0.5;
        }
        return s;
    }

    // ── Full-buffer FFT (computed once per Decode call, shared across candidates) ──

    protected Complex[] PrecomputeFft(double[] dd)
    {
        var xFull = new Complex[dd.Length];
        for (int i = 0; i < dd.Length; i++) xFull[i] = new Complex(dd[i] * 0.01, 0.0);
        Fft.ForwardInPlace(xFull);
        return xFull;
    }

    // ── Candidate search (grid-based, tone-power threshold) ──────────────────
    //
    // Candidates carry frequency only.  Timing is found during downsampling by
    // a full-range Costas-score search over the entire downsampled buffer, which
    // matches MSHV's approach and handles signals that start anywhere in the
    // recording window (typically 0.5 s after the period boundary).

    protected List<double> FindCandidates4Fsk(
        double[] dd, double freqLow, double freqHigh)
    {
        int    nh1   = _nfft1 / 2;
        int    nhsym = (dd.Length - _nfft1) / _nsps;
        double df    = (double)SampleRate / _nfft1;

        var window = _window;

        // Accumulate power spectrum in parallel.
        // Thread-local state bundles both the power accumulator and the FFT work buffer
        // so cbuf is allocated once per thread, not once per loop iteration.
        var lockObj = new Lock();
        double[] savg   = new double[nh1];
        double   savgSum = 0;

        Parallel.For(0, nhsym,
            () => (Accum: new double[nh1], Cbuf: new Complex[_nfft1]),
            (j, _, ls) =>
            {
                int ia = j * _nsps;
                for (int z = 0; z < _nfft1; z++)
                {
                    int    idx = ia + z;
                    double v   = idx < dd.Length ? dd[idx] * 0.01 : 0.0;
                    ls.Cbuf[z] = new Complex(v * window[z], 0.0);
                }
                Fft.ForwardInPlace(ls.Cbuf);
                for (int i = 0; i < nh1; i++)
                    ls.Accum[i] += ls.Cbuf[i].MagnitudeSquared;
                return ls;
            },
            ls =>
            {
                lock (lockObj)
                {
                    TensorPrimitives.Add(savg, ls.Accum, savg);
                    savgSum += TensorPrimitives.Sum<double>(ls.Accum);
                }
            });

        // sigThreshold: 4× the per-bin average power (avoids a second pass over savg[]).
        double sigThreshold = (savgSum / nh1) * 4.0;
        if (sigThreshold < 1e-20) return new List<double>(); // silence / zero input
        var    candidates   = new List<double>();

        for (double f0 = freqLow; f0 <= freqHigh; f0 += df * 0.5)
        {
            double power = 0;
            for (int t = 0; t < 4; t++)
            {
                int bin = (int)Math.Round((f0 + t * ToneSpacing) / df);
                if ((uint)bin < (uint)nh1) power += savg[bin];
            }
            if (power < sigThreshold) continue;
            candidates.Add(f0);
        }

        return candidates.Take(500).ToList();
    }

    // ── Frequency-domain downsampling ─────────────────────────────────────────

    /// <summary>
    /// Frequency-shifts the pre-computed full-spectrum FFT to baseband and
    /// inverse-FFTs to a downsampled complex waveform at <paramref name="f0"/>.
    /// Call <see cref="FindBestTimingOffset"/> then <see cref="ExtractAtOffset"/>
    /// to obtain the aligned symbol array.
    /// </summary>
    protected Complex[] GetBaseband(Complex[] xFull, double f0)
    {
        int    n      = xFull.Length;
        int    nFft2  = n / _nDown;
        double dfFull = (double)SampleRate / n;
        int    i0     = (int)(f0 / dfFull);

        var c1 = new Complex[nFft2];
        if (i0 >= 0 && i0 <= n / 2) c1[0] = xFull[i0];
        for (int i = 1; i < nFft2 / 2; i++)
        {
            if (i0 + i < n / 2) c1[i]         = xFull[i0 + i];
            if (i0 - i >= 0)    c1[nFft2 - i] = xFull[i0 - i];
        }
        double invN = 1.0 / nFft2;
        for (int i = 0; i < nFft2; i++)
            c1[i] = new Complex(c1[i].Real * invN, c1[i].Imaginary * invN);
        Fft.InverseInPlace(c1);
        return c1;
    }

    /// <summary>
    /// Extracts <see cref="NSymbols"/> × <see cref="Nss"/> samples from <paramref name="c1"/>
    /// starting at <paramref name="dtSamples"/> (downsampled-domain timing offset).
    /// Out-of-range indices produce <see cref="Complex.Zero"/>.
    /// </summary>
    protected Complex[] ExtractAtOffset(Complex[] c1, int dtSamples)
    {
        int nout = NSymbols * Nss;
        var cd   = new Complex[nout];
        FillAtOffset(c1, dtSamples, cd, nout);
        return cd;
    }

    /// <summary>
    /// Buffer-filling overload: writes into caller-supplied <paramref name="cd"/>
    /// (must have length ≥ <paramref name="count"/>).  Safe with pooled arrays.
    /// </summary>
    protected void FillAtOffset(Complex[] c1, int dtSamples, Complex[] cd, int count)
    {
        int c1Len = c1.Length;
        for (int i = 0; i < count; i++)
        {
            int idx = dtSamples + i;
            cd[i] = ((uint)idx < (uint)c1Len) ? c1[idx] : Complex.Zero;
        }
    }

    /// <summary>
    /// Convenience wrapper: computes baseband, finds optimal timing, extracts symbols.
    /// Use <see cref="GetBaseband"/> + <see cref="FindBestTimingOffset"/> +
    /// <see cref="ExtractAtOffset"/> directly when 3-channel timing diversity is needed.
    /// </summary>
    protected Complex[] Downsample(Complex[] xFull, double f0, out double dtSeconds)
    {
        var c1    = GetBaseband(xFull, f0);
        int dtBst = FindBestTimingOffset(c1, c1.Length);
        dtSeconds = dtBst / ((double)SampleRate / _nDown);
        return ExtractAtOffset(c1, dtBst);
    }

    // ── Full-range timing search (Costas-pilot SNR maximisation) ─────────────

    /// <summary>
    /// Searches the entire valid timing range of <paramref name="c1"/> for the
    /// sample offset that maximises the Costas-pilot correlation score.
    /// Uses a coarse pass (step 4) followed by a fine pass (step 1, ±8 around
    /// the coarse best), matching MSHV's segment-based search.
    /// </summary>
    protected int FindBestTimingOffset(Complex[] c1, int bufLen)
    {
        int nss      = Nss;
        int maxStart = Math.Max(0, bufLen - NSymbols * nss);

        int    best      = 0;
        double bestScore = double.MinValue;
        var    cbuf      = new Complex[nss];

        // Coarse pass — step 4
        for (int ib = 0; ib <= maxStart; ib += 4)
        {
            double score = CostasScore4Fsk(c1, cbuf, ib);
            if (score > bestScore) { bestScore = score; best = ib; }
        }

        // Fine pass — step 1, ±8 around coarse best
        int fineMin = Math.Max(0,        best - 8);
        int fineMax = Math.Min(maxStart, best + 8);
        for (int ib = fineMin; ib <= fineMax; ib++)
        {
            double score = CostasScore4Fsk(c1, cbuf, ib);
            if (score > bestScore) { bestScore = score; best = ib; }
        }
        return best;
    }

    internal double CostasScore4Fsk(Complex[] c1, Complex[] cbuf, int ibest)
    {
        int    nss   = Nss;
        double score = 0;
        for (int g = 0; g < 4; g++)
        {
            int[] cos = AllCostas[g];
            for (int k = 0; k < 4; k++)
            {
                int sym   = CostasOffsets[g] + k;
                int start = ibest + sym * nss;
                for (int i = 0; i < nss; i++)
                {
                    int idx = start + i;
                    cbuf[i] = (uint)idx < (uint)c1.Length ? c1[idx] : Complex.Zero;
                }
                Fft.ForwardInPlace(cbuf);

                int    expTone  = cos[k];
                double sigPow   = cbuf[expTone].MagnitudeSquared;
                double noisePow = 1e-20;
                for (int t = 0; t < 4; t++)
                    if (t != expTone)
                        noisePow += cbuf[t].MagnitudeSquared;
                score += sigPow / (noisePow / 3 + 1e-20);
            }
        }
        return score;
    }

    // ── LLR computation from 87 data symbols ─────────────────────────────────

    /// <summary>
    /// Fills <paramref name="s4"/>[sym, tone] with per-symbol FFT magnitudes,
    /// checks sync quality, then extracts 174 soft LLR values.
    /// </summary>
    protected double[]? ComputeLlr(Complex[] cd, double[,] s4, int minCostasMatches)
    {
        int nss  = Nss;
        // Pool the per-symbol FFT scratch buffer — nss=32 is a power of 2 so the pool
        // returns exactly 32 elements, which is required by ForwardInPlace.
        Complex[] cbuf = ArrayPool<Complex>.Shared.Rent(nss);
        try
        {
            for (int k = 0; k < NSymbols; k++)
            {
                for (int z = 0; z < nss; z++) cbuf[z] = cd[k * nss + z];
                Fft.ForwardInPlace(cbuf);
                for (int x = 0; x < NBins; x++)
                    s4[k, x] = Math.Sqrt(cbuf[x].MagnitudeSquared);
            }
        }
        finally { ArrayPool<Complex>.Shared.Return(cbuf); }

        if (CountCostasMatches(s4) < minCostasMatches) return null;

        var llr    = new double[174];
        int llrIdx = 0;

        foreach (int sym in DataPositions)
        {
            // bitPass=1 → MSB; bitPass=0 → LSB  (matches original FT4/FT2 LLR order)
            for (int bitPass = 1; bitPass >= 0; bitPass--)
            {
                double max1 = double.MinValue, max0 = double.MinValue;
                for (int t = 0; t < 4; t++)
                {
                    double v   = s4[sym, GrayMap[t]];
                    bool   bit = ((t >> bitPass) & 1) != 0;
                    if (bit) { if (v > max1) max1 = v; }
                    else     { if (v > max0) max0 = v; }
                }
                llr[llrIdx++] = LlrScaleFactor * (max1 - max0);
            }
        }
        return llr;
    }

    /// <summary>
    /// Builds a combined LLR vector from 5 timing channels at offsets
    /// <paramref name="dtBest"/>±Nss/4 and ±Nss/8 relative to the Costas-optimal offset.
    ///
    /// Algorithm (Maximum Ratio Combining):
    ///   1. Compute raw LLR at each timing; skip nulls (Costas check failed).
    ///   2. Sum raw LLRs without pre-normalization.
    ///      Channels with better timing alignment produce larger LLR magnitudes and
    ///      therefore dominate the sum — this is the "free" weighting of MRC.
    ///      Pre-normalizing to unit-RMS (equal-gain combining) would instead scale up
    ///      the noisier misaligned channels, which is suboptimal.
    ///   3. Normalize sum to unit-RMS and scale by LlrScaleFactor.
    ///
    /// Duplicate offsets near signal boundaries are automatically de-duplicated.
    /// </summary>
    protected double[]? ComputeTimingCombinedLlr(
        Complex[] c1, int dtBest, int minCostasMatches, double[,] s4Nominal)
    {
        int nFft2   = c1.Length;
        int step    = TimingHalfStep;
        int step2   = step / 2;
        int maxDt   = Math.Max(0, nFft2 - NSymbols * Nss);
        int cdCount = NSymbols * Nss;

        Complex[] cdBuf = ArrayPool<Complex>.Shared.Rent(cdCount);
        try
        {
            // Nominal channel (also fills s4Nominal for SNR measurement).
            Array.Clear(s4Nominal);
            FillAtOffset(c1, dtBest, cdBuf, cdCount);
            double[]? llrNom = ComputeLlr(cdBuf, s4Nominal, minCostasMatches);
            if (llrNom is null) return null;

            // MRC: collect raw LLRs (no unit-RMS pre-normalization).
            var channels = new List<double[]>(5) { llrNom };
            var seen     = new HashSet<int> { dtBest };

            ReadOnlySpan<int> offsets = [dtBest - step, dtBest - step2, dtBest + step2, dtBest + step];
            foreach (int off in offsets)
            {
                int clamped = Math.Clamp(off, 0, maxDt);
                if (!seen.Add(clamped)) continue;

                FillAtOffset(c1, clamped, cdBuf, cdCount);
                var llrOther = ComputeLlr(cdBuf, new double[NSymbols, NBins], minCostasMatches);
                if (llrOther is not null) channels.Add(llrOther);
            }

            int n      = channels[0].Length;
            var result = new double[n];
            foreach (var ch in channels)
                for (int i = 0; i < n; i++) result[i] += ch[i];

            double sumSq2 = 0;
            foreach (var v in result) sumSq2 += v * v;
            double rms2 = Math.Sqrt(sumSq2 / n);
            if (rms2 < 1e-20) return llrNom;
            double scale = LlrScaleFactor / rms2;
            for (int i = 0; i < n; i++) result[i] *= scale;

            return result;
        }
        finally
        {
            ArrayPool<Complex>.Shared.Return(cdBuf);
        }
    }

    /// <summary>
    /// Builds a list of LLR vectors to try with LDPC, in priority order:
    /// E (ensemble = normalized sum of A+B, tried first), A (timing-combined),
    /// B (half-tone), Max-abs(A,B), Average(A,B).
    ///
    /// Variant E combines the timing-diversity and frequency-diversity estimates
    /// into a single vector with reduced noise variance — analogous to FT8's bmetE.
    /// Trying it first captures many decodes that neither A nor B alone can resolve.
    /// </summary>
    protected static List<double[]> BuildLlrVariants(double[]? llrA, double[]? llrB)
    {
        var variants = new List<double[]>(5);

        // Ensemble E (tried first): balanced normalized sum of timing-combined + half-tone.
        if (llrA is not null && llrB is not null)
        {
            int n     = llrA.Length;
            var normA = RmsNorm(llrA);
            var normB = RmsNorm(llrB);
            if (normA is not null && normB is not null)
            {
                var e = new double[n];
                for (int i = 0; i < n; i++) e[i] = normA[i] + normB[i];
                var llrE = RmsScale(e, LlrScaleFactor);
                if (llrE is not null) variants.Add(llrE);
            }
        }

        if (llrA is not null) variants.Add(llrA);
        if (llrB is not null) variants.Add(llrB);

        if (llrA is not null && llrB is not null)
        {
            int n = llrA.Length;
            var llrMax = new double[n];
            var llrAvg = new double[n];
            for (int i = 0; i < n; i++)
            {
                // Max-abs: take the more confident value at each position
                llrMax[i] = Math.Abs(llrA[i]) >= Math.Abs(llrB[i]) ? llrA[i] : llrB[i];
                // Average: reduces noise by combining both estimates
                llrAvg[i] = (llrA[i] + llrB[i]) * 0.5;
            }
            variants.Add(llrMax);
            variants.Add(llrAvg);
        }
        return variants;
    }

    // ── RMS normalization helpers ─────────────────────────────────────────────

    /// <summary>Returns a copy of <paramref name="v"/> normalized to unit-RMS,
    /// or <c>null</c> if the vector is degenerate (all near zero).</summary>
    protected static double[]? RmsNorm(double[]? v)
    {
        if (v is null) return null;
        double sumSq = 0;
        foreach (var x in v) sumSq += x * x;
        double rms = Math.Sqrt(sumSq / v.Length);
        if (rms < 1e-20) return null;
        var out2 = new double[v.Length];
        double inv = 1.0 / rms;
        for (int i = 0; i < v.Length; i++) out2[i] = v[i] * inv;
        return out2;
    }

    /// <summary>Returns a copy of <paramref name="v"/> scaled so that its RMS equals
    /// <paramref name="targetRms"/>, or <c>null</c> if degenerate.</summary>
    protected static double[]? RmsScale(double[] v, double targetRms)
    {
        double sumSq = 0;
        foreach (var x in v) sumSq += x * x;
        double rms = Math.Sqrt(sumSq / v.Length);
        if (rms < 1e-20) return null;
        var out2 = new double[v.Length];
        double factor = targetRms / rms;
        for (int i = 0; i < v.Length; i++) out2[i] = v[i] * factor;
        return out2;
    }

    /// <summary>
    /// Scales <paramref name="v"/> in-place so that its RMS equals
    /// <paramref name="targetRms"/>.  No-op if the vector is degenerate.
    /// Use this to normalize a freshly-allocated scratch buffer to avoid an
    /// extra allocation compared to <see cref="RmsScale"/>.
    /// </summary>
    protected static void RmsScaleInPlace(double[] v, double targetRms)
    {
        double sumSq = 0;
        foreach (var x in v) sumSq += x * x;
        double rms = Math.Sqrt(sumSq / v.Length);
        if (rms < 1e-20) return;
        double factor = targetRms / rms;
        for (int i = 0; i < v.Length; i++) v[i] *= factor;
    }

    // ── Costas pilot quality check ────────────────────────────────────────────

    protected static int CountCostasMatches(double[,] s4)
    {
        int matches = 0;
        for (int g = 0; g < 4; g++)
        {
            int[] cos = AllCostas[g];
            for (int k = 0; k < 4; k++)
            {
                int sym = CostasOffsets[g] + k;
                if (sym >= NSymbols) break;
                int peak = 0;
                for (int t = 1; t < NBins; t++)
                    if (s4[sym, t] > s4[sym, peak]) peak = t;
                if (peak == cos[k]) matches++;
            }
        }
        return matches;
    }

    // ── Pilot-based SNR (analogous to FT8's ComputeSnrDb) ────────────────────

    /// <summary>
    /// Computes SNR in dB relative to a 2500 Hz noise reference bandwidth,
    /// using power at the expected tone vs. the three noise tones across all
    /// 16 Costas pilot symbols.  Matches the WSJT-X SNR convention.
    ///
    /// <para>xbase (WSJT-X 3.0): the 5th percentile of all 16×4 = 64 pilot-symbol
    /// per-tone magnitudes provides a global noise-floor baseline that prevents
    /// SNR inflation when adjacent tones carry competing signals (crowded band).</para>
    /// </summary>
    protected double ComputeSnrDb4Fsk(double[,] s4)
    {
        double sigSum = 0, noiseSum = 0;
        int    count  = 0;

        for (int g = 0; g < 4; g++)
        {
            int[] cos = AllCostas[g];
            for (int k = 0; k < 4; k++)
            {
                int sym     = CostasOffsets[g] + k;
                if (sym >= NSymbols) break;
                int expTone = cos[k];
                for (int t = 0; t < NBins; t++)
                {
                    double pow = s4[sym, t] * s4[sym, t];   // s4 holds magnitudes
                    if (t == expTone) sigSum   += pow;
                    else              noiseSum  += pow;
                }
                count++;
            }
        }

        // xbase: 5th percentile of all pilot-symbol per-tone powers (64 values).
        double xbase = ComputeXbase4Fsk(s4);

        // BW factor = 2500 Hz / tone_spacing — same convention as FT8 (uses 2500/6.25=400).
        double bwFactor   = 2500.0 / ToneSpacing;
        double adjNoise   = noiseSum / (count * 3);
        double noiseEst   = Math.Max(adjNoise, xbase) + 1e-20;
        double snrRaw     = (sigSum / count) / noiseEst;
        return Math.Round(Math.Max(-30.0, 10.0 * Math.Log10(snrRaw / bwFactor)));
    }

    /// <summary>
    /// Computes the 5th-percentile squared magnitude across all 16 Costas pilot
    /// symbols × 4 tones = 64 values in <paramref name="s4"/> (s4 holds magnitudes).
    /// Used as the xbase noise-floor estimate for SNR computation.
    /// </summary>
    private static double ComputeXbase4Fsk(double[,] s4)
    {
        const int Total = 16 * NBins; // 64
        Span<double> pows = stackalloc double[Total];
        int idx = 0;
        for (int g = 0; g < 4; g++)
        {
            int[] cos = AllCostas[g];
            for (int k = 0; k < 4; k++)
            {
                int sym = CostasOffsets[g] + k;
                if (sym >= NSymbols) { for (int t2 = 0; t2 < NBins; t2++) pows[idx++] = 0; continue; }
                for (int t = 0; t < NBins; t++)
                    pows[idx++] = s4[sym, t] * s4[sym, t];
            }
        }

        int pctIdx = Total / 20;  // floor(64 * 0.05) = 3
        return PercentileVal(pows, pctIdx);
    }

    private static double PercentileVal(Span<double> values, int kth)
    {
        double[] arr = values.ToArray();
        return QuickSelectPow(arr, 0, arr.Length - 1, kth);
    }

    private static double QuickSelectPow(double[] a, int lo, int hi, int k)
    {
        while (lo < hi)
        {
            double pivot = a[(lo + hi) >> 1];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (a[i] < pivot) i++;
                while (a[j] > pivot) j--;
                if (i <= j) { (a[i], a[j]) = (a[j], a[i]); i++; j--; }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return a[k];
        }
        return a[lo];
    }

    // ── Frequency half-tone sub-bin pass ─────────────────────────────────────

    /// <summary>
    /// Returns a copy of <paramref name="cd"/> shifted by +½ tone spacing in
    /// the downsampled baseband.  The phase rotation is π/Nss per sample (both
    /// FT4 and FT2 have Nss=32, so this equals π/32 per sample).
    /// Mirrors FT8's half-bin sub-pass, improving decoding of signals that fall
    /// between two tone-bin centres.
    /// </summary>
    protected static Complex[] ShiftByHalfTone(Complex[] cd, int nss)
    {
        var shifted = new Complex[cd.Length];
        FillShiftedByHalfTone(cd, nss, shifted, cd.Length);
        return shifted;
    }

    /// <summary>
    /// Buffer-filling overload: writes the half-tone shifted signal into
    /// <paramref name="dest"/> (must have length ≥ <paramref name="count"/>).
    /// Safe with pooled destination arrays.
    /// </summary>
    protected static void FillShiftedByHalfTone(Complex[] cd, int nss, Complex[] dest, int count)
    {
        for (int t = 0; t < count; t++)
        {
            double phi  = Math.PI * t / nss;
            double cosP = Math.Cos(phi), sinP = Math.Sin(phi);
            dest[t] = new Complex(
                cd[t].Real * cosP - cd[t].Imaginary * sinP,
                cd[t].Real * sinP + cd[t].Imaginary * cosP);
        }
    }

    // ── Abstract contract: subclass-specific Costas threshold ─────────────────

    /// <summary>
    /// Minimum number of Costas pilot symbols that must match to consider the
    /// signal present and attempt LDPC decoding.
    /// FT4 uses 4 (20.83 Hz tone spacing); FT2 uses 6 (41.67 Hz, stricter gate).
    /// </summary>
    protected abstract int MinCostasMatches { get; }

    // ── Shared LDPC decode loop ───────────────────────────────────────────────

    /// <summary>
    /// Tries each LLR variant produced by <see cref="BuildLlrVariants"/> against
    /// LDPC.  On the first successful decode populates <paramref name="result"/>
    /// and returns <c>true</c>.  Uses <see cref="BaseDecoder.Mode"/> for the
    /// result's Mode field, so both FT4 and FT2 get the correct label.
    /// Falls back to AP-assisted decode (types 2-6) if all normal variants fail
    /// and <see cref="DecoderOptions.ApDecode"/> is enabled.
    /// </summary>
    protected bool TryLdpcVariants(
        double[]? llrA, double[]? llrB, double f0, double dt, string utcTime,
        double[,] s4, ref DecodeResult? result)
    {
        bool[] msg77 = ArrayPool<bool>.Shared.Rent(77);
        bool[] cw    = ArrayPool<bool>.Shared.Rent(174);
        try
        {
            // ── Normal decode (no AP) ───────────────────────────────────────────
            var emptyMask = EmptyApMask();
            foreach (var llr in BuildLlrVariants(llrA, llrB))
            {
                Array.Clear(msg77, 0, 77);
                Array.Clear(cw, 0, 174);
                bool ok = Ldpc174_91.TryDecode(llr, emptyMask, Options.DecoderDepth,
                                                msg77, cw, out int hardErrors, out double dmin);
                if (!ok || hardErrors > 37) continue;

                for (int i = 0; i < 77; i++) msg77[i] ^= Rvec[i];

                string message = MessagePacker.Unpack77(msg77, out bool unpkOk);
                if (!unpkOk || string.IsNullOrWhiteSpace(message)) continue;

                float qual = (float)Math.Clamp(1.0 - (hardErrors + dmin) / 60.0, 0.0, 1.0);
                result = new DecodeResult
                {
                    UtcTime     = utcTime,
                    Snr         = ComputeSnrDb4Fsk(s4),
                    Dt          = dt,
                    FrequencyHz = f0,
                    Message     = message.Trim(),
                    Mode        = Mode,
                    HardErrors  = hardErrors,
                    Dmin        = dmin,
                    Quality     = qual,
                };
                return true;
            }

            // ── AP-assisted decode ──────────────────────────────────────────────
            if (Options.ApDecode)
            {
                double[]? baseLlr = llrA ?? llrB;  // ensemble or best available
                if (baseLlr is not null)
                {
                    foreach (var (apBits, apMask77) in CollectApHints())
                    {
                        var apLlr    = new double[174];
                        var ldpcMask = new bool[174];
                        baseLlr.AsSpan(0, 174).CopyTo(apLlr);
                        const double ApBias = 50.0;
                        // For FT4/FT2 the channel bit j = actual_bit[j] ^ Rvec[j].
                        // So AP known bit b → channel bit b ^ Rvec[j].
                        for (int j = 0; j < 77; j++)
                        {
                            if (apMask77[j])
                            {
                                bool channelBit = apBits[j] ^ Rvec[j];
                                apLlr[j]    = channelBit ? ApBias : -ApBias;
                                ldpcMask[j] = true;
                            }
                        }

                        Array.Clear(msg77, 0, 77);
                        Array.Clear(cw, 0, 174);
                        bool ok = Ldpc174_91.TryDecode(apLlr, ldpcMask, Options.DecoderDepth,
                                                        msg77, cw, out int hardErrors, out double dmin);
                        if (!ok || hardErrors > 40) continue;

                        for (int i = 0; i < 77; i++) msg77[i] ^= Rvec[i];

                        string message = MessagePacker.Unpack77(msg77, out bool unpkOk);
                        if (!unpkOk || string.IsNullOrWhiteSpace(message)) continue;

                        float qual = (float)Math.Clamp(1.0 - (hardErrors + dmin) / 60.0, 0.0, 1.0);
                        result = new DecodeResult
                        {
                            UtcTime     = utcTime,
                            Snr         = ComputeSnrDb4Fsk(s4),
                            Dt          = dt,
                            FrequencyHz = f0,
                            Message     = message.Trim(),
                            Mode        = Mode,
                            HardErrors  = hardErrors,
                            Dmin        = dmin,
                            Quality     = qual,
                            IsApDecode  = true,
                        };
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(msg77);
            ArrayPool<bool>.Shared.Return(cw);
        }
    }

    /// <summary>
    /// Collects AP hint pairs for FT4/FT2 based on current decoder options.
    /// Note: the channel bits include Rvec XOR; callers must XOR before biasing LLR.
    /// </summary>
    private IEnumerable<(bool[] bits, bool[] mask)> CollectApHints()
    {
        bool haveMyCall  = !string.IsNullOrEmpty(Options.MyCall);
        bool haveHisCall = !string.IsNullOrEmpty(Options.HisCall);

        yield return MessagePack77.BuildApHint77(MessagePack77.ApType.I3Only, null, null);

        if (haveMyCall)
        {
            yield return MessagePack77.BuildApHint77(
                MessagePack77.ApType.Call2, null, Options.MyCall);

            if (haveHisCall)
            {
                yield return MessagePack77.BuildApHint77(
                    MessagePack77.ApType.Call1Call2, Options.HisCall, Options.MyCall);
                yield return MessagePack77.BuildApHint77(
                    MessagePack77.ApType.Call1Call2Rrr, Options.HisCall, Options.MyCall);
                yield return MessagePack77.BuildApHint77(
                    MessagePack77.ApType.Call1Call2_73, Options.HisCall, Options.MyCall);
                yield return MessagePack77.BuildApHint77(
                    MessagePack77.ApType.Call1Call2Rr73, Options.HisCall, Options.MyCall);
            }
        }
        else if (haveHisCall)
        {
            yield return MessagePack77.BuildApHint77(
                MessagePack77.ApType.Call2, null, Options.HisCall);
        }
    }

    // ── Per-candidate single-period decode (5-channel timing + half-tone) ─────

    /// <summary>
    /// Attempts a single-period decode for the given pre-computed baseband
    /// <paramref name="c1"/> using 5-channel timing diversity and a half-tone
    /// frequency sub-pass, then calls <see cref="TryLdpcVariants"/>.
    /// </summary>
    protected bool TryDecodeBuffer3Timing(
        Complex[] c1, int dtBest, double f0, double dt, string utcTime,
        double[,] s4, ref DecodeResult? result)
    {
        double[]? llrTiming = ComputeTimingCombinedLlr(c1, dtBest, MinCostasMatches, s4);
        if (llrTiming is null) return false;

        int       cdCount = NSymbols * Nss;
        Complex[] cdBuf   = ArrayPool<Complex>.Shared.Rent(cdCount);
        Complex[] shifted = ArrayPool<Complex>.Shared.Rent(cdCount);
        try
        {
            FillAtOffset(c1, dtBest, cdBuf, cdCount);
            FillShiftedByHalfTone(cdBuf, Nss, shifted, cdCount);
            double[]? llrHalfTone = ComputeLlr(shifted, new double[NSymbols, NBins], MinCostasMatches);
            return TryLdpcVariants(llrTiming, llrHalfTone, f0, dt, utcTime, s4, ref result);
        }
        finally
        {
            ArrayPool<Complex>.Shared.Return(cdBuf);
            ArrayPool<Complex>.Shared.Return(shifted);
        }
    }

    // ── Multi-period LLR averaging (FT4 and FT2) ─────────────────────────────
    //
    // Coherent MRC in the log-likelihood domain:
    //   Each period contributes unit-RMS-normalised ensemble-E LLR.
    //   After N periods:  LlrSum ≈ N × signal  (or √N × noise).
    //   → SNR improves as √N per N periods (no phase alignment required).
    //
    // Robustness fixes (user-reported false-after-silence bug):
    //   1. Only accumulate when Costas check passes (signal gate).
    //   2. Expire accumulators after MaxConsecutiveMisses periods without signal
    //      (prevents stale LLR from a gone signal from decoding indefinitely).
    //   3. Clear accumulator immediately after a successful decode (prevents the
    //      same message from being re-decoded in the next near-silence period).

    private sealed class FreqAccum
    {
        public const  int      LlrSize = 174;
        public readonly double[] LlrSum  = new double[LlrSize];
        public int    Periods;
        public int    ConsecutiveMisses;
        public double LastFreq;
        public double LastDt;
        public double LastSnr;
    }

    // Two consecutive missed periods ≈ one TX/RX round-trip for all modes.
    private const int MaxConsecutiveMisses = 2;

    private readonly Dictionary<int, FreqAccum> _freqAcc = new();
    private int QuantizeFreq(double f) => (int)Math.Round(f * _nfft1 / (double)SampleRate);

    private List<double> AllFrequenciesInRange(double freqLow, double freqHigh)
    {
        double df     = (double)SampleRate / _nfft1;
        var    result = new List<double>();
        for (double f = freqLow; f <= freqHigh; f += df)
            result.Add(f);
        return result;
    }

    /// <summary>Resets the multi-period averaging accumulator.</summary>
    protected void ClearAveraging() => _freqAcc.Clear();

    /// <summary>
    /// Decodes using coherent multi-period LLR averaging.
    /// See class-level comment above for algorithm and robustness guarantees.
    /// </summary>
    private void DecodeAveraged(
        Complex[] xFull, double freqLow, double freqHigh, string utcTime,
        HashSet<string> decoded, List<DecodeResult> results)
    {
        var phase1Freqs = AllFrequenciesInRange(freqLow, freqHigh);
        if (phase1Freqs.Count == 0 && _freqAcc.Count == 0) return;

        // Phase 1a (parallel): compute ensemble-E unit-RMS LLR per frequency.
        // Frequencies where the Costas check fails return null and are NOT accumulated.
        var phase1 = phase1Freqs
            .AsParallel()
            .Select(freq =>
            {
                var    s4     = new double[NSymbols, NBins];
                var    c1     = GetBaseband(xFull, freq);
                int    dtBest = FindBestTimingOffset(c1, c1.Length);
                double dt     = dtBest * _nDown / (double)SampleRate;

                double[]? llrT = ComputeTimingCombinedLlr(c1, dtBest, MinCostasMatches, s4);
                if (llrT is null)
                    return (freq, (double[]?)null, 0.0, 0.0);

                int       cdCount2 = NSymbols * Nss;
                Complex[] cdBuf2   = ArrayPool<Complex>.Shared.Rent(cdCount2);
                Complex[] shifted2 = ArrayPool<Complex>.Shared.Rent(cdCount2);
                double[]? llrHT;
                try
                {
                    FillAtOffset(c1, dtBest, cdBuf2, cdCount2);
                    FillShiftedByHalfTone(cdBuf2, Nss, shifted2, cdCount2);
                    llrHT = ComputeLlr(shifted2, new double[NSymbols, NBins], MinCostasMatches);
                }
                finally
                {
                    ArrayPool<Complex>.Shared.Return(cdBuf2);
                    ArrayPool<Complex>.Shared.Return(shifted2);
                }

                var nA = RmsNorm(llrT);
                var nB = RmsNorm(llrHT);
                double[]? best;
                if (nA is not null && nB is not null)
                {
                    var e = new double[nA.Length];
                    for (int i = 0; i < nA.Length; i++) e[i] = nA[i] + nB[i];
                    best = RmsNorm(e);
                }
                else best = nA ?? nB;

                return (freq, best, dt, ComputeSnrDb4Fsk(s4));
            })
            .Where(r => r.Item2 is not null)
            .ToList();

        // Keys of quantized frequencies with signal this period.
        var activeKeys = new HashSet<int>(phase1.Select(r => QuantizeFreq(r.freq)));

        // Phase 1b (sequential): update per-frequency accumulators.
        foreach (var (freq, normLlr, dt, snr) in phase1)
        {
            int key = QuantizeFreq(freq);
            if (!_freqAcc.TryGetValue(key, out var fa))
                _freqAcc[key] = fa = new FreqAccum();
            for (int i = 0; i < FreqAccum.LlrSize; i++)
                fa.LlrSum[i] += normLlr![i];
            fa.Periods++;
            fa.ConsecutiveMisses = 0;
            fa.LastFreq          = freq;
            fa.LastDt            = dt;
            fa.LastSnr           = snr;
        }

        // Expire stale accumulators that missed too many consecutive periods.
        var toExpire = new List<int>();
        foreach (var kvp in _freqAcc)
        {
            if (activeKeys.Contains(kvp.Key)) continue;
            if (++kvp.Value.ConsecutiveMisses > MaxConsecutiveMisses)
                toExpire.Add(kvp.Key);
        }
        foreach (var k in toExpire) _freqAcc.Remove(k);

        // Phase 2 (parallel): attempt LDPC decode from all accumulated frequencies.
        // Snapshot keys first; _freqAcc is read-only during PLINQ, safe for concurrent reads.
        var keysToTry  = _freqAcc.Keys.ToList();
        var rawResults = keysToTry
            .AsParallel()
            .Select(key =>
            {
                if (!_freqAcc.TryGetValue(key, out var fa) || fa.Periods == 0)
                    return (key, (DecodeResult?)null);
                var scaledLlr = RmsScale(fa.LlrSum, LlrScaleFactor);
                if (scaledLlr is null) return (key, null);

                var          emptyS4 = new double[NSymbols, NBins];
                DecodeResult? res    = null;
                TryLdpcVariants(scaledLlr, scaledLlr, fa.LastFreq, fa.LastDt, utcTime, emptyS4, ref res);
                if (res is not null) res = res with { Snr = fa.LastSnr };
                return (key, res);
            })
            .Where(r => r.Item2 is not null)
            .ToList();

        // Collect results sequentially; clear decoded accumulators to prevent
        // re-decoding the same message from stale LLR in near-silence periods.
        foreach (var (key, result) in rawResults.OrderBy(r => r.Item2!.FrequencyHz))
        {
            if (decoded.Add(result!.Message))
            {
                results.Add(result!);
                Emit(result!);
            }
            _freqAcc.Remove(key);
        }
    }

    // ── Decode entry point (shared by FT4 and FT2) ───────────────────────────

    /// <summary>
    /// Decodes one period of PCM audio.  When <see cref="DecoderOptions.AveragingEnabled"/>
    /// is <see langword="true"/> uses coherent multi-period LLR averaging; otherwise
    /// decodes the single period in parallel across all spectrogram candidates.
    ///
    /// <para>When <see cref="DecoderOptions.DecoderCycles"/> is 2 or 3, applies WSJT-X 3.0
    /// multi-cycle time-domain smoothing before each decode pass.  Additional cycles
    /// find signals whose timing falls between half-sample boundaries, improving
    /// sensitivity at the cost of 2–3× more compute.  Averaging mode is unaffected
    /// (averaging already provides its own multi-period diversity).</para>
    /// </summary>
    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        if (samples.Length < _nMax / 4) return [];

        if (Options.ClearAverage) ClearAveraging();

        double[] originalDd = PrepareBuffer(samples);

        var results = new List<DecodeResult>();
        var decoded = new HashSet<string>();

        if (!Options.AveragingEnabled)
        {
            int cycles = Math.Max(1, Math.Min(3, Options.DecoderCycles));
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                double[]  dd    = ApplyCycleSmoothing(originalDd, cycle);
                Complex[] xFull = PrecomputeFft(dd);

                var candidates = FindCandidates4Fsk(dd, freqLow, freqHigh);
                if (candidates.Count == 0) continue;

                var rawResults = candidates
                    .AsParallel()
                    .Select(freq =>
                    {
                        var    s4Local = new double[NSymbols, NBins];
                        var    c1      = GetBaseband(xFull, freq);
                        int    dtBest  = FindBestTimingOffset(c1, c1.Length);
                        double dt      = dtBest * _nDown / (double)SampleRate;
                        DecodeResult? r = null;
                        TryDecodeBuffer3Timing(c1, dtBest, freq, dt, utcTime, s4Local, ref r);
                        return r;
                    })
                    .Where(r => r is not null)
                    .ToList();

                foreach (var result in rawResults.OrderBy(r => r!.FrequencyHz))
                    if (decoded.Add(result!.Message)) { results.Add(result!); Emit(result!); }
            }
        }
        else
        {
            Complex[] xFull = PrecomputeFft(originalDd);
            DecodeAveraged(xFull, freqLow, freqHigh, utcTime, decoded, results);
        }

        return results;
    }
}
