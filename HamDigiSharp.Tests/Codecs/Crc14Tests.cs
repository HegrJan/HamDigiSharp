using FluentAssertions;
using HamDigiSharp.Codecs;
using Xunit;

namespace HamDigiSharp.Tests.Codecs;

public class Crc14Tests
{
    // ── Helper: build a valid 91-bit block by packing msg77 + CRC ─────────────

    private static bool[] MakeValid91(bool[] msg77)
    {
        ushort crc = Crc14.Compute(msg77);
        var block = new bool[91];
        Array.Copy(msg77, block, 77);
        for (int i = 0; i < 14; i++)
            block[77 + i] = ((crc >> (13 - i)) & 1) != 0;
        return block;
    }

    [Fact]
    public void Check_AllZeroMessage_ReturnsTrue()
    {
        var msg = new bool[77]; // all zeros → CRC = 0
        var block = MakeValid91(msg);
        Crc14.Check(block).Should().BeTrue("CRC of all-zero payload is 0 → block should pass");
    }

    [Fact]
    public void Check_NonZeroMessage_RoundTrips()
    {
        // Set several payload bits and verify the round-trip encode/check.
        var msg = new bool[77];
        msg[0] = true; msg[5] = true; msg[26] = true; msg[76] = true;
        var block = MakeValid91(msg);
        Crc14.Check(block).Should().BeTrue("Compute+Check must be consistent for non-zero message");
    }

    [Fact]
    public void Check_FlipAnyPayloadBit_ReturnsFalse()
    {
        // CRC-14 detects all single-bit errors in the 77-bit payload.
        var msg = new bool[77];
        msg[3] = true; msg[42] = true; // non-trivial message
        var block = MakeValid91(msg);
        Crc14.Check(block).Should().BeTrue("baseline must pass before flip");

        for (int bitPos = 0; bitPos < 77; bitPos++)
        {
            bool[] copy = (bool[])block.Clone();
            copy[bitPos] = !copy[bitPos];
            Crc14.Check(copy).Should().BeFalse(
                $"flipping payload bit {bitPos} must invalidate CRC-14");
        }
    }

    [Fact]
    public void Check_FlipAnyCrcBit_ReturnsFalse()
    {
        // Flipping any of the 14 CRC bits must also cause Check to fail.
        var msg = new bool[77];
        msg[1] = true; msg[20] = true;
        var block = MakeValid91(msg);

        for (int i = 0; i < 14; i++)
        {
            bool[] copy = (bool[])block.Clone();
            copy[77 + i] = !copy[77 + i];
            Crc14.Check(copy).Should().BeFalse(
                $"flipping CRC bit {i} must invalidate the check");
        }
    }

    [Fact]
    public void Compute_ZeroVectorKnownCrc()
    {
        // CRC-14 of 82 zero bits (77-bit zero payload + 5 zero extension bits) is 0.
        // Verified against ft8_lib's ftx_compute_crc(zeros, 82).
        var msg = new bool[77]; // all false
        ushort crc = Crc14.Compute(msg);
        crc.Should().Be(0, "CRC-14 of all-zero payload must be zero");
    }

    [Fact]
    public void Compute_SingleBitSet_NonZero()
    {
        // Any single-bit-set message must produce a non-zero CRC.
        for (int bit = 0; bit < 77; bit++)
        {
            var msg = new bool[77];
            msg[bit] = true;
            ushort crc = Crc14.Compute(msg);
            crc.Should().NotBe(0, $"CRC of single-bit message (bit {bit}) must be non-zero");
        }
    }

    [Fact]
    public void Compute_IsConsistentWithCheck()
    {
        // Verify Compute → Check round-trips correctly for several non-trivial messages.
        var allZero = new bool[77];
        var firstBit = new bool[77]; firstBit[0] = true;
        var lastBit  = new bool[77]; lastBit[76] = true;
        var mixed    = new bool[77]; mixed[0] = true; mixed[38] = true; mixed[76] = true;

        foreach (var msg in new[] { allZero, firstBit, lastBit, mixed })
        {
            var block = MakeValid91(msg);
            Crc14.Check(block).Should().BeTrue("Compute+Check round-trip must always pass");
        }
    }
}
