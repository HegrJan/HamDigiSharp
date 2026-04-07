using System.Numerics;
using System.Text;
using HamDigiSharp.Codecs;
using HamDigiSharp.Decoders.Ft8;
using HamDigiSharp.Dsp;
using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.SuperFox;

/// <summary>
/// SuperFox decoder — MSHV multi-station Fox/Hound protocol.
/// C# port of MSHV's DecoderSFox (LZ2HV), GPL.
///
/// SuperFox has two signal directions:
///   • Hound → Fox: standard FT8 (8-FSK, LDPC 174/91, 79 symbols). Handled by the FT8 path.
///   • Fox  → Hound: QPC polar code frame (128-QAM, 151 symbols, 24 sync + 127 data).
///                   The Fox transmits 50 seven-bit symbols carrying up to 9
///                   callsign-report pairs verified by a 21-bit Jenkins CRC.
/// </summary>
public sealed class SuperFoxDecoder : BaseDecoder
{
    // ═══════════════════════════════════════════════════════════════════════════
    // FT8 physical-layer constants (for Hound detection)
    // ═══════════════════════════════════════════════════════════════════════════
    private const int SampleRate = 12000;
    private const int Nmax       = 180000; // 15 s × 12000 Hz
    private const int NspsF      = 1920;   // FT8 samples/symbol
    private const int NSymbolsF  = 79;
    private const int NBinsF     = 8;
    private static readonly int[] CostasSeqF = { 3, 1, 4, 0, 6, 5, 2 };
    private static readonly int[] PilotPositionsF;
    private static readonly int[] DataPositionsF;

    // ═══════════════════════════════════════════════════════════════════════════
    // QPC (Quick Polar Code) constants — Fox → Hound
    // ═══════════════════════════════════════════════════════════════════════════
    private const int QpcN    = 128;  // codeword symbols (unpunctured)
    private const int QpcNp   = 127;  // codeword symbols (punctured)
    private const int QpcK    = 50;   // information symbols
    private const int QpcQ    = 128;  // alphabet size (7-bit symbols)
    private const int NdsQ    = 151;  // total QPC frame symbols
    private const int NsSync  = 24;   // sync symbols in QPC frame
    private const int NspsQ   = 1024; // QPC samples/symbol at 12 kHz

    // Sync symbol positions (0-indexed) in the 151-symbol QPC frame.
    // C++ isync[] is 1-indexed: {1,2,4,7,11,16,22,29,37,39,42,43,45,48,52,57,63,70,78,80,83,84,86,89}
    private static readonly int[] IsyncF =
    {
        0,1,3,6,10,15,21,28,36,38,41,42,44,47,51,56,62,69,77,79,82,83,85,88
    };

    // QPC information-symbol position mapping (xpos[128])
    private static readonly int[] QpcXPos =
    {
        1,   2,   3,   4,   5,   6,   8,   9,  10,  12,  16,  32,  17,  18,  64,  20,
       33,  34,  24,   7,  11,  36,  13,  19,  14,  65,  40,  21,  66,  22,  35,  68,
       25,  48,  37,  26,  72,  15,  38,  28,  41,  67,  23,  80,  42,  69,  49,  96,
       44,  27,  70,  50,  73,  39,  29,  52,  74,  30,  56,  81,  76,  43,  82,  84,
       97,  45,  71,  88,  98,  46, 100,  51, 104,  53,  75, 112,  54,  57,  99, 119,
       92,  77,  58, 117,  59,  83, 106,  31,  85, 108, 115, 116, 122, 125, 124,  91,
       61,  90,  89, 111,  78,  93,  94, 126,  86, 107, 110, 118, 121,  62, 120,  87,
      105,  55, 114,  60, 127,  63, 103, 101, 123,  95, 102,  47, 109,  79, 113,   0
    };

    // QPC frozen-symbol flag: 1 = information position, 0 = frozen
    private static readonly byte[] QpcFSize =
    {
        0,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
        1,1,1,1,1,1,1,1,1,1,1,1,1,0,0,0,
        1,1,1,1,1,1,1,0,1,1,1,0,1,0,0,0,
        1,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        1,1,1,1,1,1,0,0,1,0,0,0,0,0,0,0,
        1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0
    };

    // QPC frozen-symbol values (all zero)
    private static readonly byte[] QpcF = new byte[QpcN]; // all zero

    // Dithering seed table (from decodersfox.cpp) used for weak-signal decoding
    private static readonly uint[] DitherSeeds =
    {
        60568,33762,33481,32742,30621,30412,23117,22521,20534,20457,
        20180,19959,18552,18174,17727,17450,16703,16661,16483,16120,
        16045,15684,15455,15326,15146,15093,14945,14624,14505,14434,
        14225,14214,13821,13554,13513,13052,12961,12857,12786,12633,
        12424,12045,12004,11986,11965,11807,11764,11568,11450,11149,
        10845,10322,10249,10037, 9985, 9661, 9644, 9270, 9248, 9095,
         8981, 7731, 7564, 7559, 6707, 6543, 6438, 6098, 6063, 5650,
         5433, 5397, 5330, 5246, 5229, 5091, 4968, 4852, 4455, 4143,
         3523, 3326, 3166, 3126, 3052, 2806, 2556, 2385, 2036, 2034,
         1544, 1542, 1475, 1355,  796,  666,  520,  464,  431,  424,
          398,  356,  345,  334,  324,  284,  193,  172,  111,   62,   57
    };
    private const int MaxSeed = 111; // = DitherSeeds.Length

    // Frequency/time dither tables (from decodersfox.cpp qpc_decode2)
    private static readonly int[] IdfDither =
    {
         0,  0, -1,  0, -1,  1,  0, -1,  1, -2,  0, -1,  1, -2,  2,
         0, -1,  1, -2,  2, -3,  0, -1,  1, -2,  2, -3,  3,  0, -1,
         1, -2,  2, -3,  3, -4,  0, -1,  1, -2,  2, -3,  3, -4,  4,
         0, -1,  1, -2,  2, -3,  3, -4,  4, -5, -1,  1, -2,  2, -3,
         3, -4,  4, -5,  1, -2,  2, -3,  3, -4,  4, -5, -2,  2, -3,
         3, -4,  4, -5,  2, -3,  3, -4,  4, -5, -3,  3, -4,  4, -5,
         3, -4,  4, -5, -4,  4, -5,  4, -5, -5
    };
    private static readonly int[] IdtDither =
    {
         0, -1,  0,  1, -1,  0, -2,  1, -1,  0,  2, -2,  1, -1,  0,
        -3,  2, -2,  1, -1,  0,  3, -3,  2, -2,  1, -1,  0, -4,  3,
        -3,  2, -2,  1, -1,  0,  4, -4,  3, -3,  2, -2,  1, -1,  0,
        -5,  4, -4,  3, -3,  2, -2,  1, -1,  0, -5,  4, -4,  3, -3,
         2, -2,  1, -1, -5,  4, -4,  3, -3,  2, -2,  1, -5,  4, -4,
         3, -3,  2, -2, -5,  4, -4,  3, -3,  2, -5,  4, -4,  3, -3,
        -5,  4, -4,  3, -5,  4, -4, -5,  4, -5
    };

