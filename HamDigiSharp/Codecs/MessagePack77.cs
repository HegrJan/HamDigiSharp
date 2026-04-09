namespace HamDigiSharp.Codecs;

/// <summary>
/// Encodes human-readable FT8/FT4/FT2 text messages into 77-bit bool arrays.
/// Supports Type 1 structured messages (two callsigns + grid/report), DXpedition,
/// EU VHF Contest, and free-text (up to 13 characters, i3=0 n3=0 base-42 encoding).
/// Mirrors MSHV's <c>PackUnpackMsg77::pack77</c> for the encoding direction.
/// </summary>
public static class MessagePack77
{
    private const int NTokens  = 2_063_592;
    private const int Max22    = 4_194_304;
    private const int MaxGrid4 = 32_400;

    private static readonly string A1 = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"; // 37
    private static readonly string A2 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";  // 36
    private static readonly string A3 = "0123456789";                              // 10
    private static readonly string A4 = " ABCDEFGHIJKLMNOPQRSTUVWXYZ";            // 27

    /// <summary>
    /// Pack a text message into a 77-bit bool array.
    /// Returns false if the message could not be packed (format not supported).
    /// </summary>
    public static bool TryPack77(string message, bool[] c77)
    {
        if (c77.Length < 77) throw new ArgumentException("c77 must be at least 77 elements.");
        Array.Clear(c77, 0, 77);

        string msg    = message.Trim().ToUpperInvariant();
        string[] words = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;

        // EU VHF Contest (i3=5): both calls are angle-bracket hash references
        if (words.Length is 4 or 5 && words[0].StartsWith('<') && words[1].StartsWith('<'))
            return TryPackEuVhf(words, c77);

        // DXpedition (i3=0, n3=1): "CALL1 RR73; CALL2 <DX> REPORT" — 5 words, sentinel is "RR73;"
        if (words.Length == 5 && words[1] == "RR73;" && words[3].StartsWith('<'))
            return TryPackDxped(words, c77);

        if (words.Length >= 2 && words.Length <= 4 && TryPackType1(words, c77)) return true;

        // Free-text fallback (i3=0, n3=0): up to 13 characters from the base-42 alphabet.
        return msg.Length <= 13 && TryPackFreeText(msg, c77);
    }

    private static bool TryPackType1(string[] words, bool[] c77)
    {
        if (words.Length < 2) return false;

        int n28a, n28b;
        int ir = 0, irpt = 1, igrid4 = MaxGrid4 + irpt;

        // Detect "CQ <qualifier> <callsign> <grid/report>" — 4-word messages where the
        // second word is a geographic (e.g. "DX","EU","NA") or numeric (e.g. "009","145")
        // CQ qualifier.  In this layout words[0..1] encode as a single n28a value, and
        // words[2] is the called-station callsign.
        //
        // Without this special-case, Pack28("DX") falls through to its non-callsign
        // path and returns 0 (= "DE"), silently corrupting the message.
        if (words.Length == 4 && words[0] == "CQ")
        {
            string qualifier = words[1];
            int qualN28;

            if (int.TryParse(qualifier, out int nqsy) && nqsy >= 0 && nqsy <= 999)
            {
                qualN28 = 3 + nqsy; // Numeric CQ: "CQ 009 W1AW FN42" → n28a = 12
            }
            else if (!TryPackCqSuffix(qualifier, out qualN28))
            {
                goto defaultParse; // Not a recognised qualifier → fall through
            }

            n28a = qualN28;
            if (!TryPack28Valid(words[2], out n28b)) return false;

            string last4 = words[3];
            if (TryParseGrid4(last4, out int g4))          igrid4 = g4;
            else if (last4 == "RRR")                       igrid4 = MaxGrid4 + 2;
            else if (last4 == "RR73")                      igrid4 = MaxGrid4 + 3;
            else if (last4 == "73")                        igrid4 = MaxGrid4 + 4;
            else if (last4.StartsWith("+") || last4.StartsWith("-"))
            {
                if (int.TryParse(last4, out int rpt))      igrid4 = MaxGrid4 + NormaliseReport(rpt);
                else return false;
            }
            else return false;

            goto packBits;
        }

        defaultParse:
        if (!TryPack28Valid(words[0], out n28a)) return false;
        if (!TryPack28Valid(words[1], out n28b)) return false;

        if (words.Length >= 3)
        {
            string last = words[words.Length - 1];
            string prev = words.Length >= 4 ? words[2] : "";

            if (TryParseGrid4(last, out int g))
            {
                igrid4 = g;
                ir = (words.Length >= 4 && prev == "R") ? 1 : 0;
            }
            else if (last == "RRR")   { ir = 0; igrid4 = MaxGrid4 + 2; }
            else if (last == "RR73")  { ir = 0; igrid4 = MaxGrid4 + 3; }
            else if (last == "73")    { ir = 0; igrid4 = MaxGrid4 + 4; }
            else if (last.StartsWith("R+") || last.StartsWith("R-"))
            {
                if (int.TryParse(last[1..], out int rpt))
                {
                    ir    = 1;
                    irpt  = NormaliseReport(rpt);
                    igrid4 = MaxGrid4 + irpt;
                }
                else return false;
            }
            else if (last.StartsWith("+") || last.StartsWith("-"))
            {
                if (int.TryParse(last, out int rpt))
                {
                    ir    = 0;
                    irpt  = NormaliseReport(rpt);
                    igrid4 = MaxGrid4 + irpt;
                }
                else return false;
            }
            else return false;
        }

        packBits:
        int i3  = 1;
        int ipa = 0;
        int ipb = 0;

        int pos = 0;
        SetBits(n28a,  28, c77, ref pos);
        SetBits(ipa,    1, c77, ref pos);
        SetBits(n28b,  28, c77, ref pos);
        SetBits(ipb,    1, c77, ref pos);
        SetBits(ir,     1, c77, ref pos);
        SetBits(igrid4,15, c77, ref pos);
        SetBits(i3,     3, c77, ref pos);
        return true;
    }

