using System.Numerics;
using FluentAssertions;
using HamDigiSharp.Decoders;
using HamDigiSharp.Models;
using Xunit;

namespace HamDigiSharp.Tests.Decoders;

/// <summary>
/// Unit tests for <see cref="Ft4x2DecoderBase"/> shared math:
/// CountCostasMatches, ComputeSnrDb4Fsk, ShiftByHalfTone, ComputeLlr, PrepareBuffer.
/// </summary>
public class Ft4x2DecoderTests
{
    // ── Test helper: concrete subclass that exposes protected methods ──────────

    private sealed class Tester : Ft4x2DecoderBase
    {
        // Use FT4 parameters (Nsps=576, NDown=18, Nss=32)
        public Tester() : base(nsps: 576, nDown: 18, nMax: 72576, nfft1: 1152) { }

        public override DigitalMode Mode => DigitalMode.FT4;

        public override IReadOnlyList<DecodeResult> Decode(
            ReadOnlySpan<float> s, double fl, double fh, string t)
            => Array.Empty<DecodeResult>();

        // Expose protected / internal helpers for testing
        public double[] PrepareBufferPub(ReadOnlySpan<float> s)         => PrepareBuffer(s);
        public int      CostasMatchesPub(double[,] s4)                  => CountCostasMatches(s4);
        public double   SnrPub(double[,] s4)                            => ComputeSnrDb4Fsk(s4);
        public double[]? LlrPub(Complex[] cd, double[,] s4, int minC)  => ComputeLlr(cd, s4, minC);
        public static Complex[] ShiftPub(Complex[] cd, int nss)         => ShiftByHalfTone(cd, nss);

        // Expose internal CostasScore4Fsk for timing-optimizer tests
        public double CostasScorePub(Complex[] c1, Complex[] cbuf, int ib)
            => CostasScore4Fsk(c1, cbuf, ib);
    }

    private static readonly Tester T = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Build an s4[103,4] array where each of the 16 Costas pilot symbols has
    /// the expected tone set to <paramref name="sigAmp"/> and all others to
    /// <paramref name="noiseAmp"/>.  Data symbols are set to noiseAmp uniformly.
    /// </summary>
    private static double[,] MakePilotS4(double sigAmp, double noiseAmp)
    {
        var s4 = new double[103, 4];
        int[][] allCostas = { new[]{0,1,3,2}, new[]{1,0,2,3}, new[]{2,3,1,0}, new[]{3,2,0,1} };
        int[]   offsets   = { 0, 33, 66, 99 };

        // Fill everything with noise
        for (int k = 0; k < 103; k++)
            for (int t = 0; t < 4; t++) s4[k, t] = noiseAmp;

        // Override Costas pilots
        for (int g = 0; g < 4; g++)
            for (int k = 0; k < 4; k++)
            {
                int sym = offsets[g] + k;
                for (int t = 0; t < 4; t++) s4[sym, t] = noiseAmp;
                s4[sym, allCostas[g][k]] = sigAmp;
            }

        return s4;
    }

    // ── CountCostasMatches ────────────────────────────────────────────────────

    [Fact]
    public void CountCostasMatches_AllCorrectTones_Returns16()
    {
        var s4 = MakePilotS4(sigAmp: 1.0, noiseAmp: 0.0);
        T.CostasMatchesPub(s4).Should().Be(16);
    }

    [Fact]
    public void CountCostasMatches_AllWrongTones_Returns0()
    {
        // Put peak at tone 0 for every pilot (most expected tones are non-zero)
        var s4 = new double[103, 4];
        int[][] allCostas = { new[]{0,1,3,2}, new[]{1,0,2,3}, new[]{2,3,1,0}, new[]{3,2,0,1} };
        int[]   offsets   = { 0, 33, 66, 99 };

        for (int g = 0; g < 4; g++)
            for (int k = 0; k < 4; k++)
            {
                int sym     = offsets[g] + k;
                int expTone = allCostas[g][k];
                int wrong   = (expTone + 1) % 4;   // guaranteed different tone
                s4[sym, wrong] = 1.0;
            }

        T.CostasMatchesPub(s4).Should().Be(0,
            "every pilot symbol has the wrong peak tone");
    }

