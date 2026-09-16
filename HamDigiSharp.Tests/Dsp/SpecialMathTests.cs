using FluentAssertions;
using HamDigiSharp.Dsp;
using MathNet.Numerics;
using Xunit;

namespace HamDigiSharp.Tests.Dsp;

/// <summary>
/// Tests for the fdlibm erf port in <see cref="SpecialMath"/>.
/// MathNet.Numerics (a Boost-derived, independent implementation) serves as the reference.
/// </summary>
public class SpecialMathTests
{
    private static void ShouldMatchReference(double x)
    {
        double expected = SpecialFunctions.Erf(x);
        double actual = SpecialMath.Erf(x);
        Math.Abs(actual - expected).Should().BeLessThanOrEqualTo(
            2e-16 + 1e-15 * Math.Abs(expected), $"erf({x:R})");
    }

    [Fact]
    public void Erf_MatchesMathNet_OnDenseGrid()
    {
        for (int i = -70000; i <= 70000; i++)
            ShouldMatchReference(i * 1e-4);
    }

    [Fact]
    public void Erf_MatchesMathNet_OnRandomPoints()
    {
        var rng = new Random(42);
        for (int i = 0; i < 100000; i++)
            ShouldMatchReference((rng.NextDouble() - 0.5) * 16.0);
    }

    [Theory]
    [InlineData(1e-300)]
    [InlineData(3.7252902984e-09)]   // 2**-28
    [InlineData(0.84375)]
    [InlineData(1.25)]
    [InlineData(1.0 / 0.35)]
    [InlineData(6.0)]
    public void Erf_MatchesMathNet_AroundBranchBoundaries(double b)
    {
        foreach (double x in new[] { b, Math.BitDecrement(b), Math.BitIncrement(b) })
        {
            ShouldMatchReference(x);
            ShouldMatchReference(-x);
        }
    }

    [Fact]
    public void Erf_SpecialValues()
    {
        SpecialMath.Erf(0.0).Should().Be(0.0);
        SpecialMath.Erf(double.PositiveInfinity).Should().Be(1.0);
        SpecialMath.Erf(double.NegativeInfinity).Should().Be(-1.0);
        double.IsNaN(SpecialMath.Erf(double.NaN)).Should().BeTrue();
        SpecialMath.Erf(1.0).Should().BeApproximately(0.8427007929497149, 1e-16);
        SpecialMath.Erf(30.0).Should().Be(1.0);
        SpecialMath.Erf(-30.0).Should().Be(-1.0);

        const double tiny = 1e-20;
        SpecialMath.Erf(tiny).Should().BeApproximately(2.0 * tiny / Math.Sqrt(Math.PI), 1e-35);
    }

    [Fact]
    public void Erf_IsOdd()
    {
        for (double x = 0; x < 7; x += 0.0123)
            SpecialMath.Erf(-x).Should().Be(-SpecialMath.Erf(x));
    }
}
