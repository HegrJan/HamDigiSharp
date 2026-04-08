using System.Text;
using System.Text.RegularExpressions;

namespace HamDigiSharp.Codecs;

/// <summary>
/// Packs and unpacks 77-bit WSJT-X messages (FT8/FT4/FT2/Q65 format).
/// Faithful C# port of MSHV's <c>PackUnpackMsg77</c> (LZ2HV) and the original
/// WSJT-X Fortran routines (K1JT et al.), GPL.
/// </summary>
public sealed class MessagePacker
{
    // ── Character alphabets ───────────────────────────────────────────────────
    private const string C77_04  = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/";
    private const string C77_Txt = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?";
    private const string A1_28   = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string A2_28   = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string A3_28   = "0123456789";
    private const string A4_28   = " ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    // ── ARRL Field Day sections (86 entries, 1-indexed in WSJT-X) ────────────
    private static readonly string[] ArrlSections =
    {
        "AB","AK","AL","AR","AZ","BC","CO","CT","DE","EB",
        "EMA","ENY","EPA","EWA","GA","GH","IA","ID","IL","IN",
        "KS","KY","LA","LAX","NS","MB","MDC","ME","MI","MN",
        "MO","MS","MT","NC","ND","NE","NFL","NH","NL","NLI",
        "NM","NNJ","NNY","TER","NTX","NV","OH","OK","ONE","ONN",
        "ONS","OR","ORG","PAC","PR","QC","RI","SB","SC","SCV",
        "SD","SDG","SF","SFL","SJV","SK","SNJ","STX","SV","TN",
        "UT","VA","VI","VT","WCF","WI","WMA","WNY","WPA","WTX",
        "WV","WWA","WY","DX","PE","NB"
    };

    // ── ARRL RTTY multipliers (171 entries, 1-indexed in WSJT-X) ────────────
    private static readonly string[] RttyMultipliers;

    static MessagePacker()
    {
        var m = new string[171];
        string[] head =
        {
            "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA",
            "HI","ID","IL","IN","IA","KS","KY","LA","ME","MD",
            "MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ",
            "NM","NY","NC","ND","OH","OK","OR","PA","RI","SC",
            "SD","TN","TX","UT","VT","VA","WA","WV","WI","WY",
            "NB","NS","QC","ON","MB","SK","AB","BC","NWT","NF",
            "LB","NU","YT","PEI","DC","DR","FR","GD","GR","OV",
            "ZH","ZL"
        };
        for (int i = 0; i < head.Length; i++) m[i] = head[i];
        for (int i = 0; i < 99; i++) m[72 + i] = $"X{i + 1:D2}";
        RttyMultipliers = m;
    }

    // NTOKENS = 2063592, MAX22 = 4194304
    private const int NTokens = 2063592;
    private const int Max22 = 4194304;
    private const int MaxGrid4 = 32400;

    // ── Callsign hash tables ──────────────────────────────────────────────────
    private readonly int[] _hash10 = new int[650];
    private readonly int[] _hash12 = new int[650];
    private readonly int[] _hash22 = new int[650];
    private readonly string[] _hashCall = new string[650];
    private int _hashWritePos = 16; // start after reserved slots

    public MessagePacker() { Array.Fill(_hash10, -1); Array.Fill(_hash12, -1); Array.Fill(_hash22, -1); }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Unpack 77 bits to a human-readable FT8/FT4/Q65 message string.
    /// Returns empty string on failure.
    /// </summary>
    public string Unpack77(ReadOnlySpan<bool> c77, out bool success)
    {
        success = true;
        int n3 = (c77[71] ? 4 : 0) | (c77[72] ? 2 : 0) | (c77[73] ? 1 : 0);
        int i3 = (c77[74] ? 4 : 0) | (c77[75] ? 2 : 0) | (c77[76] ? 1 : 0);

        if (i3 == 0 && n3 == 0)
        {
            string msg = UnpackText77(c77).Trim();
            if (msg.Length == 0 || msg[0] == ' ') { success = false; return ""; }
            return msg;
        }
        if (i3 == 0 && n3 == 1) return UnpackDxped(c77, ref success);
        if (i3 == 0 && n3 >= 3 && n3 <= 4) return UnpackFieldDay(c77, n3, ref success);
        if (i3 == 0 && n3 == 5) return UnpackTelemetry(c77);
        if (i3 == 0) { success = false; return ""; }

        if (i3 == 1 || i3 == 2) return UnpackType12(c77, i3, ref success);
        if (i3 == 3) return UnpackRttyContest(c77, ref success);
        if (i3 == 4) return UnpackType4(c77, ref success);
        if (i3 == 5) return UnpackEuVhf(c77, ref success);

        success = false;
        return "";
    }

