using System.Numerics;
using System.Runtime.CompilerServices;
using HamDigiSharp.Codecs;
using HamDigiSharp.Dsp;
using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Ft8;

/// <summary>
/// FT8 decoder — 15-second period, 8-FSK, LDPC(174,91).
///
/// Pipeline:
///   1. Build Hann-windowed waterfall (time_osr=2, freq_osr=2, nfft=3840).
///      Both magnitude (dB) and complex FFT bins are stored simultaneously.
///   2. Costas sync → candidate list via differential-dB metric.
///   3. For each candidate: coherent multi-symbol joint demodulation (nsym=1,2,3)
///      producing four LLR sets (llra, llrb, llrc, llrd) matching WSJT-X ft8b.f90.
///   4. LDPC(174,91) BP+OSD decode tried with each LLR set in order.
/// </summary>
public sealed class Ft8Decoder : BaseDecoder
{
    // ── Protocol constants ────────────────────────────────────────────────────
    private const int SampleRate  = 12000;
    private const int BlockSize   = 1920;   // samples per FT8 symbol at 12 kHz
    private const int TimeOsr     = 2;      // time oversampling ratio
    private const int FreqOsr     = 2;      // freq oversampling ratio (sub-bin resolution)
    private const int Nfft        = BlockSize * FreqOsr; // 3840 — sync waterfall FFT length
    private const int Subblock    = BlockSize / TimeOsr; // 960 — step per FFT row
    private const int NSymbols    = 79;
    private const int NDat        = 58;     // data symbols
    private const double SymPeriod = 0.160; // seconds per symbol
    private const double Baud      = 1.0 / SymPeriod; // 6.25 Hz tone spacing
    private const double SlotTime  = 15.0;  // seconds per slot
    private const int MaxBlocks    = (int)(SlotTime / SymPeriod); // 93
    private const float DefaultMinSyncDb = 2.5f;

    // ── Downsampling constants (WSJT-X ft8_downsample.f90) ───────────────────
    // Downsample from 12000 Hz to 200 Hz (NDown=60) for per-symbol coherent FFT.
    // NFFT1 = full-signal FFT length (192000 = NDown × NFFT2)
    // NFFT2 = baseband buffer length at 200 Hz (3200 samples ≈ 16 s)
    // NBase = baseband samples per FT8 symbol (200 Hz × 0.16 s = 32)
    private const int NDown = 60;
    private const int Nfft1 = 192000; // NDown × NFFT2
    private const int Nfft2 = 3200;   // 200 Hz × 16 s
    private const int NBase = 32;     // samples per symbol at 200 Hz

    // Gray map: natural binary index j → channel tone index
    private static readonly int[] GrayMap  = { 0, 1, 3, 2, 5, 6, 4, 7 };
    // Costas array: sync tones 0-based
    private static readonly int[] CostasSeq = { 3, 1, 4, 0, 6, 5, 2 };