    /// <summary>
    /// Encodes a 1–4 letter CQ geographic suffix ("DX","EU","NA","AP", etc.)
    /// into the n28 range 1003..532443, matching <see cref="MessagePacker.Unpack28"/>.
    /// </summary>
    private static bool TryPackCqSuffix(string qualifier, out int n28)
    {
        n28 = 0;
        // Must be 1–4 uppercase letters from the A4_28 alphabet (no digits or symbols)
        if (qualifier.Length == 0 || qualifier.Length > 4) return false;
        string s = qualifier.ToUpperInvariant().PadRight(4)[..4];
        const string A4_28 = " ABCDEFGHIJKLMNOPQRSTUVWXYZ"; // 27 chars, index 0 = ' '
        int i1 = A4_28.IndexOf(s[0]);
        int i2 = A4_28.IndexOf(s[1]);
        int i3 = A4_28.IndexOf(s[2]);
        int i4 = A4_28.IndexOf(s[3]);
        if (i1 <= 0 || i2 < 0 || i3 < 0 || i4 < 0) return false; // first char must be a letter
        n28 = 1003 + 27 * 27 * 27 * i1 + 27 * 27 * i2 + 27 * i3 + i4;
        return n28 <= 532443;
    }

    /// <summary>Encode a callsign or special token into a 28-bit integer.</summary>
    public static int Pack28(string token)
    {
        string t = token.Trim().ToUpperInvariant();

        if (t == "DE")  return 0;
        if (t == "QRZ") return 1;
        if (t == "CQ")  return 2;
        if (t.StartsWith("CQ_"))
        {
            string suffix = t[3..];
            if (int.TryParse(suffix, out int nqsy) && nqsy >= 0 && nqsy <= 999)
                return 3 + nqsy;
        }

        // Strip /R and /P suffixes
        string cs = t.Replace("/R", "").Replace("/P", "");

        // Find area digit position (must be at index 1 or 2)
        int digitPos = -1;
        for (int i = 0; i < Math.Min(cs.Length, 4); i++)
        {
            if (char.IsDigit(cs[i])) { digitPos = i; break; }
        }

        string cs6;
        if (digitPos == 1)
            cs6 = (" " + cs).PadRight(6)[..6]; // 1-letter prefix → prepend space
        else if (digitPos == 2)
            cs6 = cs.PadRight(6)[..6];          // 2-letter prefix
        else
            return 0; // non-standard fallback

        int i1 = A1.IndexOf(cs6[0]);
        int i2 = A2.IndexOf(cs6[1]);
        int i3 = A3.IndexOf(cs6[2]);
        int i4 = A4.IndexOf(cs6[3]);
        int i5 = A4.IndexOf(cs6[4]);
        int i6 = A4.IndexOf(cs6[5]);

        if (i1 < 0 || i2 < 0 || i3 < 0 || i4 < 0 || i5 < 0 || i6 < 0) return 0;

        long n = (long)36 * 10 * 27 * 27 * 27 * i1
               + (long)10 * 27 * 27 * 27 * i2
               + (long)27 * 27 * 27 * i3
               + (long)27 * 27 * i4
               + (long)27 * i5
               + i6;
        int n28 = (int)((n + NTokens + Max22) & ((1L << 28) - 1));
        return n28;
    }

