namespace HamDigiSharp.Models;

/// <summary>Options controlling a single TX encode call.</summary>
public sealed class EncoderOptions
{
    /// <summary>Audio centre frequency in Hz (default 1000 Hz).</summary>
    public double FrequencyHz { get; set; } = 1000.0;

    private double _amplitude = 0.9;

    /// <summary>
    /// Peak amplitude of the generated waveform (default 0.9).
    /// Clamped to [0.0, 0.99] to prevent clipping.
    /// </summary>
    public double Amplitude
    {
        get => _amplitude;
        set => _amplitude = Math.Clamp(value, 0.0, 0.99);
    }
}
