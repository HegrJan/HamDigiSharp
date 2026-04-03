using System.Numerics;
using HamDigiSharp.Codecs;
using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Ft2;

/// <summary>
/// FT2 decoder — 3.75-second period, 4-FSK, 103 symbols, LDPC(174,91).
/// FT2 was created by IU8LMC (Martino) for ARI Caserta; adapted into MSHV by LZ2HV, GPL.
/// Official ADIF submode SUBMODE=FT2, certified 22:0 by ADIF Development Group (Mar 2026).
///
/// Timing: 2.47 s burst (103+2 symbols × 288 sps) within a 3.75-second window.
/// Two stations alternate in 7.5-second TX/RX cycles (each holds one 3.75-second slot).
///
/// Frame structure: identical to FT4 (same Costas groups, GrayMap, LDPC, Rvec scramble) but
/// Nsps=288 (half of FT4 = twice the symbol rate), giving a 41.67 Hz tone spacing.
/// Coherent averaging across consecutive periods improves sensitivity for weak signals.
/// </summary>
public sealed class Ft2Decoder : Ft4x2DecoderBase
{
    // Nsps=288, NDown=9, NMax=48600, Nfft1=1152, Nss=32, tone spacing=41.67 Hz
    // NMax=48600=45000+3600: extra 3600 samples (300 ms) extend the search window to
    // compensate for the 300 ms guard band prepended by RealTimeDecoder, restoring the
    // original positive-DT coverage (DT ≤ +1.28 s from UTC boundary).

    public override DigitalMode Mode => DigitalMode.FT2;

    // FT2 requires more Costas matches because its wider tone spacing makes
    // random noise slightly more likely to mimic a Costas pattern.
    private const int MinCostasMatches = 6;

    // ── Per-frequency LLR accumulator (coherent-combining state) ─────────────
    //
    // Instead of accumulating the complex baseband (which requires carrier-phase
    // alignment between periods), we accumulate unit-RMS-normalised LLR vectors.
    // This is Maximum-Ratio Combining in the log-likelihood domain:
    //
    //   Each period contributes  normLlr_k  (unit-RMS ensemble-E LLR).
    //   After N periods:  LlrSum = Σ normLlr_k.
    //   For a coherent signal:  |LlrSum| grows as N.
    //   For noise:              |LlrSum| grows as √N.
    //   → SNR improves as √N per accumulated period (no phase alignment required).
    //
    // With 16 periods: +6 dB; 64 periods: +9 dB.  Robust to Doppler, HF clock drift.
    //
    // Key: round(f × _nfft1 / SampleRate) maps frequency to the nearest candidate
    // search-grid bin (10.42 Hz resolution), matching candidates across periods.
    private sealed class FreqAccum
    {
        // 174 = DataPositions.Length × 2 = 87 data symbols × 2 bits; LDPC(174,91) input size.
        public const int LlrSize = 174;
        public readonly double[] LlrSum = new double[LlrSize];
        public int    Periods;
        public double LastFreq;
        public double LastDt;
        public double LastSnr;
    }

    private readonly Dictionary<int, FreqAccum> _freqAcc = new();
    private int QuantizeFreq(double f) => (int)Math.Round(f * _nfft1 / (double)SampleRate);

    public Ft2Decoder()
        : base(nsps: 288, nDown: 9, nMax: 48600, nfft1: 1152) { }

    // ── Decode entry point ────────────────────────────────────────────────────

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        if (samples.Length < _nMax / 4) return Array.Empty<DecodeResult>();

        if (Options.ClearAverage)
            _freqAcc.Clear();

        double[] dd = PrepareBuffer(samples);

        // Pre-compute the full-spectrum FFT once; all frequency candidates reuse it.
        Complex[] xFull = PrecomputeFft(dd);

        var results = new List<DecodeResult>();
        var decoded = new HashSet<string>();

