using HamDigiSharp.Abstractions;
using HamDigiSharp.Codecs;
using HamDigiSharp.Decoders.SuperFox;
using HamDigiSharp.Models;
using MathNet.Numerics;

namespace HamDigiSharp.Encoders;

/// <summary>
/// SuperFox Fox-transmitter encoder.
/// Encodes a compound message into 180,000 float PCM samples at 12 kHz (15 s).
///
/// Message formats:
/// <list type="bullet">
///   <item><description>CQ: <c>"CQ FOXCALL GRID4"</c>  e.g. <c>"CQ LZ2HVV KN23"</c></description></item>
///   <item><description>Standard: <c>"FOXCALL HOUND1 [+NN|-NN] HOUND2 [+NN|-NN] ..."</c><br/>
///     Hound calls followed by a ±report get a report; all others get RR73.
///     Up to 5 RR73 + 4 report callsigns (9 total) per frame.</description></item>
/// </list>
///
/// <para>Set <see cref="EncoderOptions.FrequencyHz"/> to the base (tone-0/sync) frequency.
/// The 128 MFSK tones span approximately 1500 Hz above that (baud ≈ 11.7 Hz, 128 tones).
/// The WSJTX / MSHV default base is 750 Hz, placing the band at 750–2250 Hz.</para>
///
/// <para>C# port of MSHV gen_sfox.cpp (LZ2HV, GPL) and WSJTX sfox_pack.f90 / sfox_gen_gfsk.f90.</para>
/// </summary>
public sealed class SuperFoxEncoder : IDigitalModeEncoder
{
    public DigitalMode Mode => DigitalMode.SuperFox;

    // ── Physical-layer constants ───────────────────────────────────────────────
    private const int    NSps       = 1024;          // samples/symbol at 12 kHz
    private const int    NSym       = 151;           // QPC frame symbols (24 sync + 127 data)
    private const int    NWave      = NSym * NSps;   // 154,624 active samples
    private const int    NTotal     = 15 * 12000;    // 180,000 samples padded to 15 s
    private const double GfskBt     = 8.0;           // GFSK bandwidth-time product

    // ── QPC code constants ─────────────────────────────────────────────────────
    private const int QpcN     = 128;
    private const int QpcNp    = 127;  // punctured codeword length
    private const int QpcK     = 50;   // information symbols
    private const int QpcLog2N = 7;

    // Sentinel for unused callsign slots — mirrors SuperFoxDecoder.NqU1rks
    private const int NqU1rks = 203514677;

    // Sync symbol positions (0-indexed) in the 151-symbol QPC frame
    private static readonly int[] IsyncF =
    {
        0, 1, 3, 6, 10, 15, 21, 28, 36, 38, 41, 42, 44, 47,
        51, 56, 62, 69, 77, 79, 82, 83, 85, 88
    };

    // QPC information symbol positions xpos[0..49] — from gen_sfox.cpp
    private static readonly int[] QpcXPos =
    {
         1,  2,  3,  4,  5,  6,  8,  9, 10, 12,
        16, 32, 17, 18, 64, 20, 33, 34, 24,  7,
        11, 36, 13, 19, 14, 65, 40, 21, 66, 22,
        35, 68, 25, 48, 37, 26, 72, 15, 38, 28,
        41, 67, 23, 80, 42, 69, 49, 96, 44, 27
    };

    // Base-38 callsign alphabet: space + 0-9 + A-Z + /
    private const string Abc38 = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/";

    private static readonly double[] GfskPulseData = BuildGfskPulse();

    // ── Public entry point ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public float[] Encode(string message, EncoderOptions options)
    {
        byte[] xin   = PackMessage(message);
        int[]  sym   = QpcEncode(xin);
        int[]  itone = InsertSync(sym);
        return Modulate(itone, options.FrequencyHz, options.Amplitude);
    }

    // ── Phase 1: message packing ───────────────────────────────────────────────

