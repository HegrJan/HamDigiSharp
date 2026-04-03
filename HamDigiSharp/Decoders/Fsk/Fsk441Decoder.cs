using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Fsk;

/// <summary>
/// FSK441 decoder — 4-FSK meteor scatter mode, 15-second period.
/// Ported from MSHV's FSK441 decoder (LZ2HV / K1JT), GPL.
///
/// Protocol:
///   - 4-FSK: four tones at f, f+441, f+882, f+1323 Hz
///   - Symbol rate: 441 baud (11025/25 = 441)
///   - 3 symbols per character (6 bits: 4^3 = 64 → 48 valid chars)
///   - Mod-3 phase sync: first symbol of each char never uses tone-3
///   - Sample rate: 11025 Hz
/// </summary>
public sealed class Fsk441Decoder : FskBaseDecoder
{
    public Fsk441Decoder() : base(25, 441.0, DigitalMode.FSK441) { }
}

/// <summary>
/// Shared FSK meteor scatter base decoder (FSK441 and FSK315).
/// Two-pass decode: short-window spectrum scan to find signal regions, then
/// per-region coherent demodulation with ping extraction and mod-3 quality gate.
/// </summary>
public abstract class FskBaseDecoder : BaseDecoder
{
    // Character table from MSHV's config_msg_all.h: c_FSK441_RX[48]
    private static readonly char[] ValidChars =
    {
        ' ','1','2','3','4','5','6','7','8','9','.',',','?','/',
        '#',' ','$','A','B','C','D',' ','F','G','H','I','J','K','L','M','N','O',
        'P','Q','R','S','T','U','V','W','X','Y',' ','0','E','Z','*','!'
    };

    private const int    SampleRate         = 11025;
    private const int    FftLen             = 512;   // 46.4 ms per sub-window
    private const int    MaxMsg             = 46;    // max chars per MSHV spec

    // Short-window scan: 250 ms windows stepped every 100 ms.
    // Short windows concentrate burst energy, avoiding the dilution of 15 s averaging.
    private const int    ShortWinLen        = SampleRate / 4;   // 2756 samples
    private const int    ShortWinStep       = SampleRate / 10;  // 1103 samples

    // Quality thresholds
    private const double ShortWinSnrThresh  = 3.0;  // min spectrum SNR in a short window
    private const double Mod3QualThreshold  = 0.30; // max n4min/mean(n4) for valid sync

    private readonly int         _nsps;
    private readonly double      _toneStep;
    private readonly DigitalMode _mode;

    protected FskBaseDecoder(int nsps, double toneStep, DigitalMode mode)
    {
        _nsps = nsps; _toneStep = toneStep; _mode = mode;
    }

    public override DigitalMode Mode => _mode;

    // ── Decode entry point ────────────────────────────────────────────────────

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        if (samples.Length < _nsps * 30) return Array.Empty<DecodeResult>();

        int nMax = Math.Min(samples.Length, SampleRate * 15);
        double[] dd = new double[nMax];
        for (int i = 0; i < nMax; i++) dd[i] = samples[i];

        int    baud     = SampleRate / _nsps;
        int    lTone    = _mode == DigitalMode.FSK315 ? 3 : 2;
        double df       = SampleRate / (double)FftLen;
        int    scanStep = Math.Max(4, (int)(df * 2)); // ≈ 2 spectrum bins
        double scanHalf = Math.Min(_toneStep, 300.0); // search ±300 Hz from nominal

        // ── Pass 1: scan short windows to locate signal regions ───────────────
        // Each short window gives a per-window SNR; if a ping (burst) falls within
        // a short window its spectrum SNR is much higher than the time-averaged SNR.
        //
        // The scan maps each passing short window to the 1-second decode windows
        // that overlap it, recording the best frequency offset per decode window.

        var candidates = new Dictionary<int, (double dfx, double snr)>();

