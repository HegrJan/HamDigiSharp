using FluentAssertions;
using HamDigiSharp.Decoders.Ft2;
using HamDigiSharp.Decoders.Ft4;
using HamDigiSharp.Decoders.Ft8;
using HamDigiSharp.Decoders.Jtms;
using HamDigiSharp.Decoders.Msk;
using HamDigiSharp.Encoders;
using HamDigiSharp.Models;
using Xunit;

namespace HamDigiSharp.Tests.Decoders;

/// <summary>
/// End-to-end round-trip tests: encode a known message, feed the PCM to the matching
/// decoder, and assert the original message is recovered.
///
/// These tests act as regression guards for codec correctness — if the encoder or
/// decoder changes in an incompatible way (e.g. Rvec applied on one side but not the
/// other) the round-trip will fail.
/// </summary>
public class RoundTripTests
{
    private const double TxFreqHz   = 1000.0;
    private const double FreqLo     =  850.0;
    private const double FreqHi     = 1200.0;
    private const string TestMsg    = "CQ W1AW FN42";
    private const string TestMsg2   = "W1AW K9AN -07";

    // ── FT8 ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TestMsg)]
    [InlineData(TestMsg2)]
    public void Ft8_RoundTrip_DecodesOriginalMessage(string message)
    {
        var encoded = new Ft8Encoder().Encode(message, new EncoderOptions { FrequencyHz = TxFreqHz });

        var decoder = new Ft8Decoder();
        var results = decoder.Decode(encoded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty("FT8 encoded signal must be decodable");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void Ft8_RoundTrip_ResultHasCorrectMode()
    {
        var encoded = new Ft8Encoder().Encode(TestMsg, new EncoderOptions { FrequencyHz = TxFreqHz });
        var results = new Ft8Decoder().Decode(encoded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty();
        results.First(r => r.Message.Trim() == TestMsg).Mode.Should().Be(DigitalMode.FT8);
    }

    [Fact]
    public void Ft8_RoundTrip_FrequencyCloseToEncoded()
    {
        var encoded = new Ft8Encoder().Encode(TestMsg, new EncoderOptions { FrequencyHz = TxFreqHz });
        var results = new Ft8Decoder().Decode(encoded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty();
        var match = results.First(r => r.Message.Trim() == TestMsg);
        match.FrequencyHz.Should().BeApproximately(TxFreqHz, precision: 50,
            because: "reported frequency must be within 50 Hz of the encoded TX frequency");
    }

    // ── FT4 ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TestMsg)]
    [InlineData(TestMsg2)]
    public void Ft4_RoundTrip_DecodesOriginalMessage(string message)
    {
        var encoded = new Ft4Encoder().Encode(message, new EncoderOptions { FrequencyHz = TxFreqHz });

        var decoder = new Ft4Decoder();
        var results = decoder.Decode(encoded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty("FT4 encoded signal must be decodable");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void Ft4_RoundTrip_ResultHasCorrectMode()
    {
        var encoded = new Ft4Encoder().Encode(TestMsg, new EncoderOptions { FrequencyHz = TxFreqHz });
        var results = new Ft4Decoder().Decode(encoded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty();
        results.First(r => r.Message.Trim() == TestMsg).Mode.Should().Be(DigitalMode.FT4);
    }

    [Fact]
    public void Ft4_RoundTrip_FrequencyCloseToEncoded()
    {
        var encoded = new Ft4Encoder().Encode(TestMsg, new EncoderOptions { FrequencyHz = TxFreqHz });
        var results = new Ft4Decoder().Decode(encoded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty();
        var match = results.First(r => r.Message.Trim() == TestMsg);
        match.FrequencyHz.Should().BeApproximately(TxFreqHz, precision: 50,
            because: "reported frequency must be within 50 Hz of the encoded TX frequency");
    }

    // ── FT2 ──────────────────────────────────────────────────────────────────
    // These tests specifically verify the Rvec XOR de-scramble in the decoder.
    // If Rvec is not applied (or applied on the wrong side) the LDPC output is
    // scrambled and Unpack77 will produce garbage — causing the test to fail.

    [Theory]
    [InlineData(TestMsg)]
    [InlineData(TestMsg2)]
    public void Ft2_RoundTrip_DecodesOriginalMessage(string message)
    {
        var encoded = new Ft2Encoder().Encode(message, new EncoderOptions { FrequencyHz = TxFreqHz });

        // Disable coherent averaging so a single frame is sufficient.
        var decoder = new Ft2Decoder();
        decoder.Configure(new DecoderOptions { AveragingEnabled = false });

        var results = decoder.Decode(encoded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty("FT2 encoded signal must be decodable");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void Ft2_RoundTrip_ResultHasCorrectMode()
    {
        var encoded = new Ft2Encoder().Encode(TestMsg, new EncoderOptions { FrequencyHz = TxFreqHz });
        var decoder = new Ft2Decoder();
        decoder.Configure(new DecoderOptions { AveragingEnabled = false });

        var results = decoder.Decode(encoded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty();
        results.First(r => r.Message.Trim() == TestMsg).Mode.Should().Be(DigitalMode.FT2);
    }

    [Fact]
    public void Ft2_RoundTrip_FrequencyCloseToEncoded()
    {
        var encoded = new Ft2Encoder().Encode(TestMsg, new EncoderOptions { FrequencyHz = TxFreqHz });
        var decoder = new Ft2Decoder();
        decoder.Configure(new DecoderOptions { AveragingEnabled = false });

        var results = decoder.Decode(encoded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty();
        var match = results.First(r => r.Message.Trim() == TestMsg);
        match.FrequencyHz.Should().BeApproximately(TxFreqHz, precision: 100,
            because: "reported frequency must be within 100 Hz of the encoded TX frequency (FT2 tone spacing = 41.67 Hz)");
    }

    /// <summary>
    /// Regression guard: if Rvec is NOT applied by the decoder, LDPC will hand back
    /// scrambled bits and Unpack77 will produce a different (garbage) message.
    /// This test fails with the pre-fix Ft2Decoder and passes after the fix.
    /// </summary>
    [Fact]
    public void Ft2_RoundTrip_RvecSymmetry_EncoderAndDecoderMustAgree()
    {
        // Encode two different messages; their decoded results must match exactly.
        var enc = new Ft2Encoder();
        var dec = new Ft2Decoder();
        dec.Configure(new DecoderOptions { AveragingEnabled = false });

        foreach (var msg in new[] { TestMsg, TestMsg2, "CQ LZ2HV KN22" })
        {
            var encoded = enc.Encode(msg, new EncoderOptions { FrequencyHz = TxFreqHz });
            var results = dec.Decode(encoded, FreqLo, FreqHi, "000000");
            dec.Configure(new DecoderOptions { AveragingEnabled = false }); // reset between iterations

            results.Should().NotBeEmpty($"FT2 must decode '{msg}'");
            results.Any(r => r.Message.Trim() == msg).Should().BeTrue(
                $"Rvec symmetry broken: encoder wrote '{msg}' but decoder recovered " +
                $"[{string.Join(", ", results.Select(r => r.Message))}]");
        }
    }

    /// <summary>
    /// FT2 uses a 3.75-second window (NMax = 45000 samples @ 12 kHz).
    /// Two stations alternate in 7.5-second TX/RX cycles.
    /// Regression guard: the old (wrong) value was 30 seconds.
    /// </summary>
    [Fact]
    public void Ft2_Period_Is_3_75_Seconds()
    {
        DigitalMode.FT2.PeriodSeconds().Should().Be(3.75,
            "FT2 window = NMax/SampleRate = 45000/12000 = 3.75 s; two stations alternate in 7.5 s cycles");
    }

    // ── Timing-shift robustness ──────────────────────────────────────────────
    //
    // A station may transmit slightly late relative to the UTC period boundary;
    // propagation delays also mean the received signal may not start at t=0.
    // These tests verify that the Costas-pilot full-range timing search recovers
    // the message even when the signal begins several hundred milliseconds into
    // the buffer.

    /// <summary>
    /// FT4 decoder must find and decode a signal that starts 500 ms into the buffer
    /// (the remote station transmitted 500 ms late or propagation delayed it).
    /// FT4 buffer = 6.048 s; signal = 4.93 s; 500 ms pad still fits with margin.
    /// </summary>
    [Theory]
    [InlineData(500,  TestMsg)]
    [InlineData(1000, TestMsg2)]
    public void Ft4_TimingShift_DecodesSignalStartingLate(int latencyMs, string message)
    {
        int padSamples = latencyMs * 12000 / 1000;
        var signal  = new Ft4Encoder().Encode(message, new EncoderOptions { FrequencyHz = TxFreqHz });
        var buf     = new float[padSamples + signal.Length];
        Array.Copy(signal, 0, buf, padSamples, signal.Length);

        var results = new Ft4Decoder().Decode(buf, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty($"FT4 must decode a signal starting {latencyMs} ms late");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}' (with {latencyMs} ms head padding); got: " +
            $"[{string.Join(", ", results.Select(r => r.Message))}]");
    }

    /// <summary>
    /// FT4 decoded Dt should be approximately equal to the pad duration.
    /// This verifies the timing search returns the correct offset, not just that
    /// decoding succeeds.
    /// </summary>
    [Fact]
    public void Ft4_TimingShift_DtReflectsActualStartOffset()
    {
        const int latencyMs = 750;
        int padSamples = latencyMs * 12000 / 1000;
        var signal = new Ft4Encoder().Encode(TestMsg, new EncoderOptions { FrequencyHz = TxFreqHz });
        var buf    = new float[padSamples + signal.Length];
        Array.Copy(signal, 0, buf, padSamples, signal.Length);

        var results = new Ft4Decoder().Decode(buf, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty();
        var match = results.First(r => r.Message.Trim() == TestMsg);
        match.Dt.Should().BeApproximately(latencyMs / 1000.0, precision: 0.1,
            because: $"Dt should reflect the {latencyMs} ms start offset (±100 ms)");
    }

    /// <summary>
    /// FT2 buffer = 3.75 s; signal = 2.47 s; 600 ms pad still fits.
    /// </summary>
    [Theory]
    [InlineData(300, TestMsg)]
    [InlineData(600, TestMsg2)]
    public void Ft2_TimingShift_DecodesSignalStartingLate(int latencyMs, string message)
    {
        int padSamples = latencyMs * 12000 / 1000;
        var signal = new Ft2Encoder().Encode(message, new EncoderOptions { FrequencyHz = TxFreqHz });
        var buf    = new float[padSamples + signal.Length];
        Array.Copy(signal, 0, buf, padSamples, signal.Length);

        var decoder = new Ft2Decoder();
        decoder.Configure(new DecoderOptions { AveragingEnabled = false });
        var results = decoder.Decode(buf, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty($"FT2 must decode a signal starting {latencyMs} ms late");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}' (with {latencyMs} ms head padding); got: " +
            $"[{string.Join(", ", results.Select(r => r.Message))}]");
    }

    /// <summary>
    /// FT2 single-period decode must complete well within the 3.75-second period
    /// budget.  We allow 2 seconds as the ceiling — leaving at least 1.75 s to
    /// process the decodes and begin the reply TX.
    /// </summary>
    [Fact]
    public void Ft2_Decode_CompletesWithinPeriodBudget()
    {
        var signal = new Ft2Encoder().Encode(TestMsg, new EncoderOptions { FrequencyHz = TxFreqHz });

        var decoder = new Ft2Decoder();
        decoder.Configure(new DecoderOptions { AveragingEnabled = false });

        // Warm-up (JIT, thread-pool spin-up)
        decoder.Decode(signal, FreqLo, FreqHi, "000000");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = decoder.Decode(signal, FreqLo, FreqHi, "000000");
        sw.Stop();

        results.Should().NotBeEmpty("FT2 must decode the test signal");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.0),
            "FT2 decode must complete in <2 s to leave reaction time before next period (3.75 s)");
    }

    // ── MSK144 ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ W1AW")]
    [InlineData("W1AW K9AN -10")]
    public void Msk144_RoundTrip_DecodesOriginalMessage(string message)
    {
        // MSK144 encoder produces an 864-sample burst (72 ms at 12 kHz).
        // Pad with 432 silence samples before and after so the decoder's timing
        // search has margin to find the burst start.
        const int pad = 432;
        var burst  = new Msk144Encoder().Encode(message, new EncoderOptions { FrequencyHz = TxFreqHz });
        var padded = new float[pad + burst.Length + pad];
        Array.Copy(burst, 0, padded, pad, burst.Length);

        var decoder = new Msk144Decoder();
        var results = decoder.Decode(padded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty("MSK144 encoded burst must be decodable");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void Msk144_RoundTrip_ResultHasCorrectMode()
    {
        const string message = "CQ W1AW";
        const int pad = 432;
        var burst  = new Msk144Encoder().Encode(message, new EncoderOptions { FrequencyHz = TxFreqHz });
        var padded = new float[pad + burst.Length + pad];
        Array.Copy(burst, 0, padded, pad, burst.Length);

        var results = new Msk144Decoder().Decode(padded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty();
        results.First(r => r.Message.Trim() == message).Mode.Should().Be(DigitalMode.MSK144);
    }

    // ── Time-offset regression tests ─────────────────────────────────────────
    // These tests would have caught the FT4 timing-search bug (±96 ms window)
    // that made real recordings fail: signals starting at t>0.1 s were not found.
    // FT4 period is 7.5 s; usable recording window is ~5.04 s of signal + silence.
    // At 12 kHz: nMax=72576. Signal is 103×576=59328 samples. Max safe dt≈0.8 s.

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void Ft4_RoundTrip_DecodesSignalAtTimeOffset(double dtSeconds)
    {
        const string message = "CQ W1AW FN42";
        var signal   = new Ft4Encoder().Encode(message, new EncoderOptions { FrequencyHz = TxFreqHz });
        int silenceSamples = (int)(dtSeconds * 12000);

        // Pad: silence before signal, zero-pad end to fill nMax=72576
        const int nMax = 72576;
        var buffer = new float[nMax];
        int copyLen = Math.Min(signal.Length, nMax - silenceSamples);
        Array.Copy(signal, 0, buffer, silenceSamples, copyLen);

        var decoder = new Ft4Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Normal });
        var results = decoder.Decode(buffer, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty($"FT4 must decode '{message}' at dt={dtSeconds:F2}s");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"FT4: expected '{message}' at dt={dtSeconds:F2}s offset");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Ft8_RoundTrip_DecodesSignalAtTimeOffset(double dtSeconds)
    {
        const string message = "CQ W1AW FN42";
        var signal   = new Ft8Encoder().Encode(message, new EncoderOptions { FrequencyHz = TxFreqHz });
        int silenceSamples = (int)(dtSeconds * 12000);

        // FT8 nMax = 15 s × 12000 Hz = 180 000 samples
        const int nMax = 180000;
        var buffer = new float[nMax];
        int copyLen = Math.Min(signal.Length, nMax - silenceSamples);
        Array.Copy(signal, 0, buffer, silenceSamples, copyLen);

        var decoder = new Ft8Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Normal });
        var results = decoder.Decode(buffer, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty($"FT8 must decode '{message}' at dt={dtSeconds:F2}s");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"FT8: expected '{message}' at dt={dtSeconds:F2}s offset");
    }

    // ── MSK40 (MSKMS) ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ W1AW FN42")]
    [InlineData("W1AW K9AN -07")]
    [InlineData("K9AN W1AW RR73")]
    public void Msk40_RoundTrip_DecodesOriginalMessage(string message)
    {
        const int pad = 432;
        var burst  = new Msk40Encoder().Encode(message, new EncoderOptions { FrequencyHz = TxFreqHz });
        var padded = new float[pad + burst.Length + pad];
        Array.Copy(burst, 0, padded, pad, burst.Length);

        var results = new Msk40Decoder().Decode(padded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty("MSKMS encoded burst must be decodable");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"Expected '{message}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void Msk40_RoundTrip_ResultHasCorrectMode()
    {
        const string message = "CQ W1AW";
        const int pad = 432;
        var burst  = new Msk40Encoder().Encode(message, new EncoderOptions { FrequencyHz = TxFreqHz });
        var padded = new float[pad + burst.Length + pad];
        Array.Copy(burst, 0, padded, pad, burst.Length);

        var results = new Msk40Decoder().Decode(padded, FreqLo, FreqHi, "000000");

        results.Should().NotBeEmpty();
        results.First(r => r.Message.Trim() == message).Mode.Should().Be(DigitalMode.MSKMS);
    }

    // ── JTMS ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CQ W1AW FN42")]
    [InlineData("K9AN W1AW")]
    public void Jtms_RoundTrip_DecodesOriginalMessage(string message)
    {
        // JTMS encoder fills the full 15 s period; decoder searches within
        var samples = new JtmsEncoder().Encode(message, new EncoderOptions { FrequencyHz = 1000.0 });

        var results = new JtmsDecoder().Decode(samples, 800, 2000, "000000");

        results.Should().NotBeEmpty("JTMS encoded signal must be decodable");
        results.Any(r => r.Message.Contains(message)).Should().BeTrue(
            $"Expected message to contain '{message}'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void Jtms_RoundTrip_ResultHasCorrectMode()
    {
        const string message = "CQ W1AW";
        var samples = new JtmsEncoder().Encode(message, new EncoderOptions { FrequencyHz = 1000.0 });

        var results = new JtmsDecoder().Decode(samples, 800, 2000, "000000");

        results.Should().NotBeEmpty();
        results.First().Mode.Should().Be(DigitalMode.JTMS);
    }

    // ── Low-SNR sensitivity regression guards ────────────────────────────────
    // These tests specifically guard against regressions introduced by algorithmic
    // changes (e.g. FastTanh approximation, ArrayPool stale-data, rotation bugs).
    // They mirror the existing FT8 −10 dB test from Ft8DecoderTests.cs.
    //
    // Noise model: additive white Gaussian noise (Box-Muller) at signal amplitude 0.1
    // and noise σ=0.316 → broad SNR ≈ −10 dB in 2500 Hz bandwidth.

    private static float[] AddGaussianNoise(float[] signal, double amplitude, double noiseSd, int seed)
    {
        var rng     = new Random(seed);
        var samples = new float[signal.Length];
        signal.AsSpan().CopyTo(samples.AsSpan());
        for (int i = 0; i < samples.Length; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            double n  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            samples[i] = (float)(samples[i] * amplitude + n * noiseSd);
        }
        return samples;
    }

    [Fact]
    public void Ft4_LowSnr_Minus10dB_DecodesCorrectMessage()
    {
        const string message = "CQ W1AW FN42";
        var clean   = new Ft4Encoder().Encode(message, new EncoderOptions { FrequencyHz = TxFreqHz });
        var samples = AddGaussianNoise(clean, amplitude: 1.0, noiseSd: 0.316, seed: 42);

        var buf = new float[72576]; // FT4 nMax
        samples.AsSpan(0, Math.Min(samples.Length, buf.Length)).CopyTo(buf);

        var decoder = new Ft4Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Normal });
        var results = decoder.Decode(buf, FreqLo, FreqHi, "000000");

        results.Should().Contain(r => r.Message.Trim() == message,
            "FT4 must decode a −10 dB SNR signal (guards against FastTanh/pool regressions)");
    }

    // ── OK1ICQ/OK1TE/JN89 regression (MaxLength fix) ─────────────────────────
    // Before the MaxLength=13→22 fix the UI blocked entry of this 17-char message.
    // These tests verify that the full encode→decode round-trip works for it.

    [Theory]
    [InlineData("FT8")]
    [InlineData("FT4")]
    [InlineData("FT2")]
    public void FtX_RoundTrip_Ok1IcqOk1TeJn89_Decodes(string modeName)
    {
        const string message = "OK1ICQ OK1TE JN89";
        const double freq = 1000.0;

        float[] encoded = modeName switch
        {
            "FT8" => new Ft8Encoder().Encode(message, new EncoderOptions { FrequencyHz = freq }),
            "FT4" => new Ft4Encoder().Encode(message, new EncoderOptions { FrequencyHz = freq }),
            "FT2" => new Ft2Encoder().Encode(message, new EncoderOptions { FrequencyHz = freq }),
            _     => throw new ArgumentException(modeName),
        };

        IReadOnlyList<DecodeResult> results;
        switch (modeName)
        {
            case "FT8":
                results = new Ft8Decoder().Decode(encoded, 850, 1200, "000000");
                break;
            case "FT4":
                results = new Ft4Decoder().Decode(encoded, 850, 1200, "000000");
                break;
            case "FT2":
            {
                var dec = new Ft2Decoder();
                dec.Configure(new DecoderOptions { AveragingEnabled = false });
                results = dec.Decode(encoded, 850, 1200, "000000");
                break;
            }
            default: throw new ArgumentException(modeName);
        }

        results.Should().NotBeEmpty($"{modeName} must encode and decode '{message}'");
        results.Any(r => r.Message.Trim() == message).Should().BeTrue(
            $"{modeName}: expected '{message}'; got [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    // ── JTMS noise-rejection guard ───────────────────────────────────────────
    // Before the power-threshold fix the decoder emitted dozens of false decodes
    // for every buffer of Gaussian noise.  After the fix pure noise returns ≤1
    // result (empirically 0 for seed=42 and seed=99).

    [Theory]
    [InlineData(42)]
    [InlineData(99)]
    public void Jtms_PureNoise_ProducesNoFalseDecodes(int seed)
    {
        // 15-second buffer of Gaussian noise at 11025 Hz
        const int n = 11025 * 15;
        var rng     = new Random(seed);
        var noise   = new float[n];
        for (int i = 0; i < n; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            noise[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2) * 0.1);
        }

        var results = new JtmsDecoder().Decode(noise, 800, 2000, "000000");

        results.Should().BeEmpty(
            $"JTMS decoder must not emit false decodes from pure noise (seed={seed}); " +
            $"got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }
}

