using HamDigiSharp.Models;
using HamDigiSharp.Protocols;

namespace HamDigiSharp.Messaging;

/// <summary>
/// Constructs and validates ham radio digital-mode message strings.
/// Every method returns a <see cref="BuildResult"/> with either the ready-to-encode
/// message string or a human-readable error description.
/// <para>
/// The builder enforces:
/// <list type="bullet">
///   <item>Callsign structure (area digit at position 1 or 2, or compound PREFIX/CALL)</item>
///   <item>Grid4 format ([A-R][A-R][0-9]{2})</item>
///   <item>Per-mode character sets and length limits (via <see cref="ProtocolRegistry"/>)</item>
///   <item>SuperFox-specific constraints (hound counts, report range)</item>
/// </list>
/// </para>
/// </summary>
public static class MessageBuilder
{
    // ── Standard exchange (FT8/FT4/FT2/JT65/Q65/MSK144/MSKMS/JT6M) ─────────

    /// <summary>
    /// Builds a CQ call: <c>"CQ [qualifier] CALLSIGN GRID4"</c>.
    /// </summary>
    /// <param name="callsign">Transmitting station's callsign (e.g. "W1AW").</param>
    /// <param name="grid4">4-character Maidenhead locator (e.g. "FN42").</param>
    /// <param name="qualifier">
    /// Optional CQ qualifier: a 1-4 letter geographic region (e.g. "DX","EU","NA","AP")
    /// or a 1-3 digit frequency in MHz (e.g. "145","009"). Pass <see langword="null"/> to omit.
    /// </param>
    public static BuildResult Cq(string callsign, string grid4, string? qualifier = null)
    {
        callsign = Normalize(callsign);
        grid4    = Normalize(grid4);

        if (!IsValidCallsign(callsign, out string csErr))
            return BuildResult.Fail($"Callsign: {csErr}");
        if (!IsValidGrid4(grid4))
            return BuildResult.Fail($"Grid locator '{grid4}' is not a valid 4-character Maidenhead locator (e.g. FN42)");

        if (qualifier is not null)
        {
            qualifier = qualifier.Trim().ToUpperInvariant();
            if (!IsValidCqQualifier(qualifier))
                return BuildResult.Fail($"CQ qualifier '{qualifier}' must be 1-4 letters (e.g. DX, EU) or 3 digits (e.g. 145)");
            string msg4 = $"CQ {qualifier} {callsign} {grid4}";
            return BuildResult.Ok(msg4);
        }

        return BuildResult.Ok($"CQ {callsign} {grid4}");
    }

    /// <summary>
    /// Builds a point-to-point exchange message: <c>"FROM TO EXCHANGE"</c>.
    /// </summary>
    /// <param name="from">Transmitting station's callsign.</param>
    /// <param name="to">Addressed station's callsign.</param>
    /// <param name="exchange">
    /// Exchange payload: a Maidenhead grid (e.g. "FN42"), a signed SNR report (e.g. "+07", "-12"),
    /// or one of the tokens "RRR", "RR73", "73".
    /// </param>
    public static BuildResult Exchange(string from, string to, string exchange)
    {
        from     = Normalize(from);
        to       = Normalize(to);
        exchange = exchange?.Trim().ToUpperInvariant() ?? "";

        if (!IsValidCallsign(from, out string fromErr))
            return BuildResult.Fail($"From callsign: {fromErr}");
        if (!IsValidCallsign(to, out string toErr))
            return BuildResult.Fail($"To callsign: {toErr}");
        if (!IsValidExchangeToken(exchange, out string exErr))
            return BuildResult.Fail($"Exchange token: {exErr}");

        return BuildResult.Ok($"{from} {to} {exchange}");
    }

    /// <summary>
    /// Builds a point-to-point exchange with a numeric SNR report.
    /// The report is formatted as "+07" or "-12" (two-digit, signed).
    /// </summary>
    /// <param name="from">Transmitting station's callsign.</param>
    /// <param name="to">Addressed station's callsign.</param>
    /// <param name="snrDb">SNR in dB, range −50 to +49.</param>
    public static BuildResult Exchange(string from, string to, int snrDb)
    {
        if (snrDb < -50 || snrDb > 49)
            return BuildResult.Fail($"SNR report {snrDb} dB is outside the valid range −50 to +49");

        string rpt = snrDb >= 0 ? $"+{snrDb:D2}" : $"-{Math.Abs(snrDb):D2}";
        return Exchange(from, to, rpt);
    }

