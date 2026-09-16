using FluentAssertions;
using HamDigiSharp.Dsp;
using MathNet.Numerics.IntegralTransforms;
using System.Buffers;
using System.Numerics;
using Xunit;

namespace HamDigiSharp.Tests.Dsp;

/// <summary>
/// Tests for the KISS FFT port behind <see cref="Fft"/>.
/// MathNet.Numerics (a test-only reference) serves as the independent reference.
///
/// The butterflies have Vector512/256/128 kernels plus a scalar fallback that are
/// meant to be bit-identical. To exercise the narrower paths, re-run this class with
/// hardware intrinsics restricted, e.g. (PowerShell):
///   $env:DOTNET_EnableAVX512F=0; dotnet test --filter KissFftTests
///   $env:DOTNET_EnableAVX2=0;    dotnet test --filter KissFftTests
///   $env:DOTNET_EnableHWIntrinsic=0; dotnet test --filter KissFftTests
/// </summary>
public class KissFftTests
{
    public static TheoryData<int> Sizes =>
    [
        1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 16, 25, 32, 49, 64, 125, 143, 144, 288,
        512, 1000, 1152, 3200, 3840, 4096, 192000,
    ];

    private static Complex[] RandomComplex(int n, int seed)
    {
        var rng = new Random(seed);
        var x = new Complex[n];
        for (int i = 0; i < n; i++) x[i] = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
        return x;
    }

    private static void ShouldMatch(Complex[] actual, Complex[] expected, double tol)
    {
        actual.Should().HaveCount(expected.Length);
        double maxErr = 0;
        for (int i = 0; i < expected.Length; i++)
            maxErr = Math.Max(maxErr, (actual[i] - expected[i]).Magnitude);
        maxErr.Should().BeLessThan(tol);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Forward_MatchesMathNet(int n)
    {
        var x = RandomComplex(n, n);
        var expected = x.ToArray();
        Fourier.Forward(expected, FourierOptions.AsymmetricScaling);

        var actual = x.ToArray();
        Fft.ForwardInPlace(actual);

        ShouldMatch(actual, expected, 1e-9 * Math.Max(1, Math.Sqrt(n)));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Inverse_MatchesMathNet(int n)
    {
        var x = RandomComplex(n, n + 1);
        var expected = x.ToArray();
        Fourier.Inverse(expected, FourierOptions.AsymmetricScaling);

        var actual = x.ToArray();
        Fft.InverseInPlace(actual);

        ShouldMatch(actual, expected, 1e-12 * Math.Max(1, Math.Sqrt(n)));
    }

    [Fact]
    public void Forward_SignConvention_IsNegativeExponent()
    {
        // x = δ[n-1] → X[k] = e^{-j2πk/N}
        const int n = 12;
        var x = new Complex[n];
        x[1] = Complex.One;
        Fft.ForwardInPlace(x);
        for (int k = 0; k < n; k++)
        {
            var expected = Complex.FromPolarCoordinates(1.0, -2.0 * Math.PI * k / n);
            (x[k] - expected).Magnitude.Should().BeLessThan(1e-12, $"bin {k}");
        }
    }

    [Theory]
    [InlineData(3840, new[] { 4, 960, 4, 240, 4, 60, 4, 15, 3, 5, 5, 1 })]
    [InlineData(192000, new[] { 4, 48000, 4, 12000, 4, 3000, 4, 750, 2, 375, 3, 125, 5, 25, 5, 5, 5, 1 })]
    [InlineData(32, new[] { 4, 8, 4, 2, 2, 1 })]
    [InlineData(143, new[] { 11, 13, 13, 1 })]
    [InlineData(1, new[] { 1, 1 })]
    public void Factor_FollowsKissOrder(int n, int[] expected)
        => KissFftPlan.Factor(n).Should().Equal(expected);

    [Fact]
    public void SpanOverload_OnPooledSlice_LeavesTailUntouched()
    {
        const int n = 3200;
        var x = RandomComplex(n, 5);
        var expected = x.ToArray();
        Fft.ForwardInPlace(expected);

        Complex[] pooled = ArrayPool<Complex>.Shared.Rent(n);
        try
        {
            pooled.Length.Should().BeGreaterThan(n);
            var sentinel = new Complex(123.0, -456.0);
            pooled.AsSpan(n).Fill(sentinel);
            x.CopyTo(pooled, 0);

            Fft.ForwardInPlace(pooled.AsSpan(0, n));

            pooled[..n].Should().Equal(expected);
            pooled.AsSpan(n).ToArray().Should().AllSatisfy(c => c.Should().Be(sentinel));
        }
        finally
        {
            ArrayPool<Complex>.Shared.Return(pooled);
        }
    }

    [Theory]
    [InlineData(1152, false)]
    [InlineData(3840, true)]
    public void OutOfPlace_EqualsInPlace(int n, bool inverse)
    {
        var x = RandomComplex(n, 9);
        var plan = new KissFftPlan(n, inverse);

        var outOfPlace = new Complex[n];
        plan.Transform(x, outOfPlace);

        var inPlace = x.ToArray();
        plan.Transform(inPlace, inPlace);

        inPlace.Should().Equal(outOfPlace);
    }

    [Fact]
    public void InputStride_ReadsEveryNthSample()
    {
        const int n = 60, stride = 3;
        var wide = RandomComplex(n * stride, 17);
        var dense = new Complex[n];
        for (int i = 0; i < n; i++) dense[i] = wide[i * stride];

        var plan = new KissFftPlan(n, inverse: false);
        var fromStride = new Complex[n];
        var fromDense  = new Complex[n];
        plan.Transform(wide, fromStride, stride);
        plan.Transform(dense, fromDense);

        fromStride.Should().Equal(fromDense);
    }

    [Fact]
    public void SharedPlan_ConcurrentUse_IsDeterministic()
    {
        const int n = 3840;
        var x = RandomComplex(n, 23);
        var expected = x.ToArray();
        Fft.ForwardInPlace(expected);

        var mismatches = 0;
        Parallel.For(0, 64, _ =>
        {
            var buf = x.ToArray();
            Fft.ForwardInPlace(buf);
            if (!buf.AsSpan().SequenceEqual(expected)) Interlocked.Increment(ref mismatches);
        });

        mismatches.Should().Be(0);
    }
}
