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
        byte[] xin   = PackMessage(message, options.SuperFoxSignature);
        int[]  sym   = QpcEncode(xin);
        int[]  itone = InsertSync(sym);
        return Modulate(itone, options.FrequencyHz, options.Amplitude);
    }

    // ── Phase 1: message packing ───────────────────────────────────────────────

    /// <summary>
    /// Packs a SuperFox message into 50 × 7-bit symbols (47 data + 3 CRC), symbol order reversed
    /// so the 21-bit Jenkins CRC occupies the first three symbols (as in WSJTX sfox_pack.f90).
    ///
    /// <para>Message formats:</para>
    /// <list type="bullet">
    ///   <item>i3=3: <c>"CQ FOXCALL GRID4"</c></item>
    ///   <item>i3=0: <c>"FOXCALL H1 [±NN] H2 [±NN] …"</c> — up to 9 hounds</item>
    ///   <item>i3=2: <c>"FOXCALL H1 [±NN] … ~ FREE TEXT"</c> — up to 4 hounds + 26-char text</item>
    /// </list>
    ///
    /// <para>
    /// <paramref name="notp"/> is the 20-bit one-time-pad (OTP) digital signature (0 = none).
    /// It is placed in bits 306–325 of the message before CRC computation.
    /// </para>
    /// </summary>
    internal static byte[] PackMessage(string message, uint notp = 0)
    {
        string[] words = message.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var msgbits = new bool[329]; // 47×7 data bits + 2 padding bits + 3-bit i3

        // Route by message type:
        //   i3=3: "CQ FOXCALL GRID4"              (no tilde)
        //   i3=3: "CQ FOXCALL GRID4 ~ FREE TEXT"  (tilde after CQ header)
        //   i3=2: "FOXCALL H1 [±NN] ... ~ TEXT"   (tilde, non-CQ)
        //   i3=0: "FOXCALL H1 [±NN] ..."           (no tilde, non-CQ)
        bool isCq    = words.Length >= 1 && words[0].Equals("CQ", StringComparison.OrdinalIgnoreCase);
        int  tildeIdx = Array.IndexOf(words, "~");
        string? freeText = tildeIdx >= 0
            ? string.Join(" ", words[(tildeIdx + 1)..]).Trim()
            : null;

        if (isCq && freeText != null)
            PackCqMessage(words[..tildeIdx], freeText, msgbits);
        else if (!isCq && freeText != null)
            PackTextResponseMessage(words[..tildeIdx], freeText, msgbits);
        else if (isCq)
            PackCqMessage(words, null, msgbits);
        else
            PackStandardMessage(words, msgbits);

        // Write 20-bit OTP digital signature to bits 306-325 (Fortran msgbits(307:326))
        if (notp != 0)
        {
            notp &= 0xFFFFFu; // mask to 20 bits
            for (int i = 0; i < 20; i++)
                msgbits[306 + i] = ((notp >> (19 - i)) & 1) != 0;
        }

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

    // i3=3: CQ FOXCALL GRID4  (or CQ FOXCALL GRID4 ~ FREE TEXT)
    private static void PackCqMessage(string[] words, string? freeText, bool[] msgbits)
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

        if (!string.IsNullOrWhiteSpace(freeText))
        {
            // Pack 26-char free text as two 13-char halves at bits 73-143 and 144-214.
            string padded = (freeText + new string('.', 26))[..26];
            PackText71(padded[..13], msgbits, 73);   // bits 73-143
            PackText71(padded[13..], msgbits, 144);  // bits 144-214
        }
        else
        {
            // Write NqU1rks sentinel (0x0C2049D5) at bits 73-296 for WSJTX interoperability.
            // The decoder checks bits 73-104 for this sentinel before attempting free-text unpack.
            for (int block = 0; block < 7; block++)
                SetBits(NqU1rks, 32, msgbits, 73 + block * 32);
        }

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

    // i3=2: Fox callsign + up to 4 hound calls with reports + 26-char free text message.
    // Mirrors WSJTX sfox_pack.f90 behaviour for bSendMsg=true.
    // RR73 hounds are packed first; report hounds follow.
    private static void PackTextResponseMessage(string[] words, string freeText, bool[] msgbits)
    {
        if (words.Length == 0) return;

        int foxN28 = Pack28Clamped(words[0]);
        SetBits(foxN28, 28, msgbits, 0); // bits 0-27: fox callsign

        // Pre-fill all 4 report slots (bits 140-159) with n=31 (RR73 sentinel)
        for (int i = 0; i < 20; i++) msgbits[140 + i] = true;

        // Collect RR73 hounds (no following report token) — at most 4 total
        var rr73Hounds = new List<int>();
        for (int i = 1; i < words.Length && rr73Hounds.Count < 4; i++)
        {
            if (words[i].StartsWith('+') || words[i].StartsWith('-')) continue;
            int iNext = i + 1 < words.Length ? i + 1 : i;
            if (words[iNext].StartsWith('+') || words[iNext].StartsWith('-')) continue;
            rr73Hounds.Add(Pack28Clamped(words[i]));
        }

        // Collect report hounds (followed by ±NN token) — up to 4−nh1 total
        var rptHounds = new List<(int N28, int Report)>();
        for (int i = 1; i < words.Length && rr73Hounds.Count + rptHounds.Count < 4; i++)
        {
            if (words[i].StartsWith('+') || words[i].StartsWith('-')) continue;
            if (i + 1 < words.Length
                && (words[i + 1].StartsWith('+') || words[i + 1].StartsWith('-'))
                && int.TryParse(words[i + 1], out int rptVal))
            {
                rptHounds.Add((Pack28Clamped(words[i]), Math.Clamp(rptVal, -18, 12) + 18));
                i++; // consume the report word
            }
        }

        // Write RR73 hounds (slots 0..nh1-1); their report slots stay as n=31
        for (int i = 0; i < rr73Hounds.Count; i++)
            SetBits(rr73Hounds[i], 28, msgbits, 28 + 28 * i);

        // Write report hounds (slots nh1..nh1+nh2-1) and their actual reports
        int nh1 = rr73Hounds.Count;
        for (int i = 0; i < rptHounds.Count; i++)
        {
            SetBits(rptHounds[i].N28,    28, msgbits, 28  + 28 * (nh1 + i));
            SetBits(rptHounds[i].Report,  5, msgbits, 140 +  5 * (nh1 + i));
        }

        // Fill unused hound slots with sentinel
        for (int i = nh1 + rptHounds.Count; i < 4; i++)
            SetBits(NqU1rks, 28, msgbits, 28 + 28 * i);

        // Write 26-char free text as two 13-char halves (packtext77, base-42)
        string padded = freeText.PadRight(26)[..26];
        PackText71(padded[..13], msgbits, 160);  // bits 160-230
        PackText71(padded[13..], msgbits, 231);  // bits 231-301

        SetBits(2, 3, msgbits, 326); // i3=2 (binary 010)
    }

    /// <summary>
    /// Packs 13 characters into 71 bits using base-42 multi-precision arithmetic.
    /// Inverse of <c>SuperFoxDecoder.UnpackText71</c>; alphabet: <c>" 0-9 A-Z + - . / ?"</c>.
    /// </summary>
    private static void PackText71(string text, bool[] msgbits, int startBit)
    {
        const string Abc42 = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?";
        string m = (text ?? "").ToUpperInvariant().PadRight(13)[..13];

        // Build big-endian 71-bit integer in qa[1..9] (qa[0] = overflow, should be 0)
        var qa = new byte[10];
        for (int i = 0; i < 13; i++)
        {
            int c = Math.Max(0, Abc42.IndexOf(m[i]));
            int carry = c;
            for (int j = 9; j >= 1; j--)
            {
                int v = qa[j] * 42 + carry;
                qa[j] = (byte)(v & 0xFF);
                carry  = v >> 8;
            }
        }

        // Write 7 MSBs from qa[1], then all 8 bits from qa[2..9] (= 7 + 64 = 71 bits)
        int pos = startBit;
        for (int b = 6; b >= 0; b--) msgbits[pos++] = ((qa[1] >> b) & 1) != 0;
        for (int k = 2; k <= 9; k++)
            for (int b = 7; b >= 0; b--) msgbits[pos++] = ((qa[k] >> b) & 1) != 0;
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
