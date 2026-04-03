using FluentAssertions;
using HamDigiSharp.Abstractions;
using HamDigiSharp.Models;
using HamDigiSharp.Protocols;

namespace HamDigiSharp.Tests.Protocols;

public class ProtocolRegistryTests
{
    // ── Registry completeness ─────────────────────────────────────────────────

    [Fact]
    public void All_ContainsEveryDigitalMode()
    {
        var registered = ProtocolRegistry.All.Keys.ToHashSet();
        foreach (var mode in Enum.GetValues<DigitalMode>())
            registered.Should().Contain(mode, $"mode {mode} must be registered");
    }

    [Fact]
    public void Get_ThrowsForUnknownMode()
    {
        var act = () => ProtocolRegistry.Get((DigitalMode)999);
        act.Should().Throw<ArgumentException>();
    }

    // ── IProtocol is in Abstractions ─────────────────────────────────────────

    [Fact]
    public void IProtocol_IsInAbstractionsNamespace()
    {
        typeof(IProtocol).Namespace.Should().Be("HamDigiSharp.Abstractions");
    }

    // ── Codec factories ───────────────────────────────────────────────────────

    [Fact]
    public void CreateDecoder_ReturnsFreshInstanceWithCorrectMode()
    {
        var proto = ProtocolRegistry.Get(DigitalMode.FT8);
        var d1 = proto.CreateDecoder();
        var d2 = proto.CreateDecoder();
        d1.Should().NotBeSameAs(d2);
        d1.Mode.Should().Be(DigitalMode.FT8);
    }

    [Fact]
    public void CanEncode_TrueForModesWithEncoder()
    {
        foreach (var mode in new[] { DigitalMode.FT8, DigitalMode.FT4, DigitalMode.FT2,
                                     DigitalMode.IscatA, DigitalMode.IscatB,
                                     DigitalMode.JT65A, DigitalMode.Q65A, DigitalMode.FSK441 })
            ProtocolRegistry.Get(mode).CanEncode.Should().BeTrue($"mode {mode} has an encoder");
    }

    [Fact]
    public void CanEncode_FalseForReceiveOnlyModes()
    {
        // All modes now have encoders — this test verifies the registry is complete
        foreach (var proto in ProtocolRegistry.All.Values)
            proto.CanEncode.Should().BeTrue($"mode {proto.Mode} should have an encoder");
    }

    [Fact]
    public void CanEncode_MatchesCreateEncoder()
    {
        foreach (var proto in ProtocolRegistry.All.Values)
            proto.CanEncode.Should().Be(proto.CreateEncoder() != null,
                $"CanEncode must match CreateEncoder() != null for {proto.Mode}");
    }

    [Fact]
    public void CreateEncoder_ReturnsNullForModesWithoutEncoder()
    {
        // All modes now have encoders — verify no nulls
        foreach (var proto in ProtocolRegistry.All.Values)
            proto.CreateEncoder().Should().NotBeNull($"mode {proto.Mode} must have an encoder");
    }

    [Fact]
    public void CreateEncoder_ReturnsCorrectModeForEncodedModes()
    {
        foreach (var mode in new[] { DigitalMode.FT8, DigitalMode.FT4, DigitalMode.FT2,
                                     DigitalMode.JT65A, DigitalMode.Q65A, DigitalMode.MSK144,
                                     DigitalMode.IscatA, DigitalMode.IscatB })
        {
            var enc = ProtocolRegistry.Get(mode).CreateEncoder();
            enc.Should().NotBeNull($"mode {mode} should have an encoder");
            enc!.Mode.Should().Be(mode);
        }
    }

    // ── Timing metadata ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(DigitalMode.FT8,    15.0,  12.64, 12000)]
    [InlineData(DigitalMode.FT4,     7.5,   5.04, 12000)]
    [InlineData(DigitalMode.FT2,     3.75,  2.52, 12000)]
    [InlineData(DigitalMode.JT65A,  60.0,  46.81, 11025)]
    [InlineData(DigitalMode.Q65B,   30.0,  24.48, 12000)]
    [InlineData(DigitalMode.MSK144,  1.0,   0.072, 12000)]
    public void TimingProperties_MatchExpected(
        DigitalMode mode, double periodSec, double txSec, int sampleRate)
    {
        var proto = ProtocolRegistry.Get(mode);
        proto.PeriodDuration.TotalSeconds  .Should().BeApproximately(periodSec, 0.01);
        proto.TransmitDuration.TotalSeconds.Should().BeApproximately(txSec,     0.01);
        proto.SampleRate                   .Should().Be(sampleRate);
    }

    // ── Period boundary helpers ───────────────────────────────────────────────

    [Fact]
    public void PeriodStart_FT8_SnapsToPreviousBoundary()
    {
        var proto = ProtocolRegistry.Get(DigitalMode.FT8);
        var utc   = new DateTimeOffset(2026, 1, 1, 14, 30, 22, TimeSpan.Zero);
        proto.PeriodStart(utc)
             .Should().Be(new DateTimeOffset(2026, 1, 1, 14, 30, 15, TimeSpan.Zero));
    }

    [Fact]
    public void PeriodStart_OnBoundary_ReturnsSameMoment()
    {
        var proto = ProtocolRegistry.Get(DigitalMode.FT8);
        var utc   = new DateTimeOffset(2026, 1, 1, 14, 30, 15, TimeSpan.Zero);
        proto.PeriodStart(utc).Should().Be(utc);
    }

    [Fact]
    public void NextPeriodStart_IsExactlyOnePeriodAfter()
    {
        var proto = ProtocolRegistry.Get(DigitalMode.FT4);
        var utc   = new DateTimeOffset(2026, 1, 1, 14, 30, 3, TimeSpan.Zero);
        (proto.NextPeriodStart(utc) - proto.PeriodStart(utc))
            .Should().Be(proto.PeriodDuration);
    }