    // Pre-computed Hann window for the sync waterfall FFT — constant, shared across all calls.
    private static readonly float[] WaterfallWindow = BuildWaterfallWindow();
    private static float[] BuildWaterfallWindow()
    {
        double fftNorm = 2.0 / Nfft;
        var win = new float[Nfft];
        for (int i = 0; i < Nfft; i++)
            win[i] = (float)(fftNorm * 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / Nfft)));
        return win;
    }

    // Data symbol positions in the 79-symbol frame (all non-pilot positions)
    private static readonly int[] DataPositions;

    static Ft8Decoder()
    {
        var pilots = new HashSet<int>();
        for (int i = 0; i < 7; i++) pilots.Add(i);
        for (int i = 36; i < 43; i++) pilots.Add(i);
        for (int i = 72; i < 79; i++) pilots.Add(i);
        DataPositions = Enumerable.Range(0, NSymbols).Where(s => !pilots.Contains(s)).ToArray();
    }

    public override DigitalMode Mode => DigitalMode.FT8;

    // ── Public decode entry point ─────────────────────────────────────────────

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        if (samples.Length < BlockSize * 4) return Array.Empty<DecodeResult>();

        // ─ 1. Build sync waterfall (magnitude dB) ────────────────────────────
        int minBin = (int)(freqLow  * SymPeriod);
        int maxBin = (int)(freqHigh * SymPeriod) + 1;
        if (maxBin - minBin < 8) return Array.Empty<DecodeResult>();

        int numBins     = maxBin - minBin;
        int blockStride = TimeOsr * FreqOsr * numBins;
        float[] wf = new float[MaxBlocks * blockStride];

        // Copy to float[] once — needed for parallel BuildWaterfall and subtraction.
        int cpLen       = Math.Min(samples.Length, Nfft1);
        float[] workSamples = new float[samples.Length];
        samples.CopyTo(workSamples);

        BuildWaterfall(workSamples, wf, minBin, numBins, blockStride);

        // ─ 2. Find sync candidates ────────────────────────────────────────────
        var candidates = FindCandidates(wf, numBins, blockStride);
        if (candidates.Count == 0) return Array.Empty<DecodeResult>();

        // ─ 3. Pre-allocate the 192 000-pt FFT buffer once — reused across all three
        //    signal-subtraction passes (the content is overwritten each pass).
        var fullFft = new Complex[Nfft1];

        // ─ 4. Three-pass decode with signal subtraction ───────────────────────
        var results = new List<DecodeResult>();
        var decoded  = new HashSet<string>();

        var passCandidates = candidates; // first pass uses original candidates

        for (int pass = 0; pass < 3; pass++)
        {
            // Refill the pre-allocated fullFft buffer from current (cleaned) audio.
            for (int i = 0; i < cpLen; i++) fullFft[i] = new Complex(workSamples[i], 0.0);
            if (pass > 0) Array.Clear(fullFft, cpLen, Nfft1 - cpLen);
            Fft.ForwardInPlace(fullFft);

            // Decode candidates in parallel (TryDecodeFt8 is stateless w.r.t. fullFft —
            // it only reads fullFft and Options, and MessagePacker.Unpack77 is read-only
            // on the hash table).  AsOrdered() preserves score-priority so that if two
            // candidates produce the same message, the higher-scoring one is kept.
            var parallelResults = passCandidates
                .AsParallel().AsOrdered()
                .Select(cand =>
                {
                    TryDecodeFt8(fullFft, cand, minBin, utcTime, out var r, out var info);
                    return (r, info);
                })
                .ToList();

            // Sequentially dedup, emit, and collect subtraction info so that event
            // handlers and MessagePacker.RegisterCallsign run on a single thread.
            var newInfos = new List<DecodeInfo>();
            foreach (var (r, info) in parallelResults)
            {
                if (r is null) continue;
                if (decoded.Add(r.Message))
                {
                    results.Add(r);
                    Emit(r);
                    newInfos.Add(info);
                }
            }

            if (newInfos.Count == 0) break;

            // Subtract newly decoded signals from the working audio.
            foreach (var info in newInfos)
                SubtractFromSamples(workSamples, info);

            // Rebuild waterfall from cleaned audio → find new (previously hidden) candidates.
            float[] wf2 = new float[MaxBlocks * blockStride];
            BuildWaterfall(workSamples, wf2, minBin, numBins, blockStride);
            passCandidates = FindCandidates(wf2, numBins, blockStride);
        }
        return results;
    }

    // ─ Decoded-signal info needed for time-domain subtraction ────────────────
    private readonly record struct DecodeInfo(bool[] Cw, double F1, int Ibest, Complex[,] Cs);

    /// <summary>
    /// Converts the 174-bit FT8 codeword to the 79-symbol frame
    /// (3 Costas pilots + 58 data symbols, each Gray-coded 0-7).
    /// </summary>
    private static int[] GetFt8Symbols(bool[] cw)
    {
        var syms = new int[NSymbols];
        // Three Costas pilot arrays at symbols 0-6, 36-42, 72-78.
        for (int i = 0; i < 7; i++)
        {
            syms[i]      = CostasSeq[i];
            syms[36 + i] = CostasSeq[i];
            syms[72 + i] = CostasSeq[i];
        }
        // 58 data symbols: packed in two groups of 29 (ihalf=0 → sym 7..35, ihalf=1 → 43..71).
        // Bit order: i32 = (k-1)*3 + ihalf*87; ib=0 → MSB (bit 2), ib=2 → LSB (bit 0).
        for (int ihalf = 0; ihalf < 2; ihalf++)
        {
            int baseSymbol = ihalf == 0 ? 7 : 43;
            for (int k = 1; k <= 29; k++)
            {
                int i32 = (k - 1) * 3 + ihalf * 87;
                int natural = (cw[i32] ? 4 : 0) | (cw[i32 + 1] ? 2 : 0) | (cw[i32 + 2] ? 1 : 0);
                syms[baseSymbol + (k - 1)] = GrayMap[natural];
            }
        }
        return syms;
    }

    /// <summary>
    /// Synthesizes the decoded FT8 signal and subtracts it from
    /// <paramref name="samples"/> in the time domain.
    /// Amplitude and phase are estimated from <see cref="DecodeInfo.Cs"/>.
    ///
    /// Uses complex phasor rotation (multiply by <c>e^{jΔφ}</c> each sample) instead
    /// of calling <c>Math.Cos/Sin</c> per sample.  For a 79-symbol frame this reduces
    /// trigonometric evaluations from <c>2 × 79 × 1920 = 303 360</c> to <c>2 × 79 × 2 = 316</c>
    /// (one pair per symbol for the initial phase, one for the per-sample phase step).
    /// </summary>
    private static void SubtractFromSamples(float[] samples, in DecodeInfo info)
    {
        int[] syms = GetFt8Symbols(info.Cw);
        double sqrtNDown = Math.Sqrt(NDown);
        const double Tau = 2.0 * Math.PI;

        for (int sym = 0; sym < NSymbols; sym++)
        {
            int tone = syms[sym];
            int n0   = info.Ibest + sym * NBase;

            // Recover complex amplitude from the baseband spectrogram.
            double pcAngle = -Tau * tone * n0 / NBase;
            double pcRe = Math.Cos(pcAngle), pcIm = Math.Sin(pcAngle);
            Complex cs  = info.Cs[sym, tone];
            double aRe  = (cs.Real * pcRe - cs.Imaginary * pcIm) * 2.0 / (NBase * sqrtNDown);
            double aIm  = (cs.Real * pcIm + cs.Imaginary * pcRe) * 2.0 / (NBase * sqrtNDown);

            double freq  = info.F1 + tone * Baud;
            int    tStart = info.Ibest * NDown + sym * BlockSize;

            // Initialise phasor at sample tStart, then rotate by dPhi each step.
            // e^{j·2π·freq·t/Sr} evaluated via complex multiply avoids per-sample trig.
            double phi0     = Tau * freq * tStart / SampleRate;
            double dPhi     = Tau * freq / SampleRate;
            double cosP     = Math.Cos(phi0),   sinP     = Math.Sin(phi0);
            double cosDelta = Math.Cos(dPhi),   sinDelta = Math.Sin(dPhi);

            for (int m = 0; m < BlockSize; m++)
            {
                int t = tStart + m;
                if ((uint)t < (uint)samples.Length)
                    samples[t] -= (float)(aRe * cosP - aIm * sinP);

                // Advance phasor: e^{j(φ+Δφ)} = e^{jφ} · e^{jΔφ}
                double nc = cosP * cosDelta - sinP * sinDelta;
                sinP = cosP * sinDelta + sinP * cosDelta;
                cosP = nc;
            }
        }
    }

    // ── Waterfall construction ────────────────────────────────────────────────

    /// <summary>
    /// Builds the Hann-windowed STFT waterfall in parallel over all blocks.
    ///
    /// Original implementation used a sequential sliding window (<c>lastFrame</c>)
    /// that prevented parallelisation.  This version computes each block's frame
    /// directly from the source array — the frame for (block, tsub) always starts
    /// at <c>srcStart − (Nfft − Subblock)</c>, so the computation is fully independent
    /// per block and safe to run on multiple threads simultaneously.
    /// The <c>Complex[Nfft]</c> work buffer is thread-local (Parallel.For localInit),
    /// eliminating per-block allocation while avoiding false sharing.
    /// </summary>
    private static void BuildWaterfall(
        float[] samples, float[] wf,
        int minBin, int numBins, int blockStride)
    {
        int frameOverlap = Nfft - Subblock;     // = 2880 samples of history per frame

        Parallel.For(0, MaxBlocks,
            () => new Complex[Nfft],            // thread-local FFT work buffer
            (block, _, cbuf) =>
            {
                int blockStart = block * BlockSize;
                int wfBase     = block * blockStride;

                for (int tsub = 0; tsub < TimeOsr; tsub++)
                {
                    int srcStart  = blockStart + tsub * Subblock;
                    int frameStart = srcStart - frameOverlap;  // may be negative

                    // Fill frame: left-pad with zeros if frameStart < 0
                    for (int i = 0; i < Nfft; i++)
                    {
                        int si = frameStart + i;
                        double v = (uint)si < (uint)samples.Length ? samples[si] : 0.0;
                        cbuf[i] = new Complex(v * WaterfallWindow[i], 0.0);
                    }
                    Fft.ForwardInPlace(cbuf);

                    for (int fsub = 0; fsub < FreqOsr; fsub++)
                    {
                        int wfRow = wfBase + tsub * FreqOsr * numBins + fsub * numBins;
                        for (int bin = 0; bin < numBins; bin++)
                        {
                            int srcBin = (minBin + bin) * FreqOsr + fsub;
                            double re  = cbuf[srcBin].Real;
                            double im  = cbuf[srcBin].Imaginary;
                            wf[wfRow + bin] = (float)(10.0 * Math.Log10(1e-12 + re * re + im * im));
                        }
                    }
                }
                return cbuf;
            },
            _ => { });
    }

    // ── Candidate search ──────────────────────────────────────────────────────

    private struct Candidate
    {
        public float Score;
        public short TimeOffset, FreqOffset;
        public byte  TimeSub, FreqSub;
    }

    private List<Candidate> FindCandidates(float[] wf, int numBins, int blockStride)
    {
        int maxCand = Options.MaxCandidates > 0 ? Options.MaxCandidates : 200;
        float minSync = Options.MinSyncDb < 0 ? float.NegativeInfinity
                      : Options.MinSyncDb > 0 ? Options.MinSyncDb
                      : DefaultMinSyncDb;

        // Parallelize over toff (−10 .. MaxBlocks−NSymbols+9), collect per-thread lists,
        // merge, sort and trim.  SyncScore is a pure read-only function so no locking needed.
        int toffStart = -10;
        int toffCount = MaxBlocks - NSymbols + 20;  // = 93 − 79 + 20 = 34 values

        var allCandidates = Enumerable.Range(toffStart, toffCount)
            .AsParallel()
            .SelectMany(toff =>
            {
                var local = new List<Candidate>();
                for (int tsub = 0; tsub < TimeOsr; tsub++)
                for (int fsub = 0; fsub < FreqOsr; fsub++)
                for (int foff = 0; foff <= numBins - 8; foff++)
                {
                    float score = SyncScore(wf, toff, tsub, foff, fsub, numBins, blockStride);
                    if (score >= minSync)
                        local.Add(new Candidate { Score = score, TimeOffset = (short)toff,
                                                   FreqOffset = (short)foff,
                                                   TimeSub = (byte)tsub, FreqSub = (byte)fsub });
                }
                return local;
            })
            .ToList();

        allCandidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        return allCandidates.Count > maxCand ? allCandidates.GetRange(0, maxCand) : allCandidates;
    }



    private static float SyncScore(float[] wf, int toff, int tsub, int foff, int fsub,
                                   int numBins, int blockStride)
    {
        // Base pointer into wf for this candidate's (tsub, fsub, foff)
        // Layout: wf[block][tsub][fsub][bin] flattened
        // Index for block b: b*blockStride + tsub*(FreqOsr*numBins) + fsub*numBins + foff
        int baseIdx = toff * blockStride + tsub * FreqOsr * numBins + fsub * numBins + foff;

        float score = 0;
        int  count  = 0;
        int  cosOff = 0;

        for (int m = 0; m < 3; m++, cosOff += 36) // three Costas blocks at symbols 0, 36, 72
        {
            for (int k = 0; k < 7; k++)
            {
                int block = toff + cosOff + k;
                if (block < 0 || block >= MaxBlocks) continue;

                int sm = CostasSeq[k]; // expected tone (0..7)
                int p  = baseIdx + (cosOff + k) * blockStride; // pointer to symbol

                // Check frequency neighbors
                if (sm > 0)
                {
                    score += WfGet(wf, p + sm) - WfGet(wf, p + sm - 1);
                    count++;
                }
                if (sm < 7)
                {
                    score += WfGet(wf, p + sm) - WfGet(wf, p + sm + 1);
                    count++;
                }

                // Check time neighbors
                if (k > 0 && (block - 1) >= 0)
                {
                    score += WfGet(wf, p + sm) - WfGet(wf, p + sm - blockStride);
                    count++;
                }
                if ((k + 1) < 7 && (block + 1) < MaxBlocks)
                {
                    score += WfGet(wf, p + sm) - WfGet(wf, p + sm + blockStride);
                    count++;
                }
            }
        }

        return count > 0 ? score / count : 0;
    }

    private static float WfGet(float[] wf, int idx)
        => (idx >= 0 && idx < wf.Length) ? wf[idx] : -120f;

    // ── Per-candidate coherent multi-symbol decode ────────────────────────────

    private bool TryDecodeFt8(
        Complex[] fullFft, in Candidate cand,
        int minBin, string utcTime, out DecodeResult? result, out DecodeInfo info)
    {
        result = null; info = default;

        // ─ Per-candidate baseband downsampling (WSJT-X ft8_downsample.f90) ───
        double f1 = (minBin + cand.FreqOffset + (double)cand.FreqSub / FreqOsr) * Baud;
        Complex[] cd0 = Ft8Downsample(fullFft, f1);

        // Coarse timing from sync waterfall, then refine using Costas pilot power.
        int ibest0 = (cand.TimeOffset * TimeOsr + cand.TimeSub) * (Subblock / NDown);
        int ibest  = OptimizeIbest(cd0, ibest0);

        // Reject implausible timing. ibest/200 converts 200 Hz sample index to seconds.
        // Valid FT8 signals fit within [-2.5, 3.5] s of the recording window.
        // The ±0.5 s margin over the nominal FT8 DT range accounts for any residual
        // Hann-window bias that OptimizeIbest may not fully correct.
        double dt = ibest / 200.0;
        if (dt < -2.5 || dt > 3.5) return false;

        double freq = f1;
        var apMask  = EmptyApMask();
        var msg77   = new bool[77];
        var cw      = new bool[174];

        // ─ Try frequency sub-passes: nominal f1 and +half-bin offset ─────────
        for (int ifreqPass = 0; ifreqPass < 2; ifreqPass++)
        {
            Complex[] cd = cd0;
            if (ifreqPass == 1)
            {
                // Apply +half-bin shift (= +3.125 Hz = half of 6.25 Hz tone spacing)
                const double halfBin = Baud / 2.0;
                cd = new Complex[Nfft2];
                for (int t = 0; t < Nfft2; t++)
                {
                    double phi = 2.0 * Math.PI * halfBin * t / 200.0;
                    double cr = Math.Cos(phi), ci = Math.Sin(phi);
                    cd[t] = new Complex(
                        cd0[t].Real * cr - cd0[t].Imaginary * ci,
                        cd0[t].Real * ci + cd0[t].Imaginary * cr);
                }
            }

            // ─ Extract per-symbol spectra ─────────────────────────────────────
            var cs     = new Complex[NSymbols, 8];
            var cbuf32 = new Complex[NBase];
            for (int sym = 0; sym < NSymbols; sym++)
            {
                int start = ibest + sym * NBase;
                for (int i = 0; i < NBase; i++)
                {
                    int idx = start + i;
                    cbuf32[i] = (uint)idx < (uint)cd.Length ? cd[idx] : Complex.Zero;
                }
                Fft.ForwardInPlace(cbuf32);
                for (int tone = 0; tone < 8; tone++)
                    cs[sym, tone] = cbuf32[tone];
            }

            var bmeta = new double[174]; var bmetb = new double[174];
            var bmetc = new double[174]; var bmetd = new double[174];
            ComputeMultiSymbolBmet(cs, bmeta, bmetb, bmetc, bmetd);

            NormalizeBmet(bmeta); NormalizeBmet(bmetb);
            NormalizeBmet(bmetc); NormalizeBmet(bmetd);

            // Ensemble LLR: coherent sum of all three multi-symbol variants (each already
            // normalised to unit-σ). The combined array captures signal contributions from
            // all three NSym estimates; for signals near the sensitivity threshold it can
            // decode when no individual variant succeeds alone.
            var bmetE = new double[174];
            for (int i = 0; i < 174; i++)
                bmetE[i] = bmeta[i] + bmetb[i] + bmetc[i];
            NormalizeBmet(bmetE);

            const double ScaleFac = 3.2;
            for (int i = 0; i < 174; i++)
            {
                bmeta[i] *= ScaleFac; bmetb[i] *= ScaleFac;
                bmetc[i] *= ScaleFac; bmetd[i] *= ScaleFac;
                bmetE[i] *= ScaleFac;
            }

            // Try ensemble first (highest combined information), then individual variants.
            foreach (double[] llr in (double[][])[bmetE, bmeta, bmetb, bmetc, bmetd])
            {
                bool ok = Ldpc174_91.TryDecode(llr, apMask, Options.DecoderDepth,
                                                msg77, cw, out int hardErrors, out double dmin);
                // Reject OSD false positives (phantoms).  Mirrors WSJT-X ft8b.f90 line 422:
                // "if(nharderrors.lt.0 .or. nharderrors.gt.36) cycle".
                // Our confirmed FT8_Single phantoms had errs=38 and errs=40; threshold=37
                // rejects both while keeping the highest-errs legitimate signals (~36–37).
                // NOTE: dmin-based filtering was evaluated but discarded — dmin values are not
                // portable across recordings because they depend on per-recording LLR scaling.
                if (ok && hardErrors <= 37)
                {
                    string message = MessagePacker.Unpack77(msg77, out bool unpkOk);
                    if (unpkOk && !string.IsNullOrWhiteSpace(message))
                    {
                        // cs[0] uses the nominal cd0 (not shifted) for subtraction accuracy.
                        var cs0 = (ifreqPass == 0) ? cs : ExtractCs(cd0, ibest);
                        info = new DecodeInfo((bool[])cw.Clone(), f1, ibest, cs0);
                        result = new DecodeResult
                        {
                            UtcTime     = utcTime,
                            Snr         = ComputeSnrDb(cs),
                            Dt          = dt,
                            FrequencyHz = freq,
                            Message     = message.Trim(),
                            Mode        = DigitalMode.FT8,
                            HardErrors  = hardErrors,
                            Dmin        = dmin,
                        };
                        return true;
                    }
                }
                Array.Clear(msg77, 0, msg77.Length);
                Array.Clear(cw,    0, cw.Length);
            }
        }
        return false;
    }

    /// <summary>
    /// Estimates signal SNR in dB relative to a 2500 Hz noise reference bandwidth,
    /// using the 21 Costas pilot symbols whose tones are known a priori.
    /// Matches the WSJT-X SNR scale (0 dB ≈ noise floor in 2500 Hz BW).
    /// </summary>
    private static double ComputeSnrDb(Complex[,] cs)
    {
        double sigSum = 0, noiseSum = 0;
        int count = 0;
        for (int m = 0; m < 3; m++)
        {
            int cosBase = m == 0 ? 0 : (m == 1 ? 36 : 72);
            for (int k = 0; k < 7; k++)
            {
                int sym     = cosBase + k;
                int expTone = CostasSeq[k];
                for (int t = 0; t < 8; t++)
                {
                    double pow = cs[sym, t].Real * cs[sym, t].Real
                               + cs[sym, t].Imaginary * cs[sym, t].Imaginary;
                    if (t == expTone) sigSum  += pow;
                    else              noiseSum += pow;
                }
                count++;
            }
        }
        // Signal power per pilot, noise power per tone per pilot, then normalise
        // to 2500 Hz BW (tone spacing = 6.25 Hz → 2500/6.25 = 400).
        double snrRaw = (sigSum / count) / (noiseSum / (count * 7) + 1e-20);
        return Math.Round(Math.Max(-30.0, 10.0 * Math.Log10(snrRaw / 400.0)));
    }

    // Extracts cs[79,8] from cd0 at the given ibest — used when the freq-shift pass succeeds.
    private static Complex[,] ExtractCs(Complex[] cd0, int ibest)
    {
        var cs     = new Complex[NSymbols, 8];
        var cbuf32 = new Complex[NBase];
        for (int sym = 0; sym < NSymbols; sym++)
        {
            int start = ibest + sym * NBase;
            for (int i = 0; i < NBase; i++)
            {
                int idx = start + i;
                cbuf32[i] = (uint)idx < (uint)cd0.Length ? cd0[idx] : Complex.Zero;
            }
            Fft.ForwardInPlace(cbuf32);
            for (int tone = 0; tone < 8; tone++)
                cs[sym, tone] = cbuf32[tone];
        }
        return cs;
    }

    /// <summary>
    /// Refines the timing offset by searching ±<see cref="TimingSearchRange"/> baseband
    /// samples for the ibest that maximises the Costas pilot-tone SNR.
    /// This mirrors WSJT-X's peak_up/timing refinement prior to the LLR computation.
    /// </summary>
    private const int TimingSearchCoarse = 256;  // ± range for coarse search (samples at 200 Hz)
    private const int TimingCoarseStep   = 16;   // step for coarse pass (= 1 symbol / 2)
    private const int TimingFineRange    = 8;    // ± range for fine pass around best coarse

    private static int OptimizeIbest(Complex[] cd0, int ibest0)
    {
        // Two-stage timing search using all three Costas pilot groups.
        // Coarse: large range with coarse step to recover systematic waterfall timing bias.
        // Fine: exhaustive search ±TimingFineRange around the coarse peak.
        double bestCoarse = double.MinValue;
        int bestIbest = ibest0;
        var cbuf = new Complex[NBase];

        for (int di = -TimingSearchCoarse; di <= TimingSearchCoarse; di += TimingCoarseStep)
        {
            int ib = ibest0 + di;
            double score = CostasScore(cd0, cbuf, ib, maxGroups: 3);
            if (score > bestCoarse) { bestCoarse = score; bestIbest = ib; }
        }

        double bestFine = double.MinValue;
        int fineIbest = bestIbest;
        for (int di = -TimingFineRange; di <= TimingFineRange; di++)
        {
            int ib = bestIbest + di;
            double score = CostasScore(cd0, cbuf, ib, maxGroups: 3);
            if (score > bestFine) { bestFine = score; fineIbest = ib; }
        }
        return fineIbest;
    }

    private static double CostasScore(Complex[] cd0, Complex[] cbuf, int ibest, int maxGroups = 3)
    {
        double score = 0;
        for (int m = 0; m < maxGroups; m++)
        {
            int cosBase = m == 0 ? 0 : (m == 1 ? 36 : 72);
            for (int k = 0; k < 7; k++)
            {
                int sym   = cosBase + k;
                int start = ibest + sym * NBase;
                for (int i = 0; i < NBase; i++)
                {
                    int idx = start + i;
                    cbuf[i] = (uint)idx < (uint)cd0.Length ? cd0[idx] : Complex.Zero;
                }
                Fft.ForwardInPlace(cbuf);

                int expTone = CostasSeq[k];
                double sigPow  = cbuf[expTone].Real * cbuf[expTone].Real
                               + cbuf[expTone].Imaginary * cbuf[expTone].Imaginary;
                double noisePow = 1e-20;
                for (int tone = 0; tone < 8; tone++)
                    if (tone != expTone)
                        noisePow += cbuf[tone].Real * cbuf[tone].Real
                                  + cbuf[tone].Imaginary * cbuf[tone].Imaginary;
                score += sigPow / (noisePow / 7 + 1e-20);
            }
        }
        return score;
    }

    // ── WSJT-X ft8_downsample: frequency-domain baseband downsampling ─────────

    /// <summary>
    /// Mixes the full-signal FFT to baseband at f1 Hz and produces a 200 Hz
    /// complex baseband buffer of NFFT2 samples, exactly matching WSJT-X ft8_downsample.f90.
    /// The extracted band includes FT8 tones 0–7 (0–43.75 Hz relative to f1) plus margins.
    /// </summary>
    private static Complex[] Ft8Downsample(Complex[] fullFft, double f1)
    {
        const double df = (double)SampleRate / Nfft1; // 0.0625 Hz per bin

        int i0 = (int)Math.Round(f1 / df);
        int ib = Math.Max(1, (int)Math.Round((f1 - 1.5 * Baud) / df));
        int it = Math.Min(Nfft1 / 2, (int)Math.Round((f1 + 8.5 * Baud) / df));

        var c1 = new Complex[Nfft2]; // initialized to zero

        // Copy bins ib..it into c1[0..k-1]
        int k = 0;
        for (int i = ib; i <= it && k < Nfft2; i++, k++)
            c1[k] = fullFft[i];

        // Apply cosine taper at the low-frequency edge (first 101 bins → 0→1)
        for (int j = 0; j <= 100 && j < k; j++)
        {
            double t = 0.5 * (1.0 + Math.Cos((100 - j) * Math.PI / 100.0));
            c1[j] *= t;
        }
        // Apply cosine taper at the high-frequency edge (last 101 bins → 1→0)
        for (int j = 0; j <= 100; j++)
        {
            int idx = k - 1 - 100 + j;
            if (idx >= 0 && idx < Nfft2)
            {
                double t = 0.5 * (1.0 + Math.Cos(j * Math.PI / 100.0));
                c1[idx] *= t;
            }
        }

        // Circular shift: cshift(c1, i0-ib) so that frequency f1 maps to DC (bin 0)
        // Fortran cshift(arr, shift>0) → result[j] = arr[(j+shift) mod N]
        int shift = i0 - ib;
        if (shift > 0)
        {
            var temp = new Complex[Nfft2];
            for (int j = 0; j < Nfft2; j++)
                temp[j] = c1[(j + shift) % Nfft2];
            Array.Copy(temp, c1, Nfft2);
        }

        // IFFT → complex baseband at 200 Hz (AsymmetricScaling applies 1/Nfft2)
        Fft.InverseInPlace(c1);
        // Scale (factor doesn't matter for NormalizeBmet, but keeps values comparable)
        double fac = Nfft2 / Math.Sqrt((double)Nfft1 * Nfft2); // = sqrt(Nfft2/Nfft1)
        for (int j = 0; j < Nfft2; j++) c1[j] *= fac;

        return c1;
    }

    /// <summary>
    /// Computes bmeta (nsym=1), bmetb (nsym=2), bmetc (nsym=3) and bmetd
    /// (nsym=1 normalised by max) exactly as in WSJT-X ft8b.f90 / MSHV decoderft8var.cpp.
    /// </summary>
    private static void ComputeMultiSymbolBmet(
        Complex[,] cs,
        double[] bmeta, double[] bmetb, double[] bmetc, double[] bmetd)
    {
        // Per-symbol inverse-RMS for equal-contribution coherent combining (nsym≥2).
        // Dividing each symbol's contribution by its own RMS ensures that symbols at
        // different amplitude levels (e.g. under QSB fading) contribute equally to the
        // coherent sum — true multi-symbol combining rather than amplitude-weighted.
        // Guard: 0.0 for near-silent symbols (zero-padded early-decode windows).
        Span<double> symInvRms = stackalloc double[NSymbols];
        for (int sym = 0; sym < NSymbols; sym++)
        {
            double sq = 0;
            for (int t = 0; t < 8; t++)
            {
                double r = cs[sym, t].Real, x = cs[sym, t].Imaginary;
                sq += r * r + x * x;
            }
            double rms = Math.Sqrt(sq * (1.0 / 8));
            symInvRms[sym] = rms > 1e-10 ? 1.0 / rms : 0.0;
        }

        for (int nsym = 1; nsym <= 3; nsym++)
        {
            int nt    = 1 << (3 * nsym); // 8 / 64 / 512
            int ibmax = 3 * nsym - 1;    // 2 / 5 / 8
            var s2 = new double[nt];

            for (int ihalf = 0; ihalf < 2; ihalf++)
            {
                // Symbol frame positions:
                //   ihalf=0 → data symbols 7..35 (first half)
                //   ihalf=1 → data symbols 43..71 (second half)
                int baseSymbol = ihalf == 0 ? 7 : 43;

                for (int k = 1; k <= 30 - nsym; k += nsym)
                {
                    int ks  = baseSymbol + (k - 1);
                    int ks1 = ks + 1;
                    int ks2 = ks + 2;

                    // Bit offset in the 174-element LLR array
                    int i32 = (k - 1) * 3 + ihalf * 87;

                    // ── Fill s2[]: magnitude of coherent sum across nsym symbols ──
                    for (int i = 0; i < nt; i++)
                    {
                        // Decompose i into per-symbol natural-binary tone indices
                        int ii1 = i >> 6;        // tone index for 1st symbol (nsym=3 only)
                        int ii2 = (i >> 3) & 7;  // tone index for 2nd symbol (nsym>=2), or 1st (nsym=2 joint)
                        int ii3 = i & 7;         // tone index for last symbol

                        double re, im;
                        if (nsym == 1)
                        {
                            // No per-symbol normalization for single-symbol: preserve
                            // relative amplitude info that benefits from global NormalizeBmet.
                            ref readonly Complex c = ref GetCs(cs, ks, GrayMap[ii3]);
                            re = c.Real; im = c.Imaginary;
                        }
                        else if (nsym == 2)
                        {
                            // Per-symbol normalization: equal contribution from both symbols.
                            ref readonly Complex c1 = ref GetCs(cs, ks,  GrayMap[ii2]);
                            ref readonly Complex c2 = ref GetCs(cs, ks1, GrayMap[ii3]);
                            double inv1 = symInvRms[ks], inv2 = symInvRms[ks1];
                            re = c1.Real * inv1 + c2.Real * inv2;
                            im = c1.Imaginary * inv1 + c2.Imaginary * inv2;
                        }
                        else // nsym == 3
                        {
                            ref readonly Complex c1 = ref GetCs(cs, ks,  GrayMap[ii1]);
                            ref readonly Complex c2 = ref GetCs(cs, ks1, GrayMap[ii2]);
                            ref readonly Complex c3 = ref GetCs(cs, ks2, GrayMap[ii3]);
                            double inv1 = symInvRms[ks], inv2 = symInvRms[ks1], inv3 = symInvRms[ks2];
                            re = c1.Real * inv1 + c2.Real * inv2 + c3.Real * inv3;
                            im = c1.Imaginary * inv1 + c2.Imaginary * inv2 + c3.Imaginary * inv3;
                        }
                        s2[i] = Math.Sqrt(re * re + im * im);
                    }

                    // ── Extract one LLR per output bit position ───────────────────
                    for (int ib = 0; ib <= ibmax; ib++)
                    {
                        int bitIdx = i32 + ib;
                        if (bitIdx > 173) continue;

                        // Bit `ibmax-ib` of the combined index selects 1 vs 0 class
                        int bitPos = ibmax - ib;
                        double max1 = 0, max0 = 0;
                        for (int i = 0; i < nt; i++)
                        {
                            double v = s2[i];
                            if (((i >> bitPos) & 1) != 0) { if (v > max1) max1 = v; }
                            else                           { if (v > max0) max0 = v; }
                        }
                        double bm = max1 - max0;

                        if (nsym == 1)
                        {
                            bmeta[bitIdx] = bm;
                            double den = max1 > max0 ? max1 : max0;
                            bmetd[bitIdx] = den > 0 ? bm / den : 0;
                        }
                        else if (nsym == 2) bmetb[bitIdx] = bm;
                        else               bmetc[bitIdx] = bm;
                    }
                }
            }
        }
    }

    // Returns a ref to cs[sym, tone], or a ref to a zero sentinel if out of bounds.
    private static readonly Complex _csZero = Complex.Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref readonly Complex GetCs(Complex[,] cs, int sym, int tone)
    {
        if ((uint)sym < NSymbols)
            return ref cs[sym, tone];
        return ref _csZero;
    }

    /// <summary>
    /// Normalises <paramref name="bmet"/> by its RMS (root-mean-square), matching
    /// WSJT-X's <c>normalizebmet</c> exactly. After normalisation σ ≈ 1 for zero-mean
    /// arrays; caller then scales by 3.2 to match the LDPC LLR calibration.
    /// Uses hardware-accelerated SIMD when available.
    /// </summary>
    private static void NormalizeBmet(double[] bmet)
    {
        int n = bmet.Length;
        double sum2 = 0.0;

        if (Vector.IsHardwareAccelerated)
        {
            int vw = Vector<double>.Count;
            int vLen = n - (n % vw);
            var vSum2 = Vector<double>.Zero;
            for (int i = 0; i < vLen; i += vw)
            {
                var v = new Vector<double>(bmet, i);
                vSum2 += v * v;
            }
            for (int lane = 0; lane < vw; lane++) sum2 += vSum2[lane];
            for (int i = vLen; i < n; i++) sum2 += bmet[i] * bmet[i];

            double sigma = sum2 > 0 ? Math.Sqrt(sum2 / n) : 1e-5;
            if (sigma < 1e-5) sigma = 1e-5;
            double inv = 1.0 / sigma;

            var vInv = new Vector<double>(inv);
            for (int i = 0; i < vLen; i += vw)
                (new Vector<double>(bmet, i) * vInv).CopyTo(bmet, i);
            for (int i = vLen; i < n; i++) bmet[i] *= inv;
        }
        else
        {
            for (int i = 0; i < n; i++) sum2 += bmet[i] * bmet[i];
            double sigma = sum2 > 0 ? Math.Sqrt(sum2 / n) : 1e-5;
            if (sigma < 1e-5) sigma = 1e-5;
            double inv = 1.0 / sigma;
            for (int i = 0; i < n; i++) bmet[i] *= inv;
        }
    }

    // Min-heap helpers (by Score, ascending — so heap[0] is the WORST/lowest score)
}
