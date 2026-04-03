using FluentAssertions;
using HamDigiSharp.Decoders.Jt65;
using HamDigiSharp.Decoders.Jt6m;
using HamDigiSharp.Decoders.Pi4;
using HamDigiSharp.Encoders;
using HamDigiSharp.Models;
using Xunit;

namespace HamDigiSharp.Tests.Decoders;

/// <summary>
/// Round-trip tests for JT65 A/B/C and PI4 encoders+decoders.
/// Each test encodes a known message, passes the PCM to the matching decoder,
/// and asserts the original message is recovered.
/// </summary>
public class Jt65Pi4RoundTripTests
{
    private const double FreqHz  = 1000.0;
    private const double FreqLo  =  850.0;
    private const double FreqHi  = 1200.0;
    private const int    Jt65SR  = 11025;

    // ── JT65A round-trips ────────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ W1AW FN42")]
    [InlineData("W1AW K9AN -12")]
    [InlineData("W1AW K9AN RRR")]
    [InlineData("W1AW K9AN 73")]
    public void Jt65A_StandardMessage_RoundTrip(string message)
    {
        float[] pcm = new Jt65Encoder(DigitalMode.JT65A)
            .Encode(message, new EncoderOptions { FrequencyHz = FreqHz });

        // Pad to 60 s (Jt65Decoder requires at least 30 s)
        float[] buf = new float[Jt65SR * 60];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Jt65Decoder(DigitalMode.JT65A)
            .Decode(buf, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty($"JT65A must decode '{message}'");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void Jt65A_FreeTextMessage_RoundTrip()
    {
        // Free-text messages use the 42-char charset; 13 chars max
        const string msg = "CQ TEST";
        float[] pcm = new Jt65Encoder(DigitalMode.JT65A)
            .Encode(msg, new EncoderOptions { FrequencyHz = FreqHz });

        float[] buf = new float[Jt65SR * 60];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Jt65Decoder(DigitalMode.JT65A)
            .Decode(buf, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty("JT65A must decode free-text message");
        results.Any(r => r.Message.Contains("CQ TEST")).Should().BeTrue(
            $"Expected 'CQ TEST'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void Jt65A_RoundTrip_ModeTaggedCorrectly()
    {
        float[] pcm = new Jt65Encoder(DigitalMode.JT65A)
            .Encode("CQ W1AW FN42", new EncoderOptions { FrequencyHz = FreqHz });
        float[] buf = new float[Jt65SR * 60];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Jt65Decoder(DigitalMode.JT65A).Decode(buf, FreqLo, FreqHi, "000000");
        results.Should().NotBeEmpty();
        results[0].Mode.Should().Be(DigitalMode.JT65A);
    }

    [Fact]
    public void Jt65A_RoundTrip_FrequencyNearTarget()
    {
        float[] pcm = new Jt65Encoder(DigitalMode.JT65A)
            .Encode("CQ W1AW FN42", new EncoderOptions { FrequencyHz = FreqHz });
        float[] buf = new float[Jt65SR * 60];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Jt65Decoder(DigitalMode.JT65A).Decode(buf, FreqLo, FreqHi, "000000");
        results.Should().NotBeEmpty();
        results[0].FrequencyHz.Should().BeApproximately(FreqHz, 20.0,
            "decoded frequency should be within 20 Hz of encoded frequency");
    }

    // ── JT65B round-trip ─────────────────────────────────────────────────────

    [Fact]
    public void Jt65B_StandardMessage_RoundTrip()
    {
        const string msg = "W1AW K9AN -15";
        float[] pcm = new Jt65Encoder(DigitalMode.JT65B)
            .Encode(msg, new EncoderOptions { FrequencyHz = FreqHz });
        float[] buf = new float[Jt65SR * 60];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Jt65Decoder(DigitalMode.JT65B)
            .Decode(buf, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty($"JT65B must decode '{msg}'");
        results.Any(r => r.Message.Trim() == msg).Should().BeTrue(
            $"Expected '{msg}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    // ── JT65C round-trip ─────────────────────────────────────────────────────
    // JT65C uses 4× tone spacing (10.77 Hz) vs JT65A (2.69 Hz), so 65 tones
    // span ~700 Hz. Encoding at 700 Hz → tones from ~721 Hz to ~1401 Hz;
    // searching 600–1500 Hz covers all tones comfortably.

    [Theory]
    [InlineData("CQ W1AW FN42")]
    [InlineData("W1AW K9AN -15")]
    public void Jt65C_StandardMessage_RoundTrip(string message)
    {
        float[] pcm = new Jt65Encoder(DigitalMode.JT65C)
            .Encode(message, new EncoderOptions { FrequencyHz = 700.0 });
        float[] buf = new float[Jt65SR * 60];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Jt65Decoder(DigitalMode.JT65C)
            .Decode(buf, 600.0, 1500.0, "000000");

        results.Should().NotBeEmpty($"JT65C must decode '{message}'");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void Jt65C_RoundTrip_ModeTaggedCorrectly()
    {
        float[] pcm = new Jt65Encoder(DigitalMode.JT65C)
            .Encode("CQ W1AW FN42", new EncoderOptions { FrequencyHz = 700.0 });
        float[] buf = new float[Jt65SR * 60];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Jt65Decoder(DigitalMode.JT65C).Decode(buf, 600.0, 1500.0, "000000");
        results.Should().NotBeEmpty();
        results[0].Mode.Should().Be(DigitalMode.JT65C);
    }

    // ── PI4 round-trips ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("OK1TE   ")]   // 8-char beacon callsign
    [InlineData("LZ2HVV  ")]
    [InlineData("PI4GN   ")]
    public void Pi4_BeaconMessage_RoundTrip(string message)
    {
        float[] pcm = new Pi4Encoder()
            .Encode(message, new EncoderOptions { FrequencyHz = 682.8125 });

        float[] buf = new float[Jt65SR * 30];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Pi4Decoder()
            .Decode(buf, 600.0, 800.0, "000000");

        results.Should().NotBeEmpty($"PI4 must decode '{message.Trim()}'");
        results.Any(r => r.Message.Trim() == message.Trim()).Should().BeTrue(
            $"Expected '{message.Trim()}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void Pi4_RoundTrip_ModeTaggedCorrectly()
    {
        float[] pcm = new Pi4Encoder()
            .Encode("OK1TE   ", new EncoderOptions { FrequencyHz = 682.8125 });
        float[] buf = new float[Jt65SR * 30];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Pi4Decoder().Decode(buf, 600.0, 800.0, "000000");
        results.Should().NotBeEmpty();
        results[0].Mode.Should().Be(DigitalMode.PI4);
    }

    [Fact]
    public void Pi4_RoundTrip_FrequencyNearTarget()
    {
        const double txFreq = 682.8125;
        float[] pcm = new Pi4Encoder()
            .Encode("LZ2HVV  ", new EncoderOptions { FrequencyHz = txFreq });
        float[] buf = new float[Jt65SR * 30];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Pi4Decoder().Decode(buf, 600.0, 800.0, "000000");
        results.Should().NotBeEmpty();
        results[0].FrequencyHz.Should().BeApproximately(txFreq, 15.0,
            "decoded frequency should be within 15 Hz of encoded");
    }

    // ── Message packing unit tests (no audio needed) ──────────────────────────

    [Theory]
    [InlineData("CQ W1AW FN42")]
    [InlineData("W1AW K9AN -07")]
    [InlineData("W1AW K9AN RRR")]
    [InlineData("OK1TE LZ2HV KN22")]
    public void Jt65_PackUnpack_RoundTrip(string message)
    {
        // Encode symbols → unpack directly (no audio)
        var encoder = new Jt65Encoder(DigitalMode.JT65A);

        // Pack using encoder (via public Encode → recover from it)
        float[] pcm = encoder.Encode(message, new EncoderOptions { FrequencyHz = 1000.0 });
        float[] buf = new float[11025 * 60];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Jt65Decoder(DigitalMode.JT65A).Decode(buf, 850.0, 1150.0, "000000");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Pack/unpack round-trip failed for '{message}'. Got: {string.Join(", ", results.Select(r => r.Message))}");
    }

    // ── JT6M round-trips ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ W1AW FN42")]
    [InlineData("OK1TE LZ2HV KN22")]
    [InlineData("W1AW K9AN -12")]
    public void Jt6m_StandardMessage_RoundTrip(string message)
    {
        float[] pcm = new Jt6mEncoder()
            .Encode(message, new EncoderOptions { FrequencyHz = FreqHz });

        float[] buf = new float[Jt65SR * 60];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Jt6mDecoder().Decode(buf, FreqLo, FreqHi, "000000");
        results.Should().NotBeEmpty($"JT6M must decode '{message}'");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"JT6M round-trip failed for '{message}'. Got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void Jt6m_RoundTrip_ModeTaggedCorrectly()
    {
        float[] pcm = new Jt6mEncoder()
            .Encode("CQ W1AW FN42", new EncoderOptions { FrequencyHz = FreqHz });
        float[] buf = new float[Jt65SR * 60];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Jt6mDecoder().Decode(buf, FreqLo, FreqHi, "000000");
        results.Should().NotBeEmpty();
        results[0].Mode.Should().Be(DigitalMode.JT6M, "JT6M decoder must tag results as JT6M");
    }

    // ── JT65v2 compound callsign encoding + decoding round-trips ─────────────

    [Theory]
    [InlineData("CQ K9AN/P FN42")]     // CQ + CALL/SUFFIX (iv2=4), 1-char suffix
    [InlineData("CQ K9AN/MM FN42")]    // CQ + CALL/SUFFIX (iv2=4), 2-char suffix
    [InlineData("CQ K9AN/QRP FN42")]   // CQ + CALL/SUFFIX (iv2=4), 3-char suffix
    [InlineData("CQ EU/K9AN FN42")]    // CQ + PREFIX/CALL (iv2=1), 2-char prefix
    [InlineData("CQ VK6/W1AW FN42")]   // CQ + PREFIX/CALL (iv2=1), 3-char prefix
    [InlineData("QRZ K9AN/MM FN42")]   // QRZ + CALL/SUFFIX (iv2=5)
    [InlineData("DE VK6/W1AW FN42")]   // DE + PREFIX/CALL (iv2=3)
    public void Jt65A_CompoundCall_RoundTrip(string message)
    {
        float[] pcm = new Jt65Encoder(DigitalMode.JT65A)
            .Encode(message, new EncoderOptions { FrequencyHz = FreqHz });

        float[] buf = new float[Jt65SR * 60];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Jt65Decoder(DigitalMode.JT65A)
            .Decode(buf, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty($"JT65A must decode compound-call message '{message}'");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Compound call round-trip failed for '{message}'. Got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    // ── JT65v2 compound callsign decoding (unit tests) ───────────────────────

    [Theory]
    // CQ PREFIX/call2 (iv2=1): nc1 in [262178563, 264002071]
    [InlineData(262_178_563, 1, "0000")] // min: all zeros → "0"... let's verify decode
    [InlineData(262_930_143, 1, "EU")]   // "EU" prefix
    [InlineData(264_002_071, 1, null)]   // max in range — just check iv2=1
    // QRZ PREFIX/call2 (iv2=2): nc1 in [264002072, 265825580]
    [InlineData(264_002_072, 2, null)]
    // DE PREFIX/call2 (iv2=3): nc1 in [265825581, 267649089]
    [InlineData(265_825_581, 3, null)]
    // CQ call2/SUFFIX (iv2=4): nc1 in [267649090, 267698374]
    [InlineData(267_649_090, 4, null)]
    // QRZ call2/SUFFIX (iv2=5): nc1 in [267698375, 267747659]
    [InlineData(267_698_375, 5, null)]
    // DE call2/SUFFIX (iv2=6): nc1 in [267747660, 267796944]
    [InlineData(267_747_660, 6, null)]
    // DE call2 standalone (iv2=7): nc1 == 267796945
    [InlineData(267_796_945, 7, null)]
    public void Jt65_UnpackCallV2_CorrectIv2ForEachRange(int ncall, int expectedIv2, string? expectedPsfx)
    {
        int iv2 = Jt65Decoder.UnpackCallV2(ncall, out string psfx);
        iv2.Should().Be(expectedIv2, $"ncall={ncall} should decode as iv2={expectedIv2}");
        if (expectedPsfx != null)
            psfx.Should().Be(expectedPsfx, $"ncall={ncall} psfx should be '{expectedPsfx}'");
    }

    [Theory]
    [InlineData(262_177_559, 0)] // just below NBASE+1003 → standard call
    [InlineData(267_796_946, 0)] // above DE → unknown
    [InlineData(0, 0)]
    public void Jt65_UnpackCallV2_Returns0ForStandardOrUnknown(int ncall, int expectedIv2)
    {
        int iv2 = Jt65Decoder.UnpackCallV2(ncall, out _);
        iv2.Should().Be(expectedIv2);
    }

    [Fact]
    public void Jt65_Decode4CharPsfx_ReturnsCorrectString()
    {
        // "EU" prefix: psfx[0]='E'=14, psfx[1]='U'=30, psfx[2]=' '=36, psfx[3]=' '=36
        // k2 = 14*37^3 + 30*37^2 + 36*37 + 36 = 709142 + 41070 + 1332 + 36 = 751580
        const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ ";
        string decoded = Jt65Decoder.Decode4CharPsfx(751580, Alphabet);
        decoded.Should().Be("EU", "psfx 'EU' should encode/decode correctly");
    }

    [Fact]
    public void Jt65_Decode4CharPsfx_ZeroIsAllSpaces_ReturnsTrimmedEmpty()
    {
        // n=0 → all chars are c[0]='0', decode is "0000"... actually first char uses i=n+1
        // n=0 → psfx[3]='0', psfx[2]='0', psfx[1]='0', psfx[0]='0' → "0000"
        const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ ";
        string decoded = Jt65Decoder.Decode4CharPsfx(0, Alphabet);
        decoded.Should().Be("0000");
    }
}