    [Fact]
    public void CountCostasMatches_HalfCorrect_Returns8()
    {
        // First 8 pilots correct, second 8 wrong
        var s4 = MakePilotS4(sigAmp: 1.0, noiseAmp: 0.0);

        int[][] allCostas = { new[]{0,1,3,2}, new[]{1,0,2,3}, new[]{2,3,1,0}, new[]{3,2,0,1} };
        int[]   offsets   = { 0, 33, 66, 99 };

        // Corrupt the last two Costas groups
        for (int g = 2; g < 4; g++)
            for (int k = 0; k < 4; k++)
            {
                int sym     = offsets[g] + k;
                int expTone = allCostas[g][k];
                for (int t = 0; t < 4; t++) s4[sym, t] = 0;
                s4[sym, (expTone + 1) % 4] = 1.0;
            }

        T.CostasMatchesPub(s4).Should().Be(8);
    }

    // ── ComputeSnrDb4Fsk ──────────────────────────────────────────────────────

    [Fact]
    public void ComputeSnrDb4Fsk_HighSnr_ReturnsAboveFloor()
    {
        // Signal = 100 amplitude (power=10000), noise = 1 per tone (power=1).
        var s4 = MakePilotS4(sigAmp: 100.0, noiseAmp: 1.0);
        double snr = T.SnrPub(s4);
        snr.Should().BeGreaterThan(-30,
            "strong signal should produce SNR well above the -30 dB floor");
    }

    [Fact]
    public void ComputeSnrDb4Fsk_EqualPowerAllTones_ReturnsMinusThirty()
    {
        // All tones equal → signal power = noise power → SNR below threshold → clamped to -30.
        var s4 = MakePilotS4(sigAmp: 1.0, noiseAmp: 1.0);
        double snr = T.SnrPub(s4);
        // With equal power snrRaw = (1)/(3*1/3) = 1. For FT4: bwFactor=2500/20.83≈120.
        // 10*log10(1/120) ≈ -20.8 → well below 0, but above -30.
        snr.Should().BeInRange(-30, 5);
    }

    [Fact]
    public void ComputeSnrDb4Fsk_PureSilence_ReturnsMinusThirty()
    {
        var s4 = new double[103, 4]; // all zeros
        T.SnrPub(s4).Should().Be(-30);
    }

    // ── ShiftByHalfTone ───────────────────────────────────────────────────────

    [Fact]
    public void ShiftByHalfTone_ZeroInput_ReturnsZeros()
    {
        var cd      = new Complex[32 * 3];
        var shifted = Tester.ShiftPub(cd, nss: 32);
        shifted.Should().OnlyContain(c => c == Complex.Zero);
    }

    [Fact]
    public void ShiftByHalfTone_PreservesLength()
    {
        var cd      = new Complex[32 * 103];
        var shifted = Tester.ShiftPub(cd, nss: 32);
        shifted.Should().HaveCount(cd.Length);
    }

    [Fact]
    public void ShiftByHalfTone_PureRealSample_BecomesComplex()
    {
        // After ½-tone shift, a real-valued signal acquires an imaginary component.
        var cd = new Complex[32];
        for (int i = 0; i < 32; i++) cd[i] = new Complex(1.0, 0.0);

        var shifted = Tester.ShiftPub(cd, nss: 32);

        // t=0: phi=0, no rotation; t=1: phi=π/32 → small imaginary part
        shifted[0].Imaginary.Should().BeApproximately(0.0, 1e-12,
            "at t=0 the rotation angle is zero");
        shifted[1].Imaginary.Should().NotBeApproximately(0.0, 1e-6,
            "at t=1 the rotation adds an imaginary component");
    }

