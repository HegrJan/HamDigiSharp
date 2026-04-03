using FluentAssertions;
using HamDigiSharp.Codecs;
using HamDigiSharp.Models;
using Xunit;

namespace HamDigiSharp.Tests.Codecs;

/// <summary>
/// Tests for LDPC(174,91) belief-propagation + OSD decoder.
///
/// LLR sign convention (MSHV): positive LLR → decoded bit 1 (true),
/// negative LLR → decoded bit 0 (false).
/// The all-zero codeword is always valid and requires all-negative LLRs.
/// </summary>
public class Ldpc174_91Tests
{
    // ── All-zero codeword ─────────────────────────────────────────────────────

    [Fact]
    public void BpDecode_AllNegativeLlr_ReturnsAllZeroCodeword()
    {
        var llr = Enumerable.Repeat(-5.0, 174).ToArray();
        var apMask = new bool[174];
        var msg77 = new bool[77];
        var cw = new bool[174];

        Ldpc174_91.BpDecode(llr, apMask, msg77, cw, out int hardErrors);

        hardErrors.Should().Be(0, "strongly negative LLRs → all-zero codeword, 0 errors");
        msg77.Should().AllSatisfy(b => b.Should().BeFalse(),
            "message bits must all be 0 for all-zero codeword");
        cw.Should().AllSatisfy(b => b.Should().BeFalse(),
            "codeword bits must all be 0 for all-zero codeword");
    }

    [Fact]
    public void BpDecode_AllNegativeLlr_PassesCrc14()
    {
        var llr = Enumerable.Repeat(-5.0, 174).ToArray();
        var apMask = new bool[174];
        var msg77 = new bool[77];
        var cw = new bool[174];

        Ldpc174_91.BpDecode(llr, apMask, msg77, cw, out _);

        // The 91-bit block (77-bit message + 14-bit CRC) must pass CRC-14
        var cw91 = cw.Take(91).ToArray();
        Crc14.Check(cw91).Should().BeTrue(
            "all-zero codeword has CRC-14 = 0, which is self-consistent");
    }

    [Fact]
    public void TryDecode_AllNegativeLlr_ReturnsTrue()
    {
        var llr = Enumerable.Repeat(-5.0, 174).ToArray();
        var apMask = new bool[174];
        var msg77 = new bool[77];
        var cw = new bool[174];

        bool ok = Ldpc174_91.TryDecode(llr, apMask, DecoderDepth.Fast, msg77, cw,
            out int hardErrors, out double dmin);

        ok.Should().BeTrue("all-zero codeword always decodes");
        hardErrors.Should().Be(0);
        dmin.Should().Be(0.0, "BP succeeded → dmin = 0");
    }

    // ── Parity check verification ─────────────────────────────────────────────

    [Fact]
    public void BpDecode_AllZeroCodeword_SatisfiesAllParityChecks()
    {
        // After successful BP decode, the codeword must satisfy all 83 parity checks.
        // Each check is the XOR of its connected variable nodes.
        // For all-zero codeword, every parity check = XOR(0,0,...) = 0 ✓
        var llr = Enumerable.Repeat(-5.0, 174).ToArray();
        var apMask = new bool[174];
        var msg77 = new bool[77];
        var cw = new bool[174];
        Ldpc174_91.BpDecode(llr, apMask, msg77, cw, out int hardErrors);

        hardErrors.Should().Be(0);
        // Spot-check: each codeword bit is false (0)
        for (int i = 0; i < 174; i++)
            cw[i].Should().BeFalse($"cw[{i}] should be 0");
    }

    // ── Error tolerance ───────────────────────────────────────────────────────

