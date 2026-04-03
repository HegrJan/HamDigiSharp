using FluentAssertions;
using HamDigiSharp.Decoders.Q65;
using Xunit;

namespace HamDigiSharp.Tests.Decoders;

/// <summary>
/// Tests for the QRA LDPC codec over GF(64) — Q65Subs.
///
/// Protocol summary:
///   K=13 info symbols (each 6-bit, GF(64))
///   N=63 transmitted codeword symbols (65-symbol codeword minus 2 punctured CRC symbols)
///   CRC-12 over the 13 info symbols, split into 2 × 6-bit GF symbols
///   Belief-propagation decoder on the Tanner graph
///
/// Round-trip test strategy:
///   1. Encode 13 info symbols → 63 transmitted symbols (Encode)
///   2a. Build ideal s3prob (spike at correct symbol, zero elsewhere)
///   2b. OR build ideal spectrogram and run ComputeIntrinsics
///   3. Decode → recovered 13 info symbols
///   4. Verify decoded == original (exact match)
/// </summary>
public class Q65SubsTests
{
    private const int NInfo = 13;  // info symbols
    private const int NCode = 63;  // transmitted codeword symbols
    private const int QraM  = 64;  // GF(64) alphabet size

    // ── Encode: basic properties ──────────────────────────────────────────────

    [Fact]
    public void Encode_OutputSymbolsAreInGfRange()
    {
        var x = new int[] { 1, 7, 15, 23, 31, 39, 47, 55, 60, 62, 0, 3, 11 };
        var y = new int[NCode];
        Q65Subs.Encode(x, y);

        y.Should().AllSatisfy(s => s.Should().BeInRange(0, 63),
            "all codeword symbols must be in GF(64) = {0..63}");
    }

    [Fact]
    public void Encode_IsDeterministic()
    {
        var x = new int[] { 5, 10, 20, 30, 40, 50, 63, 1, 2, 4, 8, 16, 32 };
        var y1 = new int[NCode];
        var y2 = new int[NCode];
        Q65Subs.Encode(x, y1);
        Q65Subs.Encode(x, y2);

        y1.Should().BeEquivalentTo(y2, "Encode must be deterministic");
    }

    [Fact]
    public void Encode_ZeroInfoSymbols_ProducesDeterministicOutput()
    {
        var x = new int[NInfo]; // all zero — valid GF(64) element
        var y = new int[NCode];
        Q65Subs.Encode(x, y);

        y.Should().AllSatisfy(s => s.Should().BeInRange(0, 63));
        // Encode called twice must match
        var y2 = new int[NCode];
        Q65Subs.Encode(x, y2);
        y.Should().BeEquivalentTo(y2);
    }

    [Fact]
    public void Encode_DifferentInputs_ProduceDifferentCodewords()
    {
        var x1 = new int[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var x2 = new int[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var y1 = new int[NCode];
        var y2 = new int[NCode];
        Q65Subs.Encode(x1, y1);
        Q65Subs.Encode(x2, y2);

        // Different info symbols → different codewords (since the code is injective)
        y1.Should().NotBeEquivalentTo(y2,
            "different info symbols must produce different codewords");
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 })]
    [InlineData(new[] { 63, 62, 61, 60, 59, 58, 57, 56, 55, 54, 53, 52, 51 })]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void Encode_VariousInputs_OutputSymbolsInGfRange(int[] x)
    {
        var y = new int[NCode];
        Q65Subs.Encode(x, y);
        y.Should().AllSatisfy(s => s.Should().BeInRange(0, 63));
    }

    // ── Round-trip via ideal s3prob (direct soft inputs) ─────────────────────

