using FluentAssertions;
using HamDigiSharp.Dsp;
using Xunit;

namespace HamDigiSharp.Tests.Dsp;

/// <summary>
/// Tests for the Resampler class (polyphase linear interpolation).
/// The resampler is critical infrastructure: every RealTimeDecoder call that accepts
/// a non-native sample rate passes audio through it.
/// </summary>
public class ResamplerTests
{
    // ── Identity (in_rate == out_rate) ───────────────────────────────────────

    [Fact]
    public void Process_SameRate_ReturnsCopyOfInput()
    {
        var r = new Resampler(12000, 12000);
        float[] input = { 0.1f, 0.3f, -0.2f, 0.9f, -0.8f };
        float[] output = r.Process(input.AsSpan());

        output.Should().HaveCount(input.Length, "identity resampling must preserve length");
        for (int i = 0; i < input.Length; i++)
            output[i].Should().BeApproximately(input[i], 1e-6f,
                $"identity resampling must preserve sample[{i}]");
    }

    [Fact]
    public void ProcessInto_SameRate_CopiesExact()
    {
        var r = new Resampler(12000, 12000);
        float[] input  = { 1f, 2f, 3f, 4f, 5f };
        float[] output = new float[input.Length];
        r.ProcessInto(input.AsSpan(), output.AsSpan());
        output.Should().BeEquivalentTo(input, "identity pass-through must copy exactly");
    }

    // ── Output length correctness ────────────────────────────────────────────

    [Theory]
    [InlineData(48000, 12000, 4800,  1200)]  // 4:1 downsample
    [InlineData(11025, 12000, 11025, 12000)] // 11025→12000 (used by JT65/ISCAT decoders)
    [InlineData(6000,  12000, 6000,  12000)] // 1:2 upsample
    [InlineData(44100, 12000, 4410,  1200)]  // CD→12k
    public void Process_OutputLength_MatchesCeilingFormula(
        int inRate, int outRate, int inLen, int expectedOutLen)
    {
        var r = new Resampler(inRate, outRate);
        float[] input = new float[inLen]; // all-zero, length is what matters
        float[] output = r.Process(input.AsSpan());
        // Allow ±1 sample for rounding
        output.Length.Should().BeCloseTo(expectedOutLen, 1,
            $"resampling {inLen} samples at {inRate}→{outRate} Hz");
    }

    // ── DC preservation ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(48000, 12000)]
    [InlineData(11025, 12000)]
    public void Process_ConstantInput_PreservesAmplitudeAfterTransient(int inRate, int outRate)
    {
        var r = new Resampler(inRate, outRate);
        // 0.5-second block of constant amplitude 0.8
        int inLen = inRate / 2;
        float[] input = Enumerable.Repeat(0.8f, inLen).ToArray();
        float[] output = r.Process(input.AsSpan());

        // Skip the initial transient (first ~5% of output) and check steady state
        int skipSamples = output.Length / 10;
        for (int i = skipSamples; i < output.Length; i++)
            output[i].Should().BeApproximately(0.8f, 0.05f,
                $"steady-state DC at output sample {i} should be preserved");
    }

    // ── Sinusoid frequency preservation ──────────────────────────────────────

    [Theory]
    [InlineData(48000, 12000, 200.0)] // 200 Hz sine: well below Nyquist of 6 kHz
    [InlineData(44100, 12000, 440.0)] // A440
    public void Process_LowFreqSine_PreservesFrequency(int inRate, int outRate, double freqHz)
    {
        // Generate a long sine at freqHz in the input rate
        int inLen = inRate; // 1 second
        float[] input = new float[inLen];
        for (int i = 0; i < inLen; i++)
            input[i] = (float)Math.Sin(2.0 * Math.PI * freqHz * i / inRate);

        var r = new Resampler(inRate, outRate);
        float[] output = r.Process(input.AsSpan());

        // The output should still be a sine at freqHz; verify by finding its period.
        // Expected period in output samples: outRate / freqHz
        double expectedPeriodSamples = outRate / freqHz;

        // Find two consecutive zero-crossings to measure period
        // (skip the transient, use the middle of the output)
        int start = output.Length / 4;
        int? firstCross = null;
        int? secondCross = null;
        for (int i = start; i < output.Length - 1; i++)
        {
            if (output[i] < 0 && output[i + 1] >= 0)
            {
                if (firstCross == null) firstCross = i;
                else { secondCross = i; break; }
            }
        }

        firstCross.Should().NotBeNull("output sine must have at least one zero-crossing");
        secondCross.Should().NotBeNull("output sine must have at least two zero-crossings");

        double measuredPeriod = secondCross!.Value - firstCross!.Value;
        measuredPeriod.Should().BeApproximately(expectedPeriodSamples, expectedPeriodSamples * 0.05,
            $"output sine period should match {freqHz} Hz at {outRate} Hz sample rate");
    }

    // ── No NaN / infinity ────────────────────────────────────────────────────

    [Theory]
    [InlineData(48000, 12000)]
    [InlineData(11025, 12000)]
    [InlineData(12000, 11025)]
    public void Process_RandomInput_ProducesFiniteOutput(int inRate, int outRate)
    {
        var rng = new Random(42);
        var r = new Resampler(inRate, outRate);
        float[] input = new float[inRate / 2]; // 0.5 s
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)(rng.NextDouble() * 2 - 1);

        float[] output = r.Process(input.AsSpan());

        output.Should().NotBeEmpty();
        foreach (float s in output)
        {
            float.IsNaN(s).Should().BeFalse("resampled sample must not be NaN");
            float.IsInfinity(s).Should().BeFalse("resampled sample must not be infinite");
        }
    }

    // ── ProcessInto vs Process give same result ──────────────────────────────

    [Fact]
    public void ProcessInto_MatchesProcess_ForDownsample()
    {
        const int InRate = 48000, OutRate = 12000;
        var rng = new Random(99);
        float[] input = new float[4800];
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)(rng.NextDouble() * 2 - 1);

        // Use two fresh instances (same state from construction)
        var r1 = new Resampler(InRate, OutRate);
        var r2 = new Resampler(InRate, OutRate);

        float[] fromProcess = r1.Process(input.AsSpan());

        float[] fromInto = new float[fromProcess.Length];
        r2.ProcessInto(input.AsSpan(), fromInto.AsSpan());

        for (int i = 0; i < fromProcess.Length; i++)
            fromInto[i].Should().BeApproximately(fromProcess[i], 1e-5f,
                $"ProcessInto and Process must agree at output[{i}]");
    }

    // ── Rate properties ─────────────────────────────────────────────────────

    [Fact]
    public void Properties_ExposedRates_MatchConstruction()
    {
        var r = new Resampler(44100, 12000);
        r.InputRate.Should().Be(44100);
        r.OutputRate.Should().Be(12000);
    }

    // ── Double overload ─────────────────────────────────────────────────────

    [Fact]
    public void Process_DoubleOverload_SameRate_ReturnsCopy()
    {
        var r = new Resampler(12000, 12000);
        double[] input = { 0.1, 0.5, -0.3, 0.8, -0.7 };
        double[] output = r.Process(input.AsSpan());
        output.Should().HaveCount(input.Length);
        for (int i = 0; i < input.Length; i++)
            output[i].Should().BeApproximately(input[i], 1e-12);
    }

    [Fact]
    public void Process_DoubleOverload_Downsample_LengthCorrect()
    {
        var r = new Resampler(48000, 12000);
        double[] input = new double[4800];
        double[] output = r.Process(input.AsSpan());
        output.Length.Should().BeCloseTo(1200, 1);
    }
}