    // ── Hash table management ─────────────────────────────────────────────────

    /// <summary>Register a callsign so it can be looked up by hash during unpack.</summary>
    public void RegisterCallsign(string callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return;
        callsign = callsign.Trim().ToUpperInvariant();
        ComputeHashes(callsign, out int h10, out int h12, out int h22);
        int pos = _hashWritePos % _hashCall.Length;
        _hash10[pos] = h10;
        _hash12[pos] = h12;
        _hash22[pos] = h22;
        _hashCall[pos] = callsign;
        _hashWritePos++;
    }

    // ── Bit extraction ────────────────────────────────────────────────────────

    private static int BinToInt(ReadOnlySpan<bool> bits, int start, int end)
    {
        int v = 0;
        for (int i = start; i < end; i++) v = (v << 1) | (bits[i] ? 1 : 0);
        return v;
    }

    private static long BinToLong(ReadOnlySpan<bool> bits, int start, int end)
    {
        long v = 0;
        for (int i = start; i < end; i++) v = (v << 1) | (bits[i] ? 1L : 0L);
        return v;
    }

    // ── Unpack28 ─────────────────────────────────────────────────────────────

    private bool Unpack28(int n28, out string call)
    {
        call = "";
        if (n28 < NTokens)
        {
            if (n28 == 0) { call = "DE"; return true; }
            if (n28 == 1) { call = "QRZ"; return true; }
            if (n28 == 2) { call = "CQ"; return true; }
            if (n28 <= 1002) { call = $"CQ {n28 - 3:D3}"; return true; }
            if (n28 <= 532443)
            {
                int n = n28 - 1003;
                int i1 = n / (27 * 27 * 27); n -= 27 * 27 * 27 * i1;
                int i2 = n / (27 * 27);       n -= 27 * 27 * i2;
                int i3 = n / 27;
                int i4 = n - 27 * i3;
                string s = $"{A4_28[i1]}{A4_28[i2]}{A4_28[i3]}{A4_28[i4]}".Trim();
                call = "CQ " + s;
                return true;
            }
        }

        int rem = n28 - NTokens;
        if (rem < Max22)
        {
            // 22-bit hash
            call = LookupHash22(rem);
            return true;
        }

        // Standard callsign
        int nn = rem - Max22;
        int ii1 = nn / (36 * 10 * 27 * 27 * 27); nn -= 36 * 10 * 27 * 27 * 27 * ii1;
        int ii2 = nn / (10 * 27 * 27 * 27);       nn -= 10 * 27 * 27 * 27 * ii2;
        int ii3 = nn / (27 * 27 * 27);             nn -= 27 * 27 * 27 * ii3;
        int ii4 = nn / (27 * 27);                  nn -= 27 * 27 * ii4;
        int ii5 = nn / 27;
        int ii6 = nn - 27 * ii5;

        if (ii1 >= A1_28.Length || ii2 >= A2_28.Length || ii3 >= A3_28.Length ||
            ii4 >= A4_28.Length || ii5 >= A4_28.Length || ii6 >= A4_28.Length)
        { call = ""; return false; }

        call = $"{A1_28[ii1]}{A2_28[ii2]}{A3_28[ii3]}{A4_28[ii4]}{A4_28[ii5]}{A4_28[ii6]}".Trim();
        if (call.Contains(' ')) { call = ""; return false; }
        return true;
    }

    // ── Grid unpack ───────────────────────────────────────────────────────────

