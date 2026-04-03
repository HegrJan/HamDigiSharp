using FluentAssertions;
using HamDigiSharp.Codecs;
using Xunit;

namespace HamDigiSharp.Tests.Codecs;

/// <summary>
/// Tests for MessagePack77 (encoder) and its round-trip with MessagePacker.Unpack77 (decoder).
///
/// A broken round-trip means TX and RX interpret the message differently — the most
/// fundamental correctness guarantee for any digital mode implementation.
/// </summary>
public class MessagePack77Tests
{
    // ── Pack77 → Unpack77 round-trip ─────────────────────────────────────────

    [Theory]
    [InlineData("CQ OK1TE JN89")]
    [InlineData("W1AW OK1TE -07")]
    [InlineData("OK1TE W1AW R-10")]
    [InlineData("OK1TE W1AW RR73")]
    [InlineData("OK1TE W1AW RRR")]
    [InlineData("OK1TE W1AW 73")]
    [InlineData("W1AW K1JT FN31")]
    [InlineData("DL1ABC VK2ZD -15")]
    [InlineData("K9AN W1AW +05")]
    // Geographic CQ qualifiers — these were silently corrupted before the encoder fix
    // (Pack28("DX") returned 0="DE"; the fix added TryPackCqSuffix + qualifier detection).
    [InlineData("CQ DX W1AW FN42")]
    [InlineData("CQ EU K9AN EN52")]
    [InlineData("CQ NA DL1ABC JO31")]
    [InlineData("CQ AP VK2ZD QF56")]
    // Numeric CQ frequency qualifiers
    [InlineData("CQ 009 W1AW FN42")]
    [InlineData("CQ 145 DL1ABC JO31")]
    public void Pack77_ThenUnpack77_ReproducesMessage(string message)
    {
        var c77 = new bool[77];
        bool packed = MessagePack77.TryPack77(message, c77);
        packed.Should().BeTrue($"TryPack77 must succeed for \"{message}\"");

        string decoded = new MessagePacker().Unpack77(c77, out bool ok);
        ok.Should().BeTrue($"Unpack77 must succeed for the packed bits of \"{message}\"");

        // Normalise: trim whitespace, collapse runs, uppercase
        string expected = string.Join(" ", message.Trim().ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string actual   = string.Join(" ", decoded.Trim().ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        actual.Should().Be(expected,
            $"decoded message must match original for \"{message}\"");
    }

    // ── Special report tokens ─────────────────────────────────────────────────

    [Theory]
    [InlineData("OK1TE W1AW -01")]
    [InlineData("OK1TE W1AW -30")]
    [InlineData("OK1TE W1AW +01")]
    [InlineData("OK1TE W1AW +20")]
    [InlineData("OK1TE W1AW R-05")]
    [InlineData("OK1TE W1AW R+10")]
    public void Pack77_ReportVariants_PackSucceeds(string message)
    {
        var c77 = new bool[77];
        MessagePack77.TryPack77(message, c77).Should().BeTrue(
            $"\"{message}\" must pack successfully");
    }

    [Theory]
    [InlineData("OK1TE W1AW -01")]
    [InlineData("OK1TE W1AW +05")]
    [InlineData("OK1TE W1AW R-12")]
    [InlineData("OK1TE W1AW R+08")]
    public void Pack77_ReportVariants_RoundTrip_PreservesReport(string message)
    {
        var c77 = new bool[77];
        MessagePack77.TryPack77(message, c77);
        string decoded = new MessagePacker().Unpack77(c77, out bool ok);
        ok.Should().BeTrue($"Unpack77 must succeed for \"{message}\"");

        // The decoded report token should numerically match the original
        string[] origWords = message.Trim().ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] decWords  = decoded.Trim().ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        string origReport = origWords[^1];
        string decReport  = decWords[^1];
        decReport.Should().Be(origReport,
            $"report token must survive round-trip for \"{message}\"");
    }

    // ── Grid encoding ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("AA00", 0)]
    [InlineData("JN89", (9 - 0) * 1800 + (13) * 100 + 8 * 10 + 9)]  // J=9, N=13, 8, 9
    [InlineData("FN31", (5) * 1800 + (13) * 100 + 3 * 10 + 1)]
    [InlineData("RR99", (17) * 1800 + (17) * 100 + 9 * 10 + 9)]
    public void TryParseGrid4_KnownGrids_GivesExpectedEncoding(string grid, int expected)
    {
        bool ok = MessagePack77.TryParseGrid4(grid, out int igrid4);
        ok.Should().BeTrue($"\"{grid}\" must parse as a valid grid locator");
        igrid4.Should().Be(expected, $"grid encoding for {grid}");
    }

    [Theory]
    [InlineData("AA00")]
    [InlineData("JN89")]
    [InlineData("FN31")]
    [InlineData("RR99")]
    [InlineData("IO91")]
    [InlineData("em00")]  // lowercase accepted
    public void TryParseGrid4_ValidGrids_Succeed(string grid)
    {
        MessagePack77.TryParseGrid4(grid, out _).Should().BeTrue(
            $"\"{grid}\" must be accepted as a valid Maidenhead locator");
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("AB")]
    [InlineData("ABC")]
    [InlineData("12AB")]   // digits before letters
    [InlineData("ABCD")]   // all letters
    [InlineData("RR9X")]   // letter in digit position
    [InlineData("SS00")]   // S is out of A-R range (valid field pair must give igrid4 < 32400)
    public void TryParseGrid4_InvalidGrids_Fail(string grid)
    {
        // The parser must return false for clearly invalid inputs.
        // Note: "SS00" uses 'S' which is beyond the valid 18×18 grid (A-R), so igrid4 >= 32400.
        bool ok = MessagePack77.TryParseGrid4(grid, out _);
        ok.Should().BeFalse($"\"{grid}\" should not be accepted as a valid grid");
    }

    // ── Pack28: callsign variants ─────────────────────────────────────────────

    [Theory]
    [InlineData("K1AA")]    // 1-letter prefix, area digit at position 1
    [InlineData("W0AA")]    // W-prefix
    [InlineData("DL1ABC")]  // 2-letter prefix, 6 chars
    [InlineData("VK2ZD")]   // VK-prefix, 5 chars
    [InlineData("JA1YYY")]  // JA-prefix
    [InlineData("OK1TE")]   // European, 5-char
    public void Pack28_VariousCallsigns_InValidRange(string call)
    {
        int n28 = MessagePack77.Pack28(call);
        // Valid encoded callsigns are in range [NTokens + Max22 + 1, (1<<28)-1]
        // NTokens = 2_063_592, Max22 = 4_194_304 → floor = 6_257_896
        n28.Should().BeGreaterThan(6_257_896, $"Pack28({call}) should be above special-token range");
        n28.Should().BeLessThan(1 << 28, $"Pack28({call}) must fit in 28 bits");
    }

    [Theory]
    [InlineData("K1AA", "K1AA")]     // same call → same pack
    [InlineData("DL1ABC", "DL1ABC")]
    public void Pack28_SameCallTwice_GivesSameResult(string call1, string call2)
    {
        MessagePack77.Pack28(call1).Should().Be(MessagePack77.Pack28(call2),
            "Pack28 must be deterministic");
    }

    [Theory]
    [InlineData("W1AW", "K1AA")]  // different callsigns
    [InlineData("OK1TE", "DL1ABC")]
    public void Pack28_DifferentCalls_GiveDifferentValues(string call1, string call2)
    {
        MessagePack77.Pack28(call1).Should().NotBe(MessagePack77.Pack28(call2),
            "distinct callsigns must pack to distinct values");
    }

    // ── TryPack77: invalid messages ──────────────────────────────────────────

    [Theory]
    [InlineData("")]                              // empty
    [InlineData("CQ")]                            // only one token
    [InlineData("A B C D E")]                     // too many words
    [InlineData("CQ W1AW FN31 EXTRA WORD")]       // > 4 words
    public void TryPack77_InvalidMessages_ReturnFalse(string message)
    {
        var c77 = new bool[77];
        MessagePack77.TryPack77(message, c77).Should().BeFalse(
            $"\"{message}\" should not pack successfully");
    }

    [Fact]
    public void TryPack77_ShortArray_Throws()
    {
        var shortArray = new bool[70]; // must be >= 77
        var act = () => MessagePack77.TryPack77("CQ W1AW FN31", shortArray);
        act.Should().Throw<ArgumentException>("c77 must be at least 77 elements");
    }

    // ── Pack77 determinism ───────────────────────────────────────────────────

    [Fact]
    public void TryPack77_CalledTwice_ProducesIdenticalBits()
    {
        var c1 = new bool[77];
        var c2 = new bool[77];
        MessagePack77.TryPack77("CQ OK1TE JN89", c1);
        MessagePack77.TryPack77("CQ OK1TE JN89", c2);
        c1.Should().BeEquivalentTo(c2, "packing is deterministic");
    }

    [Fact]
    public void TryPack77_DifferentMessages_ProduceDifferentBits()
    {
        var c1 = new bool[77];
        var c2 = new bool[77];
        MessagePack77.TryPack77("CQ OK1TE JN89", c1);
        MessagePack77.TryPack77("W1AW OK1TE -07", c2);
        c1.Should().NotBeEquivalentTo(c2, "different messages must produce different 77-bit payloads");
    }

    // ── Full TX path: Pack77 → Ldpc174_91.Encode → CheckParity ─────────────

    [Theory]
    [InlineData("CQ OK1TE JN89")]
    [InlineData("W1AW OK1TE -07")]
    [InlineData("OK1TE W1AW RR73")]
    public void Pack77_ThenEncode174_91_SatisfiesAllParityChecks(string message)
    {
        var msg77 = new bool[77];
        MessagePack77.TryPack77(message, msg77).Should().BeTrue();

        var codeword = new bool[174];
        Ldpc174_91.Encode(msg77, codeword);

        Ldpc174_91.CheckParity(codeword).Should().BeTrue(
            $"LDPC(174,91) parity checks must all pass for encoded \"{message}\"");
    }

    [Theory]
    [InlineData("CQ OK1TE JN89")]
    [InlineData("W1AW OK1TE -07")]
    [InlineData("OK1TE W1AW RR73")]
    public void Pack77_ThenEncode174_91_FirstBitsMatchCrc14(string message)
    {
        // After encoding, the first 91 bits [msg77 | crc14] must pass CRC-14 check.
        var msg77 = new bool[77];
        MessagePack77.TryPack77(message, msg77);
        var codeword = new bool[174];
        Ldpc174_91.Encode(msg77, codeword);

        Crc14.Check(codeword.AsSpan(0, 91)).Should().BeTrue(
            $"CRC-14 of first 91 bits must be valid for \"{message}\"");
    }
}
