using System.Numerics;
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
        AllCostas      = new[] { CostasA, CostasB, CostasC, CostasD };
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
        var dd = new double[_nMax];
        int len = Math.Min(samples.Length, _nMax);
        for (int i = 0; i < len; i++) dd[i] = samples[i];
        return dd;
    }

    // ── Full-buffer FFT (computed once per Decode call, shared across candidates) ──

    protected Complex[] PrecomputeFft(double[] dd)
    {
        var xFull = new Complex[_nMax];
        for (int i = 0; i < _nMax; i++) xFull[i] = new Complex(dd[i] * 0.01, 0.0);
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
        int    nhsym = (_nMax - _nfft1) / _nsps;
        double df    = (double)SampleRate / _nfft1;

        var window = _window;

        // Accumulate power spectrum in parallel.
        // Thread-local state bundles both the power accumulator and the FFT work buffer
        // so cbuf is allocated once per thread, not once per loop iteration.
        var lockObj = new object();
        double[] savg = new double[nh1];

        Parallel.For(0, nhsym,
            () => (Accum: new double[nh1], Cbuf: new Complex[_nfft1]),
            (j, _, ls) =>
            {
                int ia = j * _nsps;
                for (int z = 0; z < _nfft1; z++)
                {
                    int    idx = ia + z;
                    double v   = idx < _nMax ? dd[idx] * 0.01 : 0.0;
                    ls.Cbuf[z] = new Complex(v * window[z], 0.0);
                }
                Fft.ForwardInPlace(ls.Cbuf);
                for (int i = 0; i < nh1; i++)
                    ls.Accum[i] += ls.Cbuf[i].Real * ls.Cbuf[i].Real + ls.Cbuf[i].Imaginary * ls.Cbuf[i].Imaginary;
                return ls;
            },
            ls => { lock (lockObj) for (int i = 0; i < nh1; i++) savg[i] += ls.Accum[i]; });

        double sigThreshold = savg.Average() * 4.0;
        if (sigThreshold < 1e-20) return new List<double>(); // silence / zero input
        var    candidates   = new List<double>();

        for (double f0 = freqLow; f0 <= freqHigh; f0 += df)
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
        int    nFft2  = _nMax / _nDown;
        double dfFull = (double)SampleRate / _nMax;
        int    i0     = (int)(f0 / dfFull);

        var c1 = new Complex[nFft2];
        if (i0 >= 0 && i0 <= _nMax / 2) c1[0] = xFull[i0];
        for (int i = 1; i < nFft2 / 2; i++)
        {
            if (i0 + i < _nMax / 2) c1[i]         = xFull[i0 + i];
            if (i0 - i >= 0)        c1[nFft2 - i] = xFull[i0 - i];
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
        for (int i = 0; i < nout; i++)
        {
            int idx = dtSamples + i;
            cd[i] = ((uint)idx < (uint)c1.Length) ? c1[idx] : Complex.Zero;
        }
        return cd;
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
                double sigPow   = cbuf[expTone].Real * cbuf[expTone].Real
                                + cbuf[expTone].Imaginary * cbuf[expTone].Imaginary;
                double noisePow = 1e-20;
                for (int t = 0; t < 4; t++)
                    if (t != expTone)
                        noisePow += cbuf[t].Real * cbuf[t].Real
                                  + cbuf[t].Imaginary * cbuf[t].Imaginary;
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
        var cbuf = new Complex[nss];

        for (int k = 0; k < NSymbols; k++)
        {
            for (int z = 0; z < nss; z++) cbuf[z] = cd[k * nss + z];
            Fft.ForwardInPlace(cbuf);
            for (int x = 0; x < NBins; x++)
                s4[k, x] = Math.Sqrt(cbuf[x].Real * cbuf[x].Real
                                   + cbuf[x].Imaginary * cbuf[x].Imaginary);
        }

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
    /// Builds a 3-timing-channel combined LLR vector from symbol arrays extracted at
    /// <paramref name="dtBest"/>−¼symbol, <paramref name="dtBest"/>, and
    /// <paramref name="dtBest"/>+¼symbol relative to the Costas-optimal offset.
    ///
    /// Algorithm (Decodium "per-channel RMS normalization"):
    ///   1. Compute LLR at each timing; skip nulls (Costas check failed).
    ///   2. Normalize each to unit-RMS so that channels of varying SNR contribute
    ///      equally — prevents the nominal channel from dominating in a fading scenario.
    ///   3. Sum normalized channels, normalize sum to unit-RMS, scale by LlrScaleFactor.
    ///
    /// Extended to 5 timing channels (±Nss/4 and ±Nss/8) for broader multipath coverage.
    /// Duplicate offsets near boundaries are automatically de-duplicated.
    /// </summary>
    protected double[]? ComputeTimingCombinedLlr(
        Complex[] c1, int dtBest, int minCostasMatches, double[,] s4Nominal)
    {
        int nFft2   = c1.Length;
        int step    = TimingHalfStep;           // = Nss / 4 = 8 for both FT4 and FT2
        int step2   = step / 2;                 // = Nss / 8 = 4
        int maxDt   = Math.Max(0, nFft2 - NSymbols * Nss);

        // Nominal channel (also fills s4Nominal for SNR measurement).
        Array.Clear(s4Nominal);
        var cdNom  = ExtractAtOffset(c1, dtBest);
        double[]? llrNom = ComputeLlr(cdNom, s4Nominal, minCostasMatches);
        if (llrNom is null) return null;     // signal not present at this frequency

        // Build normalized list starting with the nominal channel.
        var channels = new List<double[]>(5);
        var seen     = new HashSet<int> { dtBest };
        var normNom  = RmsNorm(llrNom);
        if (normNom is not null) channels.Add(normNom);

        // Four additional timing offsets: ±step and ±step/2; skip duplicates.
        foreach (int off in new[] { dtBest - step, dtBest - step2, dtBest + step2, dtBest + step })
        {
            int clamped = Math.Clamp(off, 0, maxDt);
            if (!seen.Add(clamped)) continue;       // skip if identical to a prior offset

            var llrOther = ComputeLlr(
                ExtractAtOffset(c1, clamped),
                new double[NSymbols, NBins],
                minCostasMatches);
            var norm = RmsNorm(llrOther);
            if (norm is not null) channels.Add(norm);
        }
        if (channels.Count == 0) return null;

        // Sum normalized channels.
        int n      = channels[0].Length;
        var result = new double[n];
        foreach (var ch in channels)
            for (int i = 0; i < n; i++) result[i] += ch[i];

        // Normalize the sum to unit-RMS, then scale to LlrScaleFactor.
        // This ensures LDPC always receives LLR values at the empirically-optimal scale
        // regardless of the absolute signal level in the recording.
        double sumSq2 = 0;
        foreach (var v in result) sumSq2 += v * v;
        double rms2 = Math.Sqrt(sumSq2 / n);
        if (rms2 < 1e-20) return llrNom;     // degenerate sum — fall back to nominal
        double scale = LlrScaleFactor / rms2;
        for (int i = 0; i < n; i++) result[i] *= scale;

        return result;
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

        // BW factor = 2500 Hz / tone_spacing — same convention as FT8 (uses 2500/6.25=400).
        double bwFactor = 2500.0 / ToneSpacing;
        double snrRaw   = (sigSum / count) / (noiseSum / (count * 3) + 1e-20);
        return Math.Round(Math.Max(-30.0, 10.0 * Math.Log10(snrRaw / bwFactor)));
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
        for (int t = 0; t < cd.Length; t++)
        {
            double phi  = Math.PI * t / nss;
            double cosP = Math.Cos(phi), sinP = Math.Sin(phi);
            shifted[t] = new Complex(
                cd[t].Real * cosP - cd[t].Imaginary * sinP,
                cd[t].Real * sinP + cd[t].Imaginary * cosP);
        }
        return shifted;
    }
}
