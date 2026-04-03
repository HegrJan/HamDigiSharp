using System.Numerics;
using HamDigiSharp.Codecs;
using HamDigiSharp.Dsp;
using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Msk;

/// <summary>
/// MSK144 decoder — 1-second period, MSK modulation, LDPC(128,90).
/// C# port of MSHV's MSK144 decoder (LZ2HV), GPL.
///
/// Frame structure (144 bits × 6 sps = 864 samples):
///   [sync8][codeword[0..47]][sync8][codeword[48..127]]
///
/// Demodulation follows MSHV's I/Q matched-filter approach:
///   - Even bits decoded on Q (imaginary) channel via 12-sample half-sine
///   - Odd bits decoded on I (real) channel via 12-sample half-sine
///   - Carrier phase estimated from the two known sync8 positions
///     (mirrors MSHV detectmsk144 / msk144decodeframe)
///
/// LLR convention: positive = bit 0 (matches Ldpc128_90.TryDecode).
/// </summary>
public class Msk144Decoder : BaseDecoder
{
    // ── Protocol constants ────────────────────────────────────────────────────
    private const int SampleRate   = 12000;
    private const int SampPerSym   = 6;       // 12000 Hz / 2000 baud
    private const int MfLen        = 12;      // matched-filter length (2 × SampPerSym)
    private const int FrameBits    = 144;     // [sync8][data48][sync8][data80]
    private const int SampPerFrame = FrameBits * SampPerSym; // = 864
    private const int CodeLen      = 128;     // LDPC(128,90) codeword
    private const int InfoBits     = 90;      // LDPC info bits (77 msg + 13 CRC)
    private const double Sigma     = 0.60;    // AWGN noise sigma for LLR scaling

    private static readonly int[] DefaultSyncSeq = { 0, 1, 1, 1, 0, 0, 1, 0 };

    // 12-sample half-sine matched filter (pp_msk144 in MSHV)
    private static readonly double[] Pp;

    // 42-sample sync waveform template for carrier phase estimation
    // Mirrors MSHV first_msk144(): cb_msk144[i] = cbi[i] + j*cbq[i]
    private static readonly Complex[] CbMsk144;

    private readonly int[] _syncSeq;
    private readonly DigitalMode _mode;

    static Msk144Decoder()
    {
        Pp = new double[MfLen];
        for (int k = 0; k < MfLen; k++)
            Pp[k] = Math.Sin(Math.PI * k / MfLen);

        CbMsk144 = BuildSyncTemplate(DefaultSyncSeq);
    }

    /// <summary>
    /// Builds the 42-sample complex sync template used for carrier phase estimation.
    /// Mirrors MSHV's <c>first_msk144()</c> with <c>cbq</c> and <c>cbi</c> arrays.
    /// </summary>
    private static Complex[] BuildSyncTemplate(int[] sync8)
    {
        int[] nrz = new int[8];
        for (int i = 0; i < 8; i++) nrz[i] = 2 * sync8[i] - 1; // {0,1} → {-1,+1}

        // Q channel (imaginary): even-indexed bits, 12-sample windows offset by 6
        var cbq = new double[42];
        for (int j = 0; j < 6;  j++) cbq[j]      = Pp[6 + j] * nrz[0]; // bits 0: pp[6..11]
        for (int j = 0; j < 12; j++) cbq[6  + j] = Pp[j]     * nrz[2]; // bit 2: pp[0..11]
        for (int j = 0; j < 12; j++) cbq[18 + j] = Pp[j]     * nrz[4]; // bit 4
        for (int j = 0; j < 12; j++) cbq[30 + j] = Pp[j]     * nrz[6]; // bit 6

        // I channel (real): odd-indexed bits, 12-sample windows
        var cbi = new double[42];
        for (int j = 0; j < 12; j++) cbi[j]      = Pp[j] * nrz[1]; // bit 1
        for (int j = 0; j < 12; j++) cbi[12 + j] = Pp[j] * nrz[3]; // bit 3
        for (int j = 0; j < 12; j++) cbi[24 + j] = Pp[j] * nrz[5]; // bit 5
        for (int j = 0; j < 6;  j++) cbi[36 + j] = Pp[j] * nrz[7]; // bit 7: pp[0..5]

        var cb = new Complex[42];
        for (int i = 0; i < 42; i++) cb[i] = new Complex(cbi[i], cbq[i]);
        return cb;
    }

    public Msk144Decoder() : this(DigitalMode.MSK144, DefaultSyncSeq) { }