        int shortLen = Math.Min(ShortWinLen, nMax);
        for (int sw = 0; sw + shortLen <= nMax; sw += ShortWinStep)
        {
            double[] ps       = AveragePowerSpectrum(dd, sw, shortLen);
            double   baseline = SpectrumBaseline(ps, freqLow, freqHigh, df);
            if (baseline <= 0) baseline = 1e-10;

            double bestSnr = 0, bestDfx = 0;
            for (int dfx = -(int)scanHalf; dfx <= (int)scanHalf; dfx += scanStep)
            {
                double f1 = lTone * baud + dfx;
                double f4 = (lTone + 3) * baud + dfx;
                if (f1 < freqLow || f4 > freqHigh) continue;

                double tonePower = 0;
                for (int t = 0; t < 4; t++)
                {
                    int bin = (int)Math.Round(((lTone + t) * baud + dfx) / df);
                    if ((uint)bin < (uint)(FftLen / 2)) tonePower += ps[bin];
                }
                double snr = tonePower / (4 * baseline);
                if (snr > bestSnr) { bestSnr = snr; bestDfx = dfx; }
            }

            if (bestSnr < ShortWinSnrThresh) continue;

            // Register all 1-second windows that overlap this short window
            for (int ws = sw - SampleRate; ws <= sw; ws += SampleRate / 2)
            {
                if (ws < 0 || ws >= nMax) continue;
                if (!candidates.TryGetValue(ws, out var cur) || cur.snr < bestSnr)
                    candidates[ws] = (bestDfx, bestSnr);
            }
        }

        // If no short window passed, nothing to decode
        if (candidates.Count == 0) return Array.Empty<DecodeResult>();

        // ── Pass 2: decode each candidate 1-second window ─────────────────────
        var results = new List<DecodeResult>();
        var decoded = new HashSet<string>();

