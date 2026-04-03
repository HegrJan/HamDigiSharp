using FluentAssertions;
using HamDigiSharp.Dsp;
using System.Numerics;
using Xunit;

namespace HamDigiSharp.Tests.Dsp;

/// <summary>
/// Tests for SignalMath utility functions.
/// </summary>
public class SignalMathTests
{
    // ── Extrema ───────────────────────────────────────────────────────────────

    [Fact]
    public void MaxValue_FindsMaximum()
    {
        double[] data = { 1, 5, 3, 7, 2, 8, 4 };
        SignalMath.MaxValue(data, 0, 6).Should().Be(8.0);
    }

    [Fact]
    public void MinValue_FindsMinimum()
    {
        double[] data = { 5, 1, 3, 7, 2, 8, 4 };
        SignalMath.MinValue(data, 0, 6).Should().Be(1.0);
    }

    [Fact]
    public void MaxLoc_ReturnsIndexOfMax()
    {
        double[] data = { 1.0, 5.0, 3.0, 7.0, 2.0, 8.0, 4.0 };
        SignalMath.MaxLoc(data, 0, 6).Should().Be(5, "8.0 is at index 5");
    }

    [Fact]
    public void MinLoc_ReturnsIndexOfMin()
    {
        double[] data = { 5.0, 1.0, 3.0, 7.0, 2.0, 8.0, 4.0 };
        SignalMath.MinLoc(data, 0, 6).Should().Be(1, "1.0 is at index 1");
    }

    [Fact]
    public void MaxLocReverse_ReturnsLastMaxIndex()
    {
        double[] data = { 1.0, 8.0, 3.0, 8.0, 2.0 };
        // Scanning from right, the first (rightmost) max of 8 is at index 3
        SignalMath.MaxLocReverse(data, 0, 4).Should().Be(3, "rightmost max at index 3");
    }

    [Fact]
    public void MaxValue_SingleElement_ReturnsThatElement()
    {
        double[] data = { 42.0 };
        SignalMath.MaxValue(data, 0, 0).Should().Be(42.0);
    }

    // ── Peakup (parabolic peak interpolation) ─────────────────────────────────

    [Fact]
    public void Peakup_SymmetricTriangle_ReturnsZero()
    {
        // (ym=1, y0=2, yp=1): symmetric → peak exactly at center
        double offset = SignalMath.Peakup(1.0, 2.0, 1.0);
        offset.Should().BeApproximately(0.0, 1e-12, "symmetric peak → offset = 0");
    }

    [Fact]
    public void Peakup_EqualSamples_ReturnsZero()
    {
        // (1,1,1): no peak → offset = 0 (d = 2*(1-1-1) = -2, numerator = 0)
        double offset = SignalMath.Peakup(1.0, 1.0, 1.0);
        offset.Should().BeApproximately(0.0, 1e-12);
    }

    [Fact]
    public void Peakup_RightBiased_ReturnsPositive()
    {
        // yp > ym → peak is to the right of center → positive offset
        double offset = SignalMath.Peakup(1.0, 4.0, 2.0);
        offset.Should().BeGreaterThan(0.0,
            "when right sample (yp) > left sample (ym), peak is to the right");
    }

    [Fact]
    public void Peakup_LeftBiased_ReturnsNegative()
    {
        // ym > yp → peak is to the left of center → negative offset
        double offset = SignalMath.Peakup(2.0, 4.0, 1.0);
        offset.Should().BeLessThan(0.0,
            "when left sample (ym) > right sample (yp), peak is to the left");
    }

    [Fact]
    public void Peakup_VerySmallDenominator_ReturnsZero()
    {
        // 2*(2*y0 - ym - yp) = 0 when 2*y0 = ym + yp (inflection, not a peak).
        // e.g. ym=2, y0=2, yp=2: d = 2*(4-2-2) = 0 → protected
        double offset = SignalMath.Peakup(2.0, 2.0, 2.0);
        offset.Should().BeApproximately(0.0, 1e-12,
            "when denominator is near zero (2·y0 = ym+yp), offset is clamped to 0");
    }