    private static bool ToGrid4(int n, out string grid)
    {
        grid = "";
        int j1 = n / (18 * 10 * 10); n -= j1 * 18 * 10 * 10;
        int j2 = n / (10 * 10);       n -= j2 * 10 * 10;
        int j3 = n / 10;
        int j4 = n - j3 * 10;
        if (j1 < 0 || j1 > 17 || j2 < 0 || j2 > 17 || j3 < 0 || j3 > 9 || j4 < 0 || j4 > 9) return false;
        grid = new string(new[] { (char)('A' + j1), (char)('A' + j2), (char)('0' + j3), (char)('0' + j4) });
        return true;
    }

    private static bool ToGrid6(int n, out string grid)
    {
        grid = "";
        int j1 = n / (18 * 10 * 10 * 24 * 24); n -= j1 * 18 * 10 * 10 * 24 * 24;
        int j2 = n / (10 * 10 * 24 * 24);       n -= j2 * 10 * 10 * 24 * 24;
        int j3 = n / (10 * 24 * 24);            n -= j3 * 10 * 24 * 24;
        int j4 = n / (24 * 24);                 n -= j4 * 24 * 24;
        int j5 = n / 24;
        int j6 = n - j5 * 24;
        if (j1 < 0 || j1 > 17 || j2 < 0 || j2 > 17 || j3 < 0 || j3 > 9 || j4 < 0 || j4 > 9 ||
            j5 < 0 || j5 > 23 || j6 < 0 || j6 > 23) return false;
        grid = new string(new[] {
            (char)('A'+j1),(char)('A'+j2),(char)('0'+j3),(char)('0'+j4),(char)('A'+j5),(char)('A'+j6)
        });
        return true;
    }

    // ── Text packing (free-text 71 bits via multi-precision arithmetic) ───────

    private string UnpackText77(ReadOnlySpan<bool> c77)
    {
        // 71 bits → 9 bytes (7+8*8 bits) → divide by 42 repeatedly → 13 chars
        byte[] qa = new byte[10];
        qa[0] = 0;
        int pos = 0;
        for (int i = 0; i < 9; i++)
        {
            int end = (i == 0) ? 7 : 8;
            int k = 0;
            for (int j = 0; j < end; j++) k = (k << 1) | (c77[pos++] ? 1 : 0);
            qa[i + 1] = (byte)k;
        }

        var sb = new StringBuilder(13);
        for (int i = 12; i >= 0; i--)
        {
            byte[] qb = new byte[9];
            int ir = MpShortDiv(qb, qa, 1, 9, 42);
            if (ir >= 0 && ir < C77_Txt.Length) sb.Insert(0, C77_Txt[ir]);
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
            ir = k % iv;
        }
        return ir;
    }

    // ── Message format unpackers ──────────────────────────────────────────────

    private string UnpackDxped(ReadOnlySpan<bool> c77, ref bool success)
    {
        int n28a = BinToInt(c77, 0, 28);
        int n28b = BinToInt(c77, 28, 56);
        int n10  = BinToInt(c77, 56, 66);
        int n5   = BinToInt(c77, 66, 71);
        int irpt = 2 * n5 - 30;
        string crpt = irpt >= 0 ? $"+{irpt:D2}" : $"-{Math.Abs(irpt):D2}";
        if (!Unpack28(n28a, out string c1) || n28a <= 2) { success = false; return ""; }
        if (!Unpack28(n28b, out string c2) || n28b <= 2) { success = false; return ""; }
        string c3 = LookupHash10(n10, c1, c2);
        return $"{c1} RR73; {c2} {c3} {crpt}"; // LookupHash10 always returns <...>
    }

    private string UnpackFieldDay(ReadOnlySpan<bool> c77, int n3, ref bool success)
    {
        int n28a   = BinToInt(c77, 0, 28);
        int n28b   = BinToInt(c77, 28, 56);
        int ir     = BinToInt(c77, 56, 57);
        int intx   = BinToInt(c77, 57, 61);
        int nclass = BinToInt(c77, 61, 64);
        int isec   = BinToInt(c77, 64, 71);
        if (!Unpack28(n28a, out string c1) || n28a <= 2) { success = false; return ""; }
        if (!Unpack28(n28b, out string c2) || n28b <= 2) { success = false; return ""; }
        if (isec < 1 || isec > ArrlSections.Length) { success = false; return ""; }
        int ntx = intx + 1 + (n3 == 4 ? 16 : 0);
        string cntx = $"{ntx}{(char)('A' + nclass)}";
        string sec = ArrlSections[isec - 1];
        return ir == 0 ? $"{c1} {c2} {cntx} {sec}" : $"{c1} {c2} R {cntx} {sec}";
    }

