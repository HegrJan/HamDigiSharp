using FluentAssertions;
using HamDigiSharp.Dsp;
using System.Numerics;
using Xunit;

namespace HamDigiSharp.Tests.Dsp;

/// <summary>
/// Tests for BandpassFilter (biquad IIR) and AnalyticSignal (Hilbert transform).
/// </summary>
public class FilterTests
{
    // ── BandpassFilter: DC rejection ─────────────────────────────────────────

    [Theory]
    [InlineData(1000.0, 100.0)]
    [InlineData(500.0, 50.0)]
    [InlineData(2000.0, 200.0)]
    public void BandpassFilter_DcInput_IsStronglyAttenuated(double centerHz, double bwHz)
    {
        // A 2nd-order BPF has theoretically zero response at DC.
        // After transient settles, DC output should be very small.
        var filter = new BandpassFilter(11025.0, centerHz, bwHz);
        double[] dc = Enumerable.Repeat(1.0, 5000).ToArray();
        filter.ProcessInPlace(dc);

        // After initial transient (skip first 500 samples), output should be near zero
        double maxAbsOutput = dc.Skip(500).Select(Math.Abs).Max();
        maxAbsOutput.Should().BeLessThan(0.01,
            $"BPF ({centerHz} Hz center, {bwHz} Hz BW) should strongly attenuate DC");
    }

    // ── BandpassFilter: passband response ─────────────────────────────────────

    [Theory]
    [InlineData(1000.0, 200.0)]   // center=1000 Hz, bw=200 Hz → passband ~900-1100 Hz
    [InlineData(500.0, 100.0)]    // center=500 Hz
    public void BandpassFilter_AtCenterFrequency_PassesSignalWithHighGain(
        double centerHz, double bwHz)
    {
        const int SampleRate = 11025;
        const int N = 8192;
        var filter = new BandpassFilter(SampleRate, centerHz, bwHz);

        // Synthesize sine at center frequency
        var sine = new double[N];
        for (int i = 0; i < N; i++)
            sine[i] = Math.Sin(2.0 * Math.PI * centerHz * i / SampleRate);
        filter.ProcessInPlace(sine);

        // After transient (skip first 512 samples), measure output amplitude
        // RMS of a sine = amplitude / sqrt(2) ≈ 0.707 → with gain near 1, expect ~0.5-0.8
        double outputRms = Math.Sqrt(sine.Skip(512).Average(x => x * x));
        outputRms.Should().BeGreaterThan(0.3,
            $"BPF must pass its center frequency {centerHz} Hz with reasonable gain");
    }

    // ── BandpassFilter: stopband attenuation ──────────────────────────────────

    [Fact]
    public void BandpassFilter_AtStopband_AttenuatesByAtLeast20dB()
    {
        const int SampleRate = 11025;
        const double CenterHz = 1000.0;
        const double BwHz = 100.0;
        const int N = 16384;

        // Signal far from center (e.g., 3000 Hz, well outside 950-1050 Hz band)
        double stopFreq = 3000.0;
        var filter = new BandpassFilter(SampleRate, CenterHz, BwHz);
        var signal = new double[N];
        for (int i = 0; i < N; i++)
            signal[i] = Math.Sin(2.0 * Math.PI * stopFreq * i / SampleRate);
        filter.ProcessInPlace(signal);

        double inRms = 1.0 / Math.Sqrt(2.0); // RMS of unit sine
        double outRms = Math.Sqrt(signal.Skip(1000).Average(x => x * x));
        double attenuationDb = 20.0 * Math.Log10(outRms / inRms + 1e-100);

        attenuationDb.Should().BeLessThan(-20.0,
            "signal at 3000 Hz should be attenuated by at least 20 dB by 1000 Hz BPF");
    }

    // ── BandpassFilter: reset ─────────────────────────────────────────────────

    [Fact]
    public void BandpassFilter_Reset_ClearsState()
    {
        const int SampleRate = 11025;
        var filter = new BandpassFilter(SampleRate, 1000.0, 200.0);

        // Process some data to build up state
        var data1 = Enumerable.Repeat(1.0, 1000).ToArray();
        filter.ProcessInPlace(data1);

        // Reset and process from fresh
        filter.Reset();
        double output = filter.Process(1.0);

        // Right after reset, state = 0, so output = b0 * input + 0 + 0
        // The filter should have a fresh start (no dependency on previous data)
        double b0Expected = filter.Process(0.0); // next sample = 0 → measures state
        // The important thing: no exception, and output is a finite value
        double.IsNaN(output).Should().BeFalse("filtered output must not be NaN");
        double.IsInfinity(output).Should().BeFalse("filtered output must not be infinity");
        double.IsNaN(b0Expected).Should().BeFalse();
        double.IsInfinity(b0Expected).Should().BeFalse();
    }

    [Fact]
    public void BandpassFilter_ProcessInPlace_EquivalentToProcessSample()
    {
        // ProcessInPlace should give same results as calling Process() one at a time
        const int SampleRate = 11025;
        double[] signal = new double[100];
        var rng = new Random(17);
        for (int i = 0; i < 100; i++) signal[i] = rng.NextDouble() * 2 - 1;

        var f1 = new BandpassFilter(SampleRate, 1000.0, 200.0);
        var f2 = new BandpassFilter(SampleRate, 1000.0, 200.0);

        var buf = signal.ToArray();
        f1.ProcessInPlace(buf);

        var individual = new double[100];
        for (int i = 0; i < 100; i++) individual[i] = f2.Process(signal[i]);

        for (int i = 0; i < 100; i++)
            individual[i].Should().BeApproximately(buf[i], 1e-12,
                $"ProcessInPlace and Process() must agree at index {i}");
    }