        if (!Options.AveragingEnabled)
        {
            // PLINQ single-period path: only evaluate candidates above the spectrogram threshold.
            var candidates = FindCandidates4Fsk(dd, freqLow, freqHigh);
            if (candidates.Count == 0) return Array.Empty<DecodeResult>();

            var rawResults = candidates
                .AsParallel()
                .Select(freq =>
                {
                    var s4Local = new double[NSymbols, NBins];
                    var c1      = GetBaseband(xFull, freq);
                    int dtBest  = FindBestTimingOffset(c1, c1.Length);
                    double dt   = dtBest / ((double)12000 / _nDown);
                    DecodeResult? res = null;
                    TryDecodeBuffer3Timing(c1, dtBest, freq, dt, utcTime, s4Local, ref res);
                    return res;
                })
                .Where(r => r is not null)
                .ToList();

            foreach (var result in rawResults.OrderBy(r => r!.FrequencyHz))
            {
                if (decoded.Add(result!.Message))
                {
                    results.Add(result!);
                    Emit(result!);
                }
            }
        }
        else
        {
            // Averaging path: accumulate unit-RMS-normalised ensemble-E LLR across periods.
            //
            // Phase 1a uses ALL frequencies in the search range (no spectrogram threshold).
            // ComputeTimingCombinedLlr's internal Costas check acts as the effective gate:
            // it returns null for noise-only frequencies, non-null for frequencies where the
            // signal is detectable (even if below the spectrogram threshold).
            // This allows signals slightly below the single-period floor to accumulate LLRs
            // in periods where the Costas check happens to pass.
            //
            // Phase 2 iterates ALL previously-accumulated frequencies (_freqAcc.Keys), not
            // just the current period's candidates — this carry-forward means a frequency
            // seen in any previous period is always tried for decoding.
            var phase1Freqs = AllFrequenciesInRange(freqLow, freqHigh);

            // Nothing to do if no frequencies in range AND nothing accumulated yet.
            if (phase1Freqs.Count == 0 && _freqAcc.Count == 0)
                return Array.Empty<DecodeResult>();

            // Phase 1a (parallel): compute per-frequency ensemble-E LLR (CPU-bound, thread-safe).
            var phase1 = phase1Freqs
                .AsParallel()
                .Select(freq =>
                {
                    var s4     = new double[NSymbols, NBins];
                    var c1     = GetBaseband(xFull, freq);
                    int dtBest = FindBestTimingOffset(c1, c1.Length);
                    double dt  = dtBest * _nDown / (double)SampleRate;

                    // 5-channel timing combined LLR (fills s4 at nominal timing for SNR).
                    double[]? llrT = ComputeTimingCombinedLlr(c1, dtBest, MinCostasMatches, s4);
                    if (llrT is null)
                        return (freq, (double[]?)null, 0.0, 0.0);  // no Costas match

                    // Half-tone frequency sub-bin variant.
                    var cdNom  = ExtractAtOffset(c1, dtBest);
                    double[]? llrHT = ComputeLlr(
                        ShiftByHalfTone(cdNom, Nss), new double[NSymbols, NBins], MinCostasMatches);

                    // Ensemble E = normalized(normA + normB): same technique as BuildLlrVariants,
                    // applied here to produce the best unit-RMS LLR estimate for accumulation.
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

            // Phase 1b (sequential): update per-frequency accumulators.
            foreach (var (freq, normLlr, dt, snr) in phase1)
            {
                int key = QuantizeFreq(freq);
                if (!_freqAcc.TryGetValue(key, out var fa))
                    _freqAcc[key] = fa = new FreqAccum();
                for (int i = 0; i < FreqAccum.LlrSize; i++)
                    fa.LlrSum[i] += normLlr![i];
                fa.Periods++;
                fa.LastFreq = freq;
                fa.LastDt   = dt;
                fa.LastSnr  = snr;
            }

            // Phase 2 (parallel): decode from ALL accumulated frequencies.
            // _freqAcc is only read here (written in Phase 1b above), so concurrent reads are safe.
            // Iterating _freqAcc.Keys (not just current candidates) provides carry-forward:
            // a frequency seen in any prior period is always retried even if not detected today.
            var rawResults = _freqAcc.Keys
                .AsParallel()
                .Select(key =>
                {
                    if (!_freqAcc.TryGetValue(key, out var fa) || fa.Periods == 0) return null;

                    // Scale accumulated sum to LDPC-optimal level.
                    // After N periods LlrSum RMS ≈ √N (noise) or N (signal) → rescale to 3.2.
                    var scaledLlr = RmsScale(fa.LlrSum, LlrScaleFactor);
                    if (scaledLlr is null) return null;

                    // Decode; half-tone is already folded in via ensemble E above,
                    // so pass null llrB (no redundant BuildLlrVariants sub-variants needed).
                    var emptyS4 = new double[NSymbols, NBins];
                    DecodeResult? res = null;
                    TryLdpcVariants(scaledLlr, null, fa.LastFreq, fa.LastDt, utcTime, emptyS4, ref res);
                    if (res is not null) res = res with { Snr = fa.LastSnr };
                    return res;
                })
                .Where(r => r is not null)
                .ToList();

            foreach (var result in rawResults.OrderBy(r => r!.FrequencyHz))
                if (decoded.Add(result!.Message))
                {
                    results.Add(result!);
                    Emit(result!);
                }
        }
        return results;
    }

    // ── Frequency enumeration (averaging path) ───────────────────────────────

    /// <summary>
    /// Returns all candidate frequencies in [freqLow, freqHigh] at the search-grid step
    /// (df = SampleRate / _nfft1 = 10.42 Hz).  Used by the averaging path to evaluate
    /// every frequency in range without requiring it to pass the spectrogram threshold.
    /// </summary>
    private List<double> AllFrequenciesInRange(double freqLow, double freqHigh)
    {
        double df = (double)SampleRate / _nfft1;
        var result = new List<double>();
        for (double f0 = freqLow; f0 <= freqHigh; f0 += df)
            result.Add(f0);
        return result;
    }

    // ── Per-candidate decode (3-timing combined, non-averaging PLINQ path) ──────

    private bool TryDecodeBuffer3Timing(
        System.Numerics.Complex[] c1, int dtBest, double f0, double dt, string utcTime,
        double[,] s4, ref DecodeResult? result)
    {
        // 3-timing combined LLR (fills s4 at nominal timing for SNR).
        double[]? llrTiming = ComputeTimingCombinedLlr(c1, dtBest, MinCostasMatches, s4);
        if (llrTiming is null) return false;

        // Half-tone frequency shift of the nominal symbol array.
        var cdNom = ExtractAtOffset(c1, dtBest);
        var s4HT  = new double[NSymbols, NBins];
        double[]? llrHalfTone = ComputeLlr(ShiftByHalfTone(cdNom, Nss), s4HT, MinCostasMatches);

        return TryLdpcVariants(llrTiming, llrHalfTone, f0, dt, utcTime, s4, ref result);
    }

    // ── Shared LDPC loop ─────────────────────────────────────────────────────

    private bool TryLdpcVariants(
        double[]? llrA, double[]? llrB, double f0, double dt, string utcTime,
        double[,] s4, ref DecodeResult? result)
    {
        var apMask = EmptyApMask();
        var msg77  = new bool[77];
        var cw     = new bool[174];

        foreach (var llr in BuildLlrVariants(llrA, llrB))
        {
            Array.Clear(msg77); Array.Clear(cw);
            bool ok = Ldpc174_91.TryDecode(llr, apMask, Options.DecoderDepth,
                                            msg77, cw, out int hardErrors, out double dmin);
            if (!ok || hardErrors > 37) continue;

            // Undo FT2's pre-LDPC XOR scramble (identical to FT4, from MSHV decoderft2.cpp).
            for (int i = 0; i < 77; i++) msg77[i] ^= Rvec[i];

            string message = MessagePacker.Unpack77(msg77, out bool unpkOk);
            if (!unpkOk || string.IsNullOrWhiteSpace(message)) continue;

            result = new DecodeResult
            {
                UtcTime     = utcTime,
                Snr         = ComputeSnrDb4Fsk(s4),
                Dt          = dt,
                FrequencyHz = f0,
                Message     = message.Trim(),
                Mode        = DigitalMode.FT2,
                HardErrors  = hardErrors,
                Dmin        = dmin,
            };
            return true;
        }
        return false;
    }
}
