using System.Numerics;
using HamDigiSharp.Codecs;
using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Ft4;

/// <summary>
/// FT4 decoder — 7.5-second period, 4-FSK, 103 symbols, LDPC(174,91).
/// C# port of MSHV's <c>DecoderFt4</c> (LZ2HV) and WSJT-X FT4 (K1JT et al.), GPL.
///
/// Frame structure (103 symbols):
///   [0..3]    Costas-A  {0,1,3,2}
///   [4..32]   29 data symbols
///   [33..36]  Costas-B  {1,0,2,3}
///   [37..65]  29 data symbols
///   [66..69]  Costas-C  {2,3,1,0}
///   [70..98]  29 data symbols
///   [99..102] Costas-D  {3,2,0,1}
///   87 data symbols × 2 bits = 174 LDPC codeword bits
/// </summary>
public sealed class Ft4Decoder : Ft4x2DecoderBase
{
    // Nsps=576, NDown=18, NMax=76176, Nfft1=1152, Nss=32, tone spacing=20.83 Hz
    // NMax=76176=72576+3600: extra 3600 samples (300 ms) extend the search window to
    // compensate for the 300 ms guard band prepended by RealTimeDecoder, restoring the
    // original positive-DT coverage (DT ≤ +1.1 s from UTC boundary).
    public Ft4Decoder() : base(nsps: 576, nDown: 18, nMax: 76176, nfft1: 1152) { }

    public override DigitalMode Mode => DigitalMode.FT4;

    // Minimum number of Costas pilot symbols that must match to attempt LDPC.
    private const int MinCostasMatches = 4;

    // ── Decode entry point ────────────────────────────────────────────────────

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        if (samples.Length < _nMax / 4) return Array.Empty<DecodeResult>();

        double[]   dd         = PrepareBuffer(samples);
        var        candidates = FindCandidates4Fsk(dd, freqLow, freqHigh);
        if (candidates.Count == 0) return Array.Empty<DecodeResult>();

        // Pre-compute the full-spectrum FFT once; each candidate reuses it.
        Complex[] xFull = PrecomputeFft(dd);

        // PLINQ: xFull is read-only; each candidate gets its own s4 buffer.
        var rawResults = candidates
            .AsParallel()
            .Select(freq =>
            {
                var s4Local = new double[NSymbols, NBins];
                return TryDecodeFt4(xFull, freq, utcTime, s4Local, out var r) ? r : null;
            })
            .Where(r => r is not null)
            .ToList();

        var results = new List<DecodeResult>();
        var decoded = new HashSet<string>();
        foreach (var result in rawResults.OrderBy(r => r!.FrequencyHz))
        {
            if (decoded.Add(result!.Message))
            {
                results.Add(result!);
                Emit(result!);
            }
        }
        return results;
    }

    // ── Per-candidate decode (nominal + half-tone sub-pass) ──────────────────

    private bool TryDecodeFt4(
        Complex[] xFull, double f0, string utcTime,
        double[,] s4, out DecodeResult? result)
    {
        result = null;

        // Step 1: baseband + optimal timing.
        var c1    = GetBaseband(xFull, f0);
        int dtBst = FindBestTimingOffset(c1, c1.Length);
        double dt = dtBst / ((double)12000 / _nDown);   // convert to seconds

        // Step 2: 3-timing-combined LLR (s4 is filled at the nominal timing).
        // Returns null if the signal isn't present at f0 (Costas check failed).
        double[]? llrTiming = ComputeTimingCombinedLlr(c1, dtBst, MinCostasMatches, s4);
        if (llrTiming is null) return false;

        // Step 3: half-tone frequency shift of the nominal cd.
        var cdNom      = ExtractAtOffset(c1, dtBst);
        var s4HT       = new double[NSymbols, NBins];
        double[]? llrHalfTone = ComputeLlr(ShiftByHalfTone(cdNom, Nss), s4HT, MinCostasMatches);

        // Step 4: try timing-combined, half-tone, max-abs(both), avg(both).
        var apMask = EmptyApMask();
        var msg77  = new bool[77];
        var cw     = new bool[174];

        foreach (var llr in BuildLlrVariants(llrTiming, llrHalfTone))
        {
            Array.Clear(msg77); Array.Clear(cw);
            bool ok = Ldpc174_91.TryDecode(llr, apMask, Options.DecoderDepth,
                                            msg77, cw, out int hardErrors, out double dmin);
            if (!ok || hardErrors > 37) continue;

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
                Mode        = DigitalMode.FT4,
                HardErrors  = hardErrors,
                Dmin        = dmin,
            };
            return true;
        }
        return false;
    }
}

