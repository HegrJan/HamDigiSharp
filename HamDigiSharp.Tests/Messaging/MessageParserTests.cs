using FluentAssertions;
using HamDigiSharp.Messaging;
using HamDigiSharp.Models;
using Xunit;

namespace HamDigiSharp.Tests.Messaging;

/// <summary>
/// Tests for <see cref="MessageParser.Parse"/>.
/// Covers all protocol families, edge cases, and the full decoded-message vocabulary.
/// </summary>
public class MessageParserTests
{
    // ── Standard modes: CQ ────────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ W1AW FN42",    "W1AW", "FN42", null)]
    [InlineData("CQ K9AN EN31",    "K9AN", "EN31", null)]
    [InlineData("CQ LZ2HV KN22",   "LZ2HV", "KN22", null)]
    [InlineData("CQ DX W1AW FN42", "W1AW", "FN42", "DX")]
    [InlineData("CQ EU W1AW FN42", "W1AW", "FN42", "EU")]
    [InlineData("CQ NA K9AN EN31", "K9AN", "EN31", "NA")]
    [InlineData("CQ 145 W1AW FN42","W1AW", "FN42", "145")]
    public void Parse_StandardMode_CqMessage_ReturnsStandardMessage(
        string raw, string from, string grid, string? qualifier)
    {
        var result = MessageParser.Parse(raw, DigitalMode.FT8);

        var msg = result.Should().BeOfType<StandardMessage>().Subject;
        msg.Direction.Should().Be(MessageDirection.CQ);
        msg.From.Should().Be(from);
        msg.Exchange.Should().Be(grid);
        msg.CqQualifier.Should().Be(qualifier);
        msg.To.Should().BeNull();
    }

    [Theory]
    [InlineData("QRZ W1AW FN42",  "W1AW", "FN42")]
    [InlineData("QRZ K9AN EN31",  "K9AN", "EN31")]
    public void Parse_StandardMode_QrzMessage_ReturnsStandardMessage(string raw, string from, string grid)
    {
        var msg = MessageParser.Parse(raw, DigitalMode.FT8)
            .Should().BeOfType<StandardMessage>().Subject;
        msg.Direction.Should().Be(MessageDirection.QRZ);
        msg.From.Should().Be(from);
        msg.Exchange.Should().Be(grid);
    }

    [Theory]
    [InlineData("DE W1AW FN42",  "W1AW", "FN42")]
    public void Parse_StandardMode_DeMessage_ReturnsStandardMessage(string raw, string from, string grid)
    {
        var msg = MessageParser.Parse(raw, DigitalMode.FT8)
            .Should().BeOfType<StandardMessage>().Subject;
        msg.Direction.Should().Be(MessageDirection.DE);
        msg.From.Should().Be(from);
        msg.Exchange.Should().Be(grid);
    }

    // ── Standard modes: Exchange ───────────────────────────────────────────────

    [Theory]
    [InlineData("W1AW K9AN -07",   "W1AW", "K9AN", "-07")]
    [InlineData("W1AW K9AN +03",   "W1AW", "K9AN", "+03")]
    [InlineData("W1AW K9AN RRR",   "W1AW", "K9AN", "RRR")]
    [InlineData("W1AW K9AN RR73",  "W1AW", "K9AN", "RR73")]
    [InlineData("W1AW K9AN 73",    "W1AW", "K9AN", "73")]
    [InlineData("W1AW K9AN FN42",  "W1AW", "K9AN", "FN42")]
    [InlineData("LZ2HV OK1TE KN22","LZ2HV","OK1TE","KN22")]
    public void Parse_StandardMode_ExchangeMessage_ReturnsStandardMessage(
        string raw, string from, string to, string exchange)
    {
        var msg = MessageParser.Parse(raw, DigitalMode.FT8)
            .Should().BeOfType<StandardMessage>().Subject;
        msg.Direction.Should().Be(MessageDirection.Exchange);
        msg.From.Should().Be(from);
        msg.To.Should().Be(to);
        msg.Exchange.Should().Be(exchange);
    }

