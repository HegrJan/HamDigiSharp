namespace HamDigiSharp.Codecs;

/// <summary>
/// WSPR message packing / unpacking.
/// Mirrors the encoding in wsprcode.f90 / wsprd.c from WSJT-X (K1JT et al.), GPL.
/// </summary>
internal static class WsprPack
{
    /// <summary>Maximum packed callsign value (37×36×10×27×27×27).</summary>
    internal const long NBase = 37L * 36 * 10 * 27 * 27 * 27; // 262 177 560

    /// <summary>Maximum packed grid value (180×180).</summary>
    internal const int NgBase = 180 * 180; // 32 400

    // ── Character encoding ────────────────────────────────────────────────────

    /// <summary>'0'-'9'→0-9, 'A'-'Z'/'a'-'z'→10-35, else→36 (space/pad).</summary>
    private static int NChar(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
        if (c >= 'a' && c <= 'z') return c - 'a' + 10;
        return 36; // space / unknown
    }

    // ── Pack / unpack callsign ────────────────────────────────────────────────

    /// <summary>
    /// Packs <paramref name="callsign"/> into a 28-bit integer <paramref name="ncall"/>.
    /// Returns false if the callsign cannot be represented.
    /// </summary>
    internal static bool PackCall(string callsign, out int ncall)
    {
        ncall = 0;
        if (string.IsNullOrWhiteSpace(callsign)) return false;

        string s = callsign.Trim().ToUpperInvariant();

        // ── Special tokens ──────────────────────────────────────────────────
        if (s == "CQ")  { ncall = (int)NBase + 1; return true; }
        if (s == "QRZ") { ncall = (int)NBase + 2; return true; }

        // "CQ NNN" – CQ with frequency (NNN = 3-digit integer)
        if (s.Length == 6 && s.StartsWith("CQ ") &&
            int.TryParse(s.Substring(3), out int nf) && nf >= 0 && nf <= 999)
        {
            ncall = (int)NBase + 3 + nf;
            return true;
        }

        // ── 3DA0 workaround (Eswatini) ───────────────────────────────────────
        // "3DA0XX" cannot be normalised normally; encode as "3D0XX"
        if (s.Length >= 4 && s.StartsWith("3DA0"))
            s = "3D0" + s.Substring(4);

        // ── Normalise to 6 characters ────────────────────────────────────────
        // A standard callsign has its digit at position 2 (0-indexed).
        string tmp = s.PadRight(6).Substring(0, 6);
        if (!(tmp[2] >= '0' && tmp[2] <= '9'))
        {
            // Try left-padding with a space (e.g. "W1AW  " → " W1AW ")
            tmp = (" " + s).PadRight(6).Substring(0, 6);
        }

        // ── Validate ─────────────────────────────────────────────────────────
        for (int i = 0; i < 6; i++)
        {
            char c = tmp[i];
            if (i == 2)
            {
                if (c < '0' || c > '9') return false;
            }
            else if (i <= 1)
            {
                // positions 0-1: alphanumeric or space
                bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || c == ' ';
                if (!ok) return false;
            }
            else
            {
                // positions 3-5: alpha or space only
                bool ok = (c >= 'A' && c <= 'Z') || c == ' ';
                if (!ok) return false;
            }
        }

        // ── Encode ───────────────────────────────────────────────────────────
        int n = NChar(tmp[0]);
        n = 36 * n + NChar(tmp[1]);
        n = 10 * n + (tmp[2] - '0');
        n = 27 * n + (NChar(tmp[3]) - 10);
        n = 27 * n + (NChar(tmp[4]) - 10);
        n = 27 * n + (NChar(tmp[5]) - 10);
        ncall = n;
        return true;
    }