    // ── Free text ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates and normalises a free-text message for the given mode.
    /// Characters are upper-cased; the message must not exceed the mode's
    /// <see cref="IMessageConstraints.MaxLength"/> and must contain only
    /// <see cref="IMessageConstraints.AllowedChars"/>.
    /// </summary>
    public static BuildResult FreeText(string text, DigitalMode mode)
    {
        string normalized = text?.Trim().ToUpperInvariant() ?? "";
        var constraints = ProtocolRegistry.Get(mode).MessageConstraints;

        string? err = constraints.Validate(normalized);
        return err is null ? BuildResult.Ok(normalized) : BuildResult.Fail(err);
    }

    // ── PI4 beacon ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a PI4 beacon message — a callsign padded to exactly 8 characters
    /// from the PI4 alphabet (<c>"0-9A-Z /"</c>).
    /// </summary>
    /// <param name="callsign">Callsign up to 8 characters.</param>
    public static BuildResult Beacon(string callsign)
    {
        string cs = callsign?.Trim().ToUpperInvariant() ?? "";

        if (cs.Length == 0)
            return BuildResult.Fail("Beacon callsign must not be empty");
        if (cs.Length > 8)
            return BuildResult.Fail($"PI4 beacon callsign '{cs}' exceeds 8 characters (got {cs.Length})");

        const string Pi4Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ /";
        foreach (char c in cs)
            if (!Pi4Chars.Contains(c))
                return BuildResult.Fail($"Character '{c}' is not valid in a PI4 callsign (allowed: A-Z 0-9 / space)");

        return BuildResult.Ok(cs.PadRight(8));
    }

    // ── SuperFox ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a SuperFox CQ beacon: <c>"CQ FOXCALL GRID4"</c>.
    /// </summary>
    /// <param name="foxCallsign">Fox station's callsign (up to 11 characters, base-38 alphabet).</param>
    /// <param name="grid4">4-character Maidenhead locator.</param>
    public static BuildResult SuperFoxCq(string foxCallsign, string grid4)
    {
        foxCallsign = Normalize(foxCallsign);
        grid4       = Normalize(grid4);

        if (foxCallsign.Length == 0)
            return BuildResult.Fail("Fox callsign must not be empty");
        if (foxCallsign.Length > 11)
            return BuildResult.Fail($"Fox callsign '{foxCallsign}' exceeds 11 characters");
        if (!IsValidGrid4(grid4))
            return BuildResult.Fail($"Grid locator '{grid4}' is not a valid 4-character Maidenhead locator");

        const string Abc38 = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/";
        foreach (char c in foxCallsign)
            if (!Abc38.Contains(c))
                return BuildResult.Fail($"Character '{c}' is not valid in a SuperFox callsign (base-38 alphabet)");

        return BuildResult.Ok($"CQ {foxCallsign} {grid4}");
    }

    /// <summary>
    /// Builds a SuperFox Fox→Hound compound response frame:
    /// <c>"FOXCALL H1 [±NN] H2 [±NN] …"</c>.
    /// </summary>
    /// <param name="foxCallsign">Fox station's callsign.</param>
    /// <param name="hounds">
    /// Hound entries to include. Maximum 5 with RR73 and 4 with a numeric report (9 total).
    /// Reports must be in the range −18 to +12 dB.
    /// </param>
    public static BuildResult SuperFoxResponse(string foxCallsign, IEnumerable<HoundEntry> hounds)
    {
        foxCallsign = Normalize(foxCallsign);

        if (!IsValidCallsign(foxCallsign, out string foxErr))
            return BuildResult.Fail($"Fox callsign: {foxErr}");

        var houndList = hounds?.ToList() ?? [];
        if (houndList.Count == 0)
            return BuildResult.Fail("SuperFox response must contain at least one hound entry");
        if (houndList.Count > 9)
            return BuildResult.Fail($"SuperFox response supports at most 9 hounds (got {houndList.Count})");

        int rr73Count  = houndList.Count(h => h.IsRr73);
        int rptCount   = houndList.Count(h => !h.IsRr73);
        if (rr73Count > 5)
            return BuildResult.Fail($"SuperFox: at most 5 hounds may receive RR73 per frame (got {rr73Count})");
        if (rptCount > 4)
            return BuildResult.Fail($"SuperFox: at most 4 hounds may receive a numeric report per frame (got {rptCount})");

        var parts = new List<string> { foxCallsign };
        for (int i = 0; i < houndList.Count; i++)
        {
            var h = houndList[i];
            string cs = Normalize(h.Callsign);

            if (!IsValidCallsign(cs, out string hErr))
                return BuildResult.Fail($"Hound[{i}] callsign: {hErr}");

            parts.Add(cs);
            if (!h.IsRr73)
            {
                if (h.ReportDb < -18 || h.ReportDb > 12)
                    return BuildResult.Fail($"Hound[{i}] report {h.ReportDb} dB is outside the SuperFox range −18 to +12");
                int r = h.ReportDb!.Value;
                parts.Add(r >= 0 ? $"+{r:D2}" : $"-{Math.Abs(r):D2}");
            }
        }

        return BuildResult.Ok(string.Join(" ", parts));
    }