        foreach (var (windowStart, (bestDfx, _)) in candidates)
        {
            int windowLen = Math.Min(SampleRate, nMax - windowStart);
            if (windowLen < _nsps * 30) continue;

            // Try the best frequency offset and one step either side
            for (int delta = -scanStep; delta <= scanStep; delta += Math.Max(1, scanStep))
            {
                double dfx = bestDfx + delta;
                double f1  = lTone * baud + dfx;
                double f2  = (lTone + 1) * baud + dfx;
                double f3  = (lTone + 2) * baud + dfx;
                double f4  = (lTone + 3) * baud + dfx;
                if (f1 < freqLow || f4 > freqHigh) continue;

                double[] y1 = new double[windowLen];
                double[] y2 = new double[windowLen];
                double[] y3 = new double[windowLen];
                double[] y4 = new double[windowLen];

                Detect(dd, windowStart, windowLen, f1, y1);
                Detect(dd, windowStart, windowLen, f2, y2);
                Detect(dd, windowStart, windowLen, f3, y3);
                Detect(dd, windowStart, windowLen, f4, y4);

                // Extract the highest-energy region (the ping / burst).
                // Without this, noise symbols before and after the ping produce
                // garbage characters that swamp the real message.
                var (pingOff, pingLen) = FindPingWindow(y1, y2, y3, y4, windowLen);
                if (pingLen < _nsps * 12) continue; // too few symbols to decode

                double[] py1 = y1[pingOff..(pingOff + pingLen)];
                double[] py2 = y2[pingOff..(pingOff + pingLen)];
                double[] py3 = y3[pingOff..(pingOff + pingLen)];
                double[] py4 = y4[pingOff..(pingOff + pingLen)];

                int     jpk = FindSync(py1, py2, py3, py4, pingLen);
                string? msg = Decode3Tone(py1, py2, py3, py4, jpk, pingLen,
                                          out double syncQuality);

                if (msg != null && msg.Length >= 3 && msg.Any(char.IsLetter)
                    && syncQuality < Mod3QualThreshold)
                {
                    if (decoded.Add(msg))
                    {
                        var r = new DecodeResult
                        {
                            UtcTime     = utcTime,
                            Snr         = EstimateSnr(0, double.NaN),
                            Dt          = (windowStart + pingOff + jpk) / (double)SampleRate,
                            FrequencyHz = f1,
                            Message     = msg,
                            Mode        = _mode,
                        };
                        results.Add(r);
                        Emit(r);
                    }
                }
            }
        }
        return results;
    }

    // ── Average power spectrum over [start, start+npts) ──────────────────────

    private static double[] AveragePowerSpectrum(double[] dd, int start, int npts)
    {
        var spec  = new double[FftLen / 2];
        var buf   = new System.Numerics.Complex[FftLen];
        int count = 0;

        for (int s = start; s + FftLen <= start + npts; s += FftLen, count++)
        {
            for (int i = 0; i < FftLen; i++)
                buf[i] = new System.Numerics.Complex(dd[s + i], 0);
            MathNet.Numerics.IntegralTransforms.Fourier.Forward(buf,
                MathNet.Numerics.IntegralTransforms.FourierOptions.AsymmetricScaling);
            for (int i = 0; i < FftLen / 2; i++)
                spec[i] += buf[i].Real * buf[i].Real + buf[i].Imaginary * buf[i].Imaginary;
        }

        if (count > 0)
            for (int i = 0; i < spec.Length; i++) spec[i] /= count;
        else
        {
            // Buffer too short for FftLen — compute a single DFT on the whole segment
            int n = npts;
            for (int i = 0; i < Math.Min(n, FftLen); i++)
                buf[i] = new System.Numerics.Complex(dd[start + i], 0);
            for (int i = n; i < FftLen; i++) buf[i] = default;
            MathNet.Numerics.IntegralTransforms.Fourier.Forward(buf,
                MathNet.Numerics.IntegralTransforms.FourierOptions.AsymmetricScaling);
            for (int i = 0; i < FftLen / 2; i++)
                spec[i] = buf[i].Real * buf[i].Real + buf[i].Imaginary * buf[i].Imaginary;
        }

        return spec;
    }

    // ── Median power in the search band (noise floor estimate) ───────────────

    private static double SpectrumBaseline(double[] ps, double freqLow, double freqHigh, double df)
    {
        int ia = Math.Max(0, (int)(freqLow / df));
        int ib = Math.Min(ps.Length - 1, (int)(freqHigh / df));
        if (ia >= ib) return 1e-10;

        var vals = ps[ia..ib].OrderBy(x => x).ToArray();
        return vals.Length == 0 ? 1e-10 : vals[vals.Length / 2];
    }

    // ── Tone detection (sliding coherent correlator, one tone at a time) ─────

    private void Detect(double[] data, int start, int npts, double freq, double[] y)
    {
        double dpha = 2 * Math.PI * freq / SampleRate;
        var    c    = new System.Numerics.Complex[npts];
        for (int i = 0; i < npts; i++)
            c[i] = data[start + i] * new System.Numerics.Complex(
                Math.Cos(dpha * i), -Math.Sin(dpha * i));

        var csum = System.Numerics.Complex.Zero;
        for (int i = 0; i < Math.Min(_nsps, npts); i++) csum += c[i];
        y[0] = csum.Real * csum.Real + csum.Imaginary * csum.Imaginary;
        for (int i = 1; i < npts - _nsps; i++)
        {
            csum = csum - c[i - 1] + c[i + _nsps - 1];
            y[i] = csum.Real * csum.Real + csum.Imaginary * csum.Imaginary;
        }
    }

    // ── Ping / burst window extractor ─────────────────────────────────────────
    // Finds the contiguous region of highest tone-energy within the 1-second window.
    // For meteor scatter pings this selects just the burst; for continuous signals
    // it selects the region with most energy (typically the whole active area).
    // Returns sample offset and length within the window.

    private (int offset, int length) FindPingWindow(
        double[] y1, double[] y2, double[] y3, double[] y4, int npts)
    {
        int nSym = npts / _nsps;
        if (nSym < 12) return (0, npts);

        // Per-symbol energy = max of the four tone correlators
        var energy = new double[nSym];
        for (int i = 0; i < nSym; i++)
        {
            int idx = i * _nsps;
            energy[i] = Math.Max(Math.Max(y1[idx], y2[idx]), Math.Max(y3[idx], y4[idx]));
        }

        // Noise floor: 20th percentile of per-symbol energies
        var sorted = (double[])energy.Clone();
        Array.Sort(sorted);
        double noiseFloor = sorted[(int)(nSym * 0.20)];

        // Sliding window: find the scanSym-symbol window with the highest
        // cumulative above-noise energy.  scanSym = 200 symbols (~454 ms at 441 baud).
        int scanSym = Math.Min(200, nSym * 2 / 3);

        double bestSum    = double.MinValue;
        int    bestCenter = nSym / 2;
        double sum        = 0;

        for (int i = 0; i < Math.Min(scanSym, nSym); i++)
            sum += Math.Max(0.0, energy[i] - noiseFloor);
        if (sum > bestSum) { bestSum = sum; bestCenter = scanSym / 2; }

        for (int i = 1; i <= nSym - scanSym; i++)
        {
            sum -= Math.Max(0.0, energy[i - 1] - noiseFloor);
            sum += Math.Max(0.0, energy[i + scanSym - 1] - noiseFloor);
            if (sum > bestSum) { bestSum = sum; bestCenter = i + scanSym / 2; }
        }

        // Expand ± 30-symbol margin and convert back to samples
        int margin  = 30;
        int pStart  = Math.Max(0, (bestCenter - scanSym / 2 - margin) * _nsps);
        int pEnd    = Math.Min(npts, (bestCenter + scanSym / 2 + margin) * _nsps);
        return (pStart, pEnd - pStart);
    }

    // ── Symbol-phase sync: DFT of (max − 2nd-max) folded at baud rate ────────

    private int FindSync(double[] y1, double[] y2, double[] y3, double[] y4, int npts)
    {
        double[] zf = new double[_nsps];
        for (int i = 0; i < npts; i++)
        {
            double a1 = y1[i], a2 = y2[i], a3 = y3[i], a4 = y4[i];
            double best = Math.Max(Math.Max(a1, a2), Math.Max(a3, a4));
            double secnd = best == a1 ? Math.Max(Math.Max(a2, a3), a4)
                         : best == a2 ? Math.Max(Math.Max(a1, a3), a4)
                         : best == a3 ? Math.Max(Math.Max(a1, a2), a4)
                         :              Math.Max(Math.Max(a1, a2), a3);
            zf[i % _nsps] += 1e-6 * (best - secnd);
        }

        double sumR = 0, sumI = 0;
        for (int j = 0; j < _nsps; j++)
        {
            double pha = j * 2 * Math.PI / _nsps;
            sumR += zf[j] * Math.Cos(pha);
            sumI += zf[j] * Math.Sin(pha);
        }
        double phase = -Math.Atan2(sumI, sumR);
        int jpk = (int)(_nsps * phase / (2 * Math.PI));
        if (jpk < 0) jpk += _nsps - 1;
        return jpk;
    }

    // ── Character decode: 3 symbols per char (mod-3 phase sync + 6-bit table) ─

    private string? Decode3Tone(double[] y1, double[] y2, double[] y3, double[] y4,
        int jpk, int npts, out double syncQuality)
    {
        syncQuality = 1.0;
        int ndits = (npts - jpk) / _nsps;
        if (ndits < 4) return null;

        int[] dit = new int[ndits];
        for (int i = 0; i < ndits; i++)
        {
            int idx = jpk + i * _nsps;
            if (idx >= npts) break;
            double a1 = y1[idx], a2 = y2[idx], a3 = y3[idx], a4 = y4[idx];
            double best = Math.Max(Math.Max(a1, a2), Math.Max(a3, a4));
            if      (best == a1) dit[i] = 0;
            else if (best == a2) dit[i] = 1;
            else if (best == a3) dit[i] = 2;
            else                 dit[i] = 3;
        }

        // In FSK441, the first symbol of each character (mod-3 phase 0) never
        // uses tone-3 (all valid codes have n/16 < 3).  Find that phase.
        int[] n4 = new int[3];
        for (int i = 0; i < ndits; i++)
            if (dit[i] == 3) n4[i % 3]++;

        int n4min = Math.Min(n4[0], Math.Min(n4[1], n4[2]));
        int jsync = n4min == n4[0] ? 3 : n4min == n4[1] ? 1 : 2;

        // Mod-3 sync quality: n4min / mean(n4). Near 0 = good sync; ~1 = noise.
        // When mean4 = 0, no tone-3 occurred — valid FSK441 (all chars without high tones).
        double mean4 = (n4[0] + n4[1] + n4[2]) / 3.0;
        syncQuality  = mean4 > 0 ? n4min / mean4 : 0.0;

        int msglen = Math.Min(ndits / 3, MaxMsg);
        var sb     = new System.Text.StringBuilder();
        for (int i = 0; i < msglen; i++)
        {
            int j = i * 3 + jsync;
            if (j + 2 >= ndits) break;
            int nc = 16 * dit[j] + 4 * dit[j + 1] + dit[j + 2];
            if ((uint)nc < (uint)ValidChars.Length)
                sb.Append(ValidChars[nc]);
        }

        string s = sb.ToString().Trim();
        return s.Length >= 3 ? s : null;
    }
}
