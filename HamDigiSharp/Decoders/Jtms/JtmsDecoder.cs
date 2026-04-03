using System.Numerics;
using HamDigiSharp.Dsp;
using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Jtms;

/// <summary>
/// JTMS decoder — 2-FSK meteor scatter, 15-second period.
/// C# port of MSHV's JTMS decoder (LZ2HV / K1JT), GPL.
///
/// Protocol:
///   - 2-FSK: two tones at 1155.47 Hz (mark) and 1844.53 Hz (space)
///   - Character encoding: 6 bits + 1 parity bit = 7 bits
///   - Symbol rate: 11025/8 ≈ 1378 baud
///   - Characters: 6-bit ASCII-subset, valid chars " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"
///   - 15-second period at 11025 Hz
/// </summary>
public sealed class JtmsDecoder : BaseDecoder
{
    // ── Protocol constants ────────────────────────────────────────────────────
    private const int    SampleRate = 11025;
    private const int    Nsps       = 8;          // samples per symbol
    private const double FreqMark   = 1155.46875; // Hz (mark tone)
    private const double FreqSpace  = 1844.53125; // Hz (space tone)
    private const int    NMax       = SampleRate * 15;
    private const int    MaxChars   = 50;

    // Precomputed waveforms for 64 6-bit + parity characters
    private readonly Complex[][] _cw = new Complex[64][];

    public JtmsDecoder()
    {
        double twopi = 2 * Math.PI;
        double dt = 1.0 / SampleRate;
        double dphi0 = twopi * dt * FreqMark;
        double dphi1 = twopi * dt * FreqSpace;

        for (int i = 0; i < 64; i++)
        {
            _cw[i] = new Complex[7 * Nsps];
            int[] nb = new int[7];
            int k = 0, m = 0;
            for (int n = 5; n >= 0; n--)
            {
                nb[k] = (i >> n) & 1;
                m += nb[k];
                k++;
            }
            nb[k] = m & 1; // parity bit

            double phi = 0;
            int j = 0;
            for (int x = 0; x < 7; x++)
            {
                double dphi = nb[x] == 0 ? dphi0 : dphi1;
                for (int ii = 0; ii < Nsps; ii++)
                {
                    phi += dphi;
                    _cw[i][j++] = new Complex(Math.Cos(phi), Math.Sin(phi));
                }
            }
        }
    }

    public override DigitalMode Mode => DigitalMode.JTMS;

    // ── Decode entry point ────────────────────────────────────────────────────

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        if (samples.Length < Nsps * 14) return Array.Empty<DecodeResult>();

        double[] dd = PrepareBuffer(samples);

        // Compute power in mark and space tone channels
        double[] pMark  = new double[dd.Length];
        double[] pSpace = new double[dd.Length];
        DetectTone(dd, FreqMark,  pMark);
        DetectTone(dd, FreqSpace, pSpace);

        // Find sync / burst start
        var results = new List<DecodeResult>();
        var decoded = new HashSet<string>();

        for (int startPos = 0; startPos < dd.Length - 7 * Nsps * 4; startPos += Nsps)
        {
            string? msg = TryDecodeAt(pMark, pSpace, startPos, dd.Length);
            if (msg is null || msg.Length < 3) continue;
            if (!decoded.Add(msg)) continue;

            var result = new DecodeResult
            {
                UtcTime = utcTime,
                Snr = EstimateSnr(0, double.NaN),
                Dt = startPos / (double)SampleRate,
                FrequencyHz = (FreqMark + FreqSpace) / 2,
                Message = msg,
                Mode = DigitalMode.JTMS,
            };
            results.Add(result);
            Emit(result);
        }
        return results;
    }

    // ── Buffer preparation ────────────────────────────────────────────────────

    private static double[] PrepareBuffer(ReadOnlySpan<float> samples)
    {
        var dd = new double[NMax];
        int n = Math.Min(samples.Length, NMax);
        for (int i = 0; i < n; i++) dd[i] = samples[i];
        return dd;
    }

    // ── Tone power detection ──────────────────────────────────────────────────

    private static void DetectTone(double[] data, double freq, double[] power)
    {
        double dpha = 2 * Math.PI * freq / SampleRate;
        var csum = Complex.Zero;
        var c = new Complex[Nsps + 2];

        for (int i = 0; i < Math.Min(Nsps, data.Length); i++)
        {
            c[i] = data[i] * new Complex(Math.Cos(dpha * i), -Math.Sin(dpha * i));
            csum += c[i];
        }
        power[0] = csum.Real * csum.Real + csum.Imaginary * csum.Imaginary;

        for (int i = 1; i < data.Length - Nsps; i++)
        {
            var newC = data[i + Nsps - 1] * new Complex(Math.Cos(dpha * (i + Nsps - 1)), -Math.Sin(dpha * (i + Nsps - 1)));
            csum = csum - c[(i - 1) % Nsps] + newC;
            c[(i - 1) % Nsps] = newC;   // write into the slot we just read, not i%Nsps
            power[i] = csum.Real * csum.Real + csum.Imaginary * csum.Imaginary;
        }
    }

    // ── Decode at a specific position ─────────────────────────────────────────

    private static string? TryDecodeAt(double[] pMark, double[] pSpace, int start, int npts)
    {
        const string chars = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?@";
        var sb = new System.Text.StringBuilder();
        int pos = start;

        while (pos + 7 * Nsps < npts && sb.Length < MaxChars)
        {
            // Decode 7 bits (6 data + 1 parity)
            int charIdx = 0;
            int parityBit = 0;
            bool valid = true;

            for (int bit = 5; bit >= 0; bit--)
            {
                int bp = pos + (5 - bit) * Nsps;
                if (bp >= npts) { valid = false; break; }

                int b = pMark[bp] >= pSpace[bp] ? 0 : 1;
                charIdx |= b << bit;
                parityBit ^= b;
            }

            // Check parity bit
            int parPos = pos + 6 * Nsps;
            if (valid && parPos < npts)
            {
                int parityReceived = pMark[parPos] >= pSpace[parPos] ? 0 : 1;
                if (parityBit != parityReceived) break; // parity error → end of burst
            }

            if (!valid) break;
            if (charIdx < chars.Length) sb.Append(chars[charIdx]);
            pos += 7 * Nsps;
        }

        return sb.Length >= 3 ? sb.ToString().TrimEnd() : null;
    }
}