    protected Msk144Decoder(DigitalMode mode, int[] syncSeq)
    {
        _mode    = mode;
        _syncSeq = syncSeq;
    }

    public override DigitalMode Mode => _mode;

    // ── Decode entry point ────────────────────────────────────────────────────

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        if (samples.Length < SampPerFrame) return Array.Empty<DecodeResult>();

        double[] dd = PrepareBuffer(samples);
        var analytic = AnalyticSignal(dd);

        int[] timeScan = TimeScan(dd.Length).ToArray();

        // Parallel scan: analytic[] is read-only; each (f0, dt) pair is fully independent.
        var rawResults = FrequencyScan(freqLow, freqHigh)
            .AsParallel()
            .SelectMany(f0 =>
            {
                var local = new List<DecodeResult>();
                foreach (int dt in timeScan)
                {
                    if (TryDecodeMsk144(analytic, f0, dt, utcTime, _syncSeq, _mode, out var r))
                        local.Add(r!);
                }
                return local;
            })
            .ToList();

        // Sequential dedup (deterministic order: lower freq first)
        var results = new List<DecodeResult>();
        var decoded  = new HashSet<string>();
        foreach (var r in rawResults.OrderBy(r => r.FrequencyHz).ThenBy(r => r.Dt))
        {
            if (decoded.Add(r.Message))
            {
                results.Add(r);
                Emit(r);
            }
        }
        return results;
    }

    // ── Buffer preparation ────────────────────────────────────────────────────

    private static double[] PrepareBuffer(ReadOnlySpan<float> samples)
    {
        int nMax = SampleRate;
        var dd = new double[nMax];
        int n = Math.Min(samples.Length, nMax);
        for (int i = 0; i < n; i++) dd[i] = samples[i];
        return dd;
    }

    // ── Frequency and time scan ───────────────────────────────────────────────

    private static IEnumerable<double> FrequencyScan(double freqLow, double freqHigh)
    {
        for (double f = freqLow; f <= freqHigh; f += 10)
            yield return f;
    }

    private static IEnumerable<int> TimeScan(int nSamples)
    {
        // Step by 2 symbol periods; reduces worst-case timing error from ±2 to ±1 symbol.
        const int step = SampPerSym * 2; // 12 samples
        int maxDt = nSamples - SampPerFrame;
        for (int dt = 0; dt <= maxDt; dt += step)
            yield return dt;
    }

    // ── Analytic signal (Hilbert transform) ───────────────────────────────────

    private static Complex[] AnalyticSignal(double[] dd)
    {
        int n = dd.Length;
        var c = new Complex[n];
        for (int i = 0; i < n; i++) c[i] = new Complex(dd[i], 0.0);
        Fft.ForwardInPlace(c);
        for (int i = n / 2 + 1; i < n; i++) c[i] = Complex.Zero;
        for (int i = 1;          i < n / 2; i++) c[i] *= 2;
        Fft.InverseInPlace(c);
        return c;
    }

    // ── Per-candidate decode ──────────────────────────────────────────────────

    private bool TryDecodeMsk144(
        Complex[] analytic, double f0, int dtSamples, string utcTime,
        int[] syncSeq, DigitalMode mode, out DecodeResult? result)
    {
        result = null;

        var baseband = FrequencyShift(analytic, f0, dtSamples);
        if (baseband is null) return false;

        // ── Carrier phase estimation (MSHV msk144decodeframe) ────────────────
        Complex cca = Complex.Zero, ccb = Complex.Zero;
        for (int i = 0; i < CbMsk144.Length; i++)
        {
            cca += baseband[i]       * Complex.Conjugate(CbMsk144[i]);
            ccb += baseband[336 + i] * Complex.Conjugate(CbMsk144[i]);
        }
        double phase0 = Math.Atan2((cca + ccb).Imaginary, (cca + ccb).Real);
        ApplyPhaseRotation(baseband, -phase0);

        double[] softBits = MskDemodulate(baseband);
        NormaliseSoftBits(softBits);
        if (!SyncOk(softBits, syncSeq)) return false;

        // Nominal data bits (unit-RMS after NormaliseSoftBits).
        var dataNom = ExtractDataBits(softBits);

        // Timing-diversity channels at ±half-symbol — demodulate without requiring sync pass.
        var dataM = DemodulateDataBitsAtOffset(analytic, f0, dtSamples - SampPerSym / 2);
        var dataP = DemodulateDataBitsAtOffset(analytic, f0, dtSamples + SampPerSym / 2);

        // Try ensemble first (analogous to FT4/FT2 ensemble variant E), then nominal.
        bool[] msg   = new bool[InfoBits];
        bool   ok    = false;
        var ensemble = CombineDataBits(dataNom, dataM, dataP);
        if (ensemble is not null) ok = TryLdpcDecode(ensemble, msg);
        if (!ok)
        {
            Array.Clear(msg);
            ok = TryLdpcDecode(dataNom, msg);
        }
        if (!ok) return false;

        string message = UnpackMsk144(msg);
        if (string.IsNullOrWhiteSpace(message)) return false;

        result = new DecodeResult
        {
            UtcTime     = utcTime,
            Snr         = -15,
            Dt          = dtSamples / (double)SampleRate,
            FrequencyHz = f0,
            Message     = message.Trim(),
            Mode        = mode,
        };
        return true;
    }

    // ── Timing-diversity helpers ──────────────────────────────────────────────

    /// <summary>
    /// Demodulates at <paramref name="dtSamples"/> without a sync check and
    /// returns the 128 data-bit soft values (unit-RMS normalised), or null on
    /// out-of-range dt.
    /// </summary>
    private double[]? DemodulateDataBitsAtOffset(Complex[] analytic, double f0, int dtSamples)
    {
        var bb = FrequencyShift(analytic, f0, dtSamples);
        if (bb is null) return null;

        Complex cca = Complex.Zero, ccb = Complex.Zero;
        for (int i = 0; i < CbMsk144.Length; i++)
        {
            cca += bb[i]       * Complex.Conjugate(CbMsk144[i]);
            ccb += bb[336 + i] * Complex.Conjugate(CbMsk144[i]);
        }
        ApplyPhaseRotation(bb, -Math.Atan2((cca + ccb).Imaginary, (cca + ccb).Real));

        var sb = MskDemodulate(bb);
        NormaliseSoftBits(sb);
        return ExtractDataBits(sb);
    }

    private static double[] ExtractDataBits(double[] softBits)
    {
        var data = new double[CodeLen];
        Array.Copy(softBits,  8, data,  0, 48);
        Array.Copy(softBits, 64, data, 48, 80);
        return data;
    }

    /// <summary>
    /// Combines up to three unit-RMS data-bit arrays by summing and
    /// renormalising to unit-RMS.  Returns null when fewer than 2 channels.
    /// </summary>
    private static double[]? CombineDataBits(
        double[] nom, double[]? minus, double[]? plus)
    {
        int count = 1 + (minus is not null ? 1 : 0) + (plus is not null ? 1 : 0);
        if (count < 2) return null;

        var sum = (double[])nom.Clone();
        if (minus is not null) for (int i = 0; i < CodeLen; i++) sum[i] += minus[i];
        if (plus  is not null) for (int i = 0; i < CodeLen; i++) sum[i] += plus[i];

        // Normalise to unit-RMS; TryLdpcDecode will apply the 2/sigma² scaling.
        double ssq = 0;
        foreach (var v in sum) ssq += v * v;
        double rms = Math.Sqrt(ssq / sum.Length);
        if (rms < 1e-10) return null;
        double inv = 1.0 / rms;
        for (int i = 0; i < sum.Length; i++) sum[i] *= inv;
        return sum;
    }

    // ── Frequency shift to baseband ───────────────────────────────────────────

    private static Complex[]? FrequencyShift(Complex[] analytic, double f0, int dtSamples)
    {
        if (dtSamples < 0 || dtSamples + SampPerFrame > analytic.Length) return null;

        var bb = new Complex[SampPerFrame];

        // MSK144 I/Q demodulation requires the signal centred at DC (±500 Hz tones).
        // f0 is the low tone; the centre frequency is f0 + 500 Hz (mirrors MSHV nrxfreq=1500).
        double dphi    = -2 * Math.PI * (f0 + 500.0) / SampleRate;
        double cosP    = Math.Cos(dphi * dtSamples);
        double sinP    = Math.Sin(dphi * dtSamples);
        double cosDphi = Math.Cos(dphi);
        double sinDphi = Math.Sin(dphi);

        for (int i = 0; i < SampPerFrame; i++)
        {
            var s = analytic[dtSamples + i];
            bb[i] = new Complex(
                s.Real * cosP - s.Imaginary * sinP,
                s.Real * sinP + s.Imaginary * cosP);

            double newCos = cosP * cosDphi - sinP * sinDphi;
            double newSin = sinP * cosDphi + cosP * sinDphi;
            cosP = newCos;
            sinP = newSin;
        }
        return bb;
    }

    // ── In-place phase rotation ───────────────────────────────────────────────

    private static void ApplyPhaseRotation(Complex[] bb, double phase)
    {
        double cosP = Math.Cos(phase);
        double sinP = Math.Sin(phase);
        for (int i = 0; i < bb.Length; i++)
        {
            double re = bb[i].Real * cosP - bb[i].Imaginary * sinP;
            double im = bb[i].Real * sinP + bb[i].Imaginary * cosP;
            bb[i] = new Complex(re, im);
        }
    }

    // ── MSK I/Q matched-filter demodulation ──────────────────────────────────
    //
    // Mirrors MSHV's detectmsk144 soft-bit computation (decodermsk144.cpp):
    //   softbits[2*i]   = sum_{j=0..11} imag(c[i*12-6+j]) * pp[j]   (Q channel)
    //   softbits[2*i+1] = sum_{j=0..11} real(c[i*12+j])   * pp[j]   (I channel)
    //
    // Convention: positive softbit → bitseq = +1 → codeword bit 1.

    private static double[] MskDemodulate(Complex[] bb)
    {
        var sb = new double[FrameBits];

        // Bit 0 (Q, wrap-around with end of frame)
        for (int j = 0; j < 6; j++)
        {
            sb[0] += bb[j].Imaginary                    * Pp[j + 6];
            sb[0] += bb[SampPerFrame - 6 + j].Imaginary * Pp[j];
        }

        // Bit 1 (I, first 12 samples)
        for (int j = 0; j < MfLen; j++)
            sb[1] += bb[j].Real * Pp[j];

        // Bits 2..143
        for (int i = 1; i < 72; i++)
        {
            double sumQ = 0.0, sumI = 0.0;
            int startQ = i * MfLen - 6;
            int startI = i * MfLen;
            for (int j = 0; j < MfLen; j++)
            {
                sumQ += bb[startQ + j].Imaginary * Pp[j];
                sumI += bb[startI + j].Real      * Pp[j];
            }
            sb[2 * i]     = sumQ;
            sb[2 * i + 1] = sumI;
        }

        return sb;
    }

    // ── Soft-bit normalisation ────────────────────────────────────────────────

    private static void NormaliseSoftBits(double[] sb)
    {
        double sav = 0, s2av = 0;
        for (int i = 0; i < sb.Length; i++) { sav += sb[i]; s2av += sb[i] * sb[i]; }
        sav  /= sb.Length;
        s2av /= sb.Length;
        double ssig = Math.Sqrt(Math.Max(s2av - sav * sav, 1e-10));
        for (int i = 0; i < sb.Length; i++) sb[i] /= ssig;
    }

    // ── Sync check (at most 1 bad bit out of 8 at either sync position) ────────
    // MSHV accepts nbadsync ≤ 1. False-positive rate ≈ 3.5% per check position.

    private static bool SyncOk(double[] sb, int[] sync)
    {
        static int BadBits(double[] sb, int offset, int[] sync)
        {
            int bad = 0;
            for (int k = 0; k < sync.Length; k++)
                if ((sb[offset + k] >= 0) != (sync[k] == 1)) bad++;
            return bad;
        }

        return BadBits(sb, 0, sync) <= 1 || BadBits(sb, 56, sync) <= 1;
    }

    // ── LDPC decode ───────────────────────────────────────────────────────────

    private static bool TryLdpcDecode(double[] dataBits, bool[] msg)
    {
        if (dataBits.Length < CodeLen) return false;

        var llr = new double[CodeLen];
        // MSHV: llr[i] = 2*softbit[i]/sigma²
        // Positive softbit → NRZ +1 → bit 1; Ldpc128_90 also treats positive LLR as bit 1.
        double scale = 2.0 / (Sigma * Sigma);
        for (int i = 0; i < CodeLen; i++) llr[i] = dataBits[i] * scale;

        var decoded90 = new bool[InfoBits];
        bool ok = Ldpc128_90.TryDecode(llr, decoded90, out _);
        if (ok) Array.Copy(decoded90, msg, Math.Min(msg.Length, decoded90.Length));
        return ok;
    }

    // ── Message unpacking ─────────────────────────────────────────────────────

    private static string UnpackMsk144(bool[] msg)
    {
        bool[] msg77 = new bool[77];
        Array.Copy(msg, msg77, Math.Min(77, msg.Length));
        var packer = new Codecs.MessagePacker();
        string unpacked = packer.Unpack77(msg77, out bool success);
        return success && !string.IsNullOrWhiteSpace(unpacked) ? unpacked : string.Empty;
    }
}
