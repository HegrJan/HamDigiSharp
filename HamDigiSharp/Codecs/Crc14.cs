namespace HamDigiSharp.Codecs;

/// <summary>
/// CRC-14 checksum used by FT8, FT4, FT2, and Q65 messages.
/// Polynomial: x^14+x^13+x^11+x^10+x^9+x^7+x^6+x^4+x^2+x+1 = 0x2757.
/// Algorithm matches ft8_lib's <c>ftx_compute_crc</c>: standard (non-augmented)
/// 14-bit CRC processed MSB-first, over exactly 82 bits
/// (77 message bits + 5 zero-extension bits, per the WSJT-X spec).
/// </summary>
public static class Crc14
{
    // Generator polynomial 0x2757, TOPBIT = 1<<13 = 0x2000
    private const uint Poly    = 0x2757u;
    private const uint TopBit  = 0x2000u;

    /// <summary>
    /// Standard CRC-14 over exactly <paramref name="numBits"/> bits, MSB first per byte.
    /// Matches ft8_lib's <c>ftx_compute_crc(message, num_bits)</c> exactly.
    /// </summary>
    public static ushort ComputeBits(ReadOnlySpan<byte> data, int numBits)
    {
        uint remainder = 0;
        int idxByte = 0;
        for (int idxBit = 0; idxBit < numBits; idxBit++)
        {
            if (idxBit % 8 == 0)
            {
                // XOR the next byte (left-aligned in the 14-bit register) into remainder
                remainder ^= (uint)data[idxByte] << (14 - 8); // << 6
                idxByte++;
            }
            if ((remainder & TopBit) != 0)
                remainder = (remainder << 1) ^ Poly;
            else
                remainder <<= 1;
        }
        return (ushort)(remainder & 0x3FFFu);
    }

    /// <summary>
    /// Compute the 14-bit CRC for the first 77 message bits.
    /// The spec says: "CRC is computed on the source-encoded message, zero-extended from 77 to 82 bits."
    /// Returns the value to be stored at bits [77..90] of the 91-bit LDPC codeword.
    /// </summary>
    public static ushort Compute(ReadOnlySpan<bool> bits77)
    {
        // Pack 77 bits into bytes[0..9] MSB-first; bits 77-81 are implicitly 0
        Span<byte> bytes = stackalloc byte[11]; // 11 bytes covers 88 bits ≥ 82
        bytes.Clear();
        for (int i = 0; i < 77; i++)
            if (bits77[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        // Compute over 82 bits (77 data + 5 zeros)
        return ComputeBits(bytes, 82);
    }

    /// <summary>
    /// Verify a decoded 91-bit array: bits [0..76] = message, bits [77..90] = CRC-14.
    /// Matches ft8_lib's <c>ftx_extract_crc</c> + <c>ftx_compute_crc(a91, 82)</c>.
    /// </summary>
    public static bool Check(ReadOnlySpan<bool> decoded91)
    {
        // Pack all 91 bits into bytes MSB-first
        Span<byte> a91 = stackalloc byte[12];
        a91.Clear();
        for (int i = 0; i < 91 && i < decoded91.Length; i++)
            if (decoded91[i]) a91[i / 8] |= (byte)(0x80 >> (i % 8));

        // Extract transmitted CRC from bits [77..90]
        // Matches: ((a91[9] & 0x07) << 11) | (a91[10] << 3) | (a91[11] >> 5)
        uint crcExtracted = (uint)((a91[9] & 0x07) << 11)
                          | (uint)(a91[10] << 3)
                          | (uint)(a91[11] >> 5);

        // Zero the CRC field (bits 77-90) before computing; keep bits 77-79 zeroed in byte 9
        a91[9]  &= 0xF8;
        a91[10]  = 0;
        // a91[11] not touched but irrelevant — only 82 bits are processed below

        // Compute CRC over 82 bits (77 payload + 5 zero extension bits)
        ushort crcComputed = ComputeBits(a91, 82);
        return crcExtracted == crcComputed;
    }
}
