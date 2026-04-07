using System.Collections.Concurrent;
using System.Numerics;
using HamDigiSharp.Codecs;
using HamDigiSharp.Models;
using MathNet.Numerics.IntegralTransforms;

namespace HamDigiSharp.Decoders.Wspr;

/// <summary>
/// WSPR decoder: two-pass pipeline with signal subtraction and AP callsign gating.
///
/// <b>Pass 1 (Fano only):</b> downmix → FFT waterfall → candidate detection →
/// 3-mode sync → deinterleave → Fano sequential decode.
/// Every successful Fano result is accepted and its callsign added to the
/// persistent AP callsign hash.
///
/// <b>Signal subtraction:</b> each Pass 1 signal is coherently subtracted from
/// the complex baseband using amplitude estimation with a 360-tap sin-window LPF
/// (subtract_signal2 from wsprd.c), improving sensitivity for co-channel signals.
///
/// <b>Pass 2 (Fano + OSD with AP gating):</b> rebuilt waterfall on the cleaned
/// baseband.  Fano first; if it fails, OSD (Ordered Statistics Decoding, depth=1).
/// OSD results are only accepted when the decoded callsign already appears in the
/// AP hash — preventing phantoms while still recovering weak signals seen in
/// earlier periods.
///
/// <b>AP hash lifetime:</b> the hash persists across all <see cref="Decode"/> calls
/// on the same instance (improving sensitivity over a long receive session).
/// Call <see cref="Reset"/> to clear it, or create a new instance.
///
/// Ported from wsprd.c / wspr_old_subs.f90 / osdwspr.f90 (K1JT / K9AN, GPL-3).
/// </summary>
public sealed class WsprDecoder : BaseDecoder
{
    // ── DSP constants (at downsampled 375 Hz) ─────────────────────────────────
    private const double SrDown    = 375.0;
    private const int    DecRate   = 32;          // 12000 / 32 = 375
    private const int    NSym      = 162;
    private const int    NSps      = 256;         // samples / symbol at 375 Hz
    private const double Df        = SrDown / NSps; // ≈ 1.4648 Hz — tone spacing
    private const int    NfftW     = 512;         // waterfall FFT size
    private const int    NfftStep  = 128;         // waterfall step
    private const double BinWidth  = SrDown / NfftW; // ≈ 0.732 Hz — FFT bin width (= Df/2)
    private const double TwoPiDt   = Math.Tau / SrDown;
    private const int    SymFac    = 50;          // soft-symbol scale factor
    private const float  MinSync1  = 0.10f;       // threshold after coarse sync
    private const float  MinSync2  = 0.12f;       // threshold before symbol extraction
    private const double MinRms    = 52.0 * SymFac / 64.0; // ≈ 40.6
    private const int    MaxDrift  = 4;           // Hz / period drift search
    private const double SnrScale  = 26.3;        // WSPR-2 SNR offset (dB, 2500-Hz ref)

    // Nominal signal start: 1 s into the period (spec) + some margin → 2 s
    private const int    NomStart  = (int)(2 * SrDown);   // 750 samples

    // ── Half-sine waterfall window ─────────────────────────────────────────────
    private static readonly float[] Win = BuildWindow();
    private static float[] BuildWindow()
    {
        var w = new float[NfftW];
        for (int i = 0; i < NfftW; i++)
            w[i] = (float)Math.Sin(Math.PI * i / NfftW);
        return w;
    }

    public override DigitalMode Mode => DigitalMode.Wspr;

    // ── AP callsign hash — persists across decode periods ────────────────────

    /// <summary>
    /// Callsigns seen via Fano decode, stored as "CALLSIGN" → true.
    /// Used to gate OSD results in pass 2 (AP check).
    /// Thread-safe; populated after pass 1 finishes.
    /// Persists across <see cref="Decode"/> calls — call <see cref="Reset"/> to clear.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _knownCalls = new(StringComparer.Ordinal);

    /// <summary>
    /// Clears the persistent AP callsign hash, resetting the decoder to a cold-start
    /// state.  Call this when beginning a new receive session where prior callsign
    /// history is no longer relevant.
    /// </summary>
    public void Reset() => _knownCalls.Clear();

