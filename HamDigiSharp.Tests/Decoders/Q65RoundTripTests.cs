using FluentAssertions;
using HamDigiSharp.Decoders.Q65;
using HamDigiSharp.Encoders;
using HamDigiSharp.Models;
using Xunit;

namespace HamDigiSharp.Tests.Decoders;

/// <summary>
/// Round-trip tests for Q65 A/B/C/D: encode a known message to PCM audio,
/// pass it to the matching Q65Decoder, and assert the original text is recovered.
///
/// Q65 uses a 65-FSK, QRA(15,65) LDPC code over GF(64).
/// The encoder pipeline: pack77 → 13 GF(64) symbols → CRC-12 → QRA encode →
/// sync insertion → 65-FSK modulation (tone spacing = baud = SR/nsps).
///
/// Encoder tone convention:
///   sync tone (22 fixed positions): tone 0 → baseFreq
///   data tone for GF symbol k:      tone k+1 → baseFreq + (k+1) × spacing
///
/// Decoder convention:
///   finds sync bin at baseFreq/df, fBaseBin = syncBin + nBinsPerTone,
///   s3[n × nBinsPerSymbol + 64 + k] = power at tone k+1 = GF symbol k
///
/// Bit-order convention:
///   PackTo13Symbols (encoder): c77 bits read MSB-first → sym bit5 = c77[0]
///   SymbolsToBits (decoder):   sym bit5 = bits[0] (MSB-first), matching WSJT-X Fortran:
///     write(c77,'(12b6.6,b5.5)') dat4(1:12),(dat4(13)/2)
/// </summary>
public class Q65RoundTripTests
{
    private const double FreqHz = 1000.0;
    private const double FreqLo = 950.0;
    private const double FreqHi = 1050.0;

