using FluentAssertions;
using HamDigiSharp.Codecs;
using Xunit;

namespace HamDigiSharp.Tests.Codecs;

public class Crc13Tests
{
    [Fact]
    public void Check_AllZeroBits_ReturnsTrue()
    {
        // CRC-13 of 77 zero data bits is 0, so all-90-zero bits passes Check()
        bool[] bits = new bool[90];
        Crc13.Check(bits).Should().BeTrue("CRC of all-zero input is zero");
    }

    [Fact]
    public void Check_FlippedCrcBit_ReturnsFalse()
    {
        // Start with all-zero (valid), flip a CRC bit
        bool[] bits = new bool[90];
        bits[77] = true; // flip the first CRC bit → should fail
        Crc13.Check(bits).Should().BeFalse("flipping a CRC bit should invalidate the check");
    }

    [Fact]
    public void Check_KnownGoodMessage_ReturnsTrue()
    {
        // Build 90-bit block: 77 zero data bits + correct CRC-13
        bool[] msg = new bool[90];

        // Pack 77 zero bits into 12 bytes (all zeros), zero bytes 10/11
        Span<byte> bytes = stackalloc byte[12];
        // all zero already

        ushort crc = Crc13.Compute(bytes, 12);

        // Write 13-bit CRC into bits [77..89]
        for (int i = 0; i < 13; i++)
            msg[77 + i] = ((crc >> (12 - i)) & 1) != 0;

        Crc13.Check(msg).Should().BeTrue("computed CRC must match extracted CRC");
    }

    [Fact]
    public void Compute_TwoCallsIdentical_AreEqual()
    {
        byte[] data = { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x11, 0x22, 0x00, 0x00 };
        ushort c1 = Crc13.Compute(data, data.Length);
        ushort c2 = Crc13.Compute(data, data.Length);
        c1.Should().Be(c2, "CRC is deterministic");
    }

    [Fact]
    public void Compute_NonZeroPayload_GivesNonZeroResult()
    {
        // CRC of an all-zero message is 0 (trivial). A non-zero message should
        // almost certainly produce a non-zero CRC for any sensible polynomial.
        byte[] data = { 0xA5, 0xC3, 0x3C, 0x5A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        ushort crc = Crc13.Compute(data, data.Length);
        crc.Should().NotBe(0, "CRC-13 of a non-zero payload should be non-zero");
    }

    [Fact]
    public void ComputeFromBits77_RoundTrip_CheckPasses()
    {
        // Build a 90-bit block using ComputeFromBits77 and verify it passes Check.
        // This validates the new encoding helper method used by the MSK144 encoder.
        var msg77 = new bool[77];
        MessagePack77.TryPack77("CQ OK1TE JN89", msg77);

        int crc = Crc13.ComputeFromBits77(msg77);

        var bits90 = new bool[90];
        for (int i = 0; i < 77; i++) bits90[i] = msg77[i];
        for (int i = 0; i < 13; i++) bits90[77 + i] = ((crc >> (12 - i)) & 1) == 1;

        Crc13.Check(bits90).Should().BeTrue(
            "ComputeFromBits77 CRC appended to message must pass Check()");
    }

    [Fact]
    public void ComputeFromBits77_AllZero_MatchesComputeOnZeroBytes()
    {
        // All-zero 77-bit message: ComputeFromBits77 must agree with Compute over 12 zero bytes.
        var zeros77 = new bool[77];
        int crcFromBits = Crc13.ComputeFromBits77(zeros77);

        Span<byte> bytes = stackalloc byte[12]; // all zero
        ushort crcFromBytes = Crc13.Compute(bytes, 12);

        crcFromBits.Should().Be(crcFromBytes,
            "all-zero message: ComputeFromBits77 must equal Compute over 12 zero bytes");
    }

    [Theory]
    [InlineData("CQ OK1TE JN89")]
    [InlineData("W1AW OK1TE RR73")]
    [InlineData("OK1TE W1AW -12")]
    public void ComputeFromBits77_PackedMessages_RoundTripCheck(string message)
    {
        var msg77 = new bool[77];
        MessagePack77.TryPack77(message, msg77);

        int crc = Crc13.ComputeFromBits77(msg77);
        var bits90 = new bool[90];
        for (int i = 0; i < 77; i++) bits90[i] = msg77[i];
        for (int i = 0; i < 13; i++) bits90[77 + i] = ((crc >> (12 - i)) & 1) == 1;

        Crc13.Check(bits90).Should().BeTrue(
            $"CRC-13 round-trip must pass for \"{message}\"");
    }

    [Fact]
    public void Compute_SingleBitFlip_ChangesCrc()
    {
        // CRC must detect every single-bit error (Hamming distance ≥ 2 property
        // of any proper CRC with a non-trivial generator polynomial).
        byte[] data = { 0xA5, 0xC3, 0x3C, 0x5A, 0xF0, 0x0F, 0x55, 0xAA, 0x11, 0x22, 0x00, 0x00 };
        ushort original = Crc13.Compute(data, data.Length);

        for (int b = 0; b < data.Length; b++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] copy = (byte[])data.Clone();
                copy[b] ^= (byte)(1 << bit);
                ushort altered = Crc13.Compute(copy, copy.Length);
                altered.Should().NotBe(original,
                    $"CRC-13 must change when byte[{b}] bit {bit} is flipped");
            }
        }
    }
}