    // ── Decoded-signal record (carries subtraction parameters) ────────────────
    private sealed record DecodedSignal(
        DecodeResult Result, float F1, int Shift1, float Drift1, byte[] ChannelSymbols);

    // ── Main decode entry ─────────────────────────────────────────────────────

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        if (samples.Length < 12000 * 100) return Array.Empty<DecodeResult>();

        double centerFreq = (freqLow + freqHigh) / 2.0;
        var (idat, qdat) = MixAndDecimate(samples, centerFreq);

        int nffts = Math.Max(1, 4 * (idat.Length / NfftW) - 1);
        var seen  = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        // ── Pass 1: Fano-only decode ───────────────────────────────────────────
        float[,] ps         = BuildWaterfall(idat, qdat, nffts);
        float[]  psavg      = ComputePsAvg(ps, nffts);
        float    noiseLevel = EstimateNoise(psavg);
        var      candidates = FindCandidates(ps, psavg, noiseLevel);

        var pass1 = DecodePass(idat, qdat, nffts, ps, candidates, seen,
                               utcTime, centerFreq, allowOsd: false);

        // Update AP hash from pass 1 results
        foreach (var sig in pass1) UpdateCallHash(sig.Result.Message);

        // ── Signal subtraction: remove pass-1 signals from baseband ───────────
        foreach (var sig in pass1)
            SubtractSignal(idat, qdat, sig.F1, sig.Shift1, sig.Drift1, sig.ChannelSymbols);

        // ── Pass 2: Fano + OSD with AP hash, on cleaned baseband ──────────────
        ps         = BuildWaterfall(idat, qdat, nffts);
        psavg      = ComputePsAvg(ps, nffts);
        noiseLevel = EstimateNoise(psavg);
        candidates = FindCandidates(ps, psavg, noiseLevel);

        var pass2 = DecodePass(idat, qdat, nffts, ps, candidates, seen,
                               utcTime, centerFreq, allowOsd: true);

        // Update AP hash from pass 2 results
        foreach (var sig in pass2) UpdateCallHash(sig.Result.Message);