    // ── QPC recursive decoder stack ───────────────────────────────────────────
    private const int QpcStackCapacity = QpcN * QpcQ * 2 + 256; // 32768 + margin
    private readonly float[] _qpcStack = new float[QpcStackCapacity];
    private int _qpcStackTop;

    // ── Random number state (LCG, matches C++ TYPE_00 rand) ──────────────────
    private uint _randState;

    // ── Message packer constants (duplicated from MessagePacker) ─────────────
    private const int NTokens28 = 2063592;
    private const int Max22_28  = 4194304;
    private const string A1_28  = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string A2_28  = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string A3_28  = "0123456789";
    private const string A4_28  = " ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string CTxt42 = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?";
    private const int NqU1rks   = 203514677; // sentinel value for empty callsign slot

    // ═══════════════════════════════════════════════════════════════════════════
    // Static initializer
    // ═══════════════════════════════════════════════════════════════════════════
    static SuperFoxDecoder()
    {
        var pilots = new List<int>();
        for (int i = 0; i < 7; i++) pilots.Add(i);
        for (int i = 36; i < 43; i++) pilots.Add(i);
        for (int i = 72; i < 79; i++) pilots.Add(i);
        PilotPositionsF = pilots.ToArray();
        DataPositionsF = Enumerable.Range(0, NSymbolsF).Except(pilots).ToArray();
    }

    public override DigitalMode Mode => DigitalMode.SuperFox;

    // Embedded FT8 decoder used for the Hound→Fox path.
    // It inherits all FT8 sensitivity improvements (ensemble LLR, multi-symbol combining,
    // per-symbol normalization, OSD) with no code duplication.
    private readonly Ft8Decoder _ft8SubDecoder = new();

    // ═══════════════════════════════════════════════════════════════════════════
    // Entry point
    // ═══════════════════════════════════════════════════════════════════════════

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        if (samples.Length < Nmax / 4) return Array.Empty<DecodeResult>();

        var results = new List<DecodeResult>();
        var decoded = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1 — Hound → Fox: delegate to the improved Ft8Decoder so all FT8
        // sensitivity improvements (ensemble LLR, multi-symbol combining, per-symbol
        // normalization, OSD) apply automatically; relabel results as SuperFox.
        DecodeFt8Pass(samples, freqLow, freqHigh, utcTime, results, decoded);

        // Pass 2 — Fox → Hound: QPC polar code frame (needs double[] dd)
        double[] dd = PrepareBuffer(samples);
        DecodeFoxPass(dd, freqLow, freqHigh, utcTime, results, decoded);

        return results;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FT8 path (Hound → Fox) — delegates to embedded Ft8Decoder
    // ═══════════════════════════════════════════════════════════════════════════

    private void DecodeFt8Pass(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime,
        List<DecodeResult> results, HashSet<string> decoded)
    {
        _ft8SubDecoder.Configure(Options);
        foreach (var r in _ft8SubDecoder.Decode(samples, freqLow, freqHigh, utcTime))
        {
            var relabeled = r with { Mode = DigitalMode.SuperFox };
            if (decoded.Add(relabeled.Message))
            {
                results.Add(relabeled);
                Emit(relabeled);
            }
        }
    }

    private static double[] PrepareBuffer(ReadOnlySpan<float> samples)
    {
        var dd = new double[Nmax];
        int n = Math.Min(samples.Length, Nmax);
        for (int i = 0; i < n; i++) dd[i] = samples[i];
        return dd;
    }

    private List<(double freq, double dt)> FindFt8Candidates(
        double[] dd, double freqLow, double freqHigh)
    {
        const int step = NspsF / 4; // 480
        const int nfft = 2 * NspsF; // 3840
        int nhsym = Nmax / step - 3;

        var window = Windowing.Nuttall(nfft);
        var spec   = new double[nhsym, nfft];
        var cbuf   = new Complex[nfft];

        for (int j = 0; j < nhsym; j++)
        {
            int start = j * step;
            for (int k = 0; k < nfft; k++)
            {
                int idx = start + k;
                double v = idx < Nmax ? dd[idx] : 0.0;
                cbuf[k] = new Complex(v * window[k], 0.0);
            }
            Fft.ForwardInPlace(cbuf);
            for (int k = 0; k < nfft; k++)
                spec[j, k] = cbuf[k].Real * cbuf[k].Real + cbuf[k].Imaginary * cbuf[k].Imaginary;
        }

        double df = (double)SampleRate / nfft;
        int fBinLo = Math.Clamp((int)(freqLow  / df), 0, nfft / 2 - 200);
        int fBinHi = Math.Clamp((int)(freqHigh / df), fBinLo + 1, nfft / 2 - 1);
        int symBins = Math.Max(1, (int)Math.Round((double)SampleRate / (32 * df)));

        var syncMap = new Dictionary<(int, int), double>();
        for (int fb = fBinLo; fb <= fBinHi - 7 * symBins; fb++)
            for (int dtStep = 0; dtStep < nhsym - 72 * 4 - 7; dtStep++)
            {
                double sync = ComputeCostasSyncF(spec, fb, dtStep, symBins, nhsym);
                var key = (fb, dtStep);
                if (!syncMap.TryGetValue(key, out double v) || sync > v) syncMap[key] = sync;
            }

        double pct90    = syncMap.Count > 0
            ? SignalMath.Pctile(syncMap.Values.ToArray(), syncMap.Count, 90.0)
            : 0.0;
        double threshold = Math.Max(pct90 * 0.3, 1e-8);

        return syncMap
            .Where(kv => kv.Value > threshold)
            .OrderByDescending(kv => kv.Value)
            .Take(200)
            .Select(kv => (kv.Key.Item1 * df, kv.Key.Item2 * step / (double)SampleRate))
            .ToList();
    }

    private static double ComputeCostasSyncF(
        double[,] spec, int fBin0, int dtStep, int symBins, int nhsym)
    {
        int[] csStart = { 0, 144, 288 };
        double sync = 0;
        foreach (int cs in csStart)
            for (int i = 0; i < 7; i++)
            {
                int tStep = dtStep + cs + i * 4;
                int fBin  = fBin0 + CostasSeqF[i] * symBins;
                if (tStep >= nhsym || fBin >= spec.GetLength(1)) break;
                sync += spec[tStep, fBin];
            }
        return sync;
    }

    private bool TryDecodeFt8(
        double[] dd, double f0, double dt, string utcTime, out DecodeResult? result)
    {
        result = null;
        var llr = ComputeFt8Llr(dd, f0, dt);
        if (llr is null) return false;

        var apMask = EmptyApMask();
        var msg77  = new bool[77];
        var cw     = new bool[174];

        bool ok = Ldpc174_91.TryDecode(llr, apMask, Options.DecoderDepth,
                                        msg77, cw, out int hardErrors, out double dmin);
        if (!ok && Options.ApDecode && !string.IsNullOrEmpty(Options.MyCall))
        {
            apMask = BuildApMask(Enumerable.Range(0, 28).ToArray());
            ok = Ldpc174_91.TryDecode(llr, apMask, Options.DecoderDepth,
                                       msg77, cw, out hardErrors, out dmin);
        }
        if (!ok) return false;

        string msg = MessagePacker.Unpack77(msg77, out bool unpkOk);
        if (!unpkOk || string.IsNullOrWhiteSpace(msg)) return false;

        result = new DecodeResult
        {
            UtcTime     = utcTime,
            Snr         = EstimateSnr(hardErrors, double.NaN),
            Dt          = dt,
            FrequencyHz = f0,
            Message     = msg.Trim(),
            Mode        = DigitalMode.SuperFox,
            HardErrors  = hardErrors,
            Dmin        = dmin,
            IsApDecode  = !AllFalse(apMask),
        };
        return true;
    }