    /// <summary>
    /// Packs a token and returns true only if the result represents a recognized special
    /// token (DE/QRZ/CQ/CQ_*) or a valid callsign.  Returns false for words that contain
    /// no area digit and are not recognized special tokens, preventing silent fallback to
    /// n28=0 ("DE") which would corrupt the structured message.
    /// </summary>
    private static bool TryPack28Valid(string word, out int n28)
    {
        n28 = Pack28(word);
        // n28=0 means either the literal "DE" was passed, or Pack28 couldn't find an
        // area digit and fell through to its 0-return fallback (invalid callsign).
        if (n28 != 0) return true;
        string t = word.Trim().ToUpperInvariant();
        return t == "DE";
    }


    public static bool TryParseGrid4(string s, out int igrid4)
    {
        igrid4 = 0;
        if (s.Length < 4) return false;
        char c0 = char.ToUpperInvariant(s[0]);
        char c1 = char.ToUpperInvariant(s[1]);
        if (!char.IsLetter(c0) || !char.IsLetter(c1)) return false;
        if (!char.IsDigit(s[2]) || !char.IsDigit(s[3])) return false;
        igrid4 = (c0 - 'A') * 1800 + (c1 - 'A') * 100 + (s[2] - '0') * 10 + (s[3] - '0');
        return igrid4 < MaxGrid4;
    }

    private static int NormaliseReport(int rpt)
    {
        if (rpt >= -50 && rpt <= -31) rpt += 101;
        return rpt + 35;
    }

    private static void SetBits(int value, int nBits, bool[] c77, ref int pos)
    {
        for (int i = nBits - 1; i >= 0; i--)
            c77[pos++] = ((value >> i) & 1) == 1;
    }

    // ── DXpedition (i3=0, n3=1) ─────────────────────────────────────────────

    /// <summary>
    /// Packs a DXpedition multi-QSO response: <c>"CALL1 RR73; CALL2 &lt;DXCALL&gt; REPORT"</c>.
    /// Bit layout: n28a(28) + n28b(28) + n10(10) + n5(5) + n3=1(3) + i3=0(3) = 77.
    /// </summary>
    private static bool TryPackDxped(string[] words, bool[] c77)
    {
        // words[0]=CALL1  words[1]="RR73;"  words[2]=CALL2  words[3]=<DXCALL>  words[4]=REPORT
        if (!int.TryParse(words[4], out int irpt)) return false;

        int n28a = Pack28(words[0]);
        int n28b = Pack28(words[2]);
        if (n28a <= 2 || n28b <= 2) return false; // must be real callsigns, not DE/QRZ/CQ

        // Strip angle brackets to get the raw DX callsign for hashing
        string dxRaw = words[3][1..^1];
        if (dxRaw.Length == 0 || dxRaw == "...") return false; // unknown hash — can't encode

        // n5 is a 5-bit value (0..31) encoding the report: irpt = 2*n5 - 30
        int n5 = Math.Clamp((irpt + 30) / 2, 0, 31);

        int n10 = IHashCall(dxRaw, 10);
        const int n3 = 1;
        const int i3 = 0;

        int pos = 0;
        SetBits(n28a, 28, c77, ref pos);
        SetBits(n28b, 28, c77, ref pos);
        SetBits(n10,  10, c77, ref pos);
        SetBits(n5,    5, c77, ref pos);
        SetBits(n3,    3, c77, ref pos);
        SetBits(i3,    3, c77, ref pos);
        return true;
    }

