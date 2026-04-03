using System.Numerics;
using HamDigiSharp.Codecs;
using HamDigiSharp.Dsp;
using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Jt65;

/// <summary>
/// JT65 decoder (sub-modes A, B, C) — 60-second period, 65-FSK, RS(63,12).
/// C# port of MSHV's JT65 decoder (LZ2HV) and WSJT-X JT65 (K1JT), GPL.
///
/// Protocol:
///   - RS(63,12) over GF(64): 12 data symbols, 51 check symbols, 63 total
///   - 65-FSK: 64 data tones + 1 sync tone, tone spacing varies per submode
///   - 126 symbols per frame (63 data symbols interleaved with 63 sync)
///   - Period: 60 seconds at 11025 Hz native sample rate
///   - JT65A: Δf = 2.6917 Hz, JT65B: 2× spacing, JT65C: 4× spacing
/// </summary>
public sealed class Jt65Decoder : BaseDecoder
{
    // ── Protocol constants ────────────────────────────────────────────────────
    private const int SampleRate = 11025;
    private const int NSymbols   = 126;    // total symbols in frame
    private const int NData      = 63;     // RS codeword length
    private const int NTones     = 64;     // data tone values (0..63); sync tone is separate
    private const int Nsps       = 4096;   // samples per symbol at 11025 Hz (from WSJT)

    // Actual NSPS at 11025 for JT65: 4096 samples ≈ 0.372 s per symbol
    // JT65A tone spacing: 2.6917 Hz = 11025/4096 ≈ 2.691 Hz
    private const double ToneSpacingA = 11025.0 / 4096; // ≈ 2.6917 Hz

    // Pseudo-random sync sequence (126 bits, 1 = sync tone)
    private static readonly int[] SyncSeq = {
        1,0,0,1,1,0,0,0,1,1,1,1,1,1,0,1,0,1,0,0,
        0,1,0,1,1,0,0,1,0,0,0,1,1,1,0,0,1,1,1,1,
        0,1,1,0,1,1,1,1,0,0,0,1,1,0,1,0,1,0,1,1,
        0,0,1,1,0,1,0,1,0,1,0,0,1,0,0,0,0,0,0,1,
        1,0,0,0,0,0,0,0,1,1,0,1,0,0,1,0,1,1,0,1,
        0,1,0,1,0,0,1,1,0,0,1,0,0,1,0,0,0,0,1,1,
        1,1,1,1,1,1
    };

    private readonly DigitalMode _mode;

    public Jt65Decoder(DigitalMode mode = DigitalMode.JT65A)
    {
        _mode = mode;
    }

    public override DigitalMode Mode => _mode;

    // ── Decode entry point ────────────────────────────────────────────────────

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        if (samples.Length < SampleRate * 30) return Array.Empty<DecodeResult>();

        double toneSpacing = _mode switch
        {
            DigitalMode.JT65B => ToneSpacingA * 2,
            DigitalMode.JT65C => ToneSpacingA * 4,
            _                 => ToneSpacingA,
        };

        double[] dd = PrepareBuffer(samples);
        var candidates = FindCandidates(dd, freqLow, freqHigh, toneSpacing);
        if (candidates.Count == 0) return Array.Empty<DecodeResult>();

        var results = new List<DecodeResult>();
        var decoded = new HashSet<string>();