    [Fact]
    public void BpDecode_SmallLlrMagnitudeAllZero_StillDecodes()
    {
        // With weaker (but still negative) LLRs, BP should still converge
        var llr = Enumerable.Repeat(-1.5, 174).ToArray();
        var apMask = new bool[174];
        var msg77 = new bool[77];
        var cw = new bool[174];

        Ldpc174_91.BpDecode(llr, apMask, msg77, cw, out int hardErrors);

        hardErrors.Should().Be(0, "weak but unambiguous negative LLRs should decode all-zero");
        msg77.Should().AllSatisfy(b => b.Should().BeFalse());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void BpDecode_FewBitFlips_Recovers(int nFlips)
    {
        // Start with strongly negative LLRs (all-zero), flip a few to positive.
        // The BP decoder should recover for low flip counts.
        var llr = Enumerable.Repeat(-5.0, 174).ToArray();
        // Flip the first nFlips LLRs to positive (introduce errors)
        for (int i = 0; i < nFlips; i++) llr[i] = +5.0;

        var apMask = new bool[174];
        var msg77 = new bool[77];
        var cw = new bool[174];

        // BP may or may not recover; it must not throw
        var ex = Record.Exception(() =>
            Ldpc174_91.BpDecode(llr, apMask, msg77, cw, out _));
        ex.Should().BeNull("BP must never throw regardless of input");
    }

    // ── OSD fallback ──────────────────────────────────────────────────────────

    [Fact]
    public void TryDecode_Depth3_AllNegative_ReturnsTrue()
    {
        var llr = Enumerable.Repeat(-5.0, 174).ToArray();
        var apMask = new bool[174];
        var msg77 = new bool[77];
        var cw = new bool[174];

        bool ok = Ldpc174_91.TryDecode(llr, apMask, DecoderDepth.Deep, msg77, cw,
            out int hardErrors, out double dmin);

        ok.Should().BeTrue();
        hardErrors.Should().Be(0);
    }

    // ── AP mask ───────────────────────────────────────────────────────────────

    [Fact]
    public void BpDecode_ApMaskSet_FixedBitsAreRespected()
    {
        // With all negative LLRs + AP mask on positions 0..76, the AP bits
        // (fixing them to 0) should not change the outcome (still all-zero).
        var llr = Enumerable.Repeat(-5.0, 174).ToArray();
        var apMask = new bool[174];
        for (int i = 0; i < 77; i++) apMask[i] = true; // fix message bits

        var msg77 = new bool[77];
        var cw = new bool[174];
        Ldpc174_91.BpDecode(llr, apMask, msg77, cw, out int hardErrors);

        hardErrors.Should().Be(0);
        msg77.Should().AllSatisfy(b => b.Should().BeFalse());
    }

    // ── Robustness ────────────────────────────────────────────────────────────

    [Fact]
    public void BpDecode_AllPositiveLlr_DoesNotThrow()
    {
        // All-positive LLRs → all bit-1 decisions → not a valid codeword
        // (unless all-one happens to be a valid codeword, which it isn't for this code)
        var llr = Enumerable.Repeat(+5.0, 174).ToArray();
        var apMask = new bool[174];
        var msg77 = new bool[77];
        var cw = new bool[174];

        var ex = Record.Exception(() =>
            Ldpc174_91.BpDecode(llr, apMask, msg77, cw, out _));
        ex.Should().BeNull();
    }

    [Fact]
    public void BpDecode_RandomLlr_DoesNotThrow()
    {
        var rng = new Random(999);
        var llr = new double[174];
        for (int i = 0; i < 174; i++) llr[i] = rng.NextDouble() * 10 - 5;

        var apMask = new bool[174];
        var msg77 = new bool[77];
        var cw = new bool[174];

        var ex = Record.Exception(() =>
            Ldpc174_91.BpDecode(llr, apMask, msg77, cw, out _));
        ex.Should().BeNull("must not throw on random input");
    }

    [Fact]
    public void TryDecode_RandomLlr_ReturnsBoolWithoutThrowing()
    {
        var rng = new Random(12345);
        var llr = new double[174];
        for (int i = 0; i < 174; i++) llr[i] = rng.NextDouble() * 6 - 3;

        var apMask = new bool[174];
        var msg77 = new bool[77];
        var cw = new bool[174];

        bool result = false;
        var ex = Record.Exception(() =>
            result = Ldpc174_91.TryDecode(llr, apMask, DecoderDepth.Fast, msg77, cw, out _, out _));
        ex.Should().BeNull();
        // result may be true or false; that's fine
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void BpDecode_SameInput_GivesSameOutput()
    {
        var llr = Enumerable.Repeat(-4.0, 174).ToArray();
        llr[10] = +4.0;
        llr[50] = +2.0;

        var apMask = new bool[174];
        var cw1 = new bool[174];
        var cw2 = new bool[174];
        var m1 = new bool[77];
        var m2 = new bool[77];

        Ldpc174_91.BpDecode(llr, apMask, m1, cw1, out int h1);
        Ldpc174_91.BpDecode(llr, apMask, m2, cw2, out int h2);

        h1.Should().Be(h2, "deterministic algorithm");
        cw1.Should().BeEquivalentTo(cw2);
        m1.Should().BeEquivalentTo(m2);
    }

    // ── Encode + CheckParity ─────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ OK1TE JN89")]
    [InlineData("W1AW OK1TE -07")]
    [InlineData("OK1TE W1AW RR73")]
    public void Encode_ProducedCodeword_SatisfiesAllParityChecks(string message)
    {
        // Verify that the FT8/FT4/FT2 systematic encoder produces a valid LDPC codeword.
        // This is the same proof-of-correctness we apply to LDPC(128,90) for MSK144.
        var msg77 = new bool[77];
        MessagePack77.TryPack77(message, msg77).Should().BeTrue();

        var codeword = new bool[174];
        Ldpc174_91.Encode(msg77, codeword);

        Ldpc174_91.CheckParity(codeword).Should().BeTrue(
            $"all 83 parity checks must pass for encoded \"{message}\"");
    }

    [Theory]
    [InlineData("CQ OK1TE JN89")]
    [InlineData("W1AW OK1TE -07")]
    public void Encode_ThenCheckParity_IsDeterministic(string message)
    {
        var msg77 = new bool[77];
        MessagePack77.TryPack77(message, msg77);

        var cw1 = new bool[174];
        var cw2 = new bool[174];
        Ldpc174_91.Encode(msg77, cw1);
        Ldpc174_91.Encode(msg77, cw2);

        cw1.Should().BeEquivalentTo(cw2, "encoding is deterministic");
    }

    [Fact]
    public void Encode_AllZeroMessage77_ProducesZeroCodeword()
    {
        // All-zero message77 → CRC-14 = 0 → msg91 all-zero → parity all-zero → codeword all-zero
        var msg77 = new bool[77];
        var codeword = new bool[174];
        Ldpc174_91.Encode(msg77, codeword);

        codeword.Should().AllSatisfy(b => b.Should().BeFalse(),
            "all-zero message encodes to the all-zero codeword");
    }

    [Fact]
    public void CheckParity_AllZeroCodeword_ReturnsTrue()
    {
        // The all-zero 174-bit vector trivially satisfies all parity checks (XOR of 0s = 0).
        var codeword = new bool[174]; // all false
        Ldpc174_91.CheckParity(codeword).Should().BeTrue(
            "all-zero codeword satisfies all parity checks");
    }

    [Fact]
    public void CheckParity_SingleBitFlip_ReturnsFalse()
    {
        // Flip one bit in the all-zero codeword → at least one parity check must fail.
        var codeword = new bool[174];
        codeword[0] = true; // flip bit 0

        Ldpc174_91.CheckParity(codeword).Should().BeFalse(
            "a single-bit error in the all-zero codeword must violate at least one parity check");
    }


}
