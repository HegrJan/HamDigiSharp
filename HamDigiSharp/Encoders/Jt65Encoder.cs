using HamDigiSharp.Abstractions;
using HamDigiSharp.Codecs;
using HamDigiSharp.Decoders.Jt65;
using HamDigiSharp.Models;

namespace HamDigiSharp.Encoders;

/// <summary>
/// JT65 encoder (sub-modes A, B, C) — 60-second period, 65-FSK, RS(63,12).
/// Implements WSJT-X JT65 protocol (K1JT/G4WJS), GPL.
///
/// Pipeline:
///   1. Pack message → nc1/nc2/ng → 12 × 6-bit symbols (packmsg)
///   2. RS(63,12) encode → 63 symbols
///   3. Interleave63 (7×9 → 9×7 matrix transpose)
///   4. Gray code (binary → Gray: n ^ (n>>1))
///   5. Insert into 126-symbol frame with sync (SyncSeq[126])
///   6. 65-FSK modulate at 11025 Hz
/// </summary>
public sealed class Jt65Encoder : IDigitalModeEncoder
{
    private const int SampleRate     = 11025;
    private const int NSymbols       = 126;
    private const int NData          = 63;
    private const int Nsps           = 4096;          // samples per symbol
    private const double ToneSpacingA = (double)SampleRate / 4096; // ≈ 2.692 Hz
    private const double DefaultFreq  = 1000.0;       // Hz

    // nroots for RS(63,12): 51 parity symbols
    private const int NBASE  = 37 * 36 * 10 * 27 * 27 * 27; // = 262177560
    private const int NGBASE = 180 * 180;                     // = 32400

    // Sync sequence (same as Jt65Decoder.SyncSeq)
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

    public Jt65Encoder(DigitalMode mode = DigitalMode.JT65A) { _mode = mode; }

    public DigitalMode Mode => _mode;

    public float[] Encode(string message, EncoderOptions options)
    {
        double freq = options.FrequencyHz > 0 ? options.FrequencyHz : DefaultFreq;
        double toneSpacing = _mode switch
        {
            DigitalMode.JT65B => ToneSpacingA * 2,
            DigitalMode.JT65C => ToneSpacingA * 4,
            _                 => ToneSpacingA,
        };

        // 1. Pack message → 12 × 6-bit symbols
        int[] dgen = PackMsg(message.Trim());

        // 2. RS(63,12) encode
        var parity = new int[51];
        ReedSolomon63.Encode(dgen, parity);
        int[] sent = new int[63];
        Array.Copy(dgen,   sent,     12);
        Array.Copy(parity, 0, sent, 12, 51);

        // 3. Interleave63: 7×9 → 9×7 matrix transpose
        sent = Jt65Decoder.Interleave63(sent);

        // 4. Gray code: n → n ^ (n >> 1)
        for (int i = 0; i < 63; i++)
            sent[i] ^= sent[i] >> 1;

        // 5. Build 126-symbol itone[] array
        // Sync positions: itone = 0 (sync tone at f0)
        // Data positions: itone = sent[k] + 2 (data tones start at f0 + 2*toneSpacing)
        int[] itone = new int[NSymbols];
        int k = 0;
        for (int j = 0; j < NSymbols; j++)
        {
            if (SyncSeq[j] == 1)
                itone[j] = 0;           // sync tone
            else
                itone[j] = sent[k++] + 2; // data tone offset by 2
        }

        // 6. Modulate: continuous-phase FSK via complex phasor (2 trig calls/symbol)
        int   totalSamples = NSymbols * Nsps;
        float[] wave       = new float[totalSamples];
        double pCos = 1.0, pSin = 0.0;  // phasor starts at phase=0

        for (int sym = 0; sym < NSymbols; sym++)
        {
            double f    = freq + itone[sym] * toneSpacing;
            double dphi = 2.0 * Math.PI * f / SampleRate;
            double rotCos = Math.Cos(dphi), rotSin = Math.Sin(dphi);
            int    base_ = sym * Nsps;
            for (int i = 0; i < Nsps; i++)
            {
                double nCos = pCos * rotCos - pSin * rotSin;
                pSin = pCos * rotSin + pSin * rotCos;
                pCos = nCos;
                wave[base_ + i] = (float)pSin;
            }
        }
        return wave;
    }

    // ── Message packing (WSJT-X packmsg) ─────────────────────────────────────

    private static int[] PackMsg(string msg)
    {
        string[] parts = msg.ToUpperInvariant().Trim()
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 3)
        {
            // JT65v2 compound call: "CQ/QRZ/DE COMPOUND GRID" where COMPOUND contains '/'
            if (parts[1].Contains('/') && parts[0] is "CQ" or "QRZ" or "DE"
                && TryPackCompound(parts[0], parts[1], parts[2], out int nc1c, out int nc2c, out int ngc))
                return BuildSymbols(nc1c, nc2c, ngc);

            // Standard three-token: "CALL1 CALL2 GRID/REPORT"
            if (TryPackCall(parts[0], out int nc1) && TryPackCall(parts[1], out int nc2)
                && TryPackGrid(parts[2], out int ng))
                return BuildSymbols(nc1, nc2, ng);
        }

