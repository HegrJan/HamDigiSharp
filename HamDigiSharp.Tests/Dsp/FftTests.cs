using FluentAssertions;
using HamDigiSharp.Dsp;
using System.Numerics;
using Xunit;

namespace HamDigiSharp.Tests.Dsp;

/// <summary>
/// Mathematical property tests for the FFT wrapper.
///
/// Sign convention: isign = -1 → forward DFT, +1 → inverse (scaled 1/N).
/// MathNet AsymmetricScaling means Forward is un-scaled, Inverse is 1/N scaled.
///
/// Parseval's theorem (with AsymmetricScaling):
///   ∑|x[n]|² = (1/N) ∑|X[k]|²
///   i.e., the forward transform multiplies energy by N.
/// </summary>
public class FftTests
{
    private const double Tol = 1e-9;
    private const double LooseTol = 1e-4;

    // ── Length contracts ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void R2C_ReturnsHalfPlusOne(int n)
    {
        var real = new double[n];
        var result = Fft.R2C(real.AsSpan());
        result.Should().HaveCount(n / 2 + 1,
            $"R2C of length-{n} signal returns {n}/2+1 = {n / 2 + 1} complex bins");
    }

    [Theory]
    [InlineData(64)]
    [InlineData(512)]
    public void R2CFull_ReturnsSameLength(int n)
    {
        var real = new double[n];
        var result = Fft.R2CFull(real.AsSpan());
        result.Should().HaveCount(n, "R2CFull returns N complex values");
    }

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    public void PowerSpectrum_ReturnsHalfPlusOne(int n)
    {
        var real = new double[n];
        var ps = Fft.PowerSpectrum(real.AsSpan());
        ps.Should().HaveCount(n / 2 + 1);
    }

    // ── DC input ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    public void PowerSpectrum_DcInput_AllEnergyInBin0(int n)
    {
        // DC signal (constant = 1.0) → all energy in bin 0
        var real = Enumerable.Repeat(1.0, n).ToArray();
        var ps = Fft.PowerSpectrum(real);

        double bin0 = ps[0];
        double maxOther = ps.Skip(1).Max();
        bin0.Should().BeGreaterThan(maxOther * 100,
            "DC signal must concentrate energy at bin 0");
    }

    [Fact]
    public void R2C_AllZeros_ReturnsAllZeroSpectrum()
    {
        var real = new double[256];
        var result = Fft.R2C(real);
        result.Should().AllSatisfy(c =>
            c.Magnitude.Should().BeApproximately(0.0, Tol,
                "FFT of all-zero input must be all-zero"));
    }

    // ── Sine wave peak ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1024, 10)]   // 10 Hz sine at 1024 Hz sample rate
    [InlineData(512, 5)]     // 5 Hz sine at 512 Hz sample rate
    [InlineData(256, 3)]     // 3 Hz sine at 256 Hz sample rate
    public void PowerSpectrum_SineWaveAtIntegerBin_PeaksAtCorrectBin(int n, int k)
    {
        // Synthesize sin(2π*k*n/N): exactly k cycles in N samples → energy in bin k
        var real = new double[n];
        for (int i = 0; i < n; i++)
            real[i] = Math.Sin(2.0 * Math.PI * k * i / n);

        var ps = Fft.PowerSpectrum(real);
        int peak = Array.IndexOf(ps, ps.Max());

        peak.Should().Be(k, $"sine at bin {k} must peak at bin {k}");
    }

    [Theory]
    [InlineData(1024, 10, 30)]   // peak at bin 10, must be >> bin 30
    [InlineData(512, 7, 20)]
    public void PowerSpectrum_SineWave_PeakDominatesOtherBins(int n, int k, int otherBin)
    {
        var real = new double[n];
        for (int i = 0; i < n; i++)
            real[i] = Math.Sin(2.0 * Math.PI * k * i / n);

        var ps = Fft.PowerSpectrum(real);

        ps[k].Should().BeGreaterThan(ps[otherBin] * 100,
            $"power at correct bin {k} must dominate bin {otherBin}");
    }

