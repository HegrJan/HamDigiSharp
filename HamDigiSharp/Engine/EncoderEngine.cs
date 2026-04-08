using HamDigiSharp.Abstractions;
using HamDigiSharp.Encoders;
using HamDigiSharp.Models;

namespace HamDigiSharp.Engine;

/// <summary>
/// Registers all available encoders and dispatches encode calls by mode.
/// </summary>
public sealed class EncoderEngine : IDisposable
{
    private readonly Dictionary<DigitalMode, IDigitalModeEncoder> _encoders;
    private volatile bool _disposed;

    public EncoderEngine()
    {
        _encoders = new Dictionary<DigitalMode, IDigitalModeEncoder>
        {
            [DigitalMode.FT8]      = new Ft8Encoder(),
            [DigitalMode.FT4]      = new Ft4Encoder(),
            [DigitalMode.FT2]      = new Ft2Encoder(),
            [DigitalMode.MSK144]   = new Msk144Encoder(),
            [DigitalMode.SuperFox] = new SuperFoxEncoder(),
            [DigitalMode.JT65A]    = new Jt65Encoder(DigitalMode.JT65A),
            [DigitalMode.JT65B]    = new Jt65Encoder(DigitalMode.JT65B),
            [DigitalMode.JT65C]    = new Jt65Encoder(DigitalMode.JT65C),
            [DigitalMode.JT6M]     = new Jt6mEncoder(),
            [DigitalMode.PI4]      = new Pi4Encoder(),
            [DigitalMode.Q65A]     = new Q65Encoder(DigitalMode.Q65A),
            [DigitalMode.Q65B]     = new Q65Encoder(DigitalMode.Q65B),
            [DigitalMode.Q65C]     = new Q65Encoder(DigitalMode.Q65C),
            [DigitalMode.Q65D]     = new Q65Encoder(DigitalMode.Q65D),
        };
    }

    /// <summary>The set of modes that this engine can encode.</summary>
    public IReadOnlyCollection<DigitalMode> SupportedModes => _encoders.Keys;

    /// <summary>Returns true if <paramref name="mode"/> has a registered encoder.</summary>
    public bool Supports(DigitalMode mode) => _encoders.ContainsKey(mode);

    /// <summary>Encode a message for the given mode.</summary>
    public float[] Encode(string message, DigitalMode mode, EncoderOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_encoders.TryGetValue(mode, out var encoder))
            throw new NotSupportedException($"Mode {mode} is not supported by the encoder.");
        return encoder.Encode(message, options ?? new EncoderOptions());
    }

    /// <inheritdoc/>
    public void Dispose() { _disposed = true; }
}
