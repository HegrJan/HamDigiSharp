using HamDigiSharp.Abstractions;
using HamDigiSharp.Codecs;
using HamDigiSharp.Models;

namespace HamDigiSharp.Encoders;

/// <summary>
/// Q65 audio encoder — 65-FSK, QRA LDPC over GF(64).
/// Implements the genq65 / q65_enc pipeline from WSJT-X (IV3NWV, K1JT).
///
/// Pipeline:
///   1. Pack message to 77 bits via MessagePack77 (shared with FT8/FT4)
///   2. Group 77 bits → 13 six-bit GF(64) symbols, MSB-first per symbol
///      (78-bit payload: last symbol's LSB is always 0, matching dgen(13)*=2 in WSJT-X)
///   3. Compute 12-bit CRC-polynomial over 13×6 bits → 2 GF(64) CRC symbols
///   4. QRA RA-code encode: (15,65) over GF(64), puncture 2 CRC positions → 63 symbols
///   5. Insert 22 sync tones (tone 0) at fixed positions; data symbols shifted +1
///   6. 65-FSK modulate: tone spacing = nsps-dependent; continuous phase
///
/// Sub-mode parameters (12 kHz sample rate):
///   A: 60 s, nsps = 6912 (≈ 1.736 Hz/sym, ≈ 113 Hz BW for submode A spacing×1)
///   B: 30 s, nsps = 3456
///   C: 15 s, nsps = 1728
///   D:  7 s, nsps =  864
/// </summary>
public sealed class Q65Encoder : IDigitalModeEncoder
{
    public DigitalMode Mode => _mode;

    private const int SampleRate = 12000;
    private const int NSym       = 85;  // total symbols per frame
    private const int NSync      = 22;  // sync symbol count
    private const int NData      = 63;  // data symbol count (K_eff=13 + 50 check)
    private const int KMsg       = 13;  // info symbols (after puncturing 2 CRC)

    // Sync positions (1-indexed in WSJT-X Fortran; stored 0-indexed here)
    private static readonly int[] SyncPos =
    {
        0,8,11,12,14,21,22,25,26,32,34,37,45,49,54,59,61,65,68,73,75,84
    };

    // 63 non-sync positions in frame order → QRA codeword positions 0-62
    private static readonly int[] DataPos;

    // GF(64) tables — primitive polynomial x^6+x+1 (from qra15_65_64_irr_e23.c)
    private static readonly int[] GfLog = {
        -1,0,1,6,2,12,7,26,3,32,13,35,8,48,27,18,4,24,33,16,
        14,52,36,54,9,45,49,38,28,41,19,56,5,62,25,11,34,31,17,47,
        15,23,53,51,37,44,55,40,10,61,46,30,50,22,39,43,29,60,42,21,
        20,59,57,58
    };
    private static readonly int[] GfExp = {
        1,2,4,8,16,32,3,6,12,24,48,35,5,10,20,40,19,38,15,30,
        60,59,53,41,17,34,7,14,28,56,51,37,9,18,36,11,22,44,27,54,
        47,29,58,55,45,25,50,39,13,26,52,43,21,42,23,46,31,62,63,61,
        57,49,33
    };

    // QRA accumulator tables (qra_acc_input_idx / qra_acc_input_wlog, NC=50)
    private static readonly int[] AccIdx = {
        13,1,3,4,8,12,9,14,10,5,
        0,7,1,11,8,9,12,6,3,10,
        7,5,2,13,12,4,8,0,1,11,
        2,9,14,5,6,13,7,12,11,2,
        9,0,10,4,7,14,8,11,3,6,
        10
    };
    private static readonly int[] AccWLog = {
        0,14,0,0,13,37,0,27,56,62,
        29,0,52,34,62,4,3,22,25,0,
        22,0,20,10,0,43,53,60,0,0,
        0,62,0,5,0,61,36,31,61,59,
        10,0,29,39,25,18,0,14,11,50,
        17
    };

    // CRC-12 generator polynomial (bit-reversed, LSB=a0): 0xF01
    private const int Crc12Poly = 0xF01;

    private readonly DigitalMode _mode;
    private readonly int _nsps;        // samples per symbol

    public Q65Encoder(DigitalMode mode = DigitalMode.Q65A)
    {
        _mode = mode;
        _nsps = mode switch
        {
            DigitalMode.Q65A => 6912,
            DigitalMode.Q65B => 3456,
            DigitalMode.Q65C => 1728,
            DigitalMode.Q65D =>  864,
            _                => 6912,
        };
    }

    static Q65Encoder()
    {
        var syncSet = new HashSet<int>(SyncPos);
        DataPos = Enumerable.Range(0, NSym).Where(i => !syncSet.Contains(i)).ToArray();
    }

    public float[] Encode(string message, EncoderOptions options)
    {
        double freq = options.FrequencyHz > 0 ? options.FrequencyHz : 1000.0;

        // 1. Pack to 77 bits
        var c77 = new bool[77];
        if (!MessagePack77.TryPack77(message, c77))
            throw new ArgumentException($"Cannot encode Q65 message: \"{message}\"");

        // 2. Group 77 bits → 13 six-bit symbols (78-bit payload, last sym LSB = 0)
        int[] dgen = PackTo13Symbols(c77);

        // 3. QRA encode: 13 info symbols + 2 computed CRC → 65 symbols, puncture 2 → 63
        int[] sent = Q65Encode(dgen);

        // 4. Build tone array (85 symbols): sync=0, data=sent[k]+1
        int[] itone = BuildTones(sent);

        // 5. 65-FSK modulate (continuous phase, no GFSK)
        return Modulate(itone, freq, options.Amplitude);
    }