    /// <summary>
    /// Builds a SuperFox Fox→Hound compound response frame with an accompanying free-text
    /// message (i3=2 format): <c>"FOXCALL H1 [±NN] H2 [±NN] … ~ FREE TEXT"</c>.
    /// <para>
    /// The tilde (<c>~</c>) separator is the SuperFox i3=2 convention recognised by
    /// <see cref="SuperFoxEncoder"/>. The resulting frame carries up to 4 hound call-signs
    /// and up to 26 characters of free text in the same 15-second transmission.
    /// </para>
    /// </summary>
    /// <param name="foxCallsign">Fox station's callsign.</param>
    /// <param name="hounds">
    /// Up to 4 hound entries. Reports must be in the range −18 to +12 dB.
    /// </param>
    /// <param name="freeText">
    /// Up to 26 characters of free text using the base-42 alphabet
    /// (<c>" 0-9 A-Z + - . / ?"</c>).
    /// </param>
    public static BuildResult SuperFoxTextResponse(
        string foxCallsign,
        IEnumerable<HoundEntry> hounds,
        string freeText)
    {
        foxCallsign = Normalize(foxCallsign);
        if (!IsValidCallsign(foxCallsign, out string foxErr))
            return BuildResult.Fail($"Fox callsign: {foxErr}");

        var houndList = hounds?.ToList() ?? [];
        if (houndList.Count > 4)
            return BuildResult.Fail($"SuperFox text-response supports at most 4 hounds (got {houndList.Count})");

        freeText = Normalize(freeText);
        if (freeText.Length > 26)
            return BuildResult.Fail($"Free text exceeds 26 characters ({freeText.Length})");

        const string Abc42 = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?";
        foreach (char c in freeText)
            if (!Abc42.Contains(c))
                return BuildResult.Fail(
                    $"Character '{c}' is not valid in SuperFox free text (base-42 alphabet: space, 0-9, A-Z, + - . / ?)");

        var parts = new List<string> { foxCallsign };
        for (int i = 0; i < houndList.Count; i++)
        {
            var h = houndList[i];
            string cs = Normalize(h.Callsign);
            if (!IsValidCallsign(cs, out string hErr))
                return BuildResult.Fail($"Hound[{i}] callsign: {hErr}");
            parts.Add(cs);
            if (!h.IsRr73)
            {
                if (h.ReportDb < -18 || h.ReportDb > 12)
                    return BuildResult.Fail(
                        $"Hound[{i}] report {h.ReportDb} dB is outside the SuperFox range −18 to +12");
                int r = h.ReportDb!.Value;
                parts.Add(r >= 0 ? $"+{r:D2}" : $"-{Math.Abs(r):D2}");
            }
        }

        parts.Add("~");
        if (!string.IsNullOrEmpty(freeText)) parts.Add(freeText);
        return BuildResult.Ok(string.Join(" ", parts));
    }

    // ── EU VHF Contest (i3=5) ────────────────────────────────────────────────

