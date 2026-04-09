using HamDigiSharp.Abstractions;

namespace HamDigiSharp.Models;

/// <summary>
/// Immutable description of the message format a protocol encoder accepts.
/// Use the static factory methods to build well-known constraint profiles.
/// </summary>
public sealed class MessageConstraints : IMessageConstraints
{
    // ── Standard character sets ───────────────────────────────────────────────

    /// <summary>
    /// Characters accepted by FT8/FT4/FT2/SuperFox/JT65/Q65/MSK144/JT6M free-text messages
    /// (uppercase letters, digits, space, and punctuation used in ham radio exchanges).
    /// </summary>
    public const string StandardChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ +-./?@";

    /// <summary>Characters accepted by ISCAT messages.</summary>
    public const string IscatChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ /.?@-";

    /// <summary>Characters accepted by JTMS messages.</summary>
    public const string JtmsChars = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?@";

    /// <summary>Characters accepted by FSK441/FSK315 messages.</summary>
    /// <remarks>
    /// Based on the 92-entry MSHV lookup table: characters whose code maps to a value &lt; 48.
    /// '@' (ASCII 64) maps to code 63 and is silently substituted with space by the encoder.
    /// </remarks>
    public const string FskChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ !\"#$%&'()*+,-./?";

    /// <summary>
    /// Characters accepted by WSPR messages.
    /// WSPR transmits structured callsign/grid/power data, not free text.
    /// The allowed chars here are for the text representation.
    /// </summary>
    public const string WsprChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ /-+";

    /// <summary>Characters accepted by PI4 beacon callsigns.</summary>
    public const string Pi4Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ /";

    // ── Properties ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public int MaxLength { get; }

    /// <inheritdoc/>
    public string? AllowedChars { get; }

    /// <inheritdoc/>
    public string FormatHint { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    private MessageConstraints(int maxLength, string? allowedChars, string formatHint)
    {
        MaxLength    = maxLength;
        AllowedChars = allowedChars;
        FormatHint   = formatHint;
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string? Validate(string message)
    {
        if (message.Length > MaxLength)
            return $"Too long — {message.Length}/{MaxLength} characters";

        if (AllowedChars is not null)
        {
            foreach (char c in message)
                if (!AllowedChars.Contains(c))
                    return $"'{c}' is not valid in {FormatHint.Split('(')[0].TrimEnd()}";
        }

        return null;
    }

    // ── Factory methods ───────────────────────────────────────────────────────

    /// <summary>
    /// Standard callsign/grid/report exchange or free-text, used by FT8, FT4, FT2,
    /// SuperFox, JT65A/B/C, Q65A/B/C/D, MSK144, MSKMS, and JT6M.
    /// Structured exchanges (two callsigns + grid/report) can reach ~20 characters;
    /// free-text messages are limited to 13 characters by the 77-bit encoding.
    /// </summary>
    public static MessageConstraints Standard(int maxLength = 22) => new(
        maxLength,
        StandardChars,
        $"Callsign/grid exchange (max ~20) or free text (max 13)");

    /// <summary>
    /// Free-text format used by ISCAT-A and ISCAT-B.
    /// </summary>
    public static MessageConstraints Iscat(int maxLength = 28) => new(
        maxLength,
        IscatChars,
        $"Free text — letters, digits, space, / . ? @ - (max {maxLength})");

    /// <summary>
    /// Free-text format used by FSK441 and FSK315 meteor-scatter modes.
    /// </summary>
    public static MessageConstraints Fsk(int maxLength = 46) => new(
        maxLength,
        FskChars,
        $"Free text (max {maxLength})");

    /// <summary>
    /// Free-text format used by JTMS meteor scatter.
    /// </summary>
    public static MessageConstraints Jtms(int maxLength = 15) => new(
        maxLength,
        JtmsChars,
        $"Free text — letters, digits, space, punctuation (max {maxLength})");

    /// <summary>
    /// Characters accepted by SuperFox messages (base-38 callsign alphabet plus report signs).
    /// </summary>
    public const string SuperFoxChars = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/+-";
    public static MessageConstraints Pi4Callsign(int maxLength = 8) => new(
        maxLength,
        Pi4Chars,
        $"Callsign (max {maxLength})");

    /// <summary>
    /// SuperFox compound message format.
    /// <list type="bullet">
    ///   <item>CQ: <c>"CQ FOXCALL GRID4"</c> e.g. <c>"CQ LZ2HVV KN23"</c></item>
    ///   <item>Standard: <c>"FOXCALL H1 [±NN] H2 [±NN] …"</c> — up to 5 RR73 + 4 with report.</item>
    /// </list>
    /// </summary>
    public static MessageConstraints SuperFox(int maxLength = 100) => new(
        maxLength,
        SuperFoxChars,
        $"\"CQ FOXCALL GRID4\" or \"FOXCALL HOUND [±NN] …\" (up to 9 hounds, max {maxLength})");

    /// <summary>
    /// WSPR beacon format: <c>"CALLSIGN GRID4 dBm"</c>, e.g. <c>"W1AW FN42 37"</c>.
    /// Power must be 0–60 dBm, preferably a value ending in 0, 3, or 7.
    /// </summary>
    public static MessageConstraints Wspr(int maxLength = 22) => new(
        maxLength,
        WsprChars,
        "\"CALLSIGN GRID4 dBm\" — e.g. \"W1AW FN42 37\" (0–60 dBm, ideally ending in 0, 3 or 7)");
}