public class Ldpc128_90Tests
{
    [Fact]
    public void TryDecode_AllNegativeLlr_ReturnsAllZeroMessage()
    {
        // All strongly negative LLRs → all bit-0 decisions → all-zero codeword
        // (In MSHV's LLR convention: positive = bit 1, negative = bit 0)
        // All-zero is always a valid codeword, and CRC-13 of all-zero bits = 0.
        var llr = new double[128];
        for (int i = 0; i < 128; i++) llr[i] = -4.0;

        var decoded = new bool[90];
        bool ok = Ldpc128_90.TryDecode(llr, decoded, out int hardErrors);

        ok.Should().BeTrue("all-zero codeword satisfies all parity checks and CRC-13");
        hardErrors.Should().Be(0);
        decoded.Should().AllSatisfy(b => b.Should().BeFalse());
    }

    [Fact]
    public void TryDecode_RandomNoise_DoesNotThrow()
    {
        // Random LLRs should not throw; may or may not decode.
        var rng = new Random(42);
        var llr = new double[128];
        for (int i = 0; i < 128; i++) llr[i] = rng.NextDouble() * 4 - 2;

        var decoded = new bool[90];
        var ex = Record.Exception(() => Ldpc128_90.TryDecode(llr, decoded, out _));
        ex.Should().BeNull("decoder must not throw on random input");
    }

    [Fact]
    public void TryDecode_IsDeterministic()
    {
        // Same LLR input must produce exactly the same output on every call.
        var rng = new Random(0xCAFE);
        var llr = new double[128];
        for (int i = 0; i < 128; i++) llr[i] = rng.NextDouble() * 4 - 2;

        var decoded1 = new bool[90];
        var decoded2 = new bool[90];
        bool ok1 = Ldpc128_90.TryDecode(llr, decoded1, out int err1);
        bool ok2 = Ldpc128_90.TryDecode(llr, decoded2, out int err2);

        ok1.Should().Be(ok2, "TryDecode is deterministic — same ok");
        err1.Should().Be(err2, "TryDecode is deterministic — same hardErrors");
        decoded1.Should().BeEquivalentTo(decoded2, "TryDecode is deterministic — same bits");
    }

    [Fact]
    public void TryDecode_AllPositiveLlr_DoesNotThrow()
    {
        // All-positive LLR → all-one hard decisions.  The all-one vector may not
        // satisfy all parity constraints (TryDecode may return false), but it
        // must never throw.
        var llr = Enumerable.Repeat(4.0, 128).ToArray();
        var decoded = new bool[90];
        var ex = Record.Exception(() => Ldpc128_90.TryDecode(llr, decoded, out _));
        ex.Should().BeNull("LDPC decoder must not throw on all-positive LLR");
    }

    [Fact]
    public void TryDecode_AllZeroCwLargeNegativeLlr_HardErrorsIsZero()
    {
        // The all-zero codeword has hard-decision: all LLR < 0 → all bits = 0.
        // The hard-error count compares our decision (all 0) with the input's
        // hard decisions (all 0) → 0 errors.
        var llr = Enumerable.Repeat(-10.0, 128).ToArray();
        var decoded = new bool[90];
        Ldpc128_90.TryDecode(llr, decoded, out int hardErrors);
        hardErrors.Should().Be(0, "all-zero codeword: hard decisions agree perfectly with LLR signs");
    }

    // ── CheckParity ───────────────────────────────────────────────────────────

    [Fact]
    public void CheckParity_AllZeroCodeword_ReturnsTrue()
    {
        var codeword = new bool[128];
        Ldpc128_90.CheckParity(codeword).Should().BeTrue(
            "all-zero codeword trivially satisfies all 38 parity checks");
    }

    [Fact]
    public void CheckParity_SingleBitFlip_ReturnsFalse()
    {
        var codeword = new bool[128];
        codeword[5] = true; // flip one bit in the all-zero codeword
        Ldpc128_90.CheckParity(codeword).Should().BeFalse(
            "a single-bit error must violate at least one parity check");
    }

    [Theory]
    [InlineData("CQ OK1TE JN89")]
    [InlineData("W1AW OK1TE RR73")]
    [InlineData("DL1ABC VK2ZD -15")]
    public void CheckParity_EncodedCodeword_PassesAllChecks(string message)
    {
        var msg77 = new bool[77];
        MessagePack77.TryPack77(message, msg77).Should().BeTrue();
        var codeword = new bool[128];
        Ldpc128_90.Encode(msg77, codeword);

        Ldpc128_90.CheckParity(codeword).Should().BeTrue(
            $"encoder output must satisfy all 38 parity checks for \"{message}\"");
    }
}