    private static double[]? ComputeFt8Llr(double[] dd, double f0, double dt)
    {
        const int nfft = 2 * NspsF;
        int dtSamples = (int)(dt * SampleRate);
        var window = Windowing.Nuttall(nfft);
        var cbuf   = new Complex[nfft];
        double df  = (double)SampleRate / nfft;
        int fBin0  = (int)(f0 / df);

        var s = new double[NSymbolsF, NBinsF];
        for (int sym = 0; sym < NSymbolsF; sym++)
        {
            int start = dtSamples + sym * NspsF;
            for (int k = 0; k < nfft; k++)
            {
                int idx = start + k;
                double v = (idx >= 0 && idx < Nmax) ? dd[idx] : 0.0;
                cbuf[k] = new Complex(v * window[k], 0.0);
            }
            Fft.ForwardInPlace(cbuf);
            for (int b = 0; b < NBinsF; b++)
            {
                int bin = fBin0 + b;
                s[sym, b] = (bin >= 0 && bin < nfft / 2)
                    ? cbuf[bin].Real * cbuf[bin].Real + cbuf[bin].Imaginary * cbuf[bin].Imaginary
                    : 0.0;
            }
        }

        var llr    = new double[174];
        int llrIdx = 0;
        foreach (int sym in DataPositionsF)
        {
            double p1 = s[sym, 0] + s[sym, 1] + s[sym, 2] + s[sym, 3];
            double p0 = s[sym, 4] + s[sym, 5] + s[sym, 6] + s[sym, 7];
            llr[llrIdx++] = SafeLog(p1, p0);

            double p1b = s[sym, 0] + s[sym, 1] + s[sym, 4] + s[sym, 5];
            double p0b = s[sym, 2] + s[sym, 3] + s[sym, 6] + s[sym, 7];
            llr[llrIdx++] = SafeLog(p1b, p0b);

            double p1c = s[sym, 0] + s[sym, 2] + s[sym, 4] + s[sym, 6];
            double p0c = s[sym, 1] + s[sym, 3] + s[sym, 5] + s[sym, 7];
            llr[llrIdx++] = SafeLog(p1c, p0c);
        }
        return llr;
    }

    private static double SafeLog(double a, double b)
    {
        const double eps = 1e-12;
        return Math.Log((a + eps) / (b + eps));
    }

