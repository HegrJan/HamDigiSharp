using HamDigiSharp.Models;

namespace HamDigiSharp.Abstractions;

/// <summary>
/// Contract that every mode-specific decoder must implement.
/// Mirrors the decode method signatures on MSHV's DecoderFt8 / DecoderFt4 / DecoderMs, etc.
/// </summary>
public interface IDigitalModeDecoder
{
    /// <summary>The mode this decoder handles.</summary>
    DigitalMode Mode { get; }

    /// <summary>Apply new options (thread-safe; can be called between decode calls).</summary>
    void Configure(DecoderOptions options);

    /// <summary>
    /// Decode one period's worth of audio samples.
    /// <paramref name="samples"/> must already be at the decoder's native sample rate
    /// (see <see cref="DigitalModeExtensions.DecoderSampleRate"/>).
    /// </summary>
    /// <param name="samples">Mono PCM, normalised [-1, +1].</param>
    /// <param name="freqLow">Lower search frequency in Hz.</param>
    /// <param name="freqHigh">Upper search frequency in Hz.</param>
    /// <param name="utcTime">Period start time string, e.g. "143015".</param>
    /// <returns>All decodes found (may be empty).</returns>
    IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples,
        double freqLow,
        double freqHigh,
        string utcTime);

    /// <summary>
    /// Raised when a result is available mid-decode (real-time display).
    /// May be raised from a background thread.
    /// </summary>
    event Action<DecodeResult>? ResultAvailable;
}