    /// <summary>Unpacks a 28-bit integer back to a callsign string.</summary>
    internal static void UnpackCall(int ncall, out string call)
    {
        if (ncall >= (int)NBase + 1)
        {
            int extra = ncall - (int)NBase;
            if (extra == 1) { call = "CQ";  return; }
            if (extra == 2) { call = "QRZ"; return; }
            if (extra >= 3 && extra <= 1002)
                { call = $"CQ {(extra - 3):D3}"; return; }
            call = "???";
            return;
        }

        const string CharSet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ ";

        int n = ncall;
        char c6 = CharSet[n % 27 + 10]; n /= 27;
        char c5 = CharSet[n % 27 + 10]; n /= 27;
        char c4 = CharSet[n % 27 + 10]; n /= 27;
        char c3 = CharSet[n % 10];      n /= 10;
        char c2 = CharSet[n % 36];      n /= 36;
        char c1 = CharSet[n];

        string raw = $"{c1}{c2}{c3}{c4}{c5}{c6}".Trim();

        // Reverse the 3DA0 workaround
        if (raw.StartsWith("3D0"))
            raw = "3DA0" + raw.Substring(3);

        call = raw;
    }

    // ── Pack / unpack grid locator / report token ─────────────────────────────

    /// <summary>
    /// Packs a Maidenhead locator or report token into a 15-bit integer.
    /// Returns false only on malformed input; blank maps to NgBase+1.
    /// </summary>
    internal static bool PackGrid(string grid, out int ng)
    {
        ng = NgBase + 1; // default: blank
        if (string.IsNullOrWhiteSpace(grid)) return true;

        string g = grid.Trim().ToUpperInvariant();

        // Report tokens
        if (g == "RO")  { ng = NgBase + 62; return true; }
        if (g == "RRR") { ng = NgBase + 63; return true; }
        if (g == "73")  { ng = NgBase + 64; return true; }

        // "-NN" (SNR report -01 to -30)
        if (g.Length >= 3 && g[0] == '-' &&
            int.TryParse(g.Substring(1), out int neg) && neg >= 1 && neg <= 30)
        {
            ng = NgBase + 1 + neg;
            return true;
        }

        // "R-NN"
        if (g.Length >= 4 && g[0] == 'R' && g[1] == '-' &&
            int.TryParse(g.Substring(2), out int rneg) && rneg >= 1 && rneg <= 30)
        {
            ng = NgBase + 31 + rneg;
            return true;
        }

        // Maidenhead locator (4-char minimum: e.g. "JO70")
        if (g.Length >= 4 &&
            g[0] >= 'A' && g[0] <= 'R' &&
            g[1] >= 'A' && g[1] <= 'R' &&
            g[2] >= '0' && g[2] <= '9' &&
            g[3] >= '0' && g[3] <= '9')
        {
            // Faithful port of wspr_old_subs.f90 packgrid:
            //   call grid2deg(grid//'mm', dlong, dlat)   with 'm'-'a'=12
            //   ng = ((int(dlong)+180)/2)*180 + int(dlat+90)
            // 'mm' subgrid = centre of 5-min subsquare → xminlong=62.5′, xminlat=31.25′
            double dlong = 180.0 - 20.0*(g[0]-'A') - 2.0*(g[2]-'0') - 62.5/60.0;
            double dlat  = -90.0 + 10.0*(g[1]-'A') + (g[3]-'0') + 31.25/60.0;
            int longInt = (int)dlong;          // C# truncates toward zero, same as Fortran
            int latInt  = (int)(dlat + 90.0);
            ng = ((longInt + 180) / 2) * 180 + latInt;
            return true;
        }

        return false;
    }

