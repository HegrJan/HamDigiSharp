namespace HamDigiSharp.Messaging;

/// <summary>
/// Result of a <see cref="MessageBuilder"/> call.
/// On success, <see cref="Message"/> holds the formatted string ready for encoding.
/// On failure, <see cref="Error"/> describes the problem.
/// </summary>
public sealed record BuildResult
{
    /// <summary><see langword="true"/> if the message was built successfully.</summary>
    public bool IsValid { get; init; }

    /// <summary>The constructed message string, or <see langword="null"/> on failure.</summary>
    public string? Message { get; init; }

    /// <summary>Human-readable error description, or <see langword="null"/> on success.</summary>
    public string? Error { get; init; }

    /// <summary>Creates a successful result containing <paramref name="message"/>.</summary>
    public static BuildResult Ok(string message) =>
        new() { IsValid = true, Message = message };

    /// <summary>Creates a failed result with the given <paramref name="error"/> message.</summary>
    public static BuildResult Fail(string error) =>
        new() { IsValid = false, Error = error };

    /// <summary>Throws <see cref="InvalidOperationException"/> if the result is not valid.</summary>
    public string Unwrap() => Message
        ?? throw new InvalidOperationException($"MessageBuilder failed: {Error}");
}