    [Theory]
    [InlineData("W1AW K9AN -07", -7)]
    [InlineData("W1AW K9AN +12", 12)]
    [InlineData("W1AW K9AN -24", -24)]
    public void Parse_ExchangeMessage_SnrDb_ParsedCorrectly(string raw, int expectedSnr)
    {
        var msg = MessageParser.Parse(raw, DigitalMode.FT8)
            .Should().BeOfType<StandardMessage>().Subject;
        msg.SnrDb.Should().Be(expectedSnr);
    }

    [Theory]
    [InlineData("W1AW K9AN FN42")]
    [InlineData("W1AW K9AN EN31")]
    public void Parse_ExchangeMessage_HasGrid_TrueForGridExchange(string raw)
    {
        var msg = MessageParser.Parse(raw, DigitalMode.FT8)
            .Should().BeOfType<StandardMessage>().Subject;
        msg.HasGrid.Should().BeTrue();
    }

    [Theory]
    [InlineData("W1AW K9AN -07")]
    [InlineData("W1AW K9AN RR73")]
    public void Parse_ExchangeMessage_HasGrid_FalseForNonGrid(string raw)
    {
        var msg = MessageParser.Parse(raw, DigitalMode.FT8)
            .Should().BeOfType<StandardMessage>().Subject;
        msg.HasGrid.Should().BeFalse();
    }

    // ── Standard modes: Free-text fallback ────────────────────────────────────

    [Theory]
    [InlineData("CQ TEST")]
    [InlineData("TNX 73")]
    [InlineData("73 DE W1AW")]
    [InlineData("TEST MESSAGE")]
    public void Parse_StandardMode_NonStructured_ReturnsFreeTextMessage(string raw)
    {
        MessageParser.Parse(raw, DigitalMode.FT8)
            .Should().BeOfType<FreeTextMessage>()
            .Which.Text.Should().NotBeEmpty();
    }

    // ── Free-text-only modes ──────────────────────────────────────────────────

    [Theory]
    [InlineData(DigitalMode.IscatA)]
    [InlineData(DigitalMode.IscatB)]
    [InlineData(DigitalMode.FSK441)]
    [InlineData(DigitalMode.FSK315)]
    [InlineData(DigitalMode.JTMS)]
    public void Parse_FreeTextMode_AlwaysReturnsFreeTextMessage(DigitalMode mode)
    {
        foreach (var raw in new[] { "CQ W1AW FN42", "W1AW K9AN -07", "HELLO WORLD" })
        {
            MessageParser.Parse(raw, mode)
                .Should().BeOfType<FreeTextMessage>($"mode {mode} is free-text only");
        }
    }

    [Fact]
    public void Parse_FreeTextMode_PreservesText()
    {
        var msg = MessageParser.Parse("cq w1aw fn42", DigitalMode.IscatA)
            .Should().BeOfType<FreeTextMessage>().Subject;
        msg.Text.Should().Be("CQ W1AW FN42"); // upper-cased
    }

    // ── PI4 beacon ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("OK1TE",    "OK1TE")]
    [InlineData("LZ2HVV",   "LZ2HVV")]
    [InlineData("PI4GN",    "PI4GN")]
    [InlineData("W1AW/4",   "W1AW/4")]
    public void Parse_Pi4Mode_ReturnsBeaconMessage(string raw, string expectedCallsign)
    {
        var msg = MessageParser.Parse(raw, DigitalMode.PI4)
            .Should().BeOfType<BeaconMessage>().Subject;
        msg.Callsign.Should().Be(expectedCallsign);
    }

    [Fact]
    public void Parse_Pi4Mode_AlwaysBeacon_EvenForCqPattern()
    {
        // PI4 never produces structured messages — the decoded field IS the callsign
        MessageParser.Parse("CQ W1AW FN42", DigitalMode.PI4)
            .Should().BeOfType<BeaconMessage>();
    }