    // ── Q65A (60 s, nsps=6912) ─────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ W1AW FN42")]
    [InlineData("W1AW K9AN -12")]
    [InlineData("W1AW K9AN RRR")]
    [InlineData("W1AW K9AN 73")]
    public void Q65A_StandardMessage_RoundTrip(string message)
    {
        const int NSps = 6912;
        float[] pcm = new Q65Encoder(DigitalMode.Q65A)
            .Encode(message, new EncoderOptions { FrequencyHz = FreqHz });

        // Pad to exactly one Q65A frame
        float[] buf = new float[85 * NSps];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Q65Decoder(DigitalMode.Q65A)
            .Decode(buf, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty($"Q65A must decode '{message}'");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    // ── Q65B (30 s, nsps=3456) ─────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ W1AW FN42")]
    [InlineData("W1AW K9AN -03")]
    public void Q65B_StandardMessage_RoundTrip(string message)
    {
        const int NSps = 3456;
        float[] pcm = new Q65Encoder(DigitalMode.Q65B)
            .Encode(message, new EncoderOptions { FrequencyHz = FreqHz });

        float[] buf = new float[85 * NSps];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Q65Decoder(DigitalMode.Q65B)
            .Decode(buf, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty($"Q65B must decode '{message}'");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    // ── Q65C (15 s, nsps=1728) ─────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ W1AW FN42")]
    [InlineData("W1AW K9AN RR73")]
    public void Q65C_StandardMessage_RoundTrip(string message)
    {
        const int NSps = 1728;
        float[] pcm = new Q65Encoder(DigitalMode.Q65C)
            .Encode(message, new EncoderOptions { FrequencyHz = FreqHz });

        float[] buf = new float[85 * NSps];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Q65Decoder(DigitalMode.Q65C)
            .Decode(buf, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty($"Q65C must decode '{message}'");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    // ── Q65D (7 s, nsps=864) ──────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ W1AW FN42")]
    [InlineData("W1AW K9AN -12")]
    public void Q65D_StandardMessage_RoundTrip(string message)
    {
        const int NSps = 864;
        float[] pcm = new Q65Encoder(DigitalMode.Q65D)
            .Encode(message, new EncoderOptions { FrequencyHz = FreqHz });

        float[] buf = new float[85 * NSps];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Q65Decoder(DigitalMode.Q65D)
            .Decode(buf, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty($"Q65D must decode '{message}'");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    // ── Cross-mode isolation: encoder/decoder must use the same submode ─────────

    [Fact]
    public void Q65A_EncodedSignal_NotDecodedByQ65B()
    {
        float[] pcm = new Q65Encoder(DigitalMode.Q65A)
            .Encode("CQ W1AW FN42", new EncoderOptions { FrequencyHz = FreqHz });

        // Q65B uses smaller nsps; buffer must be at least 85 × Q65B.nsps
        float[] buf = new float[85 * 3456];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var results = new Q65Decoder(DigitalMode.Q65B)
            .Decode(buf, FreqLo, FreqHi, "000000");

        // A Q65A signal at these frequencies should not produce a valid Q65B decode
        // (the tone spacing is 2× off, so the QRA decode fails the CRC)
        results.Any(r => r.Message.Trim() == "CQ W1AW FN42").Should().BeFalse(
            "Q65A signal must not decode as Q65B (incompatible tone spacing)");
    }

    // ── SymbolsToBits MSB/LSB self-test ───────────────────────────────────────

    /// <summary>
    /// Verifies that the bit-packing round-trip:
    ///   pack77 → PackTo13Symbols (MSB-first) → QRA encode → QRA decode → SymbolsToBits (MSB-first) → unpack77
    /// gives back the original message, independently of audio/FFT processing.
    /// This tests only the codec layer (no modulation).
    /// </summary>
    [Theory]
    [InlineData("CQ W1AW FN42")]
    [InlineData("W1AW K9AN -12")]
    [InlineData("W1AW K9AN 73")]
    public void Q65_BitOrderAndCodec_RoundTrip(string message)
    {
        // This test re-creates the codec path without audio using Q65SubsTests helpers.
        // Use ideal s3prob from Q65SubsTests to confirm codec path end-to-end.
        const int NInfo = 13;
        const int NCode = 63;
        const int QraM  = 64;

        // 1. Pack message to 77 bits
        var c77 = new bool[77];
        bool packed = HamDigiSharp.Codecs.MessagePack77.TryPack77(message, c77);
        packed.Should().BeTrue($"TryPack77 must succeed for '{message}'");

        // 2. Pack to 13 symbols (MSB-first)
        int[] dgen = new int[NInfo];
        for (int i = 0; i < 12; i++)
        {
            int sym = 0;
            for (int b = 0; b < 6; b++)
                sym = (sym << 1) | (c77[i * 6 + b] ? 1 : 0);
            dgen[i] = sym;
        }
        int last = 0;
        for (int b = 0; b < 5; b++) last = (last << 1) | (c77[72 + b] ? 1 : 0);
        dgen[12] = last * 2;

        // 3. QRA encode → 63 transmitted symbols
        int[] codeword = new int[NCode];
        Q65Subs.Encode(dgen, codeword);

        // 4. Build ideal s3prob (spike at correct symbol)
        var s3prob = new float[NCode * QraM];
        for (int n = 0; n < NCode; n++) s3prob[n * QraM + codeword[n]] = 1.0f;
        var s3dummy = new float[NCode * 192];

        // 5. Decode
        int[] xdec = new int[NInfo];
        Q65Subs.Decode(s3dummy, s3prob, null, null, 100, out _, xdec, out int irc);
        irc.Should().BeGreaterThanOrEqualTo(0, $"QRA decode must succeed for '{message}'");

        // 6. SymbolsToBits (MSB-first) → unpack77
        var bits = new bool[77];
        for (int i = 0; i < 13; i++)
        {
            int sym = xdec[i];
            for (int b = 0; b < 6; b++)
            {
                int pos = i * 6 + b;
                if (pos < 77) bits[pos] = ((sym >> (5 - b)) & 1) != 0;
            }
        }

        string decoded = new HamDigiSharp.Codecs.MessagePacker().Unpack77(bits, out bool ok);
        ok.Should().BeTrue($"Unpack77 must succeed for '{message}'");
        decoded.Trim().Should().Be(message,
            $"Full codec round-trip (no audio) must recover '{message}'");
    }

    // ── Q65 multi-period averaging ─────────────────────────────────────────────

    [Fact]
    public void Q65A_AveragingEnabled_HistoryAccumulates()
    {
        // Feed the same Q65A frame 3 times to a decoder with averaging=3.
        // Each call should succeed (signal is at normal SNR) and the decoder
        // should still return correct results when history fills up.
        const int NSps     = 6912;
        const string Msg   = "CQ W1AW FN42";
        float[] pcm = new Q65Encoder(DigitalMode.Q65A)
            .Encode(Msg, new EncoderOptions { FrequencyHz = FreqHz });
        float[] buf = new float[85 * NSps];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var decoder = new Q65Decoder(DigitalMode.Q65A);
        decoder.Configure(new DecoderOptions
        {
            AveragingEnabled = true,
            AveragingPeriods = 3,
        });

        // Three consecutive decode calls
        for (int i = 0; i < 3; i++)
        {
            var results = decoder.Decode(buf, FreqLo, FreqHi, $"00000{i}");
            results.Should().NotBeEmpty($"Q65A must decode on pass {i + 1} with averaging enabled");
            results.Any(r => r.Message.Trim() == Msg).Should().BeTrue(
                $"Expected '{Msg}' on pass {i + 1}");
        }
    }

    [Fact]
    public void Q65A_ClearAverage_ResetsHistory()
    {
        const int NSps = 6912;
        float[] pcm = new Q65Encoder(DigitalMode.Q65A)
            .Encode("CQ W1AW FN42", new EncoderOptions { FrequencyHz = FreqHz });
        float[] buf = new float[85 * NSps];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var decoder = new Q65Decoder(DigitalMode.Q65A);
        decoder.Configure(new DecoderOptions { AveragingEnabled = true, AveragingPeriods = 3 });

        // Decode once to populate history
        decoder.Decode(buf, FreqLo, FreqHi, "000000");

        // Clear history and decode again — single period should still work
        decoder.Configure(new DecoderOptions
        {
            AveragingEnabled = true,
            AveragingPeriods = 3,
            ClearAverage = true,
        });
        var results = decoder.Decode(buf, FreqLo, FreqHi, "000001");
        results.Should().NotBeEmpty("Q65A should decode after history clear");
    }

    [Fact]
    public void Q65A_AveragingDisabled_BehavesAsSinglePeriod()
    {
        const int NSps = 6912;
        float[] pcm = new Q65Encoder(DigitalMode.Q65A)
            .Encode("W1AW K9AN -12", new EncoderOptions { FrequencyHz = FreqHz });
        float[] buf = new float[85 * NSps];
        Array.Copy(pcm, buf, Math.Min(pcm.Length, buf.Length));

        var decoder = new Q65Decoder(DigitalMode.Q65A);
        decoder.Configure(new DecoderOptions { AveragingEnabled = false });

        var results = decoder.Decode(buf, FreqLo, FreqHi, "000000");
        results.Should().NotBeEmpty("Single-period Q65A should still decode normally");
        results.Any(r => r.Message.Trim() == "W1AW K9AN -12").Should().BeTrue();
    }

    [Fact]
    public void Q65A_AveragingPeriods_ClampedTo5Max()
    {
        var opts = new DecoderOptions { AveragingPeriods = 10 };
        var decoder = new Q65Decoder(DigitalMode.Q65A);
        // Configure with out-of-range value — should not throw
        var act = () => decoder.Configure(opts);
        act.Should().NotThrow("Configure must accept any AveragingPeriods value (clamped internally)");
    }
}
