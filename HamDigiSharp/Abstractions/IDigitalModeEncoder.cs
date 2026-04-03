using HamDigiSharp.Models;

namespace HamDigiSharp.Abstractions;

/// <summary>
/// Contract for a digital-mode audio encoder (transmit side).
/// Takes a text message and returns mono float PCM at the mode's native sample rate.
/// </summary>
public interface IDigitalModeEncoder
{
    DigitalMode Mode { get; }

    /// <summary>
    /// Encode <paramref name="message"/> into a float PCM audio frame.
    /// Returns samples at <see cref="DigitalModeExtensions.DecoderSampleRate"/> Hz,
    /// normalised to approximately [-1, +1].
    /// </summary>
    float[] Encode(string message, EncoderOptions options);
}