    private static bool AllFalse(bool[] arr)
    {
        foreach (bool b in arr) if (b) return false;
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Fox → Hound path — QPC polar code
    // ═══════════════════════════════════════════════════════════════════════════

    private void DecodeFoxPass(
        double[] dd, double freqLow, double freqHigh, string utcTime,
        List<DecodeResult> results, HashSet<string> decoded)
    {
        // Build the analytic signal (Hilbert transform via FFT)
        var c0 = SfoxAna(dd);

        // Try multiple centre-frequency candidates spanning freqLow..freqHigh
        double fStep = 50.0; // Hz
        for (double fqso = Math.Max(freqLow, 200.0); fqso <= Math.Min(freqHigh, 5000.0); fqso += fStep)
        {
            double fa = Math.Max(freqLow,  fqso - 200.0);
            double fb = Math.Min(freqHigh, fqso + 200.0);

            var xdec = new byte[60];
            bool crcOk = false;
            double fbest = fqso, tbest = 0.5, snr = -20.0;

            // depth=1: try without smoothing, kkk=1 only (strong-signal decode)
            TryQpcDecode2(c0, fqso, fa, fb, ndepth: 1,
                          xdec, ref crcOk, ref fbest, ref tbest, ref snr);

            if (!crcOk) continue;

            var msgs = SfoxUnpack(xdec);
            foreach (string m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m)) continue;
                if (!decoded.Add(m)) continue;

                double audioFreq = fbest - 750.0; // nominal carrier offset
                var r = new DecodeResult
                {
                    UtcTime     = utcTime,
                    Snr         = (int)Math.Clamp(snr, -30, 30),
                    Dt          = tbest,
                    FrequencyHz = Math.Max(0, audioFreq),
                    Message     = m.Trim(),
                    Mode        = DigitalMode.SuperFox,
                };
                results.Add(r);
                Emit(r);
            }

            // One successful Fox decode per frequency band is sufficient
            if (crcOk) break;
        }
    }

    // ── Analytic signal (Hilbert transform via FFT) ──────────────────────────
    // Mirrors DecoderSFox::sfox_ana.

    internal static Complex[] SfoxAna(double[] dd)
    {
        int n  = Nmax;
        double fac = (1.0 / 300.0) * 0.01;
        var c0 = new Complex[n];
        for (int i = 0; i < n; i++) c0[i] = new Complex(dd[i] * fac, 0.0);

        Fft.ForwardInPlace(c0); // forward FFT

        // Zero negative-frequency bins; halve DC
        c0[0] = c0[0] * 0.5;
        for (int i = n / 2; i < n; i++) c0[i] = Complex.Zero;

        Fft.InverseInPlace(c0); // inverse → analytic signal
        return c0;
    }

    // ── QPC sync — finds the Fox carrier frequency and start time ─────────────
    // Simplified port of DecoderSFox::qpc_sync.

    private static bool QpcSync(
        Complex[] c0, double fsync, double fa, double fb,
        out double f2, out double t2, out double snrsync)
    {
        const int NDown  = 16;
        const int N9Sec  = (int)(9.2 * 12000); // 110400
        const int Nz     = N9Sec / NDown;       // 6900
        double baud      = 12000.0 / NspsQ;     // 11.71875
        double df2       = 12000.0 / N9Sec;
        double fac       = 1.0 / N9Sec;
        int nspsd        = NspsQ / NDown;        // 64
        double dt        = (double)NDown / 12000.0;
        int lagmax       = (int)(1.5 / dt);     // ~1125

        f2 = fsync; t2 = 0.5; snrsync = 0.0;

        // Build downsampled time-domain signal near fsync
        var c00 = new Complex[N9Sec];
        for (int i = 0; i < N9Sec; i++) c00[i] = fac * c0[Math.Min(i, c0.Length - 1)];

        Fft.ForwardInPlace(c00); // → frequency domain
        // Note: only first N9Sec/2 bins contain positive freqs

        int iz = N9Sec / 4;
        var s = new double[iz + 10];
        for (int i = 0; i < iz; i++)
        {
            double re = c00[i].Real, im = c00[i].Imaginary;
            s[i] = re * re + im * im;
        }
        // Smooth 4 times (one pass each call)
        SignalMath.Smo121(s, 0, 4);

        double nbaud = baud / df2; // bins per symbol
        int iaSync   = Math.Max(0,  (int)((Math.Max(fa, fsync - 60.0)) / df2));
        int ibSync   = Math.Min(iz, (int)((Math.Min(fb, fsync + 60.0)) / df2));
        if (iaSync >= ibSync) { iaSync = Math.Max(0, (int)((fsync - 60) / df2)); ibSync = Math.Min(iz, (int)((fsync + 60) / df2)); }

        int ipk = iaSync;
        for (int i = iaSync + 1; i < ibSync; i++) if (s[i] > s[ipk]) ipk = i;

        // Parabolic refinement
        double delta = 0;
        if (ipk > 0 && ipk < iz - 1 && s[ipk] > 0)
        {
            double s0 = s[ipk], sm = s[ipk - 1], sp = s[ipk + 1];
            double denom = 2.0 * (2.0 * s0 - sm - sp);  // corrected: was (s0-sm-sp)
            if (Math.Abs(denom) > 1e-10) delta = (sp - sm) / denom;
        }
        int i0s = (int)(ipk + delta);
        f2 = i0s * df2 - 750.0; // offset from nominal 750 Hz

        // Build c1 around the peak bin
        var c1    = new Complex[Nz + 20];
        int iaBnd = Math.Max(0,   (int)(i0s - nbaud * 1.5));
        int ibBnd = Math.Min(N9Sec, (int)(i0s + nbaud * 1.5));
        for (int i = iaBnd; i <= ibBnd; i++)
        {
            int j = i - i0s;
            if (j >= 0 && j < Nz)        c1[j]      = c00[i];
            else if (j < 0 && j + Nz >= 0) c1[j + Nz] = c00[i];
        }
        Fft.InverseInPlace(c1); // → time domain

        // Build cumulative sum for sync correlation
        var c1sum = new Complex[Nz];
        c1sum[0] = c1[0];
        for (int i = 1; i < Nz; i++) c1sum[i] = c1sum[i - 1] + c1[i];

        int i0t     = (int)(0.5 * 12000.0 / NDown);
        double pmax = 0;
        int lagpk   = 0;

        for (int lag = -lagmax; lag <= lagmax; lag++)
        {
            double sp = 0;
            for (int j = 0; j < NsSync; j++)
            {
                int i1 = i0t + IsyncF[j] * nspsd + lag;
                int i2 = i1 + nspsd;
                if (i1 < 0 || i2 < 0 || i1 >= Nz || i2 >= Nz) continue;
                var z  = c1sum[i2] - c1sum[i1];
                sp += z.Real * z.Real + z.Imaginary * z.Imaginary;
            }
            if (sp > pmax) { pmax = sp; lagpk = lag; }
        }
        t2 = lagpk * dt;

        // SNR estimate
        double tsym = NspsQ / 12000.0;
        double spNoise = 0; int nsum = 0;
        double spNoiseSq = 0;
        var pArr = new double[2 * lagmax + 2];
        for (int lag = -lagmax; lag <= lagmax; lag++)
        {
            double sp = 0;
            for (int j = 0; j < NsSync; j++)
            {
                int i1 = i0t + IsyncF[j] * nspsd + lag;
                int i2 = i1 + nspsd;
                if (i1 < 0 || i2 < 0 || i1 >= Nz || i2 >= Nz) continue;
                var z  = c1sum[i2] - c1sum[i1];
                sp += z.Real * z.Real + z.Imaginary * z.Imaginary;
            }
            pArr[lag + lagmax] = sp;
            double tlag = (lag - lagpk) * dt;
            if (Math.Abs(tlag) >= tsym) { spNoise += sp; spNoiseSq += sp * sp; nsum++; }
        }
        if (nsum > 0)
        {
            double ave = spNoise / nsum;
            double rms = Math.Sqrt(spNoiseSq / nsum - ave * ave);
            snrsync = rms > 0 ? (pmax - ave) / rms : 0;
        }

        return snrsync > 1.5;
    }

    // ── Convert symbol powers to probabilities ────────────────────────────────
    // Mirrors DecoderSFox::qpc_likelihoods2.

    private static void QpcLikelihoods(double[,] py, double[,] s3)
    {
        const double EsNoDec = 3.16;
        const double CEsNode = 3.2;
        double norm = (EsNoDec / (EsNoDec + 1.0));

        for (int k = 0; k < QpcNp; k++)
        {
            double pwrMax = 0;
            for (int j = 0; j < QpcQ; j++) { double v = norm * s3[k + 1, j]; py[k, j] = v; if (v > pwrMax) pwrMax = v; }

            double pyNorm = 0;
            for (int j = 0; j < QpcQ; j++) { double v = Math.Exp(py[k, j] - pwrMax); py[k, j] = v; pyNorm += v; }
            if (pyNorm <= 0) pyNorm = 1e-6;
            pyNorm *= CEsNode;
            for (int j = 0; j < QpcQ; j++) py[k, j] /= pyNorm;
        }
    }

    // ── Diagnostic: expose demodulation for round-trip testing ───────────────

    /// <summary>
    /// Demodulates audio and returns the s3 power matrix plus timing/frequency.
    /// Returns null if QpcSync cannot find the sync tone.
    /// Used only in tests.
    /// </summary>
    internal (double[,] s3, double f2, double t2, double syncSnr)? TestDemodulate(
        float[] audio, double fsync = 750.0, double? forcedT2 = null, double? forcedF2 = null)
    {
        var dd = new double[Math.Min(audio.Length, Nmax)];
        for (int i = 0; i < dd.Length; i++) dd[i] = audio[i];
        var c0 = SfoxAna(dd);

        if (!QpcSync(c0, fsync, fsync - 200, fsync + 200, out double f2, out double t2, out double syncSnr))
            return null;

        if (forcedT2.HasValue) t2 = forcedT2.Value;
        if (forcedF2.HasValue) f2 = forcedF2.Value;

        double baud   = 12000.0 / NspsQ;
        double f      = 1500.0 + f2;
        double fshift = 1500.0 - (f + baud);
        var c = TweakFreq2(c0, Nmax, 12000.0, fshift);

        var s2 = new double[162, 138];
        var s3 = new double[138, 138];
        SfoxDemodAlt(c, 1500.0, t2, s2, s3);
        return (s3, f2, t2, syncSnr);
    }

    // ── Main QPC decode driver ────────────────────────────────────────────────
    // Simplified port of DecoderSFox::qpc_decode2.
    // ndepth=1 → no dithering; handles reasonably strong Fox signals.

    private void TryQpcDecode2(
        Complex[] c0, double fsync, double fa, double fb, int ndepth,
        byte[] xdec, ref bool crcOk, ref double fbest, ref double tbest, ref double snr)
    {
        double baud = 12000.0 / NspsQ;

        if (!QpcSync(c0, fsync, fa, fb, out double f2, out double t2, out double syncSnr))
            return;

        double f00 = 1500.0 + f2;
        double t00 = t2;
        fbest = f00; tbest = t00;

        // Dither table (freq × time)
        int maxft = (syncSnr >= 4.0 && ndepth > 0) ? Math.Min(IdfDither.Length, 20) : 1;
        int maxd  = ndepth > 0 ? Math.Min(MaxSeed + 20, 131) : 1;

        var s2 = new double[162, 138];
        var s3 = new double[138, 138];
        var py = new double[138, 138];

        for (int idith = 0; idith < maxft && !crcOk; idith++)
        {
            double deltaf = IdfDither[idith] * 0.5;
            double deltat = IdtDither[idith] * 8.0 / NspsQ;
            double f      = f00 + deltaf;
            double t      = t00 + deltat;

            // Shift analytic signal so that tone 0 aligns at 1500 Hz
            double fshift = 1500.0 - (f + baud);
            var c = TweakFreq2(c0, Nmax, 12000.0, fshift);

            // Smoothing passes (kk=1..4)
            for (int kk = 1; kk <= 4 && !crcOk; kk++)
            {
                double b = kk switch { 2 => 0.4, 3 => 0.5, 4 => 0.6, _ => 0.0 };

                SfoxDemodAlt(c, 1500.0, t, s2, s3);
                if (b > 0)
                    for (int j = 0; j < QpcQ; j++) Smo121a(s3, j, QpcNp, 1.0, b);

                // Re-normalise
                var flat = new double[QpcQ * (QpcNp + 1)];
                int cp   = 0;
                for (int x = 0; x <= QpcNp; x++)
                    for (int y = 0; y < QpcQ; y++) flat[cp++] = s3[x, y];
                double bs = SignalMath.Pctile(flat, cp, 50.0);
                if (bs <= 0) bs = 1e-6;
                for (int x = 0; x <= QpcNp; x++)
                    for (int y = 0; y < QpcQ; y++) s3[x, y] /= bs;

                QpcLikelihoods(py, s3);

                // Build float probability array for polar decoder
                var pyFlat = new float[QpcNp * QpcQ];
                for (int k = 0; k < QpcNp; k++)
                    for (int j = 0; j < QpcQ; j++) pyFlat[k * QpcQ + j] = (float)py[k, j];

                // Dithering loop (kkk=1: no dither; kkk>2: add noise)
                int cseed = 0;
                for (int kkk = 1; kkk <= maxd && !crcOk; kkk++)
                {
                    float[] pyd;
                    if (kkk == 1)
                    {
                        pyd = (float[])pyFlat.Clone();
                    }
                    else
                    {
                        // Add small random perturbation (dithering)
                        if (kkk > 2 && cseed < MaxSeed)
                        {
                            SeedRandom(DitherSeeds[cseed]);
                            cseed++;
                        }
                        const double dth  = 0.5;
                        const double damp = 1.0;
                        pyd = (float[])pyFlat.Clone();
                        for (int k = 0; k < QpcNp; k++)
                            for (int j = 0; j < QpcQ; j++)
                            {
                                int idx = k * QpcQ + j;
                                if (pyFlat[idx] <= dth)
                                    pyd[idx] = (float)(pyFlat[idx] * (1.0 + damp * RandNoise()));
                            }
                    }

                    // Normalise each row
                    for (int k = 0; k < QpcNp; k++)
                    {
                        float rowSum = 0;
                        for (int j = 0; j < QpcQ; j++) rowSum += pyd[k * QpcQ + j];
                        if (rowSum > 0)
                            for (int j = 0; j < QpcQ; j++) pyd[k * QpcQ + j] /= rowSum;
                    }

                    // Build the full 128-row py for the polar decoder
                    // (row 0 = punctured symbol with known value 0)
                    var pyFull = new float[QpcN * QpcQ];
                    // Row 0: punctured symbol — set py[0]=1, rest=0
                    pyFull[0] = 1.0f;
                    // Rows 1..127 from pyd
                    Array.Copy(pyd, 0, pyFull, QpcQ, QpcNp * QpcQ);

                    var xdec0 = new byte[QpcK + 2];
                    var ydec  = new byte[QpcN + 2];
                    _qpcStackTop = 0;
                    QpcDecode(xdec0, ydec, pyFull);

                    // Reverse xdec0 → xdec (matches C++ reversal)
                    for (int x = 0; x < QpcK; x++) xdec[x] = xdec0[QpcK - 1 - x];

                    // 21-bit CRC check
                    const uint mask21 = (1u << 21) - 1;
                    uint crcChk  = NHash2(xdec, 47, 571) & mask21;
                    uint crcSent = 128u * 128u * xdec[47] + 128u * xdec[48] + xdec[49];
                    if (crcChk == crcSent)
                    {
                        crcOk = true;
                        snr = QpcSnr(s3, ydec);
                        fbest = f; tbest = t;
                    }
                }
            }
        }
    }

    // SfoxDemodAlt: helper that reuses the static SfoxDemod with proper FFT
    private static void SfoxDemodAlt(Complex[] c, double f, double t, double[,] s2, double[,] s3)
    {
        int j0 = (int)(12000.0 * (t + 0.5));
        double df = 12000.0 / NspsQ;
        int i0 = (int)(f / df - QpcQ / 2.0);

        for (int b = 0; b < QpcQ; b++) { s2[0, b] = 0; s3[0, b] = 0; }

        var csym = new Complex[NspsQ];
        var isyncSet = new HashSet<int>(IsyncF);
        int k2 = 0, k3 = 0;
        for (int n = 0; n < NdsQ; n++)
        {
            int ja = n * NspsQ + j0;
            int jb = ja + NspsQ - 1;
            k2++;
            bool inBounds = ja >= 0 && jb < Nmax;
            for (int i = 0; i < NspsQ; i++)
                csym[i] = (inBounds && ja + i < Nmax) ? c[ja + i] : Complex.Zero;

            Fft.C2C(csym, -1);

            for (int b = 0; b < QpcQ; b++)
            {
                int bin = i0 + b;
                double re = 0, im = 0;
                if (bin >= 0 && bin < NspsQ) { re = csym[bin].Real; im = csym[bin].Imaginary; }
                s2[k2 < 162 ? k2 : 161, b] = re * re + im * im;
            }
        }

        // Normalise s2
        int total2 = 0;
        var flat2 = new double[QpcQ * 152];
        for (int x = 0; x < 152 && x < s2.GetLength(0); x++)
            for (int b = 0; b < QpcQ; b++) flat2[total2++] = s2[x, b];
        double base2 = SignalMath.Pctile(flat2, total2, 50.0);
        if (base2 <= 0) base2 = 1e-6;
        for (int x = 0; x < Math.Min(152, s2.GetLength(0)); x++)
            for (int b = 0; b < QpcQ; b++) s2[x, b] /= base2;

        // Copy non-sync to s3
        k3 = 0;
        for (int n = 0; n < NdsQ; n++)
        {
            if (isyncSet.Contains(n)) continue;
            k3++;
            int src = n + 1;
            if (src < s2.GetLength(0) && k3 < s3.GetLength(0))
                for (int b = 0; b < QpcQ; b++) s3[k3, b] = s2[src, b];
        }

        // Normalise s3
        int total3 = 0;
        var flat3 = new double[QpcQ * (QpcNp + 1)];
        for (int x = 0; x <= QpcNp && x < s3.GetLength(0); x++)
            for (int b = 0; b < QpcQ; b++) flat3[total3++] = s3[x, b];
        double base3 = SignalMath.Pctile(flat3, total3, 50.0);
        if (base3 <= 0) base3 = 1e-6;
        for (int x = 0; x <= QpcNp && x < s3.GetLength(0); x++)
            for (int b = 0; b < QpcQ; b++) s3[x, b] /= base3;
    }

    private static void Smo121a(double[,] s3, int row, int nz, double a, double b)
    {
        double fac = 1.0 / (a + 2.0 * b);
        double x0 = s3[row, 0];
        for (int i = 1; i < nz - 1 && i < s3.GetLength(1) - 1; i++)
        {
            double x1 = s3[row, i];
            s3[row, i] = fac * (a * s3[row, i] + b * (x0 + s3[row, i + 1]));
            x0 = x1;
        }
    }

    // ── Frequency shift of analytic signal ───────────────────────────────────

    internal static Complex[] TweakFreq2(Complex[] src, int npts, double fsample, double fshift)
    {
        var dst  = new Complex[npts];
        double dphi = 2.0 * Math.PI * fshift / fsample;
        double phi  = dphi; // start with first step (matches C++ w=1+1i init approx)
        for (int i = 0; i < npts; i++)
        {
            double cos = Math.Cos(phi), sin = Math.Sin(phi);
            dst[i] = new Complex(
                src[i].Real * cos - src[i].Imaginary * sin,
                src[i].Real * sin + src[i].Imaginary * cos);
            phi += dphi;
        }
        return dst;
    }

    // ── QPC polar code outer decoder ─────────────────────────────────────────
    // Mirrors DecoderSFox::qpc_decode.

    /// <summary>
    /// Testing entry point: decode a 128×128 probability matrix directly into
    /// xdec0[0..QpcK-1] (not yet reversed). Resets the stack before decoding.
    /// </summary>
    internal void QpcDecodeForTest(float[] pyFull128x128, byte[] xdec0)
    {
        var ydec = new byte[QpcN + 2];
        _qpcStackTop = 0;
        QpcDecode(xdec0, ydec, pyFull128x128);
    }

    private void QpcDecode(byte[] xdec, byte[] ydec, float[] py)
    {
        // Set punctured symbol (row 0) to known frozen value 0: already done by py[0]=1

        var x = new byte[QpcN + 2];
        QpcDecodeRecursive(x, 0, ydec, 0, py, 0, QpcF, QpcFSize, 0, QpcN);

        // Demap information symbols
        for (int k = 0; k < QpcK; k++) xdec[k] = x[QpcXPos[k]];
    }

    // ── Recursive QPC polar decoder ───────────────────────────────────────────
    // Mirrors DecoderSFox::_qpc_decode.

    private void QpcDecodeRecursive(
        byte[] xdec, int xOff,
        byte[] ydec, int yOff,
        float[] py, int pyOff,
        byte[] f, byte[] fsize, int fOff,
        int numRows)
    {
        if (numRows == 1)
        {
            xdec[xOff] = PdfMax(py, pyOff);
            ydec[yOff] = fsize[fOff] == 0 ? f[fOff] : xdec[xOff];
            return;
        }

        int nextRows = numRows >> 1;
        int size     = nextRows * QpcQ;

        // Push pyl = first half of py
        int pylOff = _qpcStackTop;
        Array.Copy(py, pyOff, _qpcStack, pylOff, size);
        _qpcStackTop += size;

        // Push pyh = second half of py
        int pyhOff = _qpcStackTop;
        Array.Copy(py, pyOff + size, _qpcStack, pyhOff, size);
        _qpcStackTop += size;

        // Convolve pyh ← conv(pyl, pyh) for each row
        for (int k = 0; k < nextRows; k++)
            PdfConv(_qpcStack, pyhOff + k * QpcQ, _qpcStack, pylOff + k * QpcQ, _qpcStack, pyhOff + k * QpcQ);

        // Recurse upper half
        QpcDecodeRecursive(xdec, xOff + nextRows, ydec, yOff + nextRows,
                           _qpcStack, pyhOff, f, fsize, fOff + nextRows, nextRows);

        // pdfarray_convhard: pyh ← convhard(py_original_upper, ydec_upper)
        for (int k = 0; k < nextRows; k++)
        {
            byte hd = ydec[yOff + nextRows + k];
            int pyhRow = pyhOff + k * QpcQ;
            int srcRow = pyOff  + size + k * QpcQ;
            for (int j = 0; j < QpcQ; j++)
                _qpcStack[pyhRow + j] = py[srcRow + (j ^ hd)];
        }

        // pdfarray_mul: pyl ← pyl * pyh
        for (int k = 0; k < nextRows; k++)
            PdfMul(_qpcStack, pylOff + k * QpcQ, _qpcStack, pylOff + k * QpcQ, _qpcStack, pyhOff + k * QpcQ);

        _qpcStackTop -= size; // pop pyh

        // Recurse lower half
        QpcDecodeRecursive(xdec, xOff, ydec, yOff,
                           _qpcStack, pylOff, f, fsize, fOff, nextRows);

        _qpcStackTop -= size; // pop pyl

        // Update: ydec_upper ^= ydec_lower
        for (int k = 0; k < nextRows; k++)
            ydec[yOff + nextRows + k] ^= ydec[yOff + k];
    }

    // ── PDF operations ────────────────────────────────────────────────────────

    private void PdfConv(float[] dst, int dstOff, float[] pdf1, int p1Off, float[] pdf2, int p2Off)
    {
        // Convolution in FWHT domain: conv(a,b) = IFWHT(FWHT(a) ⊙ FWHT(b)) / Q
        var fwht1 = new float[QpcQ];
        var fwht2 = new float[QpcQ];
        Array.Copy(pdf1, p1Off, fwht1, 0, QpcQ);
        Array.Copy(pdf2, p2Off, fwht2, 0, QpcQ);
        Fwht128InPlace(fwht1);
        Fwht128InPlace(fwht2);
        for (int k = 0; k < QpcQ; k++) fwht1[k] *= fwht2[k];
        Fwht128InPlace(fwht1);
        float norm = 1.0f / QpcQ;
        for (int k = 0; k < QpcQ; k++) dst[dstOff + k] = fwht1[k] * norm;
    }

    private void PdfMul(float[] dst, int dstOff, float[] pdf1, int p1Off, float[] pdf2, int p2Off)
    {
        float norm = 0;
        for (int k = 0; k < QpcQ; k++) { float v = pdf1[p1Off + k] * pdf2[p2Off + k]; dst[dstOff + k] = v; norm += v; }
        if (norm <= 0) { Array.Fill(dst, 1.0f / QpcQ, dstOff, QpcQ); return; }
        norm = 1.0f / norm;
        for (int k = 0; k < QpcQ; k++) dst[dstOff + k] *= norm;
    }

    private static byte PdfMax(float[] pdf, int off)
    {
        byte imax = 0;
        float pmax = pdf[off];
        for (int k = 1; k < QpcQ; k++)
            if (pdf[off + k] > pmax) { pmax = pdf[off + k]; imax = (byte)k; }
        return imax;
    }

    // ── 128-point Fast Walsh-Hadamard Transform (in-place) ───────────────────
    // Standard iterative butterfly; same result as the C++ recursive cascade.

    internal static void Fwht128InPlace(float[] y)
    {
        for (int stride = 1; stride < 128; stride <<= 1)
            for (int j = 0; j < 128; j += stride * 2)
                for (int k = 0; k < stride; k++)
                {
                    float a = y[j + k], b = y[j + k + stride];
                    y[j + k] = a + b; y[j + k + stride] = a - b;
                }
    }

    // ── SNR estimate from decoded symbols ─────────────────────────────────────

    private static double QpcSnr(double[,] s3, byte[] ydec)
    {
        double p = 0;
        for (int j = 0; j < QpcNp; j++)
        {
            int sym = j < ydec.Length ? ydec[j] : 0;
            if (sym < QpcQ && j < s3.GetLength(0)) p += s3[j, sym];
        }
        return SignalMath.Db(p / QpcNp) - SignalMath.Db(QpcNp) - 4.0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Bob Jenkins nhash2 — 21-bit CRC for QPC frames
    // ═══════════════════════════════════════════════════════════════════════════

    internal static uint NHash2(byte[] key, int length, uint initval)
    {
        uint a = 0xdeadbeef + (uint)length + initval;
        uint b = a, c = a;
        int pos = 0;

        while (length > 12)
        {
            a += key[pos]; a += (uint)key[pos + 1] << 8; a += (uint)key[pos + 2] << 16; a += (uint)key[pos + 3] << 24;
            b += key[pos + 4]; b += (uint)key[pos + 5] << 8; b += (uint)key[pos + 6] << 16; b += (uint)key[pos + 7] << 24;
            c += key[pos + 8]; c += (uint)key[pos + 9] << 8; c += (uint)key[pos + 10] << 16; c += (uint)key[pos + 11] << 24;
            NhashMix(ref a, ref b, ref c);
            pos += 12; length -= 12;
        }

        switch (length)
        {
            case 12: c += (uint)key[pos + 11] << 24; goto case 11;
            case 11: c += (uint)key[pos + 10] << 16; goto case 10;
            case 10: c += (uint)key[pos + 9]  <<  8; goto case 9;
            case 9:  c += key[pos + 8]; goto case 8;
            case 8:  b += (uint)key[pos + 7]  << 24; goto case 7;
            case 7:  b += (uint)key[pos + 6]  << 16; goto case 6;
            case 6:  b += (uint)key[pos + 5]  <<  8; goto case 5;
            case 5:  b += key[pos + 4]; goto case 4;
            case 4:  a += (uint)key[pos + 3]  << 24; goto case 3;
            case 3:  a += (uint)key[pos + 2]  << 16; goto case 2;
            case 2:  a += (uint)key[pos + 1]  <<  8; goto case 1;
            case 1:  a += key[pos]; break;
            case 0: return c;
        }

        NhashFinal(ref a, ref b, ref c);
        return c;
    }

    private static void NhashMix(ref uint a, ref uint b, ref uint c)
    {
        a -= c; a ^= (c << 4)  | (c >> 28); c += b;
        b -= a; b ^= (a << 6)  | (a >> 26); a += c;
        c -= b; c ^= (b << 8)  | (b >> 24); b += a;
        a -= c; a ^= (c << 16) | (c >> 16); c += b;
        b -= a; b ^= (a << 19) | (a >> 13); a += c;
        c -= b; c ^= (b << 4)  | (b >> 28); b += a;
    }

    private static void NhashFinal(ref uint a, ref uint b, ref uint c)
    {
        c ^= b; c -= (b << 14) | (b >> 18);
        a ^= c; a -= (c << 11) | (c >> 21);
        b ^= a; b -= (a << 25) | (a >>  7);
        c ^= b; c -= (b << 16) | (b >> 16);
        a ^= c; a -= (c <<  4) | (c >> 28);
        b ^= a; b -= (a << 14) | (a >> 18);
        c ^= b; c -= (b << 24) | (b >>  8);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Random number generator (LCG, matches C++ TYPE_00 srand/rand)
    // ═══════════════════════════════════════════════════════════════════════════

    private void SeedRandom(uint seed)
    {
        _randState = seed == 0 ? 1 : seed;
        for (int i = 0; i < 10; i++) NextRand(); // warm-up
    }

    private uint NextRand()
    {
        _randState = (_randState * 1103515245u + 12345u) & 0x7fffffff;
        return _randState;
    }

    private static readonly double _randMax = (double)515396075;
    private bool _randSet;
    private double _randPhi, _randU;

    private double RandNoise()
    {
        double stdev    = 0.5 / Math.Sqrt(2.0);
        double scalePhi = 6.283185307 / (1.0 + _randMax);
        double scaleU   = 1.0 / (1.0 + _randMax);
        const double corrmean = -0.15;

        if (_randSet)
        {
            _randSet = false;
            return Math.Sin(_randPhi) * _randU * stdev + corrmean;
        }

        _randPhi = scalePhi * (double)(NextRand() % (uint)_randMax);
        _randU   = scaleU   * (0.5 + (double)(NextRand() % (uint)_randMax));
        _randU   = Math.Sqrt(-2.0 * Math.Log(_randU));
        _randSet = true;
        return Math.Cos(_randPhi) * _randU * stdev + corrmean;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // sfox_unpack — parse 50 decoded 7-bit symbols into message strings
    // ═══════════════════════════════════════════════════════════════════════════

    internal List<string> SfoxUnpack(byte[] x)
    {
        var result = new List<string>();

        // Convert 50 × 7-bit symbols to 350 bits (MSB first)
        var bits = new bool[400];
        int co = 0;
        for (int i = 0; i < QpcK && i < x.Length; i++)
        {
            int val = x[i];
            for (int b = 6; b >= 0; b--) bits[co++] = ((val >> b) & 1) == 1;
        }

        int i3  = BinToInt(bits, 326, 329); // message type (3 bits)
        int n28 = BinToInt(bits, 0, 28);    // Fox callsign (pack28, used for i3=0/2)

        if (!Unpack28(n28, out string foxCall)) foxCall = "?";

        if (i3 == 3) // CQ FoxCall Grid (+ optional free text)
        {
            long n58 = 0;
            for (int i = 0; i < 58; i++) { n58 <<= 1; n58 |= bits[i] ? 1L : 0L; }
            foxCall = DecodeBase38Call(n58);

            int n15 = BinToInt(bits, 58, 73);
            result.Add($"CQ {foxCall} {Grid4(n15)}");

            // Optional free text in bits 73-143 and 144-214 (Fortran msgbits(74:144/145:215)).
            // The encoder fills this area with NqU1rks sentinel when no text is present;
            // check the first 32 bits (bits 73-104) for the sentinel before unpacking.
            if (BinToInt(bits, 73, 105) != NqU1rks)
            {
                string ft = (UnpackText71(bits, 73) + UnpackText71(bits, 144)).TrimEnd('.');
                if (!string.IsNullOrWhiteSpace(ft))
                    result.Add(ft);
            }
        }
        else // i3=0 (standard compound) or i3=2 (hounds + free text)
        {
            if (i3 == 2) // up to 4 hounds + free text
            {
                // Free text at bits 160-230 and 231-301 (71 bits each)
                var ft1 = UnpackText71(bits, 160);
                var ft2 = UnpackText71(bits, 231);
                string ft = (ft1 + ft2).TrimEnd('.');
                if (!string.IsNullOrWhiteSpace(ft)) result.Add(ft);
            }

            // Reports (5 bits each): bits 280-299 for i3=0, bits 140-159 for i3=2
            int jRpt = i3 == 2 ? 140 : 280;
            var crpt = new string[4];
            for (int i = 0; i < 4; i++)
            {
                int n = BinToInt(bits, jRpt, jRpt + 5);
                if (n == 31) crpt[i] = "RR73";
                else
                {
                    int irpt = n - 18;
                    crpt[i] = irpt >= 0 ? $"+{irpt:D2}" : $"-{Math.Abs(irpt):D2}";
                }
                jRpt += 5;
            }

            // Hound callsigns: 9 slots for i3=0, 4 for i3=2
            int iz  = i3 == 2 ? 4 : 9;
            int ncq = 0;
            for (int i = 0; i < iz; i++)
            {
                int j    = (i + 1) * 28;
                int n28h = BinToInt(bits, j, j + 28);
                if (n28h == 0 || n28h == NqU1rks) continue;
                if (!Unpack28(n28h, out string c13)) continue;

                string msg = $"{c13} {foxCall}";
                if (msg.StartsWith("CQ ", StringComparison.Ordinal)) ncq++;
                else
                {
                    if (i3 == 2)     msg += $" {crpt[i]}";
                    else if (i <= 4) msg += " RR73";
                    else             msg += $" {crpt[i - 5]}";
                }
                if (ncq <= 1 || !msg.StartsWith("CQ ", StringComparison.Ordinal))
                    result.Add(msg);
            }

            // Optional CQ beacon (MoreCQs flag, bit 305)
            if (BinToInt(bits, 305, 306) == 1 && ncq < 1)
                result.Add($"CQ {foxCall}");
        }

        // Digital signature — bits 306-325 (Fortran msgbits(307:326)), 20-bit OTP notp.
        // Present for all message types; non-zero means the fox signed this transmission.
        uint notp = 0;
        for (int i = 306; i < 326; i++) { notp = (notp << 1) | (bits[i] ? 1u : 0u); }
        if (notp != 0)
            result.Add($"$VERIFY$ {foxCall} {notp:D6}");

        return result;
    }

    // ── Bit extraction helpers ────────────────────────────────────────────────

    internal static int BinToInt(bool[] bits, int from, int to)
    {
        int v = 0;
        for (int i = from; i < to; i++) v = (v << 1) | (bits[i] ? 1 : 0);
        return v;
    }

    // ── Callsign decode (mirrors MessagePacker.Unpack28) ─────────────────────

    internal bool Unpack28(int n28, out string call)
    {
        call = "";
        if (n28 < NTokens28)
        {
            if (n28 == 0) { call = "DE";  return true; }
            if (n28 == 1) { call = "QRZ"; return true; }
            if (n28 == 2) { call = "CQ";  return true; }
            if (n28 <= 1002)  { call = $"CQ_{n28 - 3:D3}"; return true; }
            if (n28 <= 532443)
            {
                int n = n28 - 1003;
                int i1 = n / (27 * 27 * 27); n -= 27 * 27 * 27 * i1;
                int i2 = n / (27 * 27);       n -= 27 * 27 * i2;
                int i3 = n / 27;
                int i4 = n - 27 * i3;
                call = "CQ_" + $"{A4_28[i1]}{A4_28[i2]}{A4_28[i3]}{A4_28[i4]}".Trim();
                return true;
            }
        }

        int rem = n28 - NTokens28;
        if (rem < Max22_28)
        {
            // Hash-22 lookup — hash table is internal to MessagePacker;
            // fall back to angle-bracket placeholder.
            call = "<...>";
            return true;
        }

        int nn = rem - Max22_28;
        int ii1 = nn / (36 * 10 * 27 * 27 * 27); nn -= 36 * 10 * 27 * 27 * 27 * ii1;
        int ii2 = nn / (10 * 27 * 27 * 27);       nn -= 10 * 27 * 27 * 27 * ii2;
        int ii3 = nn / (27 * 27 * 27);             nn -= 27 * 27 * 27 * ii3;
        int ii4 = nn / (27 * 27);                  nn -= 27 * 27 * ii4;
        int ii5 = nn / 27;
        int ii6 = nn - 27 * ii5;

        if (ii1 >= A1_28.Length || ii2 >= A2_28.Length || ii3 >= A3_28.Length ||
            ii4 >= A4_28.Length || ii5 >= A4_28.Length || ii6 >= A4_28.Length)
            return false;

        call = $"{A1_28[ii1]}{A2_28[ii2]}{A3_28[ii3]}{A4_28[ii4]}{A4_28[ii5]}{A4_28[ii6]}".Trim();
        if (call.Contains(' ')) return false;
        return true;
    }

    internal static string DecodeBase38Call(long n58)
    {
        const string C38 = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/";
        var c = new char[11];
        for (int i = 10; i >= 0; i--) { c[i] = C38[(int)(n58 % 38)]; n58 /= 38; }
        return new string(c).Trim();
    }

    internal static string Grid4(int n)
    {
        if (n < 0) return "??";
        int j1 = n / (18 * 10 * 10); n -= j1 * 18 * 10 * 10;
        int j2 = n / (10 * 10);       n -= j2 * 10 * 10;
        int j3 = n / 10;
        int j4 = n - j3 * 10;
        if (j1 > 17 || j2 > 17 || j3 > 9 || j4 > 9) return "??";
        return $"{(char)('A' + j1)}{(char)('A' + j2)}{(char)('0' + j3)}{(char)('0' + j4)}";
    }

    private static string UnpackText71(bool[] bits, int start)
    {
        // 71 bits → 13 chars using base-42 encoding (matches MessagePacker.UnpackText77)
        var qa = new byte[10];
        int pos = start;
        for (int i = 0; i < 9; i++)
        {
            int end = (i == 0) ? 7 : 8;
            int k = 0;
            for (int j = 0; j < end && pos < bits.Length; j++) k = (k << 1) | (bits[pos++] ? 1 : 0);
            qa[i + 1] = (byte)k;
        }
        var sb = new StringBuilder(13);
        for (int i = 12; i >= 0; i--)
        {
            var qb = new byte[9];
            int ir = MpShortDiv(qb, qa, 1, 9, 42);
            if (ir >= 0 && ir < CTxt42.Length) sb.Insert(0, CTxt42[ir]);
            for (int x = 0; x < 9; x++) qa[x + 1] = qb[x];
        }
        return sb.ToString();
    }

    private static int MpShortDiv(byte[] w, byte[] u, int buStart, int n, int iv)
    {
        int ir = 0;
        for (int j = 0; j < n; j++)
        {
            int k = 256 * ir + u[j + buStart];
            w[j] = (byte)(k / iv);
            ir   = k % iv;
        }
        return ir;
    }
}

// ── Array extension helper ────────────────────────────────────────────────────
// (Not needed after refactor — kept as a namespace placeholder.)