    /// <summary>
    /// Packs a SuperFox message into 50 × 7-bit symbols (47 data + 3 CRC), symbol order reversed
    /// so the 21-bit Jenkins CRC occupies the first three symbols (as in WSJTX sfox_pack.f90).
    /// </summary>
    internal static byte[] PackMessage(string message)
    {
        string[] words = message.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var msgbits = new bool[329]; // 47×7 data bits + 2 padding bits + 3-bit i3

        if (words.Length >= 3 && words[0].Equals("CQ", StringComparison.OrdinalIgnoreCase))
            PackCqMessage(words, msgbits);
        else
            PackStandardMessage(words, msgbits);

        // Convert msgbits to 47 × 7-bit symbols
        var xin = new byte[50];
        for (int k = 0; k < 47; k++)
            for (int b = 0; b < 7; b++)
                if (msgbits[k * 7 + b]) xin[k] |= (byte)(1 << (6 - b));

        // 21-bit Jenkins CRC over first 47 symbols (initval=571, matches decoder)
        uint mask21  = (1u << 21) - 1;
        uint ncrc21  = SuperFoxDecoder.NHash2(xin, 47, 571) & mask21;
        xin[47]      = (byte)( ncrc21 >> 14);          // CRC bits 20-14
        xin[48]      = (byte)((ncrc21 >> 7) & 0x7F);   // CRC bits 13-7
        xin[49]      = (byte)( ncrc21        & 0x7F);   // CRC bits 6-0

        // Reverse symbol order: CRC symbols become xin[0..2] (front), matching WSJTX
        Array.Reverse(xin);
        return xin;
    }

    // i3=3: CQ FOXCALL GRID4
    private static void PackCqMessage(string[] words, bool[] msgbits)
    {
        // Fox callsign encoded as 11-char base-38 string → 58-bit integer
        string foxcall = words[1].ToUpperInvariant().PadRight(11)[..11];
        long n58 = 0;
        foreach (char c in foxcall)
        {
            int idx = Abc38.IndexOf(c);
            n58 = n58 * 38 + Math.Max(0, idx);
        }
        SetBitsLong(n58, 58, msgbits, 0);           // bits 0-57

        if (words.Length >= 3 && MessagePack77.TryParseGrid4(words[2], out int igrid4))
            SetBits(igrid4, 15, msgbits, 58);        // bits 58-72

        SetBits(3, 3, msgbits, 326);                 // i3=3 at bits 326-328
    }

    // i3=0: FOXCALL [HOUND1 [±NN]] [HOUND2 [±NN]] ...
    private static void PackStandardMessage(string[] words, bool[] msgbits)
    {
        if (words.Length == 0) return;

        int foxN28 = Pack28Clamped(words[0]);
        SetBits(foxN28, 28, msgbits, 0);              // bits 0-27: fox callsign

        // Pre-fill all 9 hound slots with sentinel (bits 28-279 = 9×28)
        for (int i = 1; i <= 9; i++)
            SetBits(NqU1rks, 28, msgbits, i * 28);

        int nh1 = 0; // RR73 callsigns (at bits 28+28k, up to 5)
        int nh2 = 0; // report callsigns (at bits 168+28k, up to 4)
        int[] reportVals = new int[4];

        for (int i = 1; i < words.Length && (nh1 + nh2) < 9; i++)
        {
            // Skip bare report words
            if (words[i].StartsWith('+') || words[i].StartsWith('-')) continue;

            bool nextIsReport = i + 1 < words.Length
                && (words[i + 1].StartsWith('+') || words[i + 1].StartsWith('-'));

            int n28 = Pack28Clamped(words[i]);

            if (nextIsReport && nh2 < 4 && int.TryParse(words[i + 1], out int rptVal))
            {
                // Callsign with report
                int v = Math.Clamp(rptVal, -18, 12) + 18;
                SetBits(n28, 28, msgbits, 168 + 28 * nh2);   // report callsign slot
                reportVals[nh2] = v;
                nh2++;
                i++; // consume the report word
            }
            else if (!nextIsReport && nh1 < 5)
            {
                // RR73 callsign
                SetBits(n28, 28, msgbits, 28 + 28 * nh1);
                nh1++;
            }
        }

        // Write 5-bit reports (offset +18 encoding)
        for (int i = 0; i < nh2; i++)
            SetBits(reportVals[i], 5, msgbits, 280 + 5 * i);

        // i3=0 → bits 326-328 remain false (zero)
    }

    // Pack28 that never returns 0 for a real callsign: falls back to sentinel on failure
    private static int Pack28Clamped(string token)
    {
        int n = MessagePack77.Pack28(token);
        return n == 0 ? NqU1rks : n;
    }

    // ── Phase 2: QPC polar encoding ────────────────────────────────────────────