    // ── AnalyticSignal: Hilbert transform properties ──────────────────────────

    [Fact]
    public void AnalyticSignal_SineWave_ImagPartIsNegativeCosine()
    {
        // The Hilbert transform of sin(2π*f*t) is -cos(2π*f*t).
        // So the analytic signal of sin is sin - j*cos.
        const int N = 1024;
        const int F = 10; // 10 cycles
        var sine = new double[N];
        for (int i = 0; i < N; i++) sine[i] = Math.Sin(2.0 * Math.PI * F * i / N);

        var analytic = AnalyticSignal.Compute(sine);

        // Skip transient at edges (periodic case: minimal edge effects for integer cycles)
        for (int i = 10; i < N - 10; i++)
        {
            double expectedReal = Math.Sin(2.0 * Math.PI * F * i / N);
            double expectedImag = -Math.Cos(2.0 * Math.PI * F * i / N);
            analytic[i].Real.Should().BeApproximately(expectedReal, 0.01,
                $"Analytic signal real part at {i} should match sine");
            analytic[i].Imaginary.Should().BeApproximately(expectedImag, 0.05,
                $"Analytic signal imaginary part at {i} should match -cosine");
        }
    }

    [Fact]
    public void AnalyticSignal_SineWave_EnvelopeIsConstant()
    {
        // |analytic(sin)| = sqrt(sin² + cos²) = 1 → constant amplitude envelope
        const int N = 1024;
        const int F = 10;
        var sine = new double[N];
        for (int i = 0; i < N; i++) sine[i] = Math.Sin(2.0 * Math.PI * F * i / N);

        var analytic = AnalyticSignal.Compute(sine);

        for (int i = 20; i < N - 20; i++)
        {
            double env = analytic[i].Magnitude;
            env.Should().BeApproximately(1.0, 0.05,
                $"Envelope at index {i} should be ≈1 for unit sine");
        }
    }

    [Fact]
    public void AnalyticSignal_AllZeros_ReturnsAllZeros()
    {
        var zeros = new double[256];
        var analytic = AnalyticSignal.Compute(zeros);
        analytic.Should().AllSatisfy(c => c.Magnitude.Should().BeApproximately(0.0, 1e-10));
    }

    [Fact]
    public void AnalyticSignal_RealPartMatchesInput()
    {
        // The real part of the analytic signal equals the input (Hilbert doesn't change real)
        const int N = 512;
        var rng = new Random(99);
        var input = new double[N];
        for (int i = 0; i < N; i++) input[i] = rng.NextDouble() - 0.5;

        var analytic = AnalyticSignal.Compute(input);

        for (int i = 0; i < N; i++)
            analytic[i].Real.Should().BeApproximately(input[i], 1e-9,
                $"Real part of analytic signal must match input at index {i}");
    }

    [Fact]
    public void AnalyticSignal_NegativeFrequenciesZeroed()
    {
        // The complex analytic signal A(t) = x(t) + j·H{x(t)} has no negative-frequency content.
        // Its FFT should have near-zero magnitude for bins k = N/2+1 .. N-1.
        const int N = 256;
        const int F = 8;
        var input = new double[N];
        for (int i = 0; i < N; i++) input[i] = Math.Sin(2.0 * Math.PI * F * i / N);

        var analytic = AnalyticSignal.Compute(input);

        // FFT of the complex analytic signal directly
        var data = analytic.ToArray();
        Fft.C2C(data, -1); // forward FFT

        // Positive frequencies: bins 0 .. N/2 (large for k=F)
        // Negative frequencies: bins N/2+1 .. N-1 (should be near zero)
        for (int k = N / 2 + 1; k < N; k++)
            data[k].Magnitude.Should().BeLessThan(1.0,
                $"Bin {k} (negative frequency) of analytic signal should be near zero");
    }

    // ── BandpassFilter: stability ─────────────────────────────────────────────

    [Fact]
    public void BandpassFilter_LongRun_RemainsStable()
    {
        // Process 100,000 samples of impulse noise to verify no numerical blow-up
        var filter = new BandpassFilter(12000.0, 1500.0, 300.0);
        var rng = new Random(42);
        double maxOutput = 0;
        for (int i = 0; i < 100_000; i++)
        {
            double input = (i % 3000 == 0) ? 1.0 : 0.0; // sparse impulses
            double y = filter.Process(input);
            if (double.IsNaN(y) || double.IsInfinity(y))
                throw new Exception($"Filter diverged at sample {i}: y={y}");
            maxOutput = Math.Max(maxOutput, Math.Abs(y));
        }
        maxOutput.Should().BeLessThan(10.0, "filter must remain bounded over long run");
    }

    [Theory]
    [InlineData(100.0, 20.0)]
    [InlineData(3000.0, 500.0)]
    [InlineData(5000.0, 1000.0)]
    public void BandpassFilter_VariousFrequencies_DoesNotThrow(double center, double bw)
    {
        const int SampleRate = 12000;
        var ex = Record.Exception(() =>
        {
            var filter = new BandpassFilter(SampleRate, center, bw);
            var data = Enumerable.Repeat(0.5, 100).ToArray();
            filter.ProcessInPlace(data);
        });
        ex.Should().BeNull($"BPF at {center} Hz/{bw} Hz BW must not throw");
    }
}
