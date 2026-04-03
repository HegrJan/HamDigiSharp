namespace HamDigiSharp.Codecs;

/// <summary>
/// CRC-13 checksum used by MSK144 v2 messages (90-bit: 77 msg + 13 CRC).
/// Polynomial: 0x15D7 (boost::augmented_crc&lt;13, 0x15D7&gt; compatible).
/// Mirrors MSHV's <c>GenMsk::chkcrc13a</c>.
/// </summary>
public static class Crc13
{
    // Truncated generator polynomial (13 bits, MSB-first bit ordering)
    private const int Poly = 0x15D7;

    /// <summary>Compute augmented CRC-13 over <paramref name="data"/>[0..<paramref name="length"/>-1].</summary>
    public static ushort Compute(ReadOnlySpan<byte> data, int length)
    {
        uint crc = 0;
        for (int i = 0; i < length; i++)
        {
            byte b = data[i];
            for (int bit = 7; bit >= 0; bit--)
            {
                uint topBit = (crc >> 12) & 1;
                crc = (crc << 1) | (uint)((b >> bit) & 1);
                if (topBit != 0) crc ^= Poly;
            }
        }
        // Flush 13 zero bits (augmented CRC)
        for (int bit = 0; bit < 13; bit++)
        {
            uint topBit = (crc >> 12) & 1;
            crc <<= 1;
            if (topBit != 0) crc ^= Poly;
        }
        return (ushort)(crc & 0x1FFF);
    }

    /// <summary>
    /// Compute CRC-13 from a 77-bit message for encoding.
    /// Packs the 77 message bits into 10 bytes (bits 77-79 are 0), then runs
    /// the augmented CRC over 12 bytes (bytes 10-11 are 0).
    /// Mirrors MSHV's <c>GenMsk::encode_128_90</c> CRC step.
    /// </summary>
    public static int ComputeFromBits77(ReadOnlySpan<bool> bits77)
    {
        Span<byte> bytes = stackalloc byte[12];
        for (int ibyte = 0; ibyte < 10; ibyte++)
        {
            int v = 0;
            for (int ibit = 0; ibit < 8; ibit++)
            {
                int idx = ibyte * 8 + ibit;
                v = (v << 1) | (idx < bits77.Length && bits77[idx] ? 1 : 0);
            }
            bytes[ibyte] = (byte)v;
        }
        // bytes[10] and bytes[11] remain 0 (stackalloc zero-initialises)
        return Compute(bytes, 12);
    }

    /// <summary>
    /// Verify a decoded 90-bit array: bits [0..76] = 77-bit message, bits [77..89] = CRC-13.
    /// Returns true if the CRC matches.
    /// Mirrors MSHV's <c>GenMsk::chkcrc13a</c>.
    /// </summary>
    public static bool Check(ReadOnlySpan<bool> decoded90)
    {
        if (decoded90.Length < 90) return false;

        // Pack all 90 bits into 12 bytes (MSB-first), then zero trailing bits
        Span<byte> bytes = stackalloc byte[12];
        for (int ibyte = 0; ibyte < 12; ibyte++)
        {
            int v = 0;
            for (int ibit = 0; ibit < 8; ibit++)
            {
                int idx = ibyte * 8 + ibit;
                v = (v << 1) | (idx < decoded90.Length && decoded90[idx] ? 1 : 0);
            }
            bytes[ibyte] = (byte)v;
        }
        // Zero last 3 bits of byte 9 (bits 77-79 are CRC start; byte 9 covers bits 72-79,
        // so bits 72-76 are message, bits 77-79 start the CRC — mask off those 3)
        bytes[9] &= 0xF8; // keep top 5 bits (message bits 72-76), clear bottom 3
        bytes[10] = 0;
        bytes[11] = 0;

        // Extract transmitted CRC-13 from bits [77..89]
        int ncrc13 = 0;
        for (int ibit = 0; ibit < 13; ibit++)
            ncrc13 = (ncrc13 << 1) | (decoded90[77 + ibit] ? 1 : 0);

        ushort icrc13 = Compute(bytes, 12);
        return ncrc13 == icrc13;
    }
}