        // ── Emit ordered by frequency ─────────────────────────────────────────
        var results = pass1.Concat(pass2)
                           .Select(s => s.Result)
                           .OrderBy(r => r.FrequencyHz)
                           .ToList();
        foreach (var r in results) Emit(r);
        return results;
    }

    // ── Parallel decode pass ──────────────────────────────────────────────────

    private List<DecodedSignal> DecodePass(
        float[] idat, float[] qdat, int nffts, float[,] ps,
        List<CandInfo> candidates,
        ConcurrentDictionary<string, byte> seen,
        string utcTime, double centerFreq, bool allowOsd)
    {
        var bag = new ConcurrentBag<DecodedSignal>();

        Parallel.ForEach(candidates, cand =>
        {
            var (freq0, shift0, drift0, sync0) = CoarseSync(ps, cand.Freq, nffts);

            float f1     = freq0;
            int   shift1 = shift0;
            float drift1 = drift0;
            float sync1  = sync0;

            var symbols = new byte[NSym];

            int lagMin = shift1 - 128, lagMax = shift1 + 128;
            SyncAndDemodulate(idat, qdat, symbols,
                ref f1, 0, 0, 0f, ref shift1, lagMin, lagMax, 64, ref drift1, SymFac, ref sync1, 0);
            SyncAndDemodulate(idat, qdat, symbols,
                ref f1, -2, 2, 0.25f, ref shift1, lagMin, lagMax, 64, ref drift1, SymFac, ref sync1, 1);

            float driftp = drift1 + 0.5f, syncp = sync1;
            float driftm = drift1 - 0.5f, syncm = sync1;
            SyncAndDemodulate(idat, qdat, symbols,
                ref f1, 0, 0, 0f, ref shift1, lagMin, lagMax, 64, ref driftp, SymFac, ref syncp, 1);
            SyncAndDemodulate(idat, qdat, symbols,
                ref f1, 0, 0, 0f, ref shift1, lagMin, lagMax, 64, ref driftm, SymFac, ref syncm, 1);
            if (syncp > sync1) { drift1 = driftp; sync1 = syncp; }
            else if (syncm > sync1) { drift1 = driftm; sync1 = syncm; }

            if (sync1 < MinSync1) return;

            lagMin = shift1 - 32; lagMax = shift1 + 32;
            SyncAndDemodulate(idat, qdat, symbols,
                ref f1, 0, 0, 0f, ref shift1, lagMin, lagMax, 16, ref drift1, SymFac, ref sync1, 0);
            SyncAndDemodulate(idat, qdat, symbols,
                ref f1, -2, 2, 0.05f, ref shift1, lagMin, lagMax, 16, ref drift1, SymFac, ref sync1, 1);

            if (sync1 < MinSync2) return;

            SyncAndDemodulate(idat, qdat, symbols,
                ref f1, 0, 0, 0f, ref shift1, shift1, shift1, 1, ref drift1, SymFac, ref sync1, 2);

            double sq = 0;
            for (int i = 0; i < NSym; i++) { double y = symbols[i] - 128.0; sq += y * y; }
            if (Math.Sqrt(sq / NSym) < MinRms) return;

            WsprConv.Deinterleave(symbols);

            byte[] decoded;
            bool fanoOk = WsprConv.FanoDecode(symbols, out decoded);

            if (!fanoOk)
            {
                if (!allowOsd) return;

                // OSD fallback
                if (!WsprConv.OsdDecode(symbols, depth: 1, out decoded)) return;

                // AP check: only accept OSD results for known callsigns
                string? osdMsg = WsprPack.Decode(decoded.AsSpan());
                if (osdMsg == null) return;
                string osdCall = ExtractCallsign(osdMsg);
                if (!_knownCalls.ContainsKey(osdCall)) return;
            }

            string? message = WsprPack.Decode(decoded.AsSpan());
            if (message == null || !seen.TryAdd(message, 0)) return;

            byte[] chanSym = WsprConv.GetChannelSymbols(decoded);
            double dt      = (shift1 - NomStart) / SrDown;

            bag.Add(new DecodedSignal(
                new DecodeResult
                {
                    UtcTime     = utcTime,
                    FrequencyHz = centerFreq + f1,
                    Dt          = dt,
                    Snr         = Math.Round(cand.Snr, 1),
                    Message     = message,
                    Mode        = DigitalMode.Wspr,
                },
                f1, shift1, drift1, chanSym));
        });

        return [.. bag];
    }

    // ── AP hash helpers ───────────────────────────────────────────────────────

    private void UpdateCallHash(string message)
    {
        string call = ExtractCallsign(message);
        if (call.Length > 0)
            _knownCalls[call] = true;
    }

    /// <summary>
    /// Extracts the callsign (first whitespace-delimited token) from a decoded
    /// WSPR message string. Returns empty string on failure.
    /// </summary>
    private static string ExtractCallsign(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        int sp = message.IndexOf(' ');
        return sp > 0 ? message[..sp] : message;
    }

    // ── Signal subtraction (subtract_signal2 from wsprd.c) ───────────────────

    /// <summary>
    /// Coherently subtracts a decoded WSPR signal from the complex baseband.
    /// Builds a reference waveform at the decoded frequency/drift/symbols,
    /// estimates the complex amplitude envelope with a 360-tap sin-window LPF,
    /// and removes it.  Mirrors subtract_signal2() in wsprd.c (K9AN, GPL).
    /// </summary>
    private static void SubtractSignal(
        float[] idat, float[] qdat,
        float f0, int shift0, float drift0, byte[] chanSym)
    {
        const int NFilt  = 360;
        const int NSig   = NSym * NSps;      // = 41 472 samples
        const int NBuf   = 45_000;           // buffer size ≥ NSig + NFilt
        const float Dflt = (float)(SrDown / NSps); // tone spacing ≈ 1.4648 Hz
        const float TwoPiDtF = (float)TwoPiDt;

        var refi = new float[NBuf];
        var refq = new float[NBuf];
        var ci   = new float[NBuf];
        var cq   = new float[NBuf];
        var cfi  = new float[NBuf];
        var cfq  = new float[NBuf];

        // Build reference complex signal
        float phi = 0;
        for (int i = 0; i < NSym; i++)
        {
            float cs  = chanSym[i];
            float fp  = f0
                      + (drift0 / 2.0f) * (i - NSym / 2.0f) / (NSym / 2.0f)
                      + (cs - 1.5f) * Dflt;
            float dphi = TwoPiDtF * fp;

            for (int j = 0; j < NSps; j++)
            {
                int ii   = NSps * i + j;
                refi[ii] = MathF.Cos(phi);
                refq[ii] = MathF.Sin(phi);
                phi += dphi;
            }
        }

        // Build sin-window LPF (360 taps, normalised to unit sum)
        Span<float> w = stackalloc float[NFilt];
        Span<float> partialsum = stackalloc float[NFilt];
        float norm = 0;
        for (int i = 0; i < NFilt; i++) { w[i] = MathF.Sin(MathF.PI * i / (NFilt - 1)); norm += w[i]; }
        for (int i = 0; i < NFilt; i++) w[i] /= norm;
        for (int i = 1; i < NFilt; i++) partialsum[i] = partialsum[i - 1] + w[i];

        // s(t) * conj(r(t)) → ci / cq (offset by NFilt to leave zero pad)
        for (int i = 0; i < NSig; i++)
        {
            int k = shift0 + i;
            if (k > 0 && k < idat.Length)
            {
                ci[i + NFilt] = idat[k] * refi[i] + qdat[k] * refq[i];
                cq[i + NFilt] = qdat[k] * refi[i] - idat[k] * refq[i];
            }
        }

        // LPF: convolve ci/cq with the sin window
        int lpfEnd = Math.Min(NBuf - NFilt / 2, NSig + NFilt - NFilt / 2 + 1);
        for (int i = NFilt / 2; i < lpfEnd; i++)
        {
            float si = 0, sq2 = 0;
            int   off = i - NFilt / 2;
            for (int j = 0; j < NFilt; j++) { si += w[j] * ci[off + j]; sq2 += w[j] * cq[off + j]; }
            cfi[i] = si;
            cfq[i] = sq2;
        }

        // Subtract c(t)*r(t) from signal, with edge normalisation
        for (int i = 0; i < NSig; i++)
        {
            float edgeNorm;
            if      (i < NFilt / 2)          edgeNorm = partialsum[NFilt / 2 + i];
            else if (i > NSig - 1 - NFilt / 2) edgeNorm = partialsum[NFilt / 2 + NSig - 1 - i];
            else                               edgeNorm = 1.0f;

            int k = shift0 + i;
            if (k > 0 && k < idat.Length && edgeNorm > 1e-10f)
            {
                int j = i + NFilt;
                idat[k] -= (cfi[j] * refi[i] - cfq[j] * refq[i]) / edgeNorm;
                qdat[k] -= (cfi[j] * refq[i] + cfq[j] * refi[i]) / edgeNorm;
            }
        }
    }

    // ── Downmix real 12 kHz PCM to complex baseband at 375 Hz ────────────────

    private static (float[] idat, float[] qdat) MixAndDecimate(
        ReadOnlySpan<float> samples, double fc)
    {
        int n    = samples.Length / DecRate;
        var idat = new float[n];
        var qdat = new float[n];

        // Standard complex downconversion: z(t) = s(t) * e^{-j*2π*fc*t}
        //   I =  s * cos(2π*fc*t),  Q = -s * sin(2π*fc*t)
        double dphase = Math.Tau * fc / 12000.0;  // positive → sinP = +sin(2π*fc*t)
        double cosR = Math.Cos(dphase), sinR = Math.Sin(dphase);
        double cosP = 1.0, sinP = 0.0;   // running LO phasor

        for (int k = 0; k < n; k++)
        {
            double re = 0, im = 0;
            int baseIdx = k * DecRate;
            for (int j = 0; j < DecRate; j++)
            {
                double s = samples[baseIdx + j];
                re += s * cosP;
                im -= s * sinP;
                double nc = cosP * cosR - sinP * sinR;
                sinP = cosP * sinR + sinP * cosR;
                cosP = nc;
            }
            idat[k] = (float)(re / DecRate);
            qdat[k] = (float)(im / DecRate);

            // Renormalize LO phasor every 256 output samples to prevent magnitude drift.
            if ((k & 255) == 255)
            {
                double mag = Math.Sqrt(cosP * cosP + sinP * sinP);
                cosP /= mag; sinP /= mag;
            }
        }
        return (idat, qdat);
    }

    // ── Build FFT waterfall ───────────────────────────────────────────────────

    private static float[,] BuildWaterfall(float[] idat, float[] qdat, int nffts)
    {
        var ps  = new float[NfftW, nffts];
        var buf = new Complex[NfftW];

        for (int t = 0; t < nffts; t++)
        {
            for (int j = 0; j < NfftW; j++)
            {
                int k  = t * NfftStep + j;
                float w  = Win[j];
                double re = k < idat.Length ? idat[k] * w : 0.0;
                double im = k < qdat.Length ? qdat[k] * w : 0.0;
                buf[j] = new Complex(re, im);
            }
            Fourier.Forward(buf, FourierOptions.AsymmetricScaling);

            // fftshift: map ps[j] ← |buf[(j+256)%512]|²
            for (int j = 0; j < NfftW; j++)
            {
                var c = buf[(j + NfftW / 2) % NfftW];
                ps[j, t] = (float)(c.Real * c.Real + c.Imaginary * c.Imaginary);
            }
        }
        return ps;
    }

    // ── Noise-floor estimate (30th percentile of smoothed spectrum) ───────────

    private static float[] ComputePsAvg(float[,] ps, int nffts)
    {
        var psavg = new float[NfftW];
        for (int t = 0; t < nffts; t++)
            for (int j = 0; j < NfftW; j++)
                psavg[j] += ps[j, t];
        return psavg;
    }

    private static float EstimateNoise(float[] psavg)
    {
        const int SpecWidth = 411;
        const int StartBin  = 256 - 205; // = 51

        float[] smspec = new float[SpecWidth];
        for (int i = 0; i < SpecWidth; i++)
        {
            float s = 0;
            for (int dj = -3; dj <= 3; dj++)
                s += psavg[StartBin + i + dj];
            smspec[i] = s;
        }

        float[] sorted = (float[])smspec.Clone();
        Array.Sort(sorted);
        float nl = sorted[SpecWidth * 30 / 100];
        return nl > 0 ? nl : 1e-30f;
    }

    // ── Candidate frequency search (spectral peaks) ───────────────────────────

    private readonly record struct CandInfo(float Freq, double Snr);

    private static List<CandInfo> FindCandidates(float[,] ps, float[] psavg, float noiseLevel)
    {
        const int SpecWidth = 411;
        const int StartBin  = 256 - 205;

        float[] smspec = new float[SpecWidth];
        for (int i = 0; i < SpecWidth; i++)
        {
            float s = 0;
            for (int dj = -3; dj <= 3; dj++)
                s += psavg[StartBin + i + dj];
            smspec[i] = s / noiseLevel - 1.0f;
            if (smspec[i] < 1e-8f) smspec[i] = 0.1f * 1e-8f;
        }

        var cands = new List<CandInfo>(200);
        for (int j = 1; j < SpecWidth - 1 && cands.Count < 200; j++)
        {
            if (smspec[j] > smspec[j - 1] && smspec[j] > smspec[j + 1])
            {
                float freq = (float)((j - 205) * BinWidth);  // physical Hz from DC
                double snr = 10.0 * Math.Log10(smspec[j]) - SnrScale;
                cands.Add(new CandInfo(freq, snr));
            }
        }

        cands.Sort((a, b) => b.Snr.CompareTo(a.Snr));
        return cands;
    }

    // ── Waterfall-based coarse timing + frequency + drift estimate ────────────

    private static (float Freq, int Shift, float Drift, float Sync) CoarseSync(
        float[,] ps, float candFreq, int nffts)
    {
        // Bin corresponding to the candidate centre frequency
        int if0 = (int)(candFreq / BinWidth) + NfftW / 2;

        float sMax    = -1e30f;
        float bestFreq = candFreq;
        int   bestShift = NomStart;
        float bestDrift = 0f;

        for (int ifr = if0 - 2; ifr <= if0 + 2; ifr++)
        {
            // k0 in [-10, 21]: 32 time offsets (each step = 128 samples = 1/2 symbol)
            for (int k0 = -10; k0 < 22; k0++)
            {
                for (int idrift = -MaxDrift; idrift <= MaxDrift; idrift++)
                {
                    float ss = 0, pow = 0;
                    for (int k = 0; k < NSym; k++)
                    {
                        // Drift-corrected bin: wsprd.c ifd = ifr + (k-81)/81 * idrift / (2*df)
                        // where df = BinWidth (375/512 Hz) — not tone spacing.
                        int ifd = ifr + (int)((k - 81.0) / 81.0 * idrift / (2.0 * BinWidth));
                        int kindex = k0 + 2 * k;
                        if (kindex < 0 || kindex >= nffts) continue;

                        float p0 = GetPs(ps, ifd - 3, kindex, nffts);
                        float p1 = GetPs(ps, ifd - 1, kindex, nffts);
                        float p2 = GetPs(ps, ifd + 1, kindex, nffts);
                        float p3 = GetPs(ps, ifd + 3, kindex, nffts);
                        p0 = MathF.Sqrt(p0); p1 = MathF.Sqrt(p1);
                        p2 = MathF.Sqrt(p2); p3 = MathF.Sqrt(p3);

                        pow += p0 + p1 + p2 + p3;
                        float cmet = (p1 + p3) - (p0 + p2);
                        ss += WsprConv.SyncVector[k] == 1 ? cmet : -cmet;
                    }

                    if (pow > 0) ss /= pow;
                    if (ss > sMax)
                    {
                        sMax      = ss;
                        bestShift = 128 * (k0 + 1);
                        bestDrift = idrift;
                        bestFreq  = (float)((ifr - NfftW / 2) * BinWidth);
                    }
                }
            }
        }

        return (bestFreq, bestShift, bestDrift, sMax);
    }

    private static float GetPs(float[,] ps, int j, int t, int nffts)
    {
        if (j < 0 || j >= NfftW || t < 0 || t >= nffts) return 0f;
        return ps[j, t];
    }

    // ── sync_and_demodulate (port of wsprd.c, all 3 modes) ───────────────────
    //
    //  mode 0 — no freq / drift search; find best time lag.
    //  mode 1 — no lag / drift search; find best frequency.
    //  mode 2 — fixed lag & freq; extract soft-decision symbols.

    private static void SyncAndDemodulate(
        float[] id, float[] qd,
        byte[]   symbols,
        ref float f1, int ifMin, int ifMax, float fStep,
        ref int   shift1, int lagMin, int lagMax, int lagStep,
        ref float drift1, int symfac,
        ref float sync, int mode)
    {
        float df15 = (float)(Df * 1.5);
        float df05 = (float)(Df * 0.5);

        float syncMax  = -1e30f;
        float f0       = f1;
        float fBest    = f1;
        int   bestShift = shift1;

        float[] fsymb = mode == 2 ? new float[NSym] : [];
        float   fSum  = 0, f2Sum = 0;

        if (mode == 0) { ifMin = 0; ifMax = 0; fStep = 0f; }
        else if (mode == 1) { lagMin = shift1; lagMax = shift1; lagStep = 1; }
        else /* mode 2 */ { lagMin = shift1; lagMax = shift1; lagStep = 1; ifMin = 0; ifMax = 0; fStep = 0f; }

        for (int ifreq = ifMin; ifreq <= ifMax; ifreq++)
        {
            f0 = f1 + ifreq * fStep;
            for (int lag = lagMin; lag <= lagMax; lag += lagStep)
            {
                float ss = 0, totp = 0;
                for (int i = 0; i < NSym; i++)
                {
                    float fp   = f0 + (drift1 / 2.0f) * (i - 81.0f) / 81.0f;
                    float dphi0 = (float)(TwoPiDt * (fp - df15));
                    float dphi1 = (float)(TwoPiDt * (fp - df05));
                    float dphi2 = (float)(TwoPiDt * (fp + df05));
                    float dphi3 = (float)(TwoPiDt * (fp + df15));

                    // Pre-compute cosine/sine step for each tone
                    float cd0 = MathF.Cos(dphi0), sd0 = MathF.Sin(dphi0);
                    float cd1 = MathF.Cos(dphi1), sd1 = MathF.Sin(dphi1);
                    float cd2 = MathF.Cos(dphi2), sd2 = MathF.Sin(dphi2);
                    float cd3 = MathF.Cos(dphi3), sd3 = MathF.Sin(dphi3);

                    // Running phasors start at phase 0 for each symbol (non-coherent)
                    float c0 = 1, s0 = 0, c1 = 1, s1 = 0;
                    float c2 = 1, s2 = 0, c3 = 1, s3 = 0;

                    float ir0 = 0, qi0 = 0, ir1 = 0, qi1 = 0;
                    float ir2 = 0, qi2 = 0, ir3 = 0, qi3 = 0;

                    for (int j = 0; j < NSps; j++)
                    {
                        int k = lag + i * NSps + j;
                        if ((uint)k < (uint)id.Length)
                        {
                            float idk = id[k], qdk = qd[k];
                            ir0 += idk * c0 + qdk * s0;  qi0 += qdk * c0 - idk * s0;
                            ir1 += idk * c1 + qdk * s1;  qi1 += qdk * c1 - idk * s1;
                            ir2 += idk * c2 + qdk * s2;  qi2 += qdk * c2 - idk * s2;
                            ir3 += idk * c3 + qdk * s3;  qi3 += qdk * c3 - idk * s3;
                        }
                        // Advance phasors
                        float nc = c0 * cd0 - s0 * sd0; s0 = c0 * sd0 + s0 * cd0; c0 = nc;
                              nc = c1 * cd1 - s1 * sd1; s1 = c1 * sd1 + s1 * cd1; c1 = nc;
                              nc = c2 * cd2 - s2 * sd2; s2 = c2 * sd2 + s2 * cd2; c2 = nc;
                              nc = c3 * cd3 - s3 * sd3; s3 = c3 * sd3 + s3 * cd3; c3 = nc;
                    }

                    float p0 = MathF.Sqrt(ir0 * ir0 + qi0 * qi0);
                    float p1 = MathF.Sqrt(ir1 * ir1 + qi1 * qi1);
                    float p2 = MathF.Sqrt(ir2 * ir2 + qi2 * qi2);
                    float p3 = MathF.Sqrt(ir3 * ir3 + qi3 * qi3);

                    totp += p0 + p1 + p2 + p3;
                    float cmet = (p1 + p3) - (p0 + p2);
                    ss += WsprConv.SyncVector[i] == 1 ? cmet : -cmet;

                    if (mode == 2)
                        fsymb[i] = WsprConv.SyncVector[i] == 1 ? p3 - p1 : p2 - p0;
                }

                if (totp > 0) ss /= totp;
                if (ss > syncMax)
                {
                    syncMax   = ss;
                    bestShift = lag;
                    fBest     = f0;
                }
            }
        }

        if (mode <= 1)
        {
            sync   = syncMax;
            shift1 = bestShift;
            f1     = fBest;
            return;
        }

        // Mode 2: normalize fsymb → symbols[0..161] (0=certain-0, 255=certain-1)
        sync = syncMax;
        for (int i = 0; i < NSym; i++) { fSum += fsymb[i]; f2Sum += fsymb[i] * fsymb[i]; }
        fSum  /= NSym;
        f2Sum /= NSym;
        float fac = MathF.Sqrt(f2Sum - fSum * fSum);
        if (fac < 1e-6f) fac = 1e-6f;
        for (int i = 0; i < NSym; i++)
        {
            float v = symfac * fsymb[i] / fac;
            symbols[i] = (byte)(Math.Clamp((int)(v + 128.5f), 0, 255));
        }
    }
}