    /// <summary>
    /// Maps 50 information symbols into a 128-symbol QPC polar codeword and punctures to 127.
    /// Port of gen_sfox.cpp: qpc_encode() + _qpc_encode() + shift-by-1 puncture.
    /// </summary>
    internal static int[] QpcEncode(byte[] xin50)
    {
        // Place information symbols at designated positions; remaining positions are frozen (0)
        var f = new byte[QpcN];
        for (int k = 0; k < QpcK; k++)
            f[QpcXPos[k]] = xin50[k];

        // Polar butterfly transform — non-recursive FFT-like structure
        // Stage k: log2(N)-stage butterfly XOR pairs, matching gen_sfox.cpp _qpc_encode()
        var y = (byte[])f.Clone();
        for (int stage = 0; stage < QpcLog2N; stage++)
        {
            int groups      = 1 << (QpcLog2N - 1 - stage);
            int bfyPerGroup = 1 << stage;
            int stepGroup   = bfyPerGroup << 1;
            for (int j = 0; j < groups; j++)
            {
                int baseGroup = j * stepGroup;
                for (int m = 0; m < bfyPerGroup; m++)
                    y[baseGroup + bfyPerGroup + m] ^= y[baseGroup + m];
            }
        }

        // Puncture: remove y[0] — take y[1..127] as chansym0[0..126]
        var sym127 = new int[QpcNp];
        for (int i = 0; i < QpcNp; i++)
            sym127[i] = y[i + 1];
        return sym127;
    }

    // ── Phase 3: sync insertion ────────────────────────────────────────────────

    /// <summary>
    /// Interleaves 24 sync symbols (tone 0) at IsyncF positions into the 127 data symbols,
    /// producing the 151-symbol QPC frame itone[]. Data symbols use tones 1-127 (offset +1).
    /// </summary>
    private static int[] InsertSync(int[] sym127)
    {
        var itone    = new int[NSym];
        int syncIdx  = 0;
        int dataIdx  = 0;
        for (int i = 0; i < NSym; i++)
        {
            if (syncIdx < IsyncF.Length && i == IsyncF[syncIdx])
            {
                itone[i] = 0; // sync: tone 0
                syncIdx++;
            }
            else
            {
                itone[i] = sym127[dataIdx++] + 1; // data: tones 1-127
            }
        }
        return itone;
    }

    // ── Phase 4: GFSK modulation ───────────────────────────────────────────────

    private static float[] Modulate(int[] itone, double freqHz, double amplitude)
    {
        var    pulse    = GfskPulseData;
        double dphiPeak = 2.0 * Math.PI / NSps; // phase deviation per sample for tone 1

        var dphi = new double[(NSym + 2) * NSps];

        // Accumulate GFSK frequency-deviation pulses — each symbol spreads over 3×NSps
        for (int j = 0; j < NSym; j++)
        {
            int ib = j * NSps;
            for (int i = 0; i < 3 * NSps; i++)
                dphi[ib + i] += dphiPeak * pulse[i] * itone[j];
        }

        // Edge ISI compensation (pre/post-roll for first and last symbols)
        for (int i = 0; i < 2 * NSps; i++)
        {
            dphi[i]               += dphiPeak * itone[0]        * pulse[i + NSps];
            dphi[NSym * NSps + i] += dphiPeak * itone[NSym - 1] * pulse[i];
        }

        // Integrate dphi → sine wave. Output window starts at dphi[NSps] (skip pre-roll).
        double ofs = 2.0 * Math.PI * freqHz / 12000.0;
        double phi = 0.0;
        var samples = new float[NTotal]; // pre-zeroed; silence fills [NWave..NTotal-1]

        for (int k = 0; k < NWave; k++)
        {
            samples[k] = (float)(amplitude * Math.Sin(phi));
            phi = (phi + dphi[k + NSps] + ofs) % (2.0 * Math.PI);
        }

        // Raised cosine amplitude ramp in/out (NSps/8 = 128 samples)
        int nramp = NSps / 8;
        for (int i = 0; i < nramp; i++)
        {
            float w = (float)((1.0 - Math.Cos(Math.PI * i / nramp)) / 2.0);
            samples[i]               *= w;
            samples[NWave - nramp + i] *= 1.0f - w;
        }

        return samples;
    }

    private static double[] BuildGfskPulse()
    {
        var pulse = new double[3 * NSps];
        for (int i = 0; i < 3 * NSps; i++)
        {
            double t  = ((double)i - 1.5 * NSps) / NSps;
            double c  = Math.PI * Math.Sqrt(2.0 / Math.Log(2.0));
            pulse[i]  = 0.5 * (SpecialFunctions.Erf(c * GfskBt * (t + 0.5))
                                - SpecialFunctions.Erf(c * GfskBt * (t - 0.5)));
        }
        return pulse;
    }

    // ── Bit-packing helpers ────────────────────────────────────────────────────

    private static void SetBits(int value, int nbits, bool[] arr, int offset)
    {
        for (int i = 0; i < nbits; i++)
            arr[offset + i] = ((value >> (nbits - 1 - i)) & 1) == 1;
    }

    private static void SetBitsLong(long value, int nbits, bool[] arr, int offset)
    {
        for (int i = 0; i < nbits; i++)
            arr[offset + i] = ((value >> (nbits - 1 - i)) & 1) == 1;
    }
}