    [Fact]
    public void Peakup_OffsetMagnitude_BoundedForReasonableInputs()
    {
        // For a clear peak where y0 >> both neighbours, |offset| is well-bounded.
        // Correct formula: offset = (yp-ym) / (2*(2*y0-ym-yp))
        // With ym,yp ∈ [0,0.4] and y0 = ym+yp+extra, extra ∈ [0.2,0.6]:
        //   2*y0-ym-yp = ym+yp+2*extra  ≥  2*0.2 = 0.4
        //   |offset| ≤ |yp-ym| / (2*(ym+yp+2*extra)) ≤ 0.4/(2*0.4) = 0.5
        var rng = new Random(55);
        for (int trial = 0; trial < 200; trial++)
        {
            double ym = rng.NextDouble() * 0.4;
            double yp = rng.NextDouble() * 0.4;
            double extra = 0.2 + rng.NextDouble() * 0.4;
            double y0 = ym + yp + extra;
            double offset = SignalMath.Peakup(ym, y0, yp);
            offset.Should().BeInRange(-1.0, 1.0,
                $"For clear peak (extra={extra:F3}), offset should be in (-1,+1)");
        }
    }

    // ── Peakup regression: previously-buggy cases ─────────────────────────────

    [Fact]
    public void Peakup_AsymmetricPeak_ReturnsCorrectOffset()
    {
        // ym=1, y0=3, yp=2: old code returned 0 (d=2*(3-1-2)=0 triggered guard).
        // Correct: d = 2*(2*3-1-2) = 2*3 = 6; offset = (2-1)/6 = 1/6.
        double offset = SignalMath.Peakup(1.0, 3.0, 2.0);
        offset.Should().BeApproximately(1.0 / 6.0, 1e-10,
            "regression: old formula gave 0 (divide-by-zero guard triggered incorrectly)");
    }

    [Fact]
    public void Peakup_Formula_MatchesMshvDerivation()
    {
        // Verify: offset = (yp-ym) / (2*(2*y0-ym-yp)) for several known cases.
        // Case 1: (ym=1, y0=4, yp=2) → (2-1)/(2*(8-1-2)) = 1/10 = 0.1
        SignalMath.Peakup(1.0, 4.0, 2.0).Should().BeApproximately(0.1, 1e-10);
        // Case 2: (ym=2, y0=4, yp=1) → (1-2)/(2*(8-2-1)) = -1/10 = -0.1
        SignalMath.Peakup(2.0, 4.0, 1.0).Should().BeApproximately(-0.1, 1e-10);
        // Case 3: (ym=0, y0=1, yp=0) → 0/(2*(2-0-0)) = 0 (symmetric peak)
        SignalMath.Peakup(0.0, 1.0, 0.0).Should().BeApproximately(0.0, 1e-10);
        // Case 4: (ym=3, y0=5, yp=4) → (4-3)/(2*(10-3-4)) = 1/6 ≈ 0.1667
        SignalMath.Peakup(3.0, 5.0, 4.0).Should().BeApproximately(1.0 / 6.0, 1e-10);
    }

    // ── Db (decibel conversion) ───────────────────────────────────────────────

    [Fact]
    public void Db_OfOne_ReturnsZero()
    {
        SignalMath.Db(1.0).Should().BeApproximately(0.0, 1e-10,
            "10*log10(1) = 0 dB");
    }

    [Fact]
    public void Db_OfHundred_Returns20()
    {
        SignalMath.Db(100.0).Should().BeApproximately(20.0, 1e-9,
            "10*log10(100) = 20 dB");
    }

    [Fact]
    public void Db_OfTen_Returns10()
    {
        SignalMath.Db(10.0).Should().BeApproximately(10.0, 1e-9,
            "10*log10(10) = 10 dB");
    }

    [Fact]
    public void Db_OfPoint01_ReturnsMinus20()
    {
        SignalMath.Db(0.01).Should().BeApproximately(-20.0, 1e-9,
            "10*log10(0.01) = -20 dB");
    }

    [Fact]
    public void Db_OfZero_ReturnsMinus100()
    {
        SignalMath.Db(0.0).Should().Be(-100.0, "x <= 0 is clamped to -100 dB");
    }

    [Fact]
    public void Db_OfNegative_ReturnsMinus100()
    {
        SignalMath.Db(-1.0).Should().Be(-100.0, "negative input is clamped to -100 dB");
    }

    [Fact]
    public void Db_Monotonic_LargerInputGivesLargerDb()
    {
        double[] levels = { 0.001, 0.01, 0.1, 1.0, 10.0, 100.0 };
        for (int i = 0; i < levels.Length - 1; i++)
            SignalMath.Db(levels[i]).Should().BeLessThan(SignalMath.Db(levels[i + 1]),
                $"Db must be monotonically increasing: Db({levels[i]}) < Db({levels[i + 1]})");
    }

    // ── Smo121 (1-2-1 smoother) ───────────────────────────────────────────────