    /// <summary>
    /// Builds a European VHF Contest (i3=5) message:
    /// <c>"&lt;CALL1&gt; &lt;CALL2&gt; [R] EXCHANGE GRID6"</c>.
    /// Both callsigns are hash-referenced in the bit frame; the full callsign must
    /// be known to the decoder's hash table for resolution.
    /// </summary>
    /// <param name="call1">Callsign 1 (TO / addressed station). Angle brackets are added automatically.</param>
    /// <param name="call2">Callsign 2 (FROM / transmitting station). Angle brackets are added automatically.</param>
    /// <param name="exchange">
    /// 6-digit EU VHF exchange number (520001–594095).
    /// The first two digits encode the RST prefix (52–59); the last four encode the
    /// serial number (0001–2047). Example: 590003 = RST 599, serial 0003.
    /// </param>
    /// <param name="grid6">6-character Maidenhead locator (e.g. "IO91NP").</param>
    /// <param name="withR"><see langword="true"/> to include the R confirmation flag.</param>
    public static BuildResult EuVhfContest(
        string call1, string call2, int exchange, string grid6, bool withR = false)
    {
        call1 = ToHashedForm(call1);
        call2 = ToHashedForm(call2);
        grid6 = Normalize(grid6);

        string inner1 = call1[1..^1];  // strip < >
        string inner2 = call2[1..^1];

        if (!IsValidCallsign(inner1, out string c1Err))
            return BuildResult.Fail($"Call 1: {c1Err}");
        if (!IsValidCallsign(inner2, out string c2Err))
            return BuildResult.Fail($"Call 2: {c2Err}");
        if (exchange < 520001 || exchange > 594095)
            return BuildResult.Fail(
                $"EU VHF exchange {exchange} is outside the valid range 520001–594095");
        if (!IsValidGrid6(grid6))
            return BuildResult.Fail(
                $"Grid '{grid6}' is not a valid 6-character Maidenhead locator (e.g. IO91NP)");

        string r = withR ? " R" : "";
        return BuildResult.Ok($"{call1} {call2}{r} {exchange:D6} {grid6}");
    }

    private static string ToHashedForm(string? s)
    {
        s = (s ?? "").Trim().ToUpperInvariant();
        if (s.StartsWith('<') && s.EndsWith('>')) return s;
        return $"<{s}>";
    }

    internal static bool IsValidGrid6(string s)
    {
        if (s is not { Length: 6 }) return false;
        return s[0] >= 'A' && s[0] <= 'R' && s[1] >= 'A' && s[1] <= 'R'
            && char.IsDigit(s[2]) && char.IsDigit(s[3])
            && s[4] >= 'A' && s[4] <= 'X' && s[5] >= 'A' && s[5] <= 'X';
    }

    // ── DXpedition (i3=0, n3=1) ──────────────────────────────────────────────

    /// <summary>
    /// Builds a DXpedition multi-QSO response (i3=0, n3=1):
    /// <c>"CALLRR73 RR73; CALLREPORT &lt;DXCALL&gt; REPORT"</c>.
    /// <para>
    /// The DX/expedition station simultaneously confirms a QSO (RR73) to one hound
    /// and sends a new signal report to a second hound. Its own callsign is
    /// hash-referenced so that both common callsigns fit in the 77-bit frame.
    /// </para>
    /// <para>
    /// Only even report values (−30, −28, … +28, +30) are representable in the 5-bit
    /// field; odd inputs are rounded down to the next lower even value.
    /// </para>
    /// </summary>
    /// <param name="callRr73">Callsign receiving the RR73 (QSO complete confirmation).</param>
    /// <param name="callReport">Callsign receiving the signal report.</param>
    /// <param name="dxCallsign">
    /// DX station callsign to hash-reference (e.g. "KH1/KH7Z").
    /// Angle brackets are added automatically if absent.
    /// </param>
    /// <param name="reportDb">Signal report in dB SNR (−30..+30).</param>
    public static BuildResult DXpeditionResponse(
        string callRr73, string callReport, string dxCallsign, int reportDb)
    {
        callRr73   = Normalize(callRr73);
        callReport = Normalize(callReport);

        // Strip angle brackets so we validate the bare callsign
        dxCallsign = Normalize(dxCallsign);
        if (dxCallsign.StartsWith('<') && dxCallsign.EndsWith('>'))
            dxCallsign = dxCallsign[1..^1];

        if (!IsValidCallsign(callRr73, out string c1Err))
            return BuildResult.Fail($"CallRr73: {c1Err}");
        if (!IsValidCallsign(callReport, out string c2Err))
            return BuildResult.Fail($"CallReport: {c2Err}");
        if (!IsValidCallsign(dxCallsign, out string c3Err))
            return BuildResult.Fail($"DX callsign: {c3Err}");
        if (reportDb < -30 || reportDb > 30)
            return BuildResult.Fail(
                $"Report {reportDb} dB is outside the valid range −30..+30");

        // Round to the nearest representable even value: n5 = (irpt+30)/2, decoded = 2*n5-30
        int n5     = (reportDb + 30) / 2;
        int actual = 2 * n5 - 30;
        string rptStr = actual >= 0 ? $"+{actual:D2}" : $"-{Math.Abs(actual):D2}";

        return BuildResult.Ok($"{callRr73} RR73; {callReport} <{dxCallsign}> {rptStr}");
    }