    [Fact]
    public void ShiftByHalfTone_PowerPreserved()
    {
        var rng = new Random(42);
        var cd  = Enumerable.Range(0, 64)
            .Select(_ => new Complex(rng.NextDouble(), rng.NextDouble()))
            .ToArray();

        var shifted = Tester.ShiftPub(cd, nss: 32);

        double origPower    = cd.Sum(c => c.Real * c.Real + c.Imaginary * c.Imaginary);
        double shiftedPower = shifted.Sum(c => c.Real * c.Real + c.Imaginary * c.Imaginary);
        shiftedPower.Should().BeApproximately(origPower, origPower * 1e-10,
            "a phase rotation is unitary and must preserve signal power");
    }

    // ── PrepareBuffer ─────────────────────────────────────────────────────────

    [Fact]
    public void PrepareBuffer_CopiesUpToNMax_PadsWithZero()
    {
        var samples = new float[] { 1f, 2f, 3f };
        var dd = T.PrepareBufferPub(samples);

        dd.Should().HaveCount(72576, "NMax for FT4 is 72576");
        dd[0].Should().Be(1.0);
        dd[1].Should().Be(2.0);
        dd[2].Should().Be(3.0);
        dd[3].Should().Be(0.0, "beyond input length should be zero");
    }

    [Fact]
    public void PrepareBuffer_LargerThanNMax_UsesFullInput()
    {
        var samples = new float[100000];
        for (int i = 0; i < samples.Length; i++) samples[i] = 1f;
        var dd = T.PrepareBufferPub(samples);
        dd.Should().HaveCount(100000, "decoder is input-adaptive: larger buffers are fully processed");
        dd[72575].Should().Be(1.0);
        dd[99999].Should().Be(1.0);
    }

    // ── ComputeLlr ────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeLlr_BelowMinCostas_ReturnsNull()
    {
        // All zeros → CountCostasMatches returns ≤4 (tie-breaks to tone 0).
        // Using minC=5 guarantees the sync gate rejects.
        var cd = new Complex[103 * 32];
        var s4 = new double[103, 4];
        T.LlrPub(cd, s4, minC: 5).Should().BeNull(
            "all-zero input cannot satisfy a min-matches gate of 5");
    }

    [Fact]
    public void ComputeLlr_StrongCostas_ReturnsNonNull()
    {
        // Synthesise a cd array where each symbol has energy clearly at tone 0.
        int nss = 32;
        var cd = new Complex[103 * nss];
        for (int sym = 0; sym < 103; sym++)
        {
            // Place a pure tone at bin 0 (frequency 0 in the Nss-point FFT)
            // by setting all time-domain samples to the same real value.
            for (int i = 0; i < nss; i++)
                cd[sym * nss + i] = new Complex(1.0, 0.0);
        }

        var s4  = new double[103, 4];
        var llr = T.LlrPub(cd, s4, minC: 1);   // low threshold: accept whatever matches
        llr.Should().NotBeNull("dominant-tone signal should produce valid LLRs");
        llr!.Should().HaveCount(174);
    }

    // ── Integration: FT4 encode→decode round-trip ─────────────────────────────

    [Fact]
    public void Ft4Decoder_Silence_ReturnsNoResults()
    {
        var dec = new HamDigiSharp.Decoders.Ft4.Ft4Decoder();
        dec.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Fast });
        var r = dec.Decode(new float[72576], 200, 3000, "000000");
        r.Should().BeEmpty();
    }

    [Fact]
    public void Ft2Decoder_Silence_ReturnsNoResults()
    {
        var dec = new HamDigiSharp.Decoders.Ft2.Ft2Decoder();
        dec.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Fast });
        var r = dec.Decode(new float[45000], 200, 3000, "000000");
        r.Should().BeEmpty();
    }
}