    // ── SuperFox: CQ ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ LZ2HVV KN23", "LZ2HVV", "KN23")]
    [InlineData("CQ W1AW FN42",   "W1AW",   "FN42")]
    public void Parse_SuperFox_CqMessage_ReturnsSuperFoxCqMessage(
        string raw, string fox, string grid)
    {
        var msg = MessageParser.Parse(raw, DigitalMode.SuperFox)
            .Should().BeOfType<SuperFoxCqMessage>().Subject;
        msg.FoxCallsign.Should().Be(fox);
        msg.Grid.Should().Be(grid);
    }

    // ── SuperFox: Decoded Fox→Hound single-hound response (StandardMessage) ───

    [Theory]
    [InlineData("W1AW LZ2HVV RR73",  "W1AW", "LZ2HVV", "RR73")]
    [InlineData("K9AN LZ2HVV +05",   "K9AN", "LZ2HVV", "+05")]
    [InlineData("N0ARY LZ2HVV -12",  "N0ARY","LZ2HVV", "-12")]
    public void Parse_SuperFox_SingleHoundResponse_ReturnsStandardMessage(
        string raw, string hound, string fox, string exchange)
    {
        var msg = MessageParser.Parse(raw, DigitalMode.SuperFox)
            .Should().BeOfType<StandardMessage>().Subject;
        msg.Direction.Should().Be(MessageDirection.Exchange);
        msg.From.Should().Be(hound);
        msg.To.Should().Be(fox);
        msg.Exchange.Should().Be(exchange);
    }

    // ── SuperFox: Compound encoder format (SuperFoxResponseMessage) ───────────

    [Fact]
    public void Parse_SuperFox_CompoundTwoHounds_ReturnsSuperFoxResponseMessage()
    {
        // "LZ2HVV W1AW +05 K9AN" — 2 hound callsigns → compound
        var msg = MessageParser.Parse("LZ2HVV W1AW +05 K9AN", DigitalMode.SuperFox)
            .Should().BeOfType<SuperFoxResponseMessage>().Subject;

        msg.FoxCallsign.Should().Be("LZ2HVV");
        msg.Hounds.Should().HaveCount(2);
        msg.Hounds[0].Callsign.Should().Be("W1AW");
        msg.Hounds[0].ReportDb.Should().Be(5);
        msg.Hounds[1].Callsign.Should().Be("K9AN");
        msg.Hounds[1].IsRr73.Should().BeTrue();
    }

    [Fact]
    public void Parse_SuperFox_CompoundMixedReports_ParsesAllHounds()
    {
        string raw = "LZ2HVV W1AW +05 K9AN -12 N0ARY VK3ABC +03";
        var msg = MessageParser.Parse(raw, DigitalMode.SuperFox)
            .Should().BeOfType<SuperFoxResponseMessage>().Subject;

        msg.FoxCallsign.Should().Be("LZ2HVV");
        msg.Hounds.Should().HaveCount(4);
        msg.Hounds[0].Should().Be(new HoundEntry { Callsign = "W1AW",   ReportDb = 5 });
        msg.Hounds[1].Should().Be(new HoundEntry { Callsign = "K9AN",   ReportDb = -12 });
        msg.Hounds[2].Should().Be(new HoundEntry { Callsign = "N0ARY",  ReportDb = null });
        msg.Hounds[3].Should().Be(new HoundEntry { Callsign = "VK3ABC", ReportDb = 3 });
    }

    // ── Without mode hint ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ W1AW FN42")]
    [InlineData("W1AW K9AN -07")]
    [InlineData("W1AW K9AN RR73")]
    public void Parse_NoMode_StandardPatterns_ReturnsStandardMessage(string raw)
    {
        MessageParser.Parse(raw)
            .Should().BeOfType<StandardMessage>();
    }

