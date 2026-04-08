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

// ── EU VHF Contest ───────────────────────────────────────────────────────────

/// <summary>
/// A European VHF Contest (i3=5) message decoded from an FT8/FT4/FT2 frame.
/// Both callsigns are always hash-referenced (shown with angle brackets).
/// Example: <c>"&lt;PA3XYZ/P&gt; &lt;G4ABC/P&gt; R 590003 IO91NP"</c>.
/// </summary>
public sealed record EuVhfContestMessage : ParsedMessage
{
    /// <summary>Callsign 1 (TO / addressed station). E.g. "&lt;PA3XYZ/P&gt;" or "&lt;...&gt;" if hash unknown.</summary>
    public required string Call1 { get; init; }

    /// <summary>Callsign 2 (FROM / transmitting station). E.g. "&lt;G4ABC/P&gt;".</summary>
    public required string Call2 { get; init; }

    /// <summary><see langword="true"/> when the 'R' confirmation flag is set in the frame.</summary>
    public bool HasR { get; init; }

    /// <summary>
    /// Full 6-digit exchange string, e.g. "590003".
    /// The first two digits encode the RST prefix (52–59) and the last four encode the
    /// serial number (0001–2047 zero-padded). The full RST is conventionally the prefix
    /// appended with '9', e.g. "599".
    /// </summary>
    public required string Exchange { get; init; }

    /// <summary>6-character Maidenhead grid locator, e.g. "IO91NP".</summary>
    public required string Grid { get; init; }

    /// <summary>RST prefix (52–59).</summary>
    public int? RstPrefix => Exchange.Length >= 2 && int.TryParse(Exchange[..2], out int v) ? v : null;

    /// <summary>Contest serial number (0–2047).</summary>
    public int? SerialNumber => Exchange.Length >= 6 && int.TryParse(Exchange[2..], out int v) ? v : null;
}

// ── DXpedition messages (i3=0, n3=1) ─────────────────────────────────────────

/// <summary>
/// A DXpedition multi-QSO response (i3=0, n3=1): the DX station simultaneously
/// sends RR73 to one hound (QSO complete) and a signal report to a second hound.
/// The DX station's own callsign is always hash-referenced.
/// Example: <c>"K1ABC RR73; W9XYZ &lt;KH1/KH7Z&gt; -12"</c>.
/// </summary>
public sealed record DXpeditionMessage : ParsedMessage
{
    /// <summary>Callsign receiving the RR73 (QSO complete confirmation).</summary>
    public required string CallRr73 { get; init; }

    /// <summary>Callsign receiving the signal report.</summary>
    public required string CallReport { get; init; }

    /// <summary>
    /// Hashed DX/expedition callsign, e.g. <c>"&lt;KH1/KH7Z&gt;"</c> or
    /// <c>"&lt;...&gt;"</c> when the hash is not in the receiver's lookup table.
    /// </summary>
    public required string DxCallsign { get; init; }

    /// <summary>
    /// Signal report sent to <see cref="CallReport"/>, in dB SNR.
    /// Only even values in the range −30..+30 can be represented in the 5-bit field;
    /// the decoder always emits an even value.
    /// </summary>
    public int ReportDb { get; init; }
}

// ── SuperFox messages ─────────────────────────────────────────────────────────

/// <summary>
/// A SuperFox digital signature token decoded from a Fox transmission (message string
/// <c>"$VERIFY$ FOXCALL SIGCODE"</c>).
/// <para>
/// Emitted alongside the normal hound/CQ messages when the Fox station includes a non-zero
/// 20-bit one-time-pad (OTP) value in bits 306–325 of its SuperFox frame.
/// The receiving application can verify the Fox's authenticity by comparing
/// <see cref="SignatureCode"/> against the expected OTP for the current 30-second UTC period.
/// </para>
/// </summary>
public sealed record SuperFoxSignatureMessage : ParsedMessage
{
    /// <summary>Fox station's callsign (extracted from the same frame as the signature).</summary>
    public required string FoxCallsign { get; init; }

    /// <summary>
    /// 20-bit OTP signature code (1–1,048,575). Zero is never emitted.
    /// In WSJT-X this value is derived from a 30-second-period key seed shared between
    /// the fox operator and their network.
    /// </summary>
    public required uint SignatureCode { get; init; }
}
