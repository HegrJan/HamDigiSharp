using HamDigiSharp.Abstractions;
using HamDigiSharp.Models;

namespace HamDigiSharp.Abstractions;

/// <summary>
/// Describes a ham radio digital mode protocol: timing, audio parameters,
/// and factories for the encoder and decoder.
///
/// A GUI client uses <see cref="IProtocol"/> to avoid hard-coding any
/// mode-specific constants. Key usage pattern:
/// <code>
///   var proto = ProtocolRegistry.Get(DigitalMode.FT8);
///
///   // Timing — know when the current period started and what parity it has
///   var start  = proto.PeriodStart(DateTimeOffset.UtcNow);
///   var isEven = proto.IsEvenPeriod(DateTimeOffset.UtcNow);
///
///   // If we heard the other station in an even period, we reply in an odd one
///   // (and vice versa).  PeriodIndex gives the absolute counter since midnight.
///
///   // Decode
///   var decoder = proto.CreateDecoder();
///   var results = decoder.Decode(samples, proto.DefaultFreqLow, proto.DefaultFreqHigh, utcStr);
///
///   // Encode
///   var audio = proto.CreateEncoder()?.Encode("CQ W1AW EN31", new EncoderOptions { FreqHz = 1000 });
/// </code>
/// </summary>
public interface IProtocol
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Digital mode this protocol describes.</summary>
    DigitalMode Mode { get; }

    /// <summary>Short display name, e.g. "FT8", "Q65B".</summary>
    string Name { get; }

    /// <summary>Human-readable description of the mode.</summary>
    string Description { get; }

    // ── Timing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Duration of one TX/RX period (e.g. 15 s for FT8, 7.5 s for FT4).
    /// Periods are aligned to UTC midnight; both stations always agree on
    /// the period boundary.
    /// </summary>
    TimeSpan PeriodDuration { get; }

    /// <summary>
    /// Duration of the transmitted audio within each period (always ≤ <see cref="PeriodDuration"/>).
    /// The remainder of the period is silence, giving the receiver time to process.
    /// E.g. FT8 transmits 12.64 s of audio in a 15 s period.
    /// </summary>
    TimeSpan TransmitDuration { get; }

    // ── Audio ─────────────────────────────────────────────────────────────────

    /// <summary>PCM sample rate expected by the decoder and produced by the encoder (Hz).</summary>
    int SampleRate { get; }

    /// <summary>Lower end of the recommended audio frequency search range (Hz).</summary>
    double DefaultFreqLow { get; }

    /// <summary>Upper end of the recommended audio frequency search range (Hz).</summary>
    double DefaultFreqHigh { get; }

    // ── Message ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Maximum number of characters in a free-text message for this mode.
    /// Structured messages (callsign/grid exchanges) may be shorter.
    /// Convenience shorthand for <see cref="MessageConstraints"/>.<see cref="IMessageConstraints.MaxLength"/>.
    /// </summary>
    int MaxMessageLength { get; }

    /// <summary>
    /// Constraints on the message text this protocol's encoder accepts —
    /// maximum length, allowed character set, and a validator.
    /// A GUI uses this to cap input, filter keystrokes, and give live feedback.
    /// </summary>
    IMessageConstraints MessageConstraints { get; }

    // ── Period helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the UTC start of the period that contains <paramref name="utc"/>.
    /// Periods are aligned to UTC midnight.
    /// </summary>
    DateTimeOffset PeriodStart(DateTimeOffset utc);

    /// <summary>Returns the UTC start of the next period after <paramref name="utc"/>.</summary>
    DateTimeOffset NextPeriodStart(DateTimeOffset utc);

    /// <summary>
    /// Returns the zero-based index of the period that contains <paramref name="utc"/>,
    /// counted from UTC midnight.
    ///
    /// Two stations that both compute this value will always agree, which lets the
    /// GUI decide TX/RX assignment: if you heard the other station in period N, respond
    /// in period N+1.  Use <see cref="IsEvenPeriod"/> as a convenience.
    /// </summary>
    long PeriodIndex(DateTimeOffset utc);

    /// <summary>
    /// Returns <see langword="true"/> if the period containing <paramref name="utc"/>
    /// has an even index (0, 2, 4 …).
    ///
    /// Standard QSO convention: one station transmits on even periods, the other on odd.
    /// Once you hear the DX station, note the parity of their period; your transmission
    /// goes in the opposite parity.
    /// </summary>
    bool IsEvenPeriod(DateTimeOffset utc);

    // ── Codec factories ───────────────────────────────────────────────────────

    /// <summary>
    /// <see langword="true"/> when this protocol has an encoder and can produce audio
    /// for transmission. A GUI uses this to enable or disable TX controls.
    /// </summary>
    bool CanEncode { get; }

    /// <summary>Creates a new, independent decoder instance for this mode.</summary>
    IDigitalModeDecoder CreateDecoder();

    /// <summary>
    /// Creates a new encoder instance, or returns <see langword="null"/> if
    /// encoding is not yet implemented for this mode.
    /// </summary>
    IDigitalModeEncoder? CreateEncoder();
}