    /// <summary>
    /// The core correctness test: encode 13 info symbols, build ideal intrinsic
    /// probabilities (probability 1 at the correct GF symbol, 0 elsewhere), then
    /// run the QRA BP decoder and verify we recover the original symbols exactly.
    /// </summary>
    [Theory]
    [InlineData(new[] { 1, 7, 15, 23, 31, 39, 47, 55, 60, 62, 0, 3, 11 })]
    [InlineData(new[] { 63, 1, 63, 1, 63, 1, 63, 1, 63, 1, 63, 1, 63 })]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new[] { 5, 10, 20, 30, 40, 50, 60, 1, 2, 4, 8, 16, 32 })]
    public void Encode_ThenDecode_IdealDirectSoftInputs_RoundTrips(int[] x)
    {
        var y = new int[NCode];
        Q65Subs.Encode(x, y);

        // Build ideal s3prob: for each position n, spike at symbol y[n]
        var s3prob = new float[NCode * QraM];
        for (int n = 0; n < NCode; n++)
            s3prob[n * QraM + y[n]] = 1.0f;

        // s3 is only used for esNodB estimate (EsNodBFf); not needed for decode correctness
        var s3Dummy = new float[NCode * 192];
        var xdec    = new int[NInfo];
        Q65Subs.Decode(s3Dummy, s3prob, null, null, 100, out _, xdec, out int irc);

        irc.Should().BeGreaterThanOrEqualTo(0,
            "ideal soft inputs must decode successfully");
        xdec.Should().BeEquivalentTo(x,
            "decoded info symbols must exactly match original");
    }

    // ── Round-trip via ComputeIntrinsics with synthetic spectrogram ──────────

    /// <summary>
    /// More realistic test: build a synthetic Q65A spectrogram (spikes at the
    /// correct tone bins), run ComputeIntrinsics to compute soft-decision
    /// probabilities, then decode and verify round-trip.
    /// </summary>
    [Theory]
    [InlineData(new[] { 1, 7, 15, 23, 31, 39, 47, 55, 60, 62, 0, 3, 11 })]
    [InlineData(new[] { 5, 10, 20, 30, 40, 50, 60, 1, 2, 4, 8, 16, 32 })]
    public void Encode_ThenComputeIntrinsicsThenDecode_SyntheticSpectrogram_RoundTrips(int[] x)
    {
        var y = new int[NCode];
        Q65Subs.Encode(x, y);

        // Q65A: nsubmode=0, nBinsPerTone=1, nBinsPerSymbol = 64*(2+1) = 192
        const int NsubMode     = 0;
        const int NBinsPerTone = 1;
        const int NBinsSym     = 192; // 64 * (2 + 1)

        // Build ideal spectrogram: tiny noise floor + signal spike at correct tone
        var s3 = new float[NCode * NBinsSym];
        const float Signal = 1.0f, Noise = 1e-6f;
        Array.Fill(s3, Noise);
        for (int n = 0; n < NCode; n++)
        {
            // Guard = 64 bins, then tone k is at bin (64 + k * nBinsPerTone)
            int toneBin = 64 + y[n] * NBinsPerTone;
            s3[n * NBinsSym + toneBin] = Signal;
        }

        var s3prob = new float[NCode * QraM];
        Q65Subs.ComputeIntrinsics(s3, NsubMode, b90ts: 1.0f, fadingModel: 0, s3prob);

        // Verify intrinsics favour the correct tone for each symbol
        for (int n = 0; n < NCode; n++)
        {
            int correct = y[n];
            float pCorrect = s3prob[n * QraM + correct];
            float pMax     = Enumerable.Range(0, QraM).Max(k => s3prob[n * QraM + k]);
            pCorrect.Should().BeApproximately(pMax, 1e-5f,
                $"symbol {n}: correct tone {correct} must have maximum intrinsic probability");
        }

        var xdec = new int[NInfo];
        Q65Subs.Decode(s3, s3prob, null, null, 100, out _, xdec, out int irc);

        irc.Should().BeGreaterThanOrEqualTo(0,
            "realistic ideal spectrogram must decode successfully");
        xdec.Should().BeEquivalentTo(x,
            "decoded info symbols must match original");
    }

    // ── Multiple random round-trips ───────────────────────────────────────────

    [Fact]
    public void Encode_ThenDecode_TwentyRandomInputs_AllRoundTrip()
    {
        var rng  = new Random(0xC0DE);
        var y    = new int[NCode];
        var xdec = new int[NInfo];

        for (int trial = 0; trial < 20; trial++)
        {
            var x = new int[NInfo];
            for (int i = 0; i < NInfo; i++) x[i] = rng.Next(0, QraM); // random GF symbols

            y    = new int[NCode];
            xdec = new int[NInfo];
            Q65Subs.Encode(x, y);

            // Ideal s3prob: spike at correct symbol
            var s3prob = new float[NCode * QraM];
            for (int n = 0; n < NCode; n++) s3prob[n * QraM + y[n]] = 1.0f;

            var s3Dummy = new float[NCode * 192];
            Q65Subs.Decode(s3Dummy, s3prob, null, null, 100, out _, xdec, out int irc);

            irc.Should().BeGreaterThanOrEqualTo(0,
                $"trial {trial}: ideal soft inputs must decode successfully");
            xdec.Should().BeEquivalentTo(x,
                $"trial {trial}: decoded symbols must match original");
        }
    }

    // ── Encode × Decode self-consistency: CRC-12 embedded in codeword ─────────

    [Fact]
    public void Encode_CrcConsistency_SameInfoAlwaysProducesSameCrc()
    {
        // The CRC-12 of the 13 info symbols is deterministically embedded in the
        // codeword. Encode the same info twice → same codeword.
        var x   = new int[] { 3, 14, 15, 9, 26, 53, 5, 8, 97 % 64, 32, 0, 1, 2 };
        var y1  = new int[NCode];
        var y2  = new int[NCode];
        Q65Subs.Encode(x, y1);
        Q65Subs.Encode(x, y2);
        y1.Should().BeEquivalentTo(y2, "same info → same codeword → same embedded CRC");
    }

    // ── ComputeIntrinsics: basic properties ───────────────────────────────────

    [Fact]
    public void ComputeIntrinsics_OutputIsProbabilityDistribution_PerSymbol()
    {
        // For each of the NCode symbols, the 64 intrinsic probability values
        // must be non-negative and sum to approximately 1.0 (normalised).
        const int NBinsSym = 192; // Q65A
        var s3 = new float[NCode * NBinsSym];
        var rng = new Random(42);
        for (int i = 0; i < s3.Length; i++) s3[i] = (float)(rng.NextDouble() * 0.1 + 0.05);

        var s3prob = new float[NCode * QraM];
        Q65Subs.ComputeIntrinsics(s3, nsubmode: 0, b90ts: 1.0f, fadingModel: 0, s3prob);

        for (int n = 0; n < NCode; n++)
        {
            float sum = 0f;
            for (int k = 0; k < QraM; k++)
            {
                float p = s3prob[n * QraM + k];
                p.Should().BeGreaterThanOrEqualTo(0f,
                    $"symbol {n}, bin {k}: probability must be ≥ 0");
                sum += p;
            }
            sum.Should().BeApproximately(1.0f, 1e-4f,
                $"symbol {n}: intrinsic probabilities must sum to 1 (normalised)");
        }
    }

    [Fact]
    public void ComputeIntrinsics_DoesNotThrowForAllSubmodes()
    {
        for (int nsubmode = 0; nsubmode <= 3; nsubmode++)
        {
            int nBinsPerTone   = 1 << nsubmode;
            int nBinsPerSymbol = 64 * (2 + nBinsPerTone);
            var s3     = new float[NCode * nBinsPerSymbol];
            var s3prob = new float[NCode * QraM];
            // Fill with small uniform power
            Array.Fill(s3, 0.01f);

            var ex = Record.Exception(() =>
                Q65Subs.ComputeIntrinsics(s3, nsubmode, b90ts: 1.0f, fadingModel: 0, s3prob));
            ex.Should().BeNull($"submode {nsubmode}: ComputeIntrinsics must not throw");
        }
    }

    // ── Decode robustness ────────────────────────────────────────────────────

    [Fact]
    public void Decode_UniformSoftInputs_DoesNotThrow()
    {
        // With no information (all symbols equally likely), the decoder must
        // not throw — it may or may not find a codeword.
        var s3prob = new float[NCode * QraM];
        Array.Fill(s3prob, 1.0f / QraM);
        var s3   = new float[NCode * 192];
        var xdec = new int[NInfo];

        var ex = Record.Exception(() =>
            Q65Subs.Decode(s3, s3prob, null, null, 10, out _, xdec, out int _));
        ex.Should().BeNull("decoder must not throw on uniform soft inputs");
    }

    [Fact]
    public void Decode_ZeroSoftInputs_DoesNotThrow()
    {
        var s3prob = new float[NCode * QraM]; // all zeros
        var s3   = new float[NCode * 192];
        var xdec = new int[NInfo];

        var ex = Record.Exception(() =>
            Q65Subs.Decode(s3, s3prob, null, null, 10, out _, xdec, out int _));
        ex.Should().BeNull("decoder must not throw on zero inputs");
    }

    [Fact]
    public void Decode_IsDeterministic()
    {
        // Same s3prob → same irc, same xdec
        var x = new int[] { 11, 22, 33, 44, 55, 3, 14, 15, 9, 26, 53, 5, 8 };
        var y = new int[NCode];
        Q65Subs.Encode(x, y);

        var s3prob = new float[NCode * QraM];
        for (int n = 0; n < NCode; n++) s3prob[n * QraM + y[n]] = 1.0f;
        var s3 = new float[NCode * 192];

        var xdec1 = new int[NInfo];
        Q65Subs.Decode(s3, s3prob, null, null, 100, out _, xdec1, out int irc1);

        var xdec2 = new int[NInfo];
        Q65Subs.Decode(s3, s3prob, null, null, 100, out _, xdec2, out int irc2);

        irc1.Should().Be(irc2, "Decode is deterministic — same inputs, same irc");
        xdec1.Should().BeEquivalentTo(xdec2, "Decode is deterministic — same inputs, same output");
    }

    // ── FWHT self-inverse property (tested indirectly via codec) ─────────────

    [Fact]
    public void Encode_ThenDecode_VerifiesFwhtIsCorrect()
    {
        // If the 64-point FWHT butterfly (used in QraExtrinsic) is wrong,
        // the belief-propagation step would produce incorrect extrinsic messages
        // and the round-trip would fail.  This acts as an integration test for
        // the internal FWHT implementation.
        var x = new int[] { 17, 5, 42, 60, 3, 33, 9, 51, 27, 0, 15, 7, 44 };
        var y = new int[NCode];
        Q65Subs.Encode(x, y);

        var s3prob = new float[NCode * QraM];
        for (int n = 0; n < NCode; n++) s3prob[n * QraM + y[n]] = 1.0f;

        var xdec = new int[NInfo];
        Q65Subs.Decode(new float[NCode * 192], s3prob, null, null, 100, out _, xdec, out int irc);

        irc.Should().BeGreaterThanOrEqualTo(0, "FWHT correctness check via round-trip");
        xdec.Should().BeEquivalentTo(x);
    }
}
