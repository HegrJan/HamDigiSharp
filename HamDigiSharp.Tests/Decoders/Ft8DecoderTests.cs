using FluentAssertions;
using HamDigiSharp.Decoders.Ft8;
using HamDigiSharp.Encoders;
using HamDigiSharp.Models;
using Xunit;

namespace HamDigiSharp.Tests.Decoders;

public class Ft8DecoderTests
{
    [Fact]
    public void Decode_SilenceSamples_ReturnsNoResults()
    {
        var decoder = new Ft8Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Fast });

        var silence = new float[180000]; // 15 s at 12000 Hz
        var results = decoder.Decode(silence, 200, 3000, "000000");

        results.Should().BeEmpty("silence contains no decodable signal");
    }

    [Fact]
    public void Decode_TooShortBuffer_ReturnsNoResults()
    {
        var decoder = new Ft8Decoder();
        var tooShort = new float[100];
        var results = decoder.Decode(tooShort, 200, 3000, "000000");
        results.Should().BeEmpty();
    }

    [Fact]
    public void Decode_ResultAvailableEvent_Fires()
    {
        var decoder = new Ft8Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Fast });

        var fired = new List<DecodeResult>();
        decoder.ResultAvailable += fired.Add;

        // Silence → no events expected
        var silence = new float[180000];
        decoder.Decode(silence, 200, 3000, "000000");

        fired.Should().BeEmpty();
    }

    [Fact]
    public void Decode_Noise_DoesNotCrash()
    {
        var decoder = new Ft8Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Fast });

        var rng = new Random(42);
        var noise = new float[180000];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 2 - 1);

        // Should not throw, may return empty or results (unlikely for white noise)
        var act = () => decoder.Decode(noise, 200, 3000, "120000");
        act.Should().NotThrow();
    }

    /// <summary>
    /// Encode→decode round-trip at high SNR. The encoder produces a clean GFSK
    /// FT8 signal; the decoder should recover the original message.
    /// </summary>
    [Theory]
    [InlineData("CQ W1AW FN31", 1500.0, 1.0)]
    [InlineData("CQ W1AW FN31", 800.0, 0.5)]
    [InlineData("CQ W1AW FN31", 2500.0, 0.5)]
    public void Decode_EncodedSignal_ReturnsCorrectMessage(string message, double freqHz, double amplitude)
    {
        // Encode into a clean GFSK signal (151680 samples = 79 symbols × 1920 sps)
        var encoder = new Ft8Encoder();
        float[] encoded = encoder.Encode(message, new EncoderOptions
        {
            FrequencyHz = freqHz,
            Amplitude   = amplitude,
        });

        // Pad to 15 s (180000 samples): put signal at start of period
        var samples = new float[180000];
        encoded.AsSpan().CopyTo(samples.AsSpan());

        var decoder = new Ft8Decoder();
        decoder.Configure(new DecoderOptions
        {
            MaxCandidates = 200,
            MinSyncDb     = 2.5f,
            DecoderDepth = DecoderDepth.Normal,
        });

        var results = decoder.Decode(samples, 200, 3000, "000000");

        results.Should().ContainSingle(r => r.Message.Trim() == message.Trim(),
            $"encoded message at {freqHz} Hz should be decodable");
    }

    /// <summary>
    /// Low-SNR round-trip: add white noise to bring SNR to approximately -10 dB
    /// (signal amplitude 0.1, noise std ≈ 0.316 → SNR ≈ 0.01/0.1 = -10 dB in 2500 Hz BW).
    /// </summary>
    [Fact]
    public void Decode_EncodedSignalWithNoise_ReturnsCorrectMessage()
    {
        const string message   = "CQ DL2ABC JN59";
        const double freqHz    = 1200.0;
        const double amplitude = 0.1;   // signal
        const double noiseSd   = 0.316; // ~−10 dB SNR in 2500 Hz

        var encoder = new Ft8Encoder();
        float[] encoded = encoder.Encode(message, new EncoderOptions
        {
            FrequencyHz = freqHz,
            Amplitude   = amplitude,
        });

        var samples = new float[180000];
        encoded.AsSpan().CopyTo(samples.AsSpan());

        var rng = new Random(1234);
        for (int i = 0; i < samples.Length; i++)
            samples[i] += (float)((Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble()))
                                 * Math.Cos(2.0 * Math.PI * rng.NextDouble())) * noiseSd);

        var decoder = new Ft8Decoder();
        decoder.Configure(new DecoderOptions
        {
            MaxCandidates = 200,
            MinSyncDb     = 2.5f,
            DecoderDepth = DecoderDepth.Normal,
        });

        var results = decoder.Decode(samples, 200, 3000, "000000");

        results.Should().ContainSingle(r => r.Message.Trim() == message.Trim(),
            "−10 dB SNR FT8 signal should be decodable");
    }

    /// <summary>
    /// Verify decode works for a signal starting at the very beginning of the recording
    /// (dt=0, like many WebSDR test file signals).
    /// </summary>
    [Theory]
    [InlineData(1285.0)]  // matches test12 DH0KAI frequency
    [InlineData(800.0)]
    [InlineData(2104.0)]
    public void Decode_SignalAtDtZero_DecodesCorrectly(double freqHz)
    {
        const string message = "DH0KAI IZ0MQN -20";

        var encoder = new Ft8Encoder();
        float[] encoded = encoder.Encode(message, new EncoderOptions
        {
            FrequencyHz = freqHz,
            Amplitude   = 0.5,
        });

        // dt=0: signal starts at sample 0 of the 15-second buffer
        var samples = new float[180000];
        encoded.AsSpan().CopyTo(samples.AsSpan());

        var decoder = new Ft8Decoder();
        decoder.Configure(new DecoderOptions
        {
            MaxCandidates = 500,
            MinSyncDb     = -1.0f,  // no threshold
            DecoderDepth = DecoderDepth.Deep,
        });

        var results = decoder.Decode(samples, 200, 3000, "000000");

        results.Should().ContainSingle(r => r.Message.Trim() == message.Trim(),
            $"signal at {freqHz} Hz starting at dt=0 should be decodable");
    }

    [Fact]
    public void DecodeResult_ToString_ContainsUtcTime()
    {
        var r = new DecodeResult
        {
            UtcTime = "143015",
            Snr = -12,
            Dt = 0.3,
            FrequencyHz = 1234,
            Message = "CQ W1AW FN31",
            Mode = DigitalMode.FT8,
        };
        r.ToString().Should().Contain("143015");
        r.ToString().Should().Contain("CQ W1AW FN31");
    }
}
