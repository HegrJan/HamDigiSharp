using HamDigiSharp.Abstractions;
using HamDigiSharp.Models;

namespace HamDigiSharp.Protocols;

/// <summary>
/// Concrete, data-driven implementation of <see cref="IProtocol"/>.
/// Constructed exclusively by <see cref="ProtocolRegistry"/>.
/// </summary>
public sealed class Protocol : IProtocol
{
    private readonly Func<IDigitalModeDecoder>  _decoderFactory;
    private readonly Func<IDigitalModeEncoder>? _encoderFactory;

    public DigitalMode         Mode               { get; }
    public string              Name               { get; }
    public string              Description        { get; }
    public TimeSpan            PeriodDuration     { get; }
    public TimeSpan            TransmitDuration   { get; }
    public int                 SampleRate         { get; }
    public double              DefaultFreqLow     { get; }
    public double              DefaultFreqHigh    { get; }
    public IMessageConstraints MessageConstraints { get; }

    /// <summary>Convenience shorthand for <see cref="MessageConstraints"/>.<see cref="IMessageConstraints.MaxLength"/>.</summary>
    public int MaxMessageLength => MessageConstraints.MaxLength;

    /// <inheritdoc/>
    public bool CanEncode => _encoderFactory != null;

    internal Protocol(
        DigitalMode mode,
        string name,
        string description,
        TimeSpan periodDuration,
        TimeSpan transmitDuration,
        int sampleRate,
        double defaultFreqLow,
        double defaultFreqHigh,
        IMessageConstraints messageConstraints,
        Func<IDigitalModeDecoder>  decoderFactory,
        Func<IDigitalModeEncoder>? encoderFactory = null)
    {
        Mode               = mode;
        Name               = name;
        Description        = description;
        PeriodDuration     = periodDuration;
        TransmitDuration   = transmitDuration;
        SampleRate         = sampleRate;
        DefaultFreqLow     = defaultFreqLow;
        DefaultFreqHigh    = defaultFreqHigh;
        MessageConstraints = messageConstraints;
        _decoderFactory    = decoderFactory;
        _encoderFactory    = encoderFactory;
    }

    // ── Period timing ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public DateTimeOffset PeriodStart(DateTimeOffset utc)
    {
        double epochSec  = utc.ToUniversalTime().ToUnixTimeMilliseconds() / 1000.0;
        double windowSec = Math.Floor(epochSec / PeriodDuration.TotalSeconds)
                           * PeriodDuration.TotalSeconds;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(windowSec * 1000))
                             .ToUniversalTime();
    }

    /// <inheritdoc/>
    public DateTimeOffset NextPeriodStart(DateTimeOffset utc)
        => PeriodStart(utc).Add(PeriodDuration);

    /// <inheritdoc/>
    public long PeriodIndex(DateTimeOffset utc)
    {
        double epochSec = utc.ToUniversalTime().ToUnixTimeMilliseconds() / 1000.0;
        return (long)Math.Floor(epochSec / PeriodDuration.TotalSeconds);
    }

    /// <inheritdoc/>
    public bool IsEvenPeriod(DateTimeOffset utc)
        => PeriodIndex(utc) % 2 == 0;

    // ── Codec factories ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public IDigitalModeDecoder  CreateDecoder() => _decoderFactory();

    /// <inheritdoc/>
    public IDigitalModeEncoder? CreateEncoder() => _encoderFactory?.Invoke();

    public override string ToString() => Name;
}