    // ── Step 2: group 77 bits into 13 six-bit symbols ─────────────────────────

    private static int[] PackTo13Symbols(bool[] c77)
    {
        int[] dgen = new int[KMsg];
        // First 12 symbols: 6 bits each (bits 0-71)
        for (int i = 0; i < 12; i++)
        {
            int sym = 0;
            for (int b = 0; b < 6; b++)
                sym = (sym << 1) | (c77[i * 6 + b] ? 1 : 0);
            dgen[i] = sym;
        }
        // 13th symbol: bits 72-76 (5 bits), shifted left by 1 to make 6-bit
        int last = 0;
        for (int b = 0; b < 5; b++)
            last = (last << 1) | (c77[72 + b] ? 1 : 0);
        dgen[12] = last * 2;  // == last << 1, i.e. 77→78-bit payload
        return dgen;
    }

    // ── Step 3: QRA(15,65) encode with CRC-12, puncture 2 CRC symbols ─────────

    private int[] Q65Encode(int[] msg)
    {
        // Build full 15-symbol input: 13 info + 2 CRC
        int[] px = new int[15];
        Array.Copy(msg, px, KMsg);
        Crc12(px, KMsg);  // fills px[13] and px[14]

        // RA encode over GF(64): systematic (copy px[0..14]) + 50 check symbols
        int[] py = new int[65];
        for (int i = 0; i < 15; i++) py[i] = px[i];

        int chk = 0;
        for (int k = 0; k < 50; k++)  // NC = 50
        {
            int t = px[AccIdx[k]];
            if (t != 0)
            {
                int logT = GfLog[t];
                int logW = AccWLog[k];
                // GF(64) multiply: t * alfa^logW = gfexp[(gflog[t]+logW) mod 63]
                // logW=0 means weight=1 (alfa^0=1), so multiply by 1 = identity
                int prod = logW == 0 ? t : GfExp[(logT + logW) % 63];
                chk ^= prod;
            }
            py[15 + k] = chk;
        }

        // Puncture: output positions 0..12 (info) + 15..64 (checks), skip 13,14 (CRC)
        int[] sent = new int[63];
        Array.Copy(py, 0, sent, 0, KMsg);            // info symbols 0-12
        Array.Copy(py, 15, sent, KMsg, 50);          // check symbols 15-64
        return sent;
    }

    // CRC-12 over 13 six-bit symbols, polynomial 0xF01 (LSB=a0)
    private static void Crc12(int[] px, int sz)
    {
        int sr = 0;
        for (int k = 0; k < sz; k++)
        {
            int t = px[k];
            for (int j = 0; j < 6; j++)
            {
                if (((t ^ sr) & 1) != 0)
                    sr = (sr >> 1) ^ Crc12Poly;
                else
                    sr >>= 1;
                t >>= 1;
            }
        }
        px[sz]     = sr & 0x3F;       // lower 6 bits
        px[sz + 1] = (sr >> 6) & 0x3F; // upper 6 bits
    }

    // ── Step 4: insert sync tones ─────────────────────────────────────────────

    private static int[] BuildTones(int[] sent)
    {
        int[] itone = new int[NSym];
        int k = 0;
        int syncIdx = 0;
        for (int i = 0; i < NSym; i++)
        {
            if (syncIdx < SyncPos.Length && i == SyncPos[syncIdx])
            {
                itone[i] = 0;       // sync: tone 0
                syncIdx++;
            }
            else
            {
                itone[i] = sent[k] + 1;  // data: tone = codeword_symbol + 1
                k++;
            }
        }
        return itone;
    }

    // ── Step 5: 65-FSK modulate (continuous phase) ────────────────────────────

    private float[] Modulate(int[] itone, double baseFreq, double amplitude)
    {
        // Tone spacing = 1 baud = SR/_nsps (submode determines _nsps, NOT a spacing multiplier)
        double baud    = (double)SampleRate / _nsps;
        double spacing = baud;  // Hz per tone step

        float amp = (float)(amplitude > 0 ? amplitude : 1.0);
        float[] samples = new float[NSym * _nsps];

        // Continuous-phase FSK via complex phasor — 2 trig calls/symbol instead of _nsps.
        // Phasor (pCos, pSin) tracks cos(phase)/sin(phase); advanced BEFORE each output
        // to match the original "phase += dphi; sin(phase)" convention.
        double pCos = 1.0, pSin = 0.0;  // initial phase = 0

        for (int j = 0; j < NSym; j++)
        {
            double freq = baseFreq + itone[j] * spacing;
            double dphi = 2.0 * Math.PI * freq / SampleRate;
            double rotCos = Math.Cos(dphi), rotSin = Math.Sin(dphi);
            int offset = j * _nsps;
            for (int i = 0; i < _nsps; i++)
            {
                // Advance phasor by dphi, then output imaginary part (= sin)
                double nCos = pCos * rotCos - pSin * rotSin;
                pSin = pCos * rotSin + pSin * rotCos;
                pCos = nCos;
                samples[offset + i] = amp * (float)pSin;
            }
        }
        return samples;
    }
}
