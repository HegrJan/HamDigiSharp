using FluentAssertions;
using HamDigiSharp.Dsp;
using Xunit;

namespace HamDigiSharp.Tests.Dsp;

public class WindowingTests
{
    [Theory]
    [InlineData(256)]
    [InlineData(1024)]
    [InlineData(3840)]
    public void Nuttall_HasCorrectLength(int n)
    {
        var w = Windowing.Nuttall(n);
        w.Should().HaveCount(n);
    }

    [Fact]
    public void Nuttall_StartAndEndAreNearZero()
    {
        // 4-term Nuttall: endpoints are ≈0.000363, well below the 1st sidelobe
        var w = Windowing.Nuttall(512);
        w[0].Should().BeApproximately(0.0, 1e-2, "Nuttall endpoint must be near zero");
        w[^1].Should().BeApproximately(0.0, 1e-2, "Nuttall endpoint must be near zero");
    }

    [Fact]
    public void Nuttall_PeakIsAtCenter()
    {
        var w = Windowing.Nuttall(512);
        double peak = w.Max();
        int peakIdx = Array.IndexOf(w, peak);
        peakIdx.Should().BeInRange(254, 258);
        peak.Should().BeApproximately(1.0, 0.01);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(1024)]
    public void Hann_IsSymmetric(int n)
    {
        var w = Windowing.Hann(n);
        w.Should().HaveCount(n);
        for (int i = 0; i < n / 2; i++)
            w[i].Should().BeApproximately(w[n - 1 - i], 1e-12,
                because: $"Hann window must be symmetric at i={i}");
    }

    [Fact]
    public void Hann_EndpointsAreZero()
    {
        // w[0] = 0.5*(1-cos(0)) = 0 (exact).
        // w[N-1] = 0.5*(1-cos(2π)) ≈ 0 to floating-point precision.
        var w = Windowing.Hann(512);
        w[0].Should().BeApproximately(0.0, 1e-15, "Hann w[0] = 0 exactly");
        w[^1].Should().BeApproximately(0.0, 1e-10, "Hann w[N-1] ≈ 0 (limited by cos(2π) precision)");
    }

    [Fact]
    public void Hann_PeakIsOne()
    {
        // The maximum of the Hann window occurs at the centre and equals 1.0.
        var w = Windowing.Hann(1025); // odd length: exact centre sample
        w.Max().Should().BeApproximately(1.0, 1e-12, "Hann window peak = 1.0");
    }

    /// <summary>
    /// ENBW (Equivalent Noise Bandwidth) for the Hann window.
    ///
    /// Analytical derivation: for w[k] = 0.5*(1 - cos(2πk/(N-1))):
    ///   ∑w[k] ≈ N/2,  ∑w[k]² ≈ 3N/8
    ///   ENBW = N·∑w² / (∑w)² = N·(3N/8) / (N/2)² = 3/2 = 1.5  (exact in the limit)
    /// </summary>
    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void Hann_Enbw_IsApproximately1p5(int n)
    {
        var w = Windowing.Hann(n);
        double sumW  = w.Sum();
        double sumW2 = w.Sum(x => x * x);
        double enbw  = n * sumW2 / (sumW * sumW);
        enbw.Should().BeApproximately(1.5, 0.005,
            "Hann window ENBW = 1.5 (analytically exact in continuous limit)");
    }

    /// <summary>
    /// ENBW for the 4-term Nuttall window.
    /// With coefficients (0.3635819, 0.4891775, 0.1365995, 0.0106411),
    /// the ENBW ≈ 1.976, which is characteristically close to 2.0.
    /// </summary>
    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    public void Nuttall_Enbw_IsApproximately2(int n)
    {
        var w = Windowing.Nuttall(n);
        double sumW  = w.Sum();
        double sumW2 = w.Sum(x => x * x);
        double enbw  = n * sumW2 / (sumW * sumW);
        enbw.Should().BeInRange(1.8, 2.2,
            "4-term Nuttall ENBW is characteristically near 2.0");
    }

    [Fact]
    public void BlackmanHarris_IsSymmetric()
    {
        int n = 512;
        var w = new double[n];
        Windowing.BlackmanHarris(w);
        for (int i = 0; i < n / 2; i++)
            w[i].Should().BeApproximately(w[n - 1 - i], 1e-12,
                $"Blackman-Harris must be symmetric at i={i}");
    }
}
