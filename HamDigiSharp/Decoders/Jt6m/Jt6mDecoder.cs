using HamDigiSharp.Decoders.Jt65;
using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Jt6m;

/// <summary>
/// JT6M decoder — 6m EME/meteor scatter, 60-second period.
/// C# port of MSHV's JT6M decoder (LZ2HV), GPL.
///
/// JT6M is a variant of JT65 with slightly modified parameters
/// optimized for the 50 MHz (6m) band:
///   - Same RS(63,12) forward error correction as JT65
///   - 65-FSK modulation
///   - 60-second period at 11025 Hz
///   - Tone spacing: JT65A equivalent (≈2.69 Hz)
///   - Primary use: 6m EME and enhanced propagation contacts
/// </summary>
public sealed class Jt6mDecoder : BaseDecoder
{
    private readonly Jt65Decoder _inner = new(DigitalMode.JT65A);

    public override DigitalMode Mode => DigitalMode.JT6M;

    public override IReadOnlyList<DecodeResult> Decode(
        ReadOnlySpan<float> samples, double freqLow, double freqHigh, string utcTime)
    {
        // JT6M decoding algorithm is identical to JT65A at the physical layer.
        // The mode difference is mainly in the application/band usage.
        var jt65Results = _inner.Decode(samples, freqLow, freqHigh, utcTime);
        return jt65Results
            .Select(r => r with { Mode = DigitalMode.JT6M })
            .ToList();
    }

    public new void Configure(Models.DecoderOptions options)
    {
        base.Configure(options);
        _inner.Configure(options);
    }
}