    // ── EU VHF Contest (i3=5) ────────────────────────────────────────────────

    private static bool TryPackEuVhf(string[] words, bool[] c77)
    {
        // words: <C1> <C2> [R] EXCHANGE GRID6
        bool hasR = words.Length == 5 && words[2] == "R";
        if (words.Length == 5 && !hasR) return false;

        int exchIdx = hasR ? 3 : 2;
        int gridIdx = hasR ? 4 : 3;

        if (!long.TryParse(words[exchIdx], out long nx) || nx < 520001 || nx > 594095)
            return false;
        if (!TryEncodeGrid6(words[gridIdx], out int igrid6)) return false;

        // Extract callsigns from angle brackets
        string raw1 = words[0][1..^1];  // strip < >
        string raw2 = words[1][1..^1];
        if (raw1.Length == 0 || raw1 == "..." || raw2.Length == 0 || raw2 == "...") return false;

        int irpt    = (int)(nx / 10000 - 52);  // 0..7
        int iserial = (int)(nx % 10000);
        if (iserial > 2047) iserial = 2047;
        int ir = hasR ? 1 : 0;

        int n12 = IHashCall(raw1, 12);
        int n22 = IHashCall(raw2, 22);
        const int i3 = 5;

        int pos = 0;
        SetBits(n12,     12, c77, ref pos);
        SetBits(n22,     22, c77, ref pos);
        SetBits(ir,       1, c77, ref pos);
        SetBits(irpt,     3, c77, ref pos);
        SetBits(iserial, 11, c77, ref pos);
        SetBits(igrid6,  25, c77, ref pos);
        SetBits(i3,       3, c77, ref pos);
        return true;
    }

    private static bool TryEncodeGrid6(string grid6, out int igrid6)
    {
        igrid6 = 0;
        if (grid6.Length != 6) return false;
        char c0 = grid6[0], c1 = grid6[1], c2 = grid6[2], c3 = grid6[3], c4 = grid6[4], c5 = grid6[5];
        if (c0 < 'A' || c0 > 'R' || c1 < 'A' || c1 > 'R') return false;
        if (!char.IsDigit(c2) || !char.IsDigit(c3)) return false;
        if (c4 < 'A' || c4 > 'X' || c5 < 'A' || c5 > 'X') return false;
        igrid6 = (c0 - 'A') * (18 * 10 * 10 * 24 * 24)
               + (c1 - 'A') * (10 * 10 * 24 * 24)
               + (c2 - '0') * (10 * 24 * 24)
               + (c3 - '0') * (24 * 24)
               + (c4 - 'A') * 24
               + (c5 - 'A');
        return igrid6 <= 18662399;
    }

    // ── Free-text (i3=0, n3=0) ───────────────────────────────────────────────

    private const string FreeTextAlphabet = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?";

    /// <summary>
    /// Packs up to 13 characters into 71 bits using the base-42 alphabet
    /// (i3=0, n3=0). Short messages are right-padded with spaces.
    /// </summary>
    private static bool TryPackFreeText(string msg, bool[] c77)
    {
        // Pad or truncate to exactly 13 characters
        string padded = msg.Length <= 13 ? msg.PadRight(13) : msg[..13];

        UInt128 n = 0;
        foreach (char ch in padded)
        {
            int idx = FreeTextAlphabet.IndexOf(char.ToUpperInvariant(ch));
            if (idx < 0) return false;
            n = n * 42 + (UInt128)idx;
        }

        // n must fit within 71 bits
        if (n >= ((UInt128)1 << 71)) return false;

        // Store n big-endian in c77[0..70]; c77[71..76] stay 0 (n3=0, i3=0)
        for (int i = 70; i >= 0; i--)
            c77[70 - i] = ((n >> i) & 1) == 1;

        return true;
    }


    private static int IHashCall(string call, int m)
    {
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
        return (int)(product >> (64 - m));
    }
}
