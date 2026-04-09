using FluentAssertions;
using HamDigiSharp.Messaging;
using HamDigiSharp.Models;
using Xunit;

namespace HamDigiSharp.Tests.Messaging;

/// <summary>
/// Tests for <see cref="MessageBuilder"/> — covers all builder methods,
/// validation rules, error messages, and edge cases.
/// </summary>
public class MessageBuilderTests
{
    // ── Cq ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("W1AW",  "FN42",  null,  "CQ W1AW FN42")]
    [InlineData("K9AN",  "EN31",  null,  "CQ K9AN EN31")]
    [InlineData("LZ2HV", "KN22",  null,  "CQ LZ2HV KN22")]
    [InlineData("W1AW",  "FN42",  "DX",  "CQ DX W1AW FN42")]
    [InlineData("W1AW",  "FN42",  "EU",  "CQ EU W1AW FN42")]
    [InlineData("W1AW",  "FN42",  "145", "CQ 145 W1AW FN42")]
    [InlineData("w1aw",  "fn42",  "dx",  "CQ DX W1AW FN42")]  // lower-case normalised
    public void Cq_ValidInputs_ReturnsExpectedMessage(
        string call, string grid, string? qualifier, string expected)
    {
        var result = MessageBuilder.Cq(call, grid, qualifier);
        result.IsValid.Should().BeTrue();
        result.Message.Should().Be(expected);
    }