    [Fact]
    public void PeriodIndex_IncrementsAtBoundary()
    {
        var proto    = ProtocolRegistry.Get(DigitalMode.FT8);
        var boundary = new DateTimeOffset(2026, 1, 1, 14, 30, 15, TimeSpan.Zero);
        var before   = proto.PeriodIndex(boundary.AddMilliseconds(-1));
        proto.PeriodIndex(boundary).Should().Be(before + 1);
    }

    // ── Even / odd period parity ──────────────────────────────────────────────

    [Fact]
    public void IsEvenPeriod_AlternatesAcrossBoundaries()
    {
        var proto = ProtocolRegistry.Get(DigitalMode.FT8);
        var p0    = proto.PeriodStart(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        bool e0   = proto.IsEvenPeriod(p0);
        bool e1   = proto.IsEvenPeriod(p0.Add(proto.PeriodDuration));
        bool e2   = proto.IsEvenPeriod(p0.Add(proto.PeriodDuration * 2));
        e1.Should().Be(!e0, "consecutive periods must have opposite parity");
        e2.Should().Be(e0,  "period N+2 must have same parity as N");
    }

    [Fact]
    public void IsEvenPeriod_TwoStationsAgreeWithin50msSkew()
    {
        var proto = ProtocolRegistry.Get(DigitalMode.FT2);
        var utc   = new DateTimeOffset(2026, 4, 3, 18, 30, 7, 123, TimeSpan.Zero);
        // 50 ms clock skew between stations must not flip parity
        proto.IsEvenPeriod(utc)
             .Should().Be(proto.IsEvenPeriod(utc.AddMilliseconds(50)));
    }

    [Fact]
    public void PeriodStart_ConsistentWithPeriodScheduler()
    {
        var proto = ProtocolRegistry.Get(DigitalMode.FT8);
        var utc   = new DateTimeOffset(2026, 1, 1, 14, 30, 22, TimeSpan.Zero);
        proto.PeriodStart(utc)
             .Should().Be(HamDigiSharp.Engine.PeriodScheduler.CurrentWindowStart(DigitalMode.FT8, utc));
    }

    // ── MessageConstraints ────────────────────────────────────────────────────

    [Fact]
    public void MessageConstraints_AllModesHaveConstraints()
    {
        foreach (var proto in ProtocolRegistry.All.Values)
            proto.MessageConstraints.Should().NotBeNull($"mode {proto.Mode} must expose MessageConstraints");
    }

    [Fact]
    public void MessageConstraints_MaxLengthMatchesMaxMessageLength()
    {
        foreach (var proto in ProtocolRegistry.All.Values)
            proto.MaxMessageLength.Should().Be(proto.MessageConstraints.MaxLength,
                $"MaxMessageLength must be a shorthand for MessageConstraints.MaxLength on {proto.Mode}");
    }

    [Fact]
    public void MessageConstraints_FormatHintIsNonEmpty()
    {
        foreach (var proto in ProtocolRegistry.All.Values)
            proto.MessageConstraints.FormatHint.Should().NotBeNullOrWhiteSpace(
                $"mode {proto.Mode} needs a format hint");
    }

    [Theory]
    [InlineData(DigitalMode.FT8,      "CQ W1AW FN42")]
    [InlineData(DigitalMode.FT4,      "CQ DX W1AW")]
    [InlineData(DigitalMode.FT2,      "R-09")]
    [InlineData(DigitalMode.IscatA,   "HELLO WORLD")]
    [InlineData(DigitalMode.IscatB,   "TEST 123")]
    [InlineData(DigitalMode.FSK441,   "CQ ANYONE")]
    [InlineData(DigitalMode.PI4,      "OK1TE")]
    [InlineData(DigitalMode.SuperFox, "CQ LZ2HVV KN23")]
    [InlineData(DigitalMode.SuperFox, "LZ2HVV W1AW +09 W2JQ -03 K1ABC K2DEF K3GHI")]
    public void MessageConstraints_ValidMessagesPassValidation(DigitalMode mode, string message)
    {
        var err = ProtocolRegistry.Get(mode).MessageConstraints.Validate(message);
        err.Should().BeNull($"'{message}' should be valid for {mode}");
    }

    [Theory]
    [InlineData(DigitalMode.FT8,    "THIS MESSAGE IS WAY TOO LONG FOR FT8")]  // exceeds 13
    [InlineData(DigitalMode.IscatA, "HELLO\x01WORLD")]                         // control char
    [InlineData(DigitalMode.PI4,    "AVERYLONGCALLSIGN")]                       // exceeds 8
    [InlineData(DigitalMode.FSK441, "@INVALID")]                                // @ not in FSK alphabet
    public void MessageConstraints_InvalidMessagesFailValidation(DigitalMode mode, string message)
    {
        var err = ProtocolRegistry.Get(mode).MessageConstraints.Validate(message);
        err.Should().NotBeNull($"'{message}' should be invalid for {mode}");
    }

    [Fact]
    public void MessageConstraints_EmptyMessageAlwaysValid()
    {
        foreach (var proto in ProtocolRegistry.All.Values)
            proto.MessageConstraints.Validate("").Should().BeNull(
                $"empty string must be valid for {proto.Mode}");
    }

    [Fact]
    public void MessageConstraints_IscatAllowedCharsMatchDecoder()
    {
        const string IscatAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ /.?@-";
        var proto = ProtocolRegistry.Get(DigitalMode.IscatA);
        proto.MessageConstraints.AllowedChars.Should().Be(IscatAlphabet,
            "ISCAT allowed chars must match the decoder's CharTable alphabet");
    }
}
