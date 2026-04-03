namespace HamDigiSharp.Models;

/// <summary>
/// A single decoded message from a digital-mode frame.
/// Mirrors the QStringList emitted by MSHV's EmitDecodetText signal.
/// </summary>
public sealed record DecodeResult
{
    /// <summary>UTC time string at which the period started (e.g. "143015").</summary>
    public required string UtcTime { get; init; }

    /// <summary>Signal-to-noise ratio in dB (relative to 2500 Hz noise bandwidth).</summary>
    public double Snr { get; init; }

    /// <summary>Time offset of the signal from nominal period start, in seconds.</summary>
    public double Dt { get; init; }

    /// <summary>Audio frequency of the decoded signal in Hz.</summary>
    public double FrequencyHz { get; init; }

    /// <summary>Decoded message text.</summary>
    public required string Message { get; init; }

    /// <summary>Digital mode that produced this decode.</summary>
    public DigitalMode Mode { get; init; }

    /// <summary>Number of LDPC hard errors (for FT8/FT4/FT2/Q65). -1 = not applicable.</summary>
    public int HardErrors { get; init; } = -1;

    /// <summary>OSD distance metric (for FT8/FT4). NaN = not used.</summary>
    public double Dmin { get; init; } = double.NaN;

    /// <summary>True if decoded via a priori (AP) information.</summary>
    public bool IsApDecode { get; init; }

    public override string ToString() =>
        $"{UtcTime} {Snr,4:+0;-0;+0} {Dt,5:F1} {FrequencyHz,7:F0} {Message}";
}
