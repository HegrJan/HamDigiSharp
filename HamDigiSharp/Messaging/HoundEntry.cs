namespace HamDigiSharp.Messaging;

/// <summary>
/// A single hound entry inside a SuperFox Fox→Hound compound response frame.
/// </summary>
public sealed record HoundEntry
{
    /// <summary>The hound station's callsign (e.g. "W1AW").</summary>
    public required string Callsign { get; init; }

    /// <summary>
    /// SNR report in dB, or <see langword="null"/> when the hound receives RR73 (QSO complete).
    /// Range when present: −18 to +12 dB (SuperFox encoding constraint).
    /// </summary>
    public int? ReportDb { get; init; }

    /// <summary><see langword="true"/> when <see cref="ReportDb"/> is <see langword="null"/>
    /// (the hound gets RR73 rather than a numeric report).</summary>
    public bool IsRr73 => ReportDb is null;
}