    [Fact]
    public void Smo121_Impulse_GetsSpread()
    {
        // After 1 pass of the MSHV-compatible smo121 on {0,0,0,0,1,0,0,0,0}:
        // Each update uses the ORIGINAL (pre-update) left-neighbour via saved x0.
        //   i=3: x0=0(orig x[2]); x[3] = 0.25*0 + 0.5*0 + 0.25*1 = 0.25; save x1=0
        //   i=4: x0=0(orig x[3]=0); x[4] = 0.25*0 + 0.5*1 + 0.25*0 = 0.5; save x1=1
        //   i=5: x0=1(orig x[4]=1); x[5] = 0.25*1 + 0.5*0 + 0.25*0 = 0.25; save x1=0
        //   i=6: x0=0(orig x[5]=0); x[6] = 0.25*0 + 0.5*0 + 0.25*0 = 0; save x1=0
        // Result is a symmetric {…,0.25,0.5,0.25,…} — true FIR, no rightward leakage.
        double[] data = { 0, 0, 0, 0, 1, 0, 0, 0, 0 };
        SignalMath.Smo121(data, 0, 1);

        data[3].Should().BeApproximately(0.25, 1e-9, "left neighbour of peak = 0.25");
        data[4].Should().BeApproximately(0.50, 1e-9, "peak centre = 0.5 (uses ORIGINAL x[3]=0)");
        data[5].Should().BeApproximately(0.25, 1e-9, "right neighbour = 0.25 (uses ORIGINAL x[4]=1)");
        data[6].Should().BeApproximately(0.0,  1e-9, "no rightward leakage beyond one step");
        data[2].Should().BeApproximately(0.0,  1e-9, "left of x[3] unchanged");
    }

    [Fact]
    public void Smo121_ConstantSignal_Unchanged()
    {
        double[] data = { 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0 };
        SignalMath.Smo121(data, 0, 5);

        // Interior samples: 0.25*2 + 0.5*2 + 0.25*2 = 2 → unchanged
        for (int i = 1; i < data.Length - 1; i++)
            data[i].Should().BeApproximately(2.0, 1e-9,
                "smoothing a constant signal must not change it");
    }

    [Fact]
    public void Smo121_ZeroPasses_LeavesDataUnchanged()
    {
        double[] data = { 1, 3, 2, 5, 1, 4, 2, 6 };
        var expected = data.ToArray();
        SignalMath.Smo121(data, 0, 0);
        data.Should().BeEquivalentTo(expected, "0 passes must not modify data");
    }

    [Fact]
    public void Smo121_MultiplePassesReduceVariance()
    {
        var rng = new Random(42);
        double[] data = new double[200];
        for (int i = 0; i < 200; i++) data[i] = rng.NextDouble();

        double varBefore = data.Select(x => x * x).Average() - Math.Pow(data.Average(), 2);
        SignalMath.Smo121(data, 0, 10);
        double varAfter = data.Select(x => x * x).Average() - Math.Pow(data.Average(), 2);

        varAfter.Should().BeLessThan(varBefore,
            "multiple smoothing passes must reduce variance");
    }

    [Fact]
    public void Smo121_ImpulseResponse_IsSymmetric()
    {
        // A single-impulse input should produce a symmetric output because
        // the filter is a true FIR (uses original left-neighbour, not updated value).
        double[] data = { 0, 0, 0, 0, 1, 0, 0, 0, 0 };
        SignalMath.Smo121(data, 0, 1);

        data[3].Should().BeApproximately(data[5], 1e-9,
            "Smo121 impulse response must be symmetric: x[peak-1] == x[peak+1]");
        data[6].Should().BeApproximately(0.0, 1e-9,
            "no energy beyond one neighbour (FIR, not causal IIR)");
        data[2].Should().BeApproximately(0.0, 1e-9,
            "no energy beyond one neighbour on the left either");
    }

    // ── Pctile (percentile) ───────────────────────────────────────────────────

    [Fact]
    public void Pctile_MedianOf5Elements_Returns3rd()
    {
        double[] data = { 3.0, 1.0, 5.0, 2.0, 4.0 };
        double median = SignalMath.Pctile(data, 5, 50.0);
        median.Should().BeApproximately(3.0, 1e-9, "median of {1,2,3,4,5} is 3");
    }

    [Fact]
    public void Pctile_MinimumPctile_ReturnsSmallest()
    {
        double[] data = { 5.0, 2.0, 8.0, 1.0, 9.0 };
        double min = SignalMath.Pctile(data, 5, 0.0);
        min.Should().BeApproximately(1.0, 1e-9, "0th percentile = minimum");
    }