    // ── Generic validation ────────────────────────────────────────────────────

    /// <summary>
    /// Validates <paramref name="message"/> against the constraints of the given mode
    /// (character set and maximum length from <see cref="ProtocolRegistry"/>).
    /// </summary>
    public static BuildResult Validate(string message, DigitalMode mode)
    {
        string normalized = message?.Trim().ToUpperInvariant() ?? "";
        var constraints = ProtocolRegistry.Get(mode).MessageConstraints;
        string? err = constraints.Validate(normalized);
        return err is null ? BuildResult.Ok(normalized) : BuildResult.Fail(err);
    }

    // ── Internal validation helpers ───────────────────────────────────────────

    internal static bool IsValidCallsign(string s, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(s))
        {
            error = "Callsign must not be empty";
            return false;
        }

        // Allow one slash for compound callsigns (PREFIX/CALL or CALL/SUFFIX)
        int slashCount = s.Count(c => c == '/');
        if (slashCount > 1)
        {
            error = $"Callsign '{s}' contains more than one '/'";
            return false;
        }

        foreach (char c in s)
        {
            if (c != '/' && !char.IsLetterOrDigit(c))
            {
                error = $"Callsign '{s}' contains invalid character '{c}'";
                return false;
            }
        }

        if (slashCount == 1)
        {
            int slash = s.IndexOf('/');
            string part1 = s[..slash];
            string part2 = s[(slash + 1)..];
            // At least one part must have standard callsign structure
            if (!HasCallsignStructure(part1) && !HasCallsignStructure(part2))
            {
                error = $"Compound callsign '{s}': neither part has a valid area-digit structure";
                return false;
            }
            return true;
        }

        if (!HasCallsignStructure(s))
        {
            error = $"'{s}' does not look like a callsign (area digit must be at position 1 or 2)";
            return false;
        }

        return true;
    }

    internal static bool IsValidGrid4(string s)
    {
        if (s is not { Length: 4 }) return false;
        char c0 = s[0], c1 = s[1];
        return c0 >= 'A' && c0 <= 'R' && c1 >= 'A' && c1 <= 'R'
            && char.IsDigit(s[2]) && char.IsDigit(s[3]);
    }

    internal static bool IsValidCqQualifier(string q)
    {
        if (q.Length == 0 || q.Length > 4) return false;
        // Numeric: exactly 1-3 digits (MHz frequency e.g. "145", "009")
        if (q.All(char.IsDigit)) return q.Length <= 3;
        // Geographic: 1-4 letters only
        return q.All(char.IsLetter);
    }

    internal static bool IsValidExchangeToken(string s, out string error)
    {
        error = "";
        if (s is "RRR" or "RR73" or "73") return true;
        if (IsValidGrid4(s)) return true;

        // Numeric report: +NN or -NN, optionally R-prefixed
        string rptStr = s.StartsWith('R') ? s[1..] : s;
        if (int.TryParse(rptStr, out int v) && v >= -50 && v <= 49) return true;

        error = $"'{s}' is not a valid exchange token (expected grid, ±dB report, RRR, RR73, or 73)";
        return false;
    }

    private static bool HasCallsignStructure(string s)
    {
        if (s.Length < 2) return false;
        for (int i = 0; i < Math.Min(s.Length, 4); i++)
        {
            if (!char.IsDigit(s[i])) continue;
            return i == 1 || i == 2;
        }
        return false;
    }

    private static string Normalize(string? s) =>
        (s ?? "").Trim().ToUpperInvariant();
}
