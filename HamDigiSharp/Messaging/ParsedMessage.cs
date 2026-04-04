namespace HamDigiSharp.Messaging;

// ── Base type ─────────────────────────────────────────────────────────────────

/// <summary>
/// Structured representation of a decoded or constructed ham radio digital-mode message.
/// Use <see cref="MessageParser.Parse"/> to obtain an instance from a raw string,
/// or pattern-match on the derived types to access specific fields.
/// </summary>
public abstract record ParsedMessage
{
    /// <summary>The original raw message string (trimmed but otherwise unmodified).</summary>
    public required string Raw { get; init; }
}

// ── Standard exchange ─────────────────────────────────────────────────────────

/// <summary>
/// A standard two-callsign exchange message used by FT8, FT4, FT2, JT65, Q65,
/// MSK144, MSKMS, JT6M and SuperFox (hound–fox side).
/// <para>
/// Examples:
/// <list type="bullet">
///   <item><c>"CQ W1AW FN42"</c>        — <see cref="MessageDirection.CQ"/>, From="W1AW", Exchange="FN42"</item>
///   <item><c>"CQ DX W1AW FN42"</c>     — CQ, CqQualifier="DX", From="W1AW", Exchange="FN42"</item>
///   <item><c>"W1AW K9AN -07"</c>        — Exchange, From="W1AW", To="K9AN", Exchange="-07"</item>
///   <item><c>"W1AW K9AN RR73"</c>       — Exchange, From="W1AW", To="K9AN", Exchange="RR73"</item>
/// </list>
/// </para>
/// </summary>
public sealed record StandardMessage : ParsedMessage
{
    /// <inheritdoc cref="MessageDirection"/>
    public MessageDirection Direction { get; init; }

    /// <summary>
    /// Optional CQ qualifier (e.g. "DX", "EU", "NA", "145").
    /// Only set when <see cref="Direction"/> is <see cref="MessageDirection.CQ"/>
    /// and the message contains a geographic or numeric qualifier token.
    /// </summary>
    public string? CqQualifier { get; init; }

    /// <summary>
    /// The transmitting station's callsign (first callsign field).
    /// For CQ messages this is the calling station; for exchanges it is the replying station.
    /// </summary>
    public required string From { get; init; }

    /// <summary>
    /// The addressed station's callsign (second callsign field).
    /// <see langword="null"/> for CQ/QRZ/DE messages where no specific station is called.
    /// </summary>
    public string? To { get; init; }

    /// <summary>
    /// The exchange payload: a Maidenhead grid locator, SNR report ("+07", "−12"),
    /// or one of the special tokens "RRR", "RR73", "73".
    /// <see langword="null"/> if no exchange token is present.
    /// </summary>
    public string? Exchange { get; init; }

    /// <summary>
    /// Attempts to parse <see cref="Exchange"/> as a numeric SNR report.
    /// Returns <see langword="null"/> if Exchange is not a numeric report.
    /// </summary>
    public int? SnrDb => TryParseReport(Exchange, out int v) ? v : null;

    /// <summary>
    /// <see langword="true"/> if <see cref="Exchange"/> is a 4-character Maidenhead locator.
    /// </summary>
    public bool HasGrid => Exchange is { Length: 4 } g
        && Exchange is not ("RR73" or "RRR" or "73")
        && g[0] is >= 'A' and <= 'R' && g[1] is >= 'A' and <= 'R'
        && char.IsDigit(g[2])  && char.IsDigit(g[3]);

    private static bool TryParseReport(string? s, out int v)
    {
        v = 0;
        return s is not null && int.TryParse(s, out v);
    }
}

// ── Free text ─────────────────────────────────────────────────────────────────

/// <summary>
/// A free-form text message. Used by ISCAT, FSK441, FSK315, JTMS, and as a fallback
/// for standard-mode messages that do not match any structured pattern.
/// </summary>
public sealed record FreeTextMessage : ParsedMessage
{
    /// <summary>Decoded text (trimmed, upper-case).</summary>
    public required string Text { get; init; }
}

// ── PI4 beacon ────────────────────────────────────────────────────────────────

/// <summary>
/// A PI4 beacon transmission containing a single callsign (up to 8 characters).
/// </summary>
public sealed record BeaconMessage : ParsedMessage
{
    /// <summary>The beacon station's callsign (trimmed, up to 8 characters).</summary>
    public required string Callsign { get; init; }
}

// ── SuperFox messages ─────────────────────────────────────────────────────────

/// <summary>
/// A SuperFox Fox→CQ beacon: <c>"CQ FOXCALL GRID4"</c>.
/// </summary>
public sealed record SuperFoxCqMessage : ParsedMessage
{
    /// <summary>Fox station's callsign (e.g. "LZ2HVV").</summary>
    public required string FoxCallsign { get; init; }

    /// <summary>Fox station's 4-character Maidenhead grid locator (e.g. "KN23").</summary>
    public required string Grid { get; init; }
}

/// <summary>
/// A SuperFox Fox→Hound compound response frame: <c>"FOXCALL H1 [±NN] H2 [±NN] …"</c>.
/// Each <see cref="HoundEntry"/> represents one hound station and its disposition
/// (numeric SNR report, or RR73 when <see cref="HoundEntry.IsRr73"/> is <see langword="true"/>).
/// </summary>
public sealed record SuperFoxResponseMessage : ParsedMessage
{
    /// <summary>Fox station's callsign.</summary>
    public required string FoxCallsign { get; init; }

    /// <summary>
    /// Ordered list of hound entries in this frame.
    /// Up to 5 entries may carry RR73 (QSO complete) and up to 4 may carry a numeric report.
    /// Total capacity: 9 hounds per frame.
    /// </summary>
    public IReadOnlyList<HoundEntry> Hounds { get; init; } = [];
}