    /// <summary>Unpacks a 15-bit integer back to a Maidenhead locator or report token.</summary>
    internal static void UnpackGrid(int ng, out string grid)
    {
        if (ng < NgBase)
        {
            // Faithful port of wspr_old_subs.f90 unpackgrid + deg2grid:
            //   dlat  = mod(ng,180) - 90
            //   dlong = (ng/180)*2 - 180 + 2
            //   then call deg2grid(dlong,dlat,grid6); grid=grid6(1:4)
            double dlat  = ng % 180 - 90.0;
            double dlong = (ng / 180) * 2.0 - 180.0 + 2.0;

            // deg2grid (grid2deg inverse) — convert to 4-char Maidenhead
            double nlong = 60.0 * (180.0 - dlong) / 5.0;
            int lon1 = (int)(nlong / 240.0);
            int lon2 = (int)((nlong - 240.0 * lon1) / 24.0);
            double nlat = 60.0 * (dlat + 90.0) / 2.5;
            int lat1 = (int)(nlat / 240.0);
            int lat2 = (int)((nlat - 240.0 * lat1) / 24.0);
            grid = $"{(char)('A' + lon1)}{(char)('A' + lat1)}{lon2}{lat2}";
            return;
        }

        int n = ng - NgBase - 1;
        if (n == 0)           { grid = "    "; return; }
        if (n >= 1 && n <= 30)  { grid = $"-{n / 10}{n % 10}"; return; }
        if (n >= 31 && n <= 60) { int r = n - 30; grid = $"R-{r / 10}{r % 10}"; return; }
        if (n == 61) { grid = "RO";  return; }
        if (n == 62) { grid = "RRR"; return; }
        if (n == 63) { grid = "73";  return; }

        grid = "????";
    }

    // ── Pack50 / Unpack50 ─────────────────────────────────────────────────────

    /// <summary>
    /// Packs 28-bit <paramref name="n1"/> (callsign) and 22-bit <paramref name="n2"/>
    /// (grid+power) into 7 bytes (only the top 50 bits are used; low 6 bits of dat[6]
    /// are zero padding / trellis tail space).
    /// </summary>
    internal static void Pack50(int n1, int n2, byte[] dat)
    {
        dat[0] = (byte)((n1 >> 20) & 0xFF);
        dat[1] = (byte)((n1 >> 12) & 0xFF);
        dat[2] = (byte)((n1 >>  4) & 0xFF);
        dat[3] = (byte)(((n1 & 0xF) << 4) | ((n2 >> 18) & 0xF));
        dat[4] = (byte)((n2 >> 10) & 0xFF);
        dat[5] = (byte)((n2 >>  2) & 0xFF);
        dat[6] = (byte)((n2 &  0x3) << 6);
    }

    /// <summary>Unpacks 7 bytes back into the original n1/n2 integers.</summary>
    internal static void Unpack50(ReadOnlySpan<byte> dat, out int n1, out int n2)
    {
        n1 = (dat[0] << 20) | (dat[1] << 12) | (dat[2] << 4) | (dat[3] >> 4);
        n2 = ((dat[3] & 0xF) << 18) | (dat[4] << 10) | (dat[5] << 2) | (dat[6] >> 6);
    }

    // ── High-level encode / decode ────────────────────────────────────────────

    // Rounding table: maps dBm % 10 → adjustment so result ends in 0, 3, or 7.
    private static readonly int[] Nu = { 0, -1, 1, 0, -1, 2, 1, 0, -1, 1 };

    /// <summary>
    /// Encodes a "CALL GRID dBm" message into a 7-byte packed array.
    /// Returns false if the message cannot be encoded.
    /// </summary>
    internal static bool TryEncode(string message, out byte[] dat)
    {
        dat = new byte[7];

        // Parse "CALL GRID POWER" — require exactly three whitespace-separated tokens
        string[] parts = message.Trim().ToUpperInvariant()
                                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return false;

        if (!PackCall(parts[0], out int n1)) return false;
        if (!PackGrid(parts[1], out int ng)) return false;
        if (!int.TryParse(parts[2], out int ndbm)) return false;

        // Round dBm to the nearest valid WSPR power level (ends in 0, 3, or 7)
        ndbm += Nu[((ndbm % 10) + 10) % 10];
        if (ndbm < 0 || ndbm > 60) return false;

        int n2 = 128 * ng + (ndbm + 64);
        Pack50(n1, n2, dat);
        return true;
    }

    /// <summary>
    /// Decodes a 7-byte packed array into a "CALL GRID dBm" message string.
    /// </summary>
    internal static string Decode(ReadOnlySpan<byte> dat)
    {
        Unpack50(dat, out int n1, out int n2);

        UnpackCall(n1, out string call);

        int ng    = n2 >> 7;
        int ntype = (n2 & 127) - 64;

        UnpackGrid(ng, out string grid);

        return $"{call} {grid} {ntype}";
    }
}
