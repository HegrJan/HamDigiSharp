namespace HamDigiSharp.Abstractions;

/// <summary>
/// Packs and unpacks the 77-bit (or 72-bit for older modes) message payload.
/// </summary>
public interface IMessageCodec
{
    /// <summary>Pack a text message into a 77-bit payload stored in <paramref name="bits"/> (length ≥ 77).</summary>
    bool TryPack(string message, bool[] bits);

    /// <summary>Unpack 77 bits to a human-readable message string.</summary>
    bool TryUnpack(ReadOnlySpan<bool> bits, out string message);
}
