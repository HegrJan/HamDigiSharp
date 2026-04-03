using FluentAssertions;
using HamDigiSharp.Engine;
using HamDigiSharp.Models;
using Xunit;

namespace HamDigiSharp.Tests.Decoders;

public class DecoderEngineTests
{
    [Fact]
    public void SupportedModes_ContainsAllRegisteredModes()
    {
        using var engine = new DecoderEngine();
        engine.SupportedModes.Should().Contain(DigitalMode.FT8);
        engine.SupportedModes.Should().Contain(DigitalMode.FT4);
        engine.SupportedModes.Should().Contain(DigitalMode.FT2);
        engine.SupportedModes.Should().Contain(DigitalMode.JT65A);
        engine.SupportedModes.Should().Contain(DigitalMode.MSK144);
        engine.SupportedModes.Should().Contain(DigitalMode.FSK441);
        engine.SupportedModes.Should().Contain(DigitalMode.IscatA);
        engine.SupportedModes.Should().Contain(DigitalMode.PI4);
        engine.SupportedModes.Should().Contain(DigitalMode.JTMS);
    }

    [Fact]
    public void Supports_AllRegisteredModes_ReturnTrue()
    {
        using var engine = new DecoderEngine();
        // All modes should now be registered
        foreach (DigitalMode mode in Enum.GetValues<DigitalMode>())
            engine.Supports(mode).Should().BeTrue($"{mode} should be registered");
    }

    [Fact]
    public void Configure_DoesNotThrow()
    {
        using var engine = new DecoderEngine();
        var act = () => engine.Configure(new DecoderOptions
        {
            MyCall = "W1AW",
            HisCall = "OK1TE",
            MyGrid = "FN31",
            DecoderDepth = DecoderDepth.Normal,
            ApDecode = true,
        });
        act.Should().NotThrow();
    }

    [Fact]
    public async Task DecodeAsync_Silence_ReturnsEmpty()
    {
        using var engine = new DecoderEngine();
        var silence = new float[180000];
        var results = await engine.DecodeAsync(silence, DigitalMode.FT8, 200, 3000, "000000");
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task DecodeAsync_UnsupportedMode_ReturnsEmpty()
    {
        using var engine = new DecoderEngine();
        var silence = new float[180000];
        var results = await engine.DecodeAsync(silence, DigitalMode.JT65A, 200, 3000, "000000");
        results.Should().BeEmpty();
    }

    [Fact]
    public void Decode_Synchronous_DoesNotThrow()
    {
        using var engine = new DecoderEngine();
        var silence = new float[180000];
        var act = () => engine.Decode(silence, DigitalMode.FT8, 200, 3000, "000000");
        act.Should().NotThrow();
    }
}