    [Fact]
    public void Parse_NoMode_FreeText_ReturnsFreeTextMessage()
    {
        MessageParser.Parse("CQ TEST")
            .Should().BeOfType<FreeTextMessage>();
    }

    // ── IsCallsignLike internals ──────────────────────────────────────────────

    [Theory]
    [InlineData("W1AW",    true)]
    [InlineData("K9AN",    true)]
    [InlineData("LZ2HV",   true)]
    [InlineData("VK3ABC",  true)]
    [InlineData("3Y0X",    false)]  // digit at position 0 → Pack28 rejects it; not standard callsign-like
    [InlineData("K9AN/P",  true)]   // portable suffix
    [InlineData("EU/W1AW", true)]   // prefix form
    [InlineData("CQ",      false)]  // reserved word
    [InlineData("QRZ",     false)]  // reserved word
    [InlineData("RR73",    false)]  // reserved word
    [InlineData("73",      false)]  // exchange token
    [InlineData("FN42",    false)]  // grid locator
    [InlineData("KN22",    false)]  // grid locator
    [InlineData("+07",     false)]  // report
    [InlineData("-12",     false)]  // report
    [InlineData("TEST",    false)]  // no digit → not a callsign
    [InlineData("123",     false)]  // no letter → not a callsign
    public void IsCallsignLike_VariousInputs_CorrectResult(string s, bool expected)
    {
        MessageParser.IsCallsignLike(s).Should().Be(expected, $"input was '{s}'");
    }

    // ── IsExchangeToken internals ─────────────────────────────────────────────

    [Theory]
    [InlineData("RRR",  true)]
    [InlineData("RR73", true)]
    [InlineData("73",   true)]
    [InlineData("+07",  true)]
    [InlineData("-12",  true)]
    [InlineData("FN42", true)]
    [InlineData("KN22", true)]
    [InlineData("W1AW", false)]  // callsign, not exchange
    [InlineData("CQ",   false)]  // keyword
    [InlineData("TEST", false)]  // free text
    public void IsExchangeToken_VariousInputs_CorrectResult(string s, bool expected)
    {
        MessageParser.IsExchangeToken(s).Should().Be(expected, $"input was '{s}'");
    }

    // ── Raw field preservation ────────────────────────────────────────────────

    [Fact]
    public void Parse_RawField_PreservesOriginalString()
    {
        string original = "  cq w1aw fn42  ";
        var result = MessageParser.Parse(original, DigitalMode.FT8);
        result.Raw.Should().Be(original, "Raw must be the unmodified input string");
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyString_ReturnsFreeTextMessage()
    {
        MessageParser.Parse("", DigitalMode.FT8)
            .Should().BeOfType<FreeTextMessage>()
            .Which.Text.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NullEquivalent_ReturnsFreeTextMessage()
    {
        MessageParser.Parse("   ", DigitalMode.FT8)
            .Should().BeOfType<FreeTextMessage>();
    }

    // ── Mode-agnostic: same modes produce same results ────────────────────────

    [Theory]
    [InlineData(DigitalMode.FT8)]
    [InlineData(DigitalMode.FT4)]
    [InlineData(DigitalMode.FT2)]
    [InlineData(DigitalMode.JT65A)]
    [InlineData(DigitalMode.JT65B)]
    [InlineData(DigitalMode.JT65C)]
    [InlineData(DigitalMode.Q65A)]
    [InlineData(DigitalMode.Q65B)]
    [InlineData(DigitalMode.MSK144)]
    [InlineData(DigitalMode.MSKMS)]
    [InlineData(DigitalMode.JT6M)]
    public void Parse_AllStandardModes_CqMessageDecodes(DigitalMode mode)
    {
        var msg = MessageParser.Parse("CQ W1AW FN42", mode)
            .Should().BeOfType<StandardMessage>().Subject;
        msg.Direction.Should().Be(MessageDirection.CQ);
        msg.From.Should().Be("W1AW");
    }
}