    [Theory]
    [InlineData("W1AW",  "ZZ99",  null)]  // invalid grid letter
    [InlineData("W1AW",  "FN",    null)]  // grid too short
    [InlineData("W1AW",  "FN42A", null)]  // grid too long
    [InlineData("NODIGIT","FN42", null)]  // callsign without area digit
    [InlineData("",       "FN42", null)]  // empty callsign
    [InlineData("W1AW",  "FN42",  "TOOLONG")]  // qualifier too long (5 chars)
    [InlineData("W1AW",  "FN42",  "1A2")]      // mixed alphanumeric qualifier is not standard
    public void Cq_InvalidInputs_ReturnsFail(string call, string grid, string? qualifier)
    {
        var result = MessageBuilder.Cq(call, grid, qualifier);
        result.IsValid.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Cq_LongCallsignWithQualifier_IsValid()
    {
        // "CQ DX VK3ABC KN22" is 18 chars but all fields are valid — encoder packs them as fields
        var result = MessageBuilder.Cq("VK3ABC", "KN22", "DX");
        result.IsValid.Should().BeTrue("all fields are valid; structured messages are not char-limited");
    }

    // ── Exchange with string token ─────────────────────────────────────────────

    [Theory]
    [InlineData("W1AW", "K9AN", "-07",  "W1AW K9AN -07")]
    [InlineData("W1AW", "K9AN", "+12",  "W1AW K9AN +12")]
    [InlineData("W1AW", "K9AN", "RRR",  "W1AW K9AN RRR")]
    [InlineData("W1AW", "K9AN", "RR73", "W1AW K9AN RR73")]
    [InlineData("W1AW", "K9AN", "73",   "W1AW K9AN 73")]
    [InlineData("W1AW", "K9AN", "FN42", "W1AW K9AN FN42")]
    [InlineData("lz2hv","ok1te","kn22", "LZ2HV OK1TE KN22")]  // lower-case normalised
    public void Exchange_ValidStringToken_ReturnsExpectedMessage(
        string from, string to, string exchange, string expected)
    {
        var result = MessageBuilder.Exchange(from, to, exchange);
        result.IsValid.Should().BeTrue();
        result.Message.Should().Be(expected);
    }

    [Theory]
    [InlineData("W1AW",   "K9AN",  "GARBAGE")]
    [InlineData("NODIG",  "K9AN",  "FN42")]   // invalid from callsign
    [InlineData("W1AW",   "NODIG", "FN42")]   // invalid to callsign
    [InlineData("",       "K9AN",  "FN42")]   // empty from
    [InlineData("W1AW",   "",      "FN42")]   // empty to
    public void Exchange_InvalidInputs_ReturnsFail(string from, string to, string exchange)
    {
        var result = MessageBuilder.Exchange(from, to, exchange);
        result.IsValid.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    // ── Exchange with numeric SNR ──────────────────────────────────────────────

    [Theory]
    [InlineData("W1AW", "K9AN",  7,  "W1AW K9AN +07")]
    [InlineData("W1AW", "K9AN", -7,  "W1AW K9AN -07")]
    [InlineData("W1AW", "K9AN", 12,  "W1AW K9AN +12")]
    [InlineData("W1AW", "K9AN", -24, "W1AW K9AN -24")]
    [InlineData("W1AW", "K9AN",  0,  "W1AW K9AN +00")]
    public void Exchange_ValidSnrDb_FormatsCorrectly(string from, string to, int snr, string expected)
    {
        MessageBuilder.Exchange(from, to, snr).Message.Should().Be(expected);
    }

    [Theory]
    [InlineData( 50)]  // too high
    [InlineData(-51)]  // too low
    public void Exchange_OutOfRangeSnr_ReturnsFail(int snr)
    {
        MessageBuilder.Exchange("W1AW", "K9AN", snr).IsValid.Should().BeFalse();
    }

    // ── FreeText ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("HELLO",          DigitalMode.IscatA)]
    [InlineData("CQ W1AW",        DigitalMode.FSK441)]
    [InlineData("TEST 73",        DigitalMode.JTMS)]
    [InlineData("METEOR BURST",   DigitalMode.FSK315)]
    public void FreeText_ValidMessage_ReturnsOk(string text, DigitalMode mode)
    {
        var result = MessageBuilder.FreeText(text, mode);
        result.IsValid.Should().BeTrue();
        result.Message.Should().Be(text.ToUpperInvariant().Trim());
    }

    [Fact]
    public void FreeText_TooLong_ReturnsFail()
    {
        string longMsg = new('A', 50);  // JTMS max = 15
        MessageBuilder.FreeText(longMsg, DigitalMode.JTMS)
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void FreeText_InvalidChar_ReturnsFail()
    {
        // ISCAT does not allow '$'
        MessageBuilder.FreeText("HELLO $ WORLD", DigitalMode.IscatA)
            .IsValid.Should().BeFalse();
    }

    // ── Beacon (PI4) ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("LZ2HVV",  "LZ2HVV  ")]   // padded to 8
    [InlineData("OK1TE",   "OK1TE   ")]
    [InlineData("W1AW/4",  "W1AW/4  ")]
    [InlineData("PI4GN",   "PI4GN   ")]
    public void Beacon_ValidCallsign_PaddedToEightChars(string input, string expected)
    {
        var result = MessageBuilder.Beacon(input);
        result.IsValid.Should().BeTrue();
        result.Message.Should().Be(expected);
        result.Message!.Length.Should().Be(8);
    }

    [Fact]
    public void Beacon_TooLong_ReturnsFail()
    {
        MessageBuilder.Beacon("TOOLONGCS").IsValid.Should().BeFalse();
    }

    [Fact]
    public void Beacon_Empty_ReturnsFail()
    {
        MessageBuilder.Beacon("").IsValid.Should().BeFalse();
    }

    [Fact]
    public void Beacon_InvalidChar_ReturnsFail()
    {
        MessageBuilder.Beacon("W1AW-P").IsValid.Should().BeFalse(
            "'-' is not in the PI4 alphabet");
    }

    // ── SuperFoxCq ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("LZ2HVV", "KN23", "CQ LZ2HVV KN23")]
    [InlineData("W1AW",   "FN42", "CQ W1AW FN42")]
    public void SuperFoxCq_ValidInputs_ReturnsExpectedMessage(
        string fox, string grid, string expected)
    {
        MessageBuilder.SuperFoxCq(fox, grid).Message.Should().Be(expected);
    }

    [Fact]
    public void SuperFoxCq_InvalidGrid_ReturnsFail()
    {
        MessageBuilder.SuperFoxCq("LZ2HVV", "ZZ99").IsValid.Should().BeFalse();
    }

    [Fact]
    public void SuperFoxCq_TooLongCallsign_ReturnsFail()
    {
        MessageBuilder.SuperFoxCq("A123456789012", "FN42").IsValid.Should().BeFalse();
    }

    [Fact]
    public void SuperFoxCq_InvalidChar_ReturnsFail()
    {
        MessageBuilder.SuperFoxCq("W1AW#", "FN42").IsValid.Should().BeFalse();
    }

    // ── SuperFoxResponse ──────────────────────────────────────────────────────

    [Fact]
    public void SuperFoxResponse_SingleRr73Hound_ProducesCorrectMessage()
    {
        var result = MessageBuilder.SuperFoxResponse("LZ2HVV",
        [
            new HoundEntry { Callsign = "W1AW" }
        ]);
        result.IsValid.Should().BeTrue();
        result.Message.Should().Be("LZ2HVV W1AW");
    }

    [Fact]
    public void SuperFoxResponse_HoundWithReport_ProducesCorrectMessage()
    {
        var result = MessageBuilder.SuperFoxResponse("LZ2HVV",
        [
            new HoundEntry { Callsign = "W1AW", ReportDb = 5 }
        ]);
        result.Message.Should().Be("LZ2HVV W1AW +05");
    }

    [Fact]
    public void SuperFoxResponse_NegativeReport_FormatsWithMinus()
    {
        var result = MessageBuilder.SuperFoxResponse("LZ2HVV",
        [
            new HoundEntry { Callsign = "K9AN", ReportDb = -12 }
        ]);
        result.Message.Should().Be("LZ2HVV K9AN -12");
    }

    [Fact]
    public void SuperFoxResponse_MixedHounds_ProducesCompoundMessage()
    {
        var result = MessageBuilder.SuperFoxResponse("LZ2HVV",
        [
            new HoundEntry { Callsign = "W1AW",  ReportDb = 5 },
            new HoundEntry { Callsign = "K9AN",  ReportDb = null },
            new HoundEntry { Callsign = "N0ARY", ReportDb = -3 },
        ]);
        result.IsValid.Should().BeTrue();
        result.Message.Should().Be("LZ2HVV W1AW +05 K9AN N0ARY -03");
    }

    [Fact]
    public void SuperFoxResponse_MaxCapacity_NineHounds_Succeeds()
    {
        var hounds = Enumerable.Range(1, 5)
            .Select(i => new HoundEntry { Callsign = $"W{i}AW" })
            .Concat(Enumerable.Range(1, 4)
                .Select(i => new HoundEntry { Callsign = $"K{i}AN", ReportDb = i }))
            .ToList();

        MessageBuilder.SuperFoxResponse("LZ2HVV", hounds)
            .IsValid.Should().BeTrue("9 hounds is the maximum");
    }

    [Fact]
    public void SuperFoxResponse_TenHounds_ReturnsFail()
    {
        var hounds = Enumerable.Range(1, 10)
            .Select(i => new HoundEntry { Callsign = $"W{i}AW" });
        MessageBuilder.SuperFoxResponse("LZ2HVV", hounds)
            .IsValid.Should().BeFalse("10 hounds exceeds the 9-hound limit");
    }

    [Fact]
    public void SuperFoxResponse_MoreThanFiveRr73_ReturnsFail()
    {
        var hounds = Enumerable.Range(1, 6)
            .Select(i => new HoundEntry { Callsign = $"W{i}AW" });  // all RR73
        MessageBuilder.SuperFoxResponse("LZ2HVV", hounds)
            .IsValid.Should().BeFalse("at most 5 RR73 per frame");
    }

    [Fact]
    public void SuperFoxResponse_MoreThanFourReports_ReturnsFail()
    {
        var hounds = Enumerable.Range(1, 5)
            .Select(i => new HoundEntry { Callsign = $"W{i}AW", ReportDb = i });  // all with reports
        MessageBuilder.SuperFoxResponse("LZ2HVV", hounds)
            .IsValid.Should().BeFalse("at most 4 with reports per frame");
    }

    [Fact]
    public void SuperFoxResponse_ReportOutOfRange_ReturnsFail()
    {
        MessageBuilder.SuperFoxResponse("LZ2HVV",
        [
            new HoundEntry { Callsign = "W1AW", ReportDb = 20 }  // max is +12
        ]).IsValid.Should().BeFalse("SuperFox report max is +12");
    }

    [Fact]
    public void SuperFoxResponse_EmptyHoundList_ReturnsFail()
    {
        MessageBuilder.SuperFoxResponse("LZ2HVV", [])
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void SuperFoxResponse_InvalidHoundCallsign_ReturnsFail()
    {
        MessageBuilder.SuperFoxResponse("LZ2HVV",
        [
            new HoundEntry { Callsign = "NODIGIT" }
        ]).IsValid.Should().BeFalse();
    }

    // ── Validate ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ W1AW FN42", DigitalMode.FT8, true)]
    [InlineData("W1AW K9AN -07", DigitalMode.FT8, true)]
    [InlineData("HELLO WORLD", DigitalMode.IscatA, true)]
    [InlineData("HELLO WORLD", DigitalMode.FSK441, true)]
    [InlineData("LZ2HVV  ", DigitalMode.PI4, true)]
    public void Validate_ValidMessages_ReturnsOk(string message, DigitalMode mode, bool expected)
    {
        MessageBuilder.Validate(message, mode).IsValid.Should().Be(expected);
    }

    [Theory]
    [InlineData("$ILLEGAL", DigitalMode.FT8)]     // '$' not in standard charset
    [InlineData("$ILLEGAL", DigitalMode.IscatA)]   // '$' not in ISCAT charset
    public void Validate_InvalidChar_ReturnsFail(string message, DigitalMode mode)
    {
        MessageBuilder.Validate(message, mode).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_TooLong_ReturnsFail()
    {
        string msg = new('A', 23);   // FT8 MaxLength = 22
        MessageBuilder.Validate(msg, DigitalMode.FT8).IsValid.Should().BeFalse();
    }

    // ── IsValidCallsign internals ─────────────────────────────────────────────

    [Theory]
    [InlineData("W1AW",    true)]
    [InlineData("K9AN",    true)]
    [InlineData("LZ2HV",   true)]
    [InlineData("VK3ABC",  true)]
    [InlineData("EU/W1AW", true)]
    [InlineData("W1AW/P",  true)]
    [InlineData("K9AN/MM", true)]
    [InlineData("",        false)]
    [InlineData("NODIGIT", false)]
    [InlineData("123",     false)]  // no letters
    [InlineData("W/1/AW",  false)]  // double slash
    public void IsValidCallsign_VariousInputs_CorrectResult(string s, bool expected)
    {
        MessageBuilder.IsValidCallsign(s, out _).Should().Be(expected, $"input was '{s}'");
    }

    // ── IsValidGrid4 internals ────────────────────────────────────────────────

    [Theory]
    [InlineData("FN42",  true)]
    [InlineData("KN22",  true)]
    [InlineData("AA00",  true)]
    [InlineData("RR99",  true)]
    [InlineData("SS00",  false)]  // 'S' > 'R' — out of grid range
    [InlineData("FN4",   false)]  // too short
    [InlineData("FN424", false)]  // too long
    [InlineData("1N42",  false)]  // starts with digit
    [InlineData("FNAB",  false)]  // last two must be digits
    public void IsValidGrid4_VariousInputs_CorrectResult(string s, bool expected)
    {
        MessageBuilder.IsValidGrid4(s).Should().Be(expected, $"input was '{s}'");
    }

    // ── BuildResult helpers ───────────────────────────────────────────────────

    [Fact]
    public void BuildResult_Unwrap_OnSuccess_ReturnsMessage()
    {
        MessageBuilder.Cq("W1AW", "FN42").Unwrap().Should().Be("CQ W1AW FN42");
    }

    [Fact]
    public void BuildResult_Unwrap_OnFailure_Throws()
    {
        var fail = BuildResult.Fail("test error");
        fail.Invoking(r => r.Unwrap())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*test error*");
    }

    // ── Round-trip: Build → Parse ─────────────────────────────────────────────

    [Fact]
    public void RoundTrip_Cq_BuildThenParse_MatchesFields()
    {
        string built = MessageBuilder.Cq("W1AW", "FN42").Unwrap();
        var parsed   = MessageParser.Parse(built, DigitalMode.FT8)
            .Should().BeOfType<StandardMessage>().Subject;

        parsed.Direction.Should().Be(MessageDirection.CQ);
        parsed.From.Should().Be("W1AW");
        parsed.Exchange.Should().Be("FN42");
    }

    [Fact]
    public void RoundTrip_Exchange_BuildThenParse_MatchesFields()
    {
        string built = MessageBuilder.Exchange("W1AW", "K9AN", -7).Unwrap();
        var parsed   = MessageParser.Parse(built, DigitalMode.FT8)
            .Should().BeOfType<StandardMessage>().Subject;

        parsed.From.Should().Be("W1AW");
        parsed.To.Should().Be("K9AN");
        parsed.SnrDb.Should().Be(-7);
    }

    [Fact]
    public void RoundTrip_SuperFoxCq_BuildThenParse_MatchesFields()
    {
        string built = MessageBuilder.SuperFoxCq("LZ2HVV", "KN23").Unwrap();
        var parsed   = MessageParser.Parse(built, DigitalMode.SuperFox)
            .Should().BeOfType<SuperFoxCqMessage>().Subject;

        parsed.FoxCallsign.Should().Be("LZ2HVV");
        parsed.Grid.Should().Be("KN23");
    }

    [Fact]
    public void RoundTrip_SuperFoxResponse_BuildThenParse_MatchesFields()
    {
        var hounds = new List<HoundEntry>
        {
            new() { Callsign = "W1AW",  ReportDb = 5   },
            new() { Callsign = "K9AN",  ReportDb = null },
            new() { Callsign = "N0ARY", ReportDb = -3  },
        };

        string built = MessageBuilder.SuperFoxResponse("LZ2HVV", hounds).Unwrap();
        var parsed   = MessageParser.Parse(built, DigitalMode.SuperFox)
            .Should().BeOfType<SuperFoxResponseMessage>().Subject;

        parsed.FoxCallsign.Should().Be("LZ2HVV");
        parsed.Hounds.Should().HaveCount(3);
        parsed.Hounds[0].Callsign.Should().Be("W1AW");
        parsed.Hounds[0].ReportDb.Should().Be(5);
        parsed.Hounds[1].IsRr73.Should().BeTrue();
        parsed.Hounds[2].ReportDb.Should().Be(-3);
    }

    // ── SuperFoxTextResponse ──────────────────────────────────────────────────

    [Fact]
    public void SuperFoxTextResponse_SingleRr73Hound_ProducesCorrectFormat()
    {
        var result = MessageBuilder.SuperFoxTextResponse("LZ2HVV",
            [new HoundEntry { Callsign = "W4ABC" }],
            "QSL QRZ");

        result.IsValid.Should().BeTrue();
        result.Message.Should().Be("LZ2HVV W4ABC ~ QSL QRZ");
    }

    [Fact]
    public void SuperFoxTextResponse_HoundWithReport_ProducesCorrectFormat()
    {
        var result = MessageBuilder.SuperFoxTextResponse("LZ2HVV",
            [new HoundEntry { Callsign = "W4ABC", ReportDb = 3 }],
            "TEST");

        result.IsValid.Should().BeTrue();
        result.Message.Should().Be("LZ2HVV W4ABC +03 ~ TEST");
    }

    [Fact]
    public void SuperFoxTextResponse_NegativeReport_FormatsWithMinus()
    {
        var result = MessageBuilder.SuperFoxTextResponse("LZ2HVV",
            [new HoundEntry { Callsign = "G4XYZ", ReportDb = -7 }],
            "DE LZ2HVV");

        result.IsValid.Should().BeTrue();
        result.Message.Should().Be("LZ2HVV G4XYZ -07 ~ DE LZ2HVV");
    }

    [Fact]
    public void SuperFoxTextResponse_EmptyFreeText_TildeOnly()
    {
        var result = MessageBuilder.SuperFoxTextResponse("LZ2HVV",
            [new HoundEntry { Callsign = "W4ABC" }],
            "");

        result.IsValid.Should().BeTrue();
        result.Message.Should().Be("LZ2HVV W4ABC ~");
    }

    [Fact]
    public void SuperFoxTextResponse_MaxFreeText26Chars_Succeeds()
    {
        string text = new string('A', 26);
        MessageBuilder.SuperFoxTextResponse("LZ2HVV",
            [new HoundEntry { Callsign = "W4ABC" }],
            text)
            .IsValid.Should().BeTrue("26 chars is the maximum allowed");
    }

    [Fact]
    public void SuperFoxTextResponse_TooManyHounds_ReturnsFail()
    {
        var hounds = Enumerable.Range(0, 5)
            .Select(i => new HoundEntry { Callsign = $"W{i}AAA" })
            .ToList();

        MessageBuilder.SuperFoxTextResponse("LZ2HVV", hounds, "TEXT")
            .IsValid.Should().BeFalse("at most 4 hounds are allowed for i3=2");
    }

    [Fact]
    public void SuperFoxTextResponse_FreeTextTooLong_ReturnsFail()
    {
        MessageBuilder.SuperFoxTextResponse("LZ2HVV",
            [new HoundEntry { Callsign = "W4ABC" }],
            new string('A', 27))
            .IsValid.Should().BeFalse("free text exceeds 26 characters");
    }

    [Fact]
    public void SuperFoxTextResponse_InvalidCharInFreeText_ReturnsFail()
    {
        MessageBuilder.SuperFoxTextResponse("LZ2HVV",
            [new HoundEntry { Callsign = "W4ABC" }],
            "HELLO!")  // '!' is not in the base-42 alphabet
            .IsValid.Should().BeFalse("'!' is not valid in the SuperFox base-42 alphabet");
    }

    [Fact]
    public void SuperFoxTextResponse_AllBase42Chars_Succeeds()
    {
        // Base-42 alphabet: space + 0-9 + A-Z + + - . / ?
        // Test that all special characters are accepted
        MessageBuilder.SuperFoxTextResponse("LZ2HVV",
            [new HoundEntry { Callsign = "W4ABC" }],
            "0 +-./?")
            .IsValid.Should().BeTrue("all base-42 special chars must be accepted");
    }

    // ── EU VHF Contest (i3=5) ─────────────────────────────────────────────────

    [Fact]
    public void EuVhfContest_ValidInputs_ReturnsExpectedMessage()
    {
        var result = MessageBuilder.EuVhfContest("PA3XYZ/P", "G4ABC/P", 590003, "IO91NP");
        result.IsValid.Should().BeTrue();
        result.Message.Should().Be("<PA3XYZ/P> <G4ABC/P> 590003 IO91NP");
    }

    [Fact]
    public void EuVhfContest_WithR_IncludesR()
    {
        var result = MessageBuilder.EuVhfContest("PA3XYZ/P", "G4ABC/P", 590003, "IO91NP", withR: true);
        result.IsValid.Should().BeTrue();
        result.Message.Should().Be("<PA3XYZ/P> <G4ABC/P> R 590003 IO91NP");
    }

    [Fact]
    public void EuVhfContest_WrapsCallsignsInBrackets()
    {
        // Calls passed without angle brackets are wrapped automatically
        var result = MessageBuilder.EuVhfContest("W1AW", "K9AN", 520001, "JN89AB");
        result.IsValid.Should().BeTrue();
        result.Message.Should().StartWith("<W1AW> <K9AN>");
    }

    [Fact]
    public void EuVhfContest_AlreadyWrapped_NotDoubleWrapped()
    {
        // Calls already in angle brackets must not be double-wrapped
        var result = MessageBuilder.EuVhfContest("<W1AW>", "<K9AN>", 520001, "JN89AB");
        result.IsValid.Should().BeTrue();
        result.Message.Should().StartWith("<W1AW> <K9AN>");
        result.Message.Should().NotContain("<<");
    }

    [Theory]
    [InlineData(520000)]  // one below minimum
    [InlineData(594096)]  // one above maximum
    [InlineData(0)]
    [InlineData(999999)]
    public void EuVhfContest_ExchangeOutOfRange_ReturnsFail(int exchange)
    {
        MessageBuilder.EuVhfContest("W1AW", "K9AN", exchange, "IO91NP")
            .IsValid.Should().BeFalse(
                $"exchange {exchange} is outside 520001–594095");
    }

    [Theory]
    [InlineData("IO91N")]    // too short (5 chars)
    [InlineData("IO91NPQ")]  // too long (7 chars)
    [InlineData("1O91NP")]   // starts with digit
    [InlineData("IO91ZZ")]   // subsquare Z > X
    [InlineData("SO91NP")]   // field S > R
    public void EuVhfContest_InvalidGrid6_ReturnsFail(string grid6)
    {
        MessageBuilder.EuVhfContest("W1AW", "K9AN", 590003, grid6)
            .IsValid.Should().BeFalse($"grid '{grid6}' is not valid");
    }

    [Fact]
    public void EuVhfContest_LowerCaseInputs_Normalised()
    {
        var result = MessageBuilder.EuVhfContest("pa3xyz/p", "g4abc/p", 590003, "io91np");
        result.IsValid.Should().BeTrue("lower-case inputs must be normalised");
        result.Message.Should().Be("<PA3XYZ/P> <G4ABC/P> 590003 IO91NP");
    }

    [Fact]
    public void EuVhfContest_RoundTrip_BuildThenParse_MatchesFields()
    {
        string built = MessageBuilder.EuVhfContest("PA3XYZ/P", "G4ABC/P", 590003, "IO91NP", withR: true).Unwrap();
        var parsed = MessageParser.Parse(built)
            .Should().BeOfType<EuVhfContestMessage>().Subject;

        parsed.Call1.Should().Be("<PA3XYZ/P>");
        parsed.Call2.Should().Be("<G4ABC/P>");
        parsed.HasR.Should().BeTrue();
        parsed.Exchange.Should().Be("590003");
        parsed.Grid.Should().Be("IO91NP");
    }

    // ── DXpedition builder (i3=0, n3=1) ─────────────────────────────────────

    [Theory]
    [InlineData("K1ABC",  "W9XYZ",  "KH1/KH7Z", -12, "K1ABC RR73; W9XYZ <KH1/KH7Z> -12")]
    [InlineData("DL1ABC", "VK2ZD",  "P29KPH",   +6,  "DL1ABC RR73; VK2ZD <P29KPH> +06")]
    [InlineData("OK1TE",  "W1AW",   "VP8ABC",    0,  "OK1TE RR73; W1AW <VP8ABC> +00")]
    [InlineData("K1ABC",  "W9XYZ",  "KH1/KH7Z", -30, "K1ABC RR73; W9XYZ <KH1/KH7Z> -30")]
    [InlineData("K1ABC",  "W9XYZ",  "KH1/KH7Z", +30, "K1ABC RR73; W9XYZ <KH1/KH7Z> +30")]
    public void DXpeditionResponse_ValidInputs_ReturnsExpectedMessage(
        string callRr73, string callReport, string dxCall, int report, string expected)
    {
        var result = MessageBuilder.DXpeditionResponse(callRr73, callReport, dxCall, report);
        result.IsValid.Should().BeTrue($"must be valid for report={report}");
        result.Message.Should().Be(expected);
    }

    [Fact]
    public void DXpeditionResponse_OddReport_RoundsDownToEven()
    {
        // -13 → n5=8 → decoded=-14; the builder emits the representable value
        var result = MessageBuilder.DXpeditionResponse("K1ABC", "W9XYZ", "KH1/KH7Z", -13);
        result.IsValid.Should().BeTrue();
        result.Message.Should().Be("K1ABC RR73; W9XYZ <KH1/KH7Z> -14",
            "odd report is rounded down to the nearest representable even value");
    }

    [Fact]
    public void DXpeditionResponse_AngleBracketInput_StrippedBeforeOutput()
    {
        // Caller passes "<KH1/KH7Z>"; builder should add its own brackets
        var result = MessageBuilder.DXpeditionResponse("K1ABC", "W9XYZ", "<KH1/KH7Z>", -12);
        result.IsValid.Should().BeTrue();
        result.Message.Should().Be("K1ABC RR73; W9XYZ <KH1/KH7Z> -12");
    }

    [Fact]
    public void DXpeditionResponse_LowerCaseInputs_Normalised()
    {
        var result = MessageBuilder.DXpeditionResponse("k1abc", "w9xyz", "kh1/kh7z", -12);
        result.IsValid.Should().BeTrue();
        result.Message.Should().Be("K1ABC RR73; W9XYZ <KH1/KH7Z> -12");
    }

    [Theory]
    [InlineData("",      "W9XYZ", "KH1/KH7Z", -12)]   // empty callRr73
    [InlineData("K1ABC", "",      "KH1/KH7Z", -12)]   // empty callReport
    [InlineData("K1ABC", "W9XYZ", "",          -12)]   // empty DX callsign
    [InlineData("K1ABC", "W9XYZ", "KH1/KH7Z", -31)]   // report below -30
    [InlineData("K1ABC", "W9XYZ", "KH1/KH7Z",  31)]   // report above +30
    [InlineData("NODIG", "W9XYZ", "KH1/KH7Z", -12)]   // invalid callsign (no area digit)
    public void DXpeditionResponse_InvalidInputs_ReturnsFail(
        string callRr73, string callReport, string dxCall, int report)
    {
        var result = MessageBuilder.DXpeditionResponse(callRr73, callReport, dxCall, report);
        result.IsValid.Should().BeFalse($"invalid inputs must fail");
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void DXpeditionResponse_RoundTrip_BuildThenParse_MatchesFields()
    {
        string built = MessageBuilder.DXpeditionResponse("K1ABC", "W9XYZ", "KH1/KH7Z", -12).Unwrap();
        var parsed = MessageParser.Parse(built)
            .Should().BeOfType<DXpeditionMessage>().Subject;

        parsed.CallRr73.Should().Be("K1ABC");
        parsed.CallReport.Should().Be("W9XYZ");
        parsed.DxCallsign.Should().Be("<KH1/KH7Z>");
        parsed.ReportDb.Should().Be(-12);
    }
}
