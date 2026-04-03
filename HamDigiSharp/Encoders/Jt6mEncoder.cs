using HamDigiSharp.Abstractions;
using HamDigiSharp.Models;

namespace HamDigiSharp.Encoders;

/// <summary>
/// JT6M encoder — wraps JT65A; JT6M uses the same protocol over 6m band.
/// </summary>
public sealed class Jt6mEncoder : IDigitalModeEncoder
{
    private readonly Jt65Encoder _inner = new(DigitalMode.JT65A);
    public DigitalMode Mode => DigitalMode.JT6M;
    public float[] Encode(string message, EncoderOptions options) => _inner.Encode(message, options);
}
