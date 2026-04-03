namespace HamDigiSharp.Abstractions;

/// <summary>
/// Describes the message text that a protocol's encoder (and free-text decoder) can handle.
///
/// A GUI uses this to:
/// <list type="bullet">
///   <item>Cap input length with <see cref="MaxLength"/>.</item>
///   <item>Filter the allowed character set with <see cref="AllowedChars"/>.</item>
///   <item>Show a validation hint via <see cref="FormatHint"/>.</item>
///   <item>Validate a complete message before calling the encoder with <see cref="Validate"/>.</item>
/// </list>
/// </summary>
public interface IMessageConstraints
{
    /// <summary>Maximum number of characters the encoder accepts.</summary>
    int MaxLength { get; }

    /// <summary>
    /// The set of characters permitted in a message, or <see langword="null"/> when
    /// any printable character is accepted.
    /// </summary>
    string? AllowedChars { get; }

    /// <summary>Short human-readable description suitable for a tooltip or placeholder.</summary>
    string FormatHint { get; }

    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="message"/> satisfies all
    /// constraints, or a short error string explaining the first violation.
    /// </summary>
    string? Validate(string message);
}