        foreach (var (freq, dt) in candidates)
        {
            if (TryDecodeJt65(dd, freq, dt, toneSpacing, utcTime, out var result))
            {
                if (decoded.Add(result!.Message))
                {
                    results.Add(result!);
                    Emit(result!);
                }
            }
        }
        return results;
    }

    // ── Buffer preparation ────────────────────────────────────────────────────

    private static double[] PrepareBuffer(ReadOnlySpan<float> samples)
    {
        int nMax = SampleRate * 60;
        var dd = new double[nMax];
        int copyLen = Math.Min(samples.Length, nMax);
        for (int i = 0; i < copyLen; i++) dd[i] = samples[i];
        return dd;
    }

    // ── Candidate search ──────────────────────────────────────────────────────

    private List<(double freq, double dt)> FindCandidates(
        double[] dd, double freqLow, double freqHigh, double toneSpacing)
    {
        // Build power spectrum via short FFTs at each symbol step
        const int nfft = 2 * Nsps;
        double df = (double)SampleRate / nfft;
        int nhsym = Math.Max(1, dd.Length / Nsps - 2);

        var window = Windowing.Nuttall(nfft);
        double[] savg = new double[nfft / 2];
        var cbuf = new Complex[nfft];

        for (int j = 0; j < nhsym; j++)
        {
            int start = j * Nsps;
            for (int k = 0; k < nfft; k++)
            {
                int idx = start + k;
                double v = idx < dd.Length ? dd[idx] : 0.0;
                cbuf[k] = new Complex(v * window[k], 0.0);
            }
            Fft.ForwardInPlace(cbuf);
            for (int k = 0; k < nfft / 2; k++)
                savg[k] += cbuf[k].Real * cbuf[k].Real + cbuf[k].Imaginary * cbuf[k].Imaginary;
        }

        int nfa = Math.Max(0, (int)(freqLow  / df));
        int nfb = Math.Min(nfft / 2 - 1, (int)(freqHigh / df));

        var cands = new List<(double, double)>();
        if (nfb <= nfa) return cands;

        double mean = savg.Skip(nfa).Take(nfb - nfa).Average();
        double threshold = mean * 2.0;

        for (int i = nfa + 1; i < nfb - 1; i++)
        {
            if (savg[i] > threshold && savg[i] >= savg[i - 1] && savg[i] >= savg[i + 1])
                cands.Add((i * df, 0.0));
        }
        return cands.Take(100).ToList();
    }

    // ── Per-candidate decode ──────────────────────────────────────────────────

    private bool TryDecodeJt65(
        double[] dd, double f0, double dt, double toneSpacing,
        string utcTime, out DecodeResult? result)
    {
        result = null;

        // Compute symbol power spectra: s3[sym, tone 0..63]
        // (f0 = sync tone frequency; data tones start at tone-index 2)
        var s3 = ComputeSymbolSpectra(dd, f0, dt, toneSpacing);
        if (s3 is null) return false;

        // Extract argmax tone for each symbol position (gives gray+interleaved RS symbol)
        var (mrsym, mrprob) = ExtractSoftSymbols(s3);

        // Remove Gray code: convert Gray-coded values back to binary
        for (int i = 0; i < NData; i++)
            mrsym[i] = GrayDecode(mrsym[i]);

        // Remove interleaving (inverse of 7×9 → 9×7 matrix transpose)
        mrsym = Deinterleave63(mrsym);

        // Pass to RS decoder. Our RS codec uses systematic form: data at [0..11], parity at [12..62].
        var rxdat = new int[Nn];
        Array.Copy(mrsym, rxdat, Nn);

        int nerr = ReedSolomon63.Decode(rxdat, Array.Empty<int>(), 0, false);
        if (nerr < 0) return false;

        // Unpack 12 RS data symbols (72 bits) → JT65 message text
        string message = UnpackJt65(rxdat);
        if (string.IsNullOrWhiteSpace(message)) return false;

        result = new DecodeResult
        {
            UtcTime = utcTime,
            Snr = EstimateSnrFromPower(s3, mrsym),
            Dt = dt,
            FrequencyHz = f0,
            Message = message.Trim(),
            Mode = _mode,
            HardErrors = nerr,
        };
        return true;
    }

    // ── Symbol spectra (65-FSK at tone-spacing Hz) ────────────────────────────
    // Sync tone is at f0; data tones for RS symbol t (0..63) are at f0 + (t+2)*toneSpacing.

    private double[,]? ComputeSymbolSpectra(double[] dd, double f0, double dt, double toneSpacing)
    {
        int dtSamples  = (int)(dt * SampleRate);
        double df      = (double)SampleRate / (2 * Nsps);  // ≈ 1.346 Hz/bin for JT65A
        int fBin0      = (int)Math.Round(f0 / df);         // bin for sync tone
        int binsPerTone = (int)Math.Round(toneSpacing / df); // = 2 for JT65A

        var s3     = new double[NData, NTones];  // NTones = 64 data tones
        var cbuf   = new Complex[2 * Nsps];
        var window = Windowing.Nuttall(2 * Nsps);

        int dataSymIdx = 0;
        for (int sym = 0; sym < NSymbols; sym++)
        {
            if (SyncSeq[sym] == 1) continue; // skip sync positions
            if (dataSymIdx >= NData) break;

            int start = dtSamples + sym * Nsps;
            for (int k = 0; k < 2 * Nsps; k++)
            {
                int idx = start + k;
                double v = (idx >= 0 && idx < dd.Length) ? dd[idx] : 0.0;
                cbuf[k] = new Complex(v * window[k], 0.0);
            }
            Fft.ForwardInPlace(cbuf);

            // Data tones: tone value t ∈ 0..63, at bin fBin0 + (t+2)*binsPerTone
            for (int t = 0; t < NTones; t++)
            {
                int bin = fBin0 + (t + 2) * binsPerTone;
                if (bin >= 0 && bin < cbuf.Length)
                    s3[dataSymIdx, t] = cbuf[bin].Real * cbuf[bin].Real
                                      + cbuf[bin].Imaginary * cbuf[bin].Imaginary;
            }
            dataSymIdx++;
        }
        return s3;
    }

    // ── Soft symbol extraction ────────────────────────────────────────────────

    private static (int[] mrsym, int[] mrprob) ExtractSoftSymbols(double[,] s3)
    {
        var mrsym  = new int[NData];
        var mrprob = new int[NData];

        for (int sym = 0; sym < NData; sym++)
        {
            double best = double.MinValue, second = double.MinValue;
            int bestTone = 0;
            for (int t = 0; t < NTones; t++) // all 64 data tone values
            {
                double v = s3[sym, t];
                if (v > best)  { second = best; best = v; bestTone = t; }
                else if (v > second) second = v;
            }
            mrsym[sym]  = bestTone;
            mrprob[sym] = (int)Math.Clamp((best - second) / (best + 1e-10) * 255, 0, 255);
        }
        return (mrsym, mrprob);
    }

    // ── Inverse Gray code (igray with idir<0 from WSJT-X igray.c) ────────────

    private static int GrayDecode(int n)
    {
        int result = n;
        while (n > 0)
        {
            n >>= 1;
            result ^= n;
        }
        return result;
    }

    // ── Deinterleave63 (inverse of 7×9 → 9×7 matrix transpose) ──────────────
    // WSJT-X interleave63: idir=+1 transposes d1[7][9] → d2[9][7] (= interleave).
    // Inverse: view as d1[9][7], transpose → d2[7][9].

    internal static int[] Deinterleave63(int[] d1)
    {
        var d2 = new int[63];
        for (int r = 0; r < 9; r++)
            for (int c = 0; c < 7; c++)
                d2[c * 9 + r] = d1[r * 7 + c];
        return d2;
    }

    // ── Interleave63 (encode direction: 7×9 → 9×7 matrix transpose) ──────────

    internal static int[] Interleave63(int[] d1)
    {
        var d2 = new int[63];
        for (int r = 0; r < 7; r++)
            for (int c = 0; c < 9; c++)
                d2[c * 7 + r] = d1[r * 9 + c];
        return d2;
    }

    private static double EstimateSnrFromPower(double[,] s3, int[] mrsym)
    {
        double sigPow = 0, noisePow = 0;
        for (int sym = 0; sym < NData; sym++)
        {
            sigPow += s3[sym, mrsym[sym]];
            for (int t = 0; t < NTones; t++)
                if (t != mrsym[sym]) noisePow += s3[sym, t];
        }
        noisePow /= Math.Max(NData * (NTones - 1), 1);
        return sigPow > 0 ? 10 * Math.Log10(sigPow / Math.Max(noisePow, 1e-10)) : -30;
    }

    // ── JT65 message unpacking (72 bits → text) ──────────────────────────────
    // Implements WSJT-X unpackmsg (packjt.f90): decodes nc1/nc2/ng from
    // 12 six-bit RS data symbols, then unpacks callsigns and grid/report.

    private static string UnpackJt65(int[] rxdat)
    {
        // Extract nc1 (28 bits), nc2 (28 bits), ng (16 bits) from 12 × 6-bit symbols
        int nc1 = (rxdat[0] << 22) | (rxdat[1] << 16) | (rxdat[2] << 10)
                | (rxdat[3] << 4) | ((rxdat[4] >> 2) & 0xF);
        int nc2 = ((rxdat[4] & 0x3) << 26) | (rxdat[5] << 20) | (rxdat[6] << 14)
                | (rxdat[7] << 8) | (rxdat[8] << 2) | ((rxdat[9] >> 4) & 0x3);
        int ng  = ((rxdat[9] & 0xF) << 12) | (rxdat[10] << 6) | rxdat[11];

        // ng >= 32768 → free-text message
        if (ng >= 32768)
        {
            // Recover 3-part nc3 from top bits moved to LSBs of nc1/nc2
            int nc3 = ng & 0x7FFF;
            if ((nc1 & 1) != 0) nc3 |= 0x8000;
            nc1 >>= 1;
            if ((nc2 & 1) != 0) nc3 |= 0x10000;
            nc2 >>= 1;

            // Decode 13 chars: chars 0-4 from nc1, 5-9 from nc2, 10-12 from nc3
            const string c = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ +-./?@";
            var msg = new char[13];
            for (int i = 4; i >= 0; i--) { msg[i] = c[nc1 % 42]; nc1 /= 42; }
            for (int i = 9; i >= 5; i--) { msg[i] = c[nc2 % 42]; nc2 /= 42; }
            for (int i = 12; i >= 10; i--) { msg[i] = c[nc3 % 42]; nc3 /= 42; }
            return new string(msg).TrimEnd();
        }

        // Try JT65v2 compound callsign (prefix/suffix messages) from nc1
        string call2 = UnpackCall(nc2);
        string grid  = UnpackGrid(ng);
        int iv2 = UnpackCallV2(nc1, out string psfx);
        if (iv2 > 0)
            return BuildV2Message(iv2, psfx, call2, grid);

        // Standard message: unpack nc1 → call1, nc2 → call2, ng → grid/report
        string call1 = UnpackCall(nc1);

        if (string.IsNullOrWhiteSpace(call1) && string.IsNullOrWhiteSpace(call2))
            return grid;
        if (string.IsNullOrWhiteSpace(grid))
            return $"{call1} {call2}".Trim();
        return $"{call1} {call2} {grid}".Trim();
    }

    /// <summary>
    /// Tries to decode nc1 as a JT65v2 compound-callsign type-indicator.
    /// Returns iv2 (1–7) and the prefix/suffix string if matched; 0 if standard call.
    /// Mirrors WSJT-X <c>unpackcall</c> (packjt.f90, map65 variant).
    /// </summary>
    internal static int UnpackCallV2(int ncall, out string psfx)
    {
        psfx = string.Empty;
        const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ ";

        // Ranges hard-coded from WSJT-X packjt.f90 / map65/libm65/packjt.f90
        // 4-char prefix variants (CQ/QRZ/DE + PREFIX/call2)
        if (ncall >= 262_178_563 && ncall <= 264_002_071)
        {
            psfx = Decode4CharPsfx(ncall - 262_178_563, Alphabet);
            return 1; // CQ {prefix}/{call2}
        }
        if (ncall >= 264_002_072 && ncall <= 265_825_580)
        {
            psfx = Decode4CharPsfx(ncall - 264_002_072, Alphabet);
            return 2; // QRZ {prefix}/{call2}
        }
        if (ncall >= 265_825_581 && ncall <= 267_649_089)
        {
            psfx = Decode4CharPsfx(ncall - 265_825_581, Alphabet);
            return 3; // DE {prefix}/{call2}
        }

        // 3-char suffix variants (CQ/QRZ/DE + call2/SUFFIX)
        if (ncall >= 267_649_090 && ncall <= 267_698_374)
        {
            psfx = Decode3CharPsfx(ncall - 267_649_090, Alphabet);
            return 4; // CQ {call2}/{suffix}
        }
        if (ncall >= 267_698_375 && ncall <= 267_747_659)
        {
            psfx = Decode3CharPsfx(ncall - 267_698_375, Alphabet);
            return 5; // QRZ {call2}/{suffix}
        }
        if (ncall >= 267_747_660 && ncall <= 267_796_944)
        {
            psfx = Decode3CharPsfx(ncall - 267_747_660, Alphabet);
            return 6; // DE {call2}/{suffix}
        }
        if (ncall == 267_796_945)
            return 7; // DE {call2} (standalone — no grid)

        return 0;
    }

    internal static string Decode4CharPsfx(int n, string c)
    {
        // Build chars right-to-left (same as Fortran mod37 chain)
        char c3 = c[n % 37]; n /= 37;
        char c2 = c[n % 37]; n /= 37;
        char c1 = c[n % 37]; n /= 37;
        char c0 = c[n];
        return new string(new[] { c0, c1, c2, c3 }).Trim();
    }

    private static string Decode3CharPsfx(int n, string c)
    {
        char c2 = c[n % 37]; n /= 37;
        char c1 = c[n % 37]; n /= 37;
        char c0 = c[n];
        return new string(new[] { c0, c1, c2 }).Trim();
    }

    private static string BuildV2Message(int iv2, string psfx, string call2, string grid)
    {
        string g = string.IsNullOrWhiteSpace(grid) ? string.Empty : " " + grid;
        return iv2 switch
        {
            1 => $"CQ {psfx}/{call2}{g}".Trim(),
            2 => $"QRZ {psfx}/{call2}{g}".Trim(),
            3 => $"DE {psfx}/{call2}{g}".Trim(),
            4 => $"CQ {call2}/{psfx}{g}".Trim(),
            5 => $"QRZ {call2}/{psfx}{g}".Trim(),
            6 => $"DE {call2}/{psfx}{g}".Trim(),
            7 => $"DE {call2}{g}".Trim(),
            _ => string.Empty,
        };
    }

    // Unpack a 28-bit callsign code (WSJT-X unpackcall, simplified)
    private static string UnpackCall(int ncall)
    {
        const int NBASE = 37 * 36 * 10 * 27 * 27 * 27; // = 262177560
        const string c37 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ ";

        if (ncall >= 267796945) return "DE";
        if (ncall >= NBASE)
        {
            if (ncall == NBASE + 1) return "CQ";
            if (ncall == NBASE + 2) return "QRZ";
            int nfreq = ncall - NBASE - 3;
            if (nfreq >= 0 && nfreq <= 999) return $"CQ {nfreq:D3}";
            return string.Empty; // JT65v2 range — caller should use UnpackCallV2 for nc1
        }

        int n = ncall;
        char[] tmp = new char[6];
        tmp[5] = c37[n % 27 + 10]; n /= 27;
        tmp[4] = c37[n % 27 + 10]; n /= 27;
        tmp[3] = c37[n % 27 + 10]; n /= 27;
        tmp[2] = c37[n % 10];       n /= 10;
        tmp[1] = c37[n % 36];       n /= 36;
        tmp[0] = c37[n];

        // Strip leading spaces
        int start = 0;
        while (start < 5 && tmp[start] == ' ') start++;
        return new string(tmp, start, 6 - start).TrimEnd();
    }

    // Unpack a 15-bit grid code (WSJT-X unpackgrid, simplified)
    private static string UnpackGrid(int ng)
    {
        const int NGBASE = 180 * 180; // = 32400
        if (ng >= NGBASE)
        {
            int n = ng - NGBASE - 1;
            if (n >= 1  && n <= 30) return $"-{n:D2}";
            if (n >= 31 && n <= 60) return $"R-{n - 30:D2}";
            if (n == 61) return "RO";
            if (n == 62) return "RRR";
            if (n == 63) return "73";
            return "";
        }
        // Maidenhead grid: lat = ng % 180 − 90, lon = (ng/180) * 2 − 180
        int lat = (ng % 180) - 90;
        int lon = (ng / 180) * 2 - 180;
        char a1 = (char)('A' + (lon + 180) / 20);
        char a2 = (char)('A' + (lat + 90)  / 10);
        char n1 = (char)('0' + ((lon + 180) % 20) / 2);
        char n2 = (char)('0' + (lat + 90) % 10);
        return $"{a1}{a2}{n1}{n2}";
    }

    private const int Nn = 63;
}