    private string UnpackTelemetry(ReadOnlySpan<bool> c77)
    {
        int b23  = BinToInt(c77, 0, 23);
        int b24a = BinToInt(c77, 23, 47);
        int b24b = BinToInt(c77, 47, 71);
        // Each part padded to its full hex width to preserve positional encoding
        string raw = b23.ToString("X6") + b24a.ToString("X6") + b24b.ToString("X6");
        return raw.TrimStart('0');
    }

    private string UnpackType12(ReadOnlySpan<bool> c77, int i3, ref bool success)
    {
        int n28a   = BinToInt(c77, 0, 28);
        int ipa    = BinToInt(c77, 28, 29);
        int n28b   = BinToInt(c77, 29, 57);
        int ipb    = BinToInt(c77, 57, 58);
        int ir     = BinToInt(c77, 58, 59);
        int igrid4 = BinToInt(c77, 59, 74);

        if (!Unpack28(n28a, out string c1)) { success = false; return ""; }
        if (!Unpack28(n28b, out string c2)) { success = false; return ""; }

        // Append /R or /P suffix
        if (!c1.Contains('<') && c1.Length >= 3)
        {
            if (ipa == 1 && i3 == 1) c1 += "/R";
            if (ipa == 1 && i3 == 2) c1 += "/P";
            RegisterCallsign(c1);
        }
        if (!c2.Contains('<') && c2.Length >= 3)
        {
            if (ipb == 1 && i3 == 1) c2 += "/R";
            if (ipb == 1 && i3 == 2) c2 += "/P";
            RegisterCallsign(c2);
        }

        if (igrid4 <= MaxGrid4)
        {
            if (!ToGrid4(igrid4, out string grid)) { success = false; return ""; }
            string m = ir == 0 ? $"{c1} {c2} {grid}" : $"{c1} {c2} R {grid}";
            if (m.StartsWith("CQ ") && ir == 1) { success = false; return ""; }
            return m;
        }
        else
        {
            int irpt = igrid4 - MaxGrid4;
            if (irpt == 1) return $"{c1} {c2}";
            if (irpt == 2) return $"{c1} {c2} RRR";
            if (irpt == 3) return $"{c1} {c2} RR73";
            if (irpt == 4) return $"{c1} {c2} 73";
            int isnr = irpt - 35;
            if (isnr > 50) isnr -= 101;
            string crpt = isnr >= 0 ? $"+{isnr:D2}" : $"-{Math.Abs(isnr):D2}";
            if (irpt >= 106)
            {
                isnr = (irpt - 101) - 35;
                if (isnr > 50) isnr -= 101;
                crpt = isnr >= 0 ? $"+{isnr:D2}" : $"-{Math.Abs(isnr):D2}";
                return ir == 0 ? $"{c1} {c2} {crpt} TU" : $"{c1} {c2} R{crpt} TU";
            }
            return ir == 0 ? $"{c1} {c2} {crpt}" : $"{c1} {c2} R{crpt}";
        }
    }

    private string UnpackRttyContest(ReadOnlySpan<bool> c77, ref bool success)
    {
        int itu   = BinToInt(c77, 0, 1);
        int n28a  = BinToInt(c77, 1, 29);
        int n28b  = BinToInt(c77, 29, 57);
        int ir    = BinToInt(c77, 57, 58);
        int irpt  = BinToInt(c77, 58, 61);
        int nexch = BinToInt(c77, 61, 74);
        string crpt = $"5{irpt + 2}9";
        if (!Unpack28(n28a, out string c1)) { success = false; return ""; }
        if (!Unpack28(n28b, out string c2)) { success = false; return ""; }
        string exc;
        if (nexch > 8000)
        {
            int imult = nexch - 8000;
            exc = (imult >= 1 && imult <= RttyMultipliers.Length) ? RttyMultipliers[imult - 1] : "";
        }
        else if (nexch >= 1)
        {
            exc = $"{nexch:D4}";
        }
        else
        {
            exc = "";
        }
        string prefix = itu == 1 ? "TU; " : "";
        string r = ir == 1 ? " R " : " ";
        return $"{prefix}{c1} {c2}{r}{crpt} {exc}".Trim();
    }