        PackFreeText(msg, out int fnc1, out int fnc2, out int fng);
        return BuildSymbols(fnc1, fnc2, fng | 0x8000);
    }

    // ── JT65v2 compound callsign packing ──────────────────────────────────────
    // Mirrors WSJT-X getpfx1() + packmsg() compound-call branch.
    //
    // Two formats (only valid when first token is CQ/QRZ/DE):
    //   PREFIX/CALL  → nc1 encodes keyword+prefix (iv2=1..3), nc2 = pack(call)
    //   CALL/SUFFIX  → nc1 encodes keyword+suffix (iv2=4..6), nc2 = pack(call)
    //
    // Disambiguation (mirrors getpfx1 logic):
    //   ispfx  = left side is 1–4 chars
    //   issfx  = right side is 1–3 chars
    //   if both: prefer CALL/SUFFIX when right < 3, PREFIX/CALL when left < 3;
    //            else check if last char of left is a digit (→ it's a prefix, so issfx=false).

    private static bool TryPackCompound(
        string keyword, string compound, string gridStr,
        out int nc1, out int nc2, out int ng)
    {
        nc1 = nc2 = ng = 0;

        int slash = compound.IndexOf('/');
        if (slash <= 0 || slash >= compound.Length - 1) return false;

        string lof = compound[..slash];
        string rof = compound[(slash + 1)..];
        int llof = lof.Length;
        int lrof = rof.Length;

        bool ispfx = llof >= 1 && llof <= 4;
        bool issfx = lrof >= 1 && lrof <= 3;

        if (ispfx && issfx)
        {
            if (llof < 3)         issfx = false;
            else if (lrof < 3)    ispfx = false;
            else
            {
                char last = lof[^1];
                if (last >= '0' && last <= '9') issfx = false;
                else                            ispfx = false;
            }
        }

        if (!ispfx && !issfx) return false;

        if (!TryPackGrid(gridStr, out ng)) ng = NGBASE + 1;

        if (ispfx)
        {
            // PREFIX/CALL: lof = prefix (1–4 chars), rof = standard callsign
            if (!TryPackCall(rof, out nc2)) return false;
            int k2 = PackPsfx4(lof);
            nc1 = keyword switch
            {
                "CQ"  => 262_178_563 + k2,
                "QRZ" => 264_002_072 + k2,
                "DE"  => 265_825_581 + k2,
                _     => 0
            };
            return nc1 != 0;
        }
        else
        {
            // CALL/SUFFIX: lof = standard callsign, rof = suffix (1–3 chars)
            if (!TryPackCall(lof, out nc2)) return false;
            int k2 = PackPsfx3(rof);
            nc1 = keyword switch
            {
                "CQ"  => 267_649_090 + k2,
                "QRZ" => 267_698_375 + k2,
                "DE"  => 267_747_660 + k2,
                _     => 0
            };
            return nc1 != 0;
        }
    }

    // 4-char base-37 prefix packing (mirrors Fortran nchar, right-pads with spaces)
    private static int PackPsfx4(string s)
    {
        string p = s.ToUpperInvariant().PadRight(4)[..4];
        int k = NChar37(p[0]);
        k = 37 * k + NChar37(p[1]);
        k = 37 * k + NChar37(p[2]);
        k = 37 * k + NChar37(p[3]);
        return k;
    }

    // 3-char base-37 suffix packing
    private static int PackPsfx3(string s)
    {
        string p = s.ToUpperInvariant().PadRight(3)[..3];
        int k = NChar37(p[0]);
        k = 37 * k + NChar37(p[1]);
        k = 37 * k + NChar37(p[2]);
        return k;
    }

    // Base-37 character value: '0'–'9' → 0–9, 'A'–'Z' → 10–35, else → 36 (space/pad)
    private static int NChar37(char c) =>
        c >= '0' && c <= '9' ? c - '0' :
        c >= 'A' && c <= 'Z' ? c - 'A' + 10 : 36;

    // ── PackCall: callsign string → 28-bit integer (WSJT-X packcall) ─────────

    private static bool TryPackCall(string callsign, out int nc)
    {
        nc = 0;
        if (string.IsNullOrWhiteSpace(callsign)) return false;

        string s = callsign.ToUpperInvariant().Trim();

        if (s == "CQ")  { nc = NBASE + 1; return true; }
        if (s == "QRZ") { nc = NBASE + 2; return true; }
        if (s.StartsWith("CQ ") && s.Length == 6 && int.TryParse(s.Substring(3), out int nf) && nf >= 0 && nf <= 999)
        { nc = NBASE + 3 + nf; return true; }
        if (s == "DE") { nc = 267796945; return true; }

        // Normalize to 6 chars: standard callsign has digit at position 2 (0-indexed)
        string tmp = s.PadRight(6).Substring(0, 6);
        if (tmp[2] < '0' || tmp[2] > '9')
        {
            // Try prepending a space (e.g., "W1AW" → " W1AW ")
            tmp = (" " + s).PadRight(6).Substring(0, 6);
        }

        static int NChar(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
            return 36; // space
        }

        // Validate: positions 0-1 alphanumeric/space, position 2 digit, 3-5 alpha/space
        for (int i = 0; i < 6; i++)
        {
            char c = tmp[i];
            bool valid = i == 2 ? (c >= '0' && c <= '9')
                                : ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || c == ' ');
            if (!valid) return false;
        }
        if (tmp[2] < '0' || tmp[2] > '9') return false;

        int n = NChar(tmp[0]);
        n = n * 36 + NChar(tmp[1]);
        n = n * 10 + (tmp[2] - '0');
        n = n * 27 + (NChar(tmp[3]) - 10);
        n = n * 27 + (NChar(tmp[4]) - 10);
        n = n * 27 + (NChar(tmp[5]) - 10);
        nc = n;
        return true;
    }

    // ── PackGrid: grid/report string → 15-bit integer (WSJT-X packgrid) ──────

    private static bool TryPackGrid(string grid, out int ng)
    {
        ng = 0;
        if (string.IsNullOrWhiteSpace(grid)) { ng = NGBASE + 1; return true; }

        string g = grid.ToUpperInvariant().Trim();

        if (g == "RRR") { ng = NGBASE + 63; return true; }
        if (g == "RO")  { ng = NGBASE + 62; return true; }
        if (g == "73")  { ng = NGBASE + 64; return true; }

        // Signal report "-12" or "R-12"
        if (g.Length >= 3 && g[0] == '-' && int.TryParse(g.Substring(1), out int neg)
            && neg >= 1 && neg <= 30) { ng = NGBASE + 1 + neg; return true; }
        if (g.Length >= 4 && g[0] == 'R' && g[1] == '-' && int.TryParse(g.Substring(2), out int rneg)
            && rneg >= 1 && rneg <= 30) { ng = NGBASE + 31 + rneg; return true; }

        // Grid locator "FN42"
        if (g.Length >= 4 && g[0] >= 'A' && g[0] <= 'R'
                           && g[1] >= 'A' && g[1] <= 'R'
                           && g[2] >= '0' && g[2] <= '9'
                           && g[3] >= '0' && g[3] <= '9')
        {
            int lon = (g[0] - 'A') * 20 + (g[2] - '0') * 2 - 180;
            int lat = (g[1] - 'A') * 10 + (g[3] - '0') - 90;
            ng = ((lon + 180) / 2) * 180 + (lat + 90);
            return true;
        }
        return false;
    }

    // ── PackFreeText: up to 13 chars → nc1/nc2/nc3 (WSJT-X packtext) ─────────

    private static void PackFreeText(string msg, out int nc1, out int nc2, out int nc3)
    {
        const string c = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ +-./?@";
        string m = msg.ToUpperInvariant().PadRight(13).Substring(0, 13);

        nc1 = 0; nc2 = 0; nc3 = 0;
        for (int i =  0; i <  5; i++) { int j = c.IndexOf(m[i]); if (j < 0) j = 36; nc1 = nc1 * 42 + j; }
        for (int i =  5; i < 10; i++) { int j = c.IndexOf(m[i]); if (j < 0) j = 36; nc2 = nc2 * 42 + j; }
        for (int i = 10; i < 13; i++) { int j = c.IndexOf(m[i]); if (j < 0) j = 36; nc3 = nc3 * 42 + j; }

        // Move top bits of nc3 into LSBs of nc1 and nc2
        nc1 *= 2; if ((nc3 & 0x8000)  != 0) nc1 |= 1;
        nc2 *= 2; if ((nc3 & 0x10000) != 0) nc2 |= 1;
        nc3 &= 0x7FFF;
    }

    // ── Build 12 six-bit symbols from nc1/nc2/ng ──────────────────────────────

    private static int[] BuildSymbols(int nc1, int nc2, int ng)
    {
        int[] dat = new int[12];
        dat[0]  = (nc1 >> 22) & 0x3F;
        dat[1]  = (nc1 >> 16) & 0x3F;
        dat[2]  = (nc1 >> 10) & 0x3F;
        dat[3]  = (nc1 >>  4) & 0x3F;
        dat[4]  = ((nc1 & 0xF) << 2) | ((nc2 >> 26) & 0x3);
        dat[5]  = (nc2 >> 20) & 0x3F;
        dat[6]  = (nc2 >> 14) & 0x3F;
        dat[7]  = (nc2 >>  8) & 0x3F;
        dat[8]  = (nc2 >>  2) & 0x3F;
        dat[9]  = ((nc2 & 0x3) << 4) | ((ng >> 12) & 0xF);
        dat[10] =  (ng >>  6) & 0x3F;
        dat[11] =   ng        & 0x3F;
        return dat;
    }
}