    // ── Parseval's theorem ────────────────────────────────────────────────────

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(1024)]
    public void PowerSpectrum_ParsevalsTheorem_EnergyConserved(int n)
    {
        // Generate a random real signal
        var rng = new Random(42);
        var real = new double[n];
        for (int i = 0; i < n; i++) real[i] = rng.NextDouble() * 2 - 1;

        // Time-domain energy
        double timeEnergy = real.Sum(x => x * x);

        // Frequency-domain energy using full spectrum
        var full = Fft.R2CFull(real);
        // For real signals: bin 0 and N/2 appear once; others appear twice
        double freqEnergy = full[0].MagnitudeSquared() / n;
        freqEnergy += full[n / 2].MagnitudeSquared() / n;
        for (int k = 1; k < n / 2; k++)
            freqEnergy += 2.0 * full[k].MagnitudeSquared() / n;

        freqEnergy.Should().BeApproximately(timeEnergy, timeEnergy * 0.001,
            "Parseval's theorem: ∑|x|² = (1/N)∑|X|² (compensating for AsymmetricScaling)");
    }

    // ── Linearity ─────────────────────────────────────────────────────────────

    [Fact]
    public void R2CFull_Linearity_ScaledInput_ScalesOutput()
    {
        var rng = new Random(7);
        var real = new double[64];
        for (int i = 0; i < 64; i++) real[i] = rng.NextDouble();

        double scale = 3.5;
        var realScaled = real.Select(x => x * scale).ToArray();

        var spec1 = Fft.R2CFull(real);
        var spec2 = Fft.R2CFull(realScaled);

        for (int k = 0; k < 64; k++)
        {
            spec2[k].Real.Should().BeApproximately(spec1[k].Real * scale, Tol,
                $"FFT linearity failed at bin {k} (real part)");
            spec2[k].Imaginary.Should().BeApproximately(spec1[k].Imaginary * scale, Tol,
                $"FFT linearity failed at bin {k} (imag part)");
        }
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    public void C2C_ForwardThenInverse_RecoversSignal(int n)
    {
        var rng = new Random(13);
        var orig = new Complex[n];
        for (int i = 0; i < n; i++)
            orig[i] = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);

        var data = orig.ToArray();
        Fft.C2C(data, -1); // forward
        Fft.C2C(data, +1); // inverse (scaled 1/N)

        for (int i = 0; i < n; i++)
        {
            data[i].Real.Should().BeApproximately(orig[i].Real, LooseTol,
                $"Round-trip failed at index {i} (real)");
            data[i].Imaginary.Should().BeApproximately(orig[i].Imaginary, LooseTol,
                $"Round-trip failed at index {i} (imag)");
        }
    }

    [Fact]
    public void ForwardInPlace_ThenInverseInPlace_RecoversSignal()
    {
        var rng = new Random(21);
        int n = 128;
        var orig = new Complex[n];
        for (int i = 0; i < n; i++)
            orig[i] = new Complex(rng.NextDouble(), 0);

        var buf = orig.ToArray();
        Fft.ForwardInPlace(buf);
        Fft.InverseInPlace(buf);

        for (int i = 0; i < n; i++)
        {
            buf[i].Real.Should().BeApproximately(orig[i].Real, LooseTol,
                $"InPlace round-trip failed at index {i}");
        }
    }

    // ── Hermitian symmetry for real input ─────────────────────────────────────

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    public void R2CFull_RealInput_HasHermitianSymmetry(int n)
    {
        var rng = new Random(55);
        var real = new double[n];
        for (int i = 0; i < n; i++) real[i] = rng.NextDouble();

        var spec = Fft.R2CFull(real);

        // X[N-k] = conj(X[k]) for real input
        for (int k = 1; k < n / 2; k++)
        {
            spec[n - k].Real.Should().BeApproximately(spec[k].Real, Tol,
                $"Hermitian symmetry: Re(X[{n - k}]) should equal Re(X[{k}])");
            spec[n - k].Imaginary.Should().BeApproximately(-spec[k].Imaginary, Tol,
                $"Hermitian symmetry: Im(X[{n - k}]) should equal -Im(X[{k}])");
        }
    }

    // ── Impulse response ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    public void PowerSpectrum_UnitImpulse_IsFlat(int n)
    {
        // δ[0] → all frequency bins have magnitude 1 → power = 1
        var impulse = new double[n];
        impulse[0] = 1.0;

        var ps = Fft.PowerSpectrum(impulse);
        // Due to AsymmetricScaling, all bins = 1/n^? → let's just check flatness
        double expected = ps[0];
        for (int k = 1; k < ps.Length; k++)
            ps[k].Should().BeApproximately(expected, expected * 1e-6,
                $"Unit impulse spectrum should be flat at bin {k}");
    }

    // ── R2C float overload ────────────────────────────────────────────────────

    [Fact]
    public void R2C_FloatOverload_MatchesDoubleOverload()
    {
        // Both overloads should give the same result (within float precision)
        int n = 128;
        var rng = new Random(3);
        var doubleArr = new double[n];
        var floatArr = new float[n];
        for (int i = 0; i < n; i++)
        {
            double v = rng.NextDouble();
            doubleArr[i] = v;
            floatArr[i] = (float)v;
        }

        var specDouble = Fft.R2C(doubleArr.AsSpan());
        var specFloat = Fft.R2C(floatArr.AsSpan());

        for (int k = 0; k < specDouble.Length; k++)
        {
            specFloat[k].Real.Should().BeApproximately(specDouble[k].Real, 1e-4,
                $"float vs double mismatch at bin {k}");
        }
    }
}

file static class ComplexExtensions
{
    public static double MagnitudeSquared(this Complex c)
        => c.Real * c.Real + c.Imaginary * c.Imaginary;
}