    [Fact]
    public void Pctile_MaximumPctile_ReturnsLargest()
    {
        double[] data = { 5.0, 2.0, 8.0, 1.0, 9.0 };
        double max = SignalMath.Pctile(data, 5, 99.0);
        max.Should().BeApproximately(9.0, 1e-9, "99th percentile ≈ maximum");
    }

    [Fact]
    public void Pctile_DoesNotModifyInputArray()
    {
        double[] data = { 3.0, 1.0, 5.0, 2.0, 4.0 };
        var original = data.ToArray();
        SignalMath.Pctile(data, 5, 50.0);
        data.Should().BeEquivalentTo(original, "Pctile must not modify the input");
    }

    [Fact]
    public void Pctile_LargeArray_ReturnsCorrectMedian()
    {
        // Create sorted array 0..999, then shuffle
        var data = Enumerable.Range(0, 1000).Select(i => (double)i).ToArray();
        var shuffled = data.OrderBy(_ => Guid.NewGuid()).ToArray();
        double median = SignalMath.Pctile(shuffled, 1000, 50.0);
        // 50th percentile of 0..999 is around 499-500
        median.Should().BeInRange(490.0, 510.0, "50th percentile of 0..999 ≈ 499");
    }

    // ── CShift (circular shift) ───────────────────────────────────────────────

    [Fact]
    public void CShift_ShiftByZero_LeavesArrayUnchanged()
    {
        var arr = new Complex[] { 1, 2, 3, 4, 5 };
        var expected = arr.ToArray();
        SignalMath.CShift(arr, 5, 0);
        arr.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void CShift_ShiftByN_LeavesArrayUnchanged()
    {
        var arr = new Complex[] { 1, 2, 3, 4, 5 };
        var expected = arr.ToArray();
        SignalMath.CShift(arr, 5, 5); // shift by full length = identity
        arr.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void CShift_ShiftByOne_RotatesRight()
    {
        var arr = new Complex[] { 1, 2, 3, 4, 5 };
        SignalMath.CShift(arr, 5, 1);
        // Right rotation by 1: last element moves to front
        arr[0].Real.Should().BeApproximately(5, 1e-9, "first element = last original");
        arr[1].Real.Should().BeApproximately(1, 1e-9);
        arr[4].Real.Should().BeApproximately(4, 1e-9);
    }

    [Fact]
    public void CShift_NegativeShift_HandledByModulo()
    {
        var arr = new Complex[] { 1, 2, 3, 4, 5 };
        var copy = arr.ToArray();
        SignalMath.CShift(arr, 5, -2);
        // Negative shift = left rotation = same as right rotation by (5-2) = 3
        SignalMath.CShift(copy, 5, 3);
        arr.Should().BeEquivalentTo(copy, "shift(-2) ≡ shift(+3) for n=5");
    }

    // ── Polyfit (polynomial least-squares) ───────────────────────────────────

    [Fact]
    public void Polyfit_LinearData_ReturnsCorrectCoefficients()
    {
        // y = 2*x + 1 → coeffs should be [1, 2]
        double[] x = { 0, 1, 2, 3, 4 };
        double[] y = x.Select(xi => 2 * xi + 1).ToArray();
        var coeffs = new double[2];
        SignalMath.Polyfit(x, y, 5, 2, coeffs);

        coeffs[0].Should().BeApproximately(1.0, 1e-6, "constant term = 1");
        coeffs[1].Should().BeApproximately(2.0, 1e-6, "linear term = 2");
    }

    [Fact]
    public void Polyfit_QuadraticData_ReturnsCorrectCoefficients()
    {
        // y = x^2 → coeffs should be [0, 0, 1]
        double[] x = { -2, -1, 0, 1, 2 };
        double[] y = x.Select(xi => xi * xi).ToArray();
        var coeffs = new double[3];
        SignalMath.Polyfit(x, y, 5, 3, coeffs);

        coeffs[0].Should().BeApproximately(0.0, 1e-6, "constant term = 0");
        coeffs[1].Should().BeApproximately(0.0, 1e-6, "linear term = 0 (symmetric data)");
        coeffs[2].Should().BeApproximately(1.0, 1e-6, "quadratic term = 1");
    }

    [Fact]
    public void Polyfit_ConstantData_ReturnsConstantCoefficient()
    {
        double[] x = { 1, 2, 3, 4, 5 };
        double[] y = { 7.0, 7.0, 7.0, 7.0, 7.0 };
        var coeffs = new double[2];
        SignalMath.Polyfit(x, y, 5, 2, coeffs);

        coeffs[0].Should().BeApproximately(7.0, 1e-6, "constant term = 7");
        coeffs[1].Should().BeApproximately(0.0, 1e-6, "linear term = 0");
    }
}