    private string UnpackType4(ReadOnlySpan<bool> c77, ref bool success)
    {
        int n12  = BinToInt(c77, 0, 12);
        long n58 = BinToLong(c77, 12, 70);
        int iflip = BinToInt(c77, 70, 71);
        int nrpt  = BinToInt(c77, 71, 73);
        int icq   = BinToInt(c77, 73, 74);

        // Decode 11-char callsign from base-38 n58
        char[] c11 = new char[11];
        long tmp = n58;
        for (int i = 10; i >= 0; i--)
        {
            c11[i] = C77_04[(int)(tmp % 38)];
            tmp /= 38;
        }
        string c11s = new string(c11).Trim();
        string c3 = LookupHash12(n12, c11s);

        string callA = iflip == 0 ? c3 : c11s;
        string callB = iflip == 0 ? c11s : c3;
        RegisterCallsign(c11s); // always the full (non-hashed) callsign

        if (icq == 1) return $"CQ {callB}";
        return nrpt switch
        {
            0 => $"{callA} {callB}",
            1 => $"{callA} {callB} RRR",
            2 => $"{callA} {callB} RR73",
            3 => $"{callA} {callB} 73",
            _ => $"{callA} {callB}"
        };
    }

    private string UnpackEuVhf(ReadOnlySpan<bool> c77, ref bool success)
    {
        int n12     = BinToInt(c77, 0, 12);
        int n22     = BinToInt(c77, 12, 34);
        int ir      = BinToInt(c77, 34, 35);
        int irpt    = BinToInt(c77, 35, 38);
        int iserial = BinToInt(c77, 38, 49);
        int igrid6  = BinToInt(c77, 49, 74);
        if (igrid6 < 0 || igrid6 > 18662399) { success = false; return ""; }
        string c2 = LookupHash22(n22);
        string c1 = LookupHash12(n12, c2);
        string cexch = $"{52 + irpt:D2}{iserial:D4}";
        if (!ToGrid6(igrid6, out string grid)) { success = false; return ""; }
        return ir == 0 ? $"{c1} {c2} {cexch} {grid}" : $"{c1} {c2} R {cexch} {grid}";
    }

    // ── Hash helpers ──────────────────────────────────────────────────────────

    private string LookupHash10(int n10, string excl1, string excl2)
    {
        for (int i = 0; i < _hashCall.Length; i++)
            if (_hash10[i] == n10 && _hashCall[i] != null &&
                _hashCall[i] != excl1 && _hashCall[i] != excl2)
                return $"<{_hashCall[i]}>";
        return "<...>";
    }

    private string LookupHash12(int n12, string excl)
    {
        for (int i = 0; i < _hashCall.Length; i++)
            if (_hash12[i] == n12 && _hashCall[i] != null && _hashCall[i] != excl)
                return $"<{_hashCall[i]}>";
        return "<...>";
    }

    private string LookupHash22(int n22)
    {
        for (int i = 0; i < _hashCall.Length; i++)
            if (_hash22[i] == n22 && _hashCall[i] != null)
                return $"<{_hashCall[i]}>";
        return "<...>";
    }

    private static void ComputeHashes(string call, out int h10, out int h12, out int h22)
    {
        // Matches WSJTX's ihashcall: n8 = sum of base-38 digits over 11 chars,
        // then extract top m bits of (47055833459 * n8) using unsigned right shift.
        const string c38 = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/";
        long n8 = 0;
        for (int i = 0; i < 11; i++)
        {
            char ch = i < call.Length ? call[i] : ' ';
            int j = c38.IndexOf(ch);
            if (j < 0) j = 0;
            n8 = 38 * n8 + j;
        }
        ulong product = unchecked((ulong)(47055833459L * n8));
        h10 = (int)(product >> 54);   // top 10 bits → 0..1023
        h12 = (int)(product >> 52);   // top 12 bits → 0..4095
        h22 = (int)(product >> 42);   // top 22 bits → 0..4194303
    }
}
