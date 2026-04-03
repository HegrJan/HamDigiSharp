using FluentAssertions;
using HamDigiSharp.Codecs;
using Xunit;

namespace HamDigiSharp.Tests.Codecs;

/// <summary>
/// Tests for RS(63,12) over GF(2^6).
/// Error correction capability: up to 25 symbol errors (nroots/2 = 25).
/// Encode: 12 data symbols → 51 parity symbols.
/// Decode: 63 received symbols → corrected in place; returns error count or -1.
/// </summary>
public class ReedSolomon63Tests
{
    // Helper: build a full 63-symbol codeword from 12 data symbols
    private static int[] BuildCodeword(int[] data12)
    {
        var parity = new int[51];
        ReedSolomon63.Encode(data12, parity);
        var cw = new int[63];
        Array.Copy(data12, cw, 12);
        Array.Copy(parity, 0, cw, 12, 51);
        return cw;
    }

    // ── Encode ────────────────────────────────────────────────────────────────

    [Fact]
    public void Encode_ZeroData_ProducesZeroParity()
    {
        // For RS with fcr=3, the parity of all-zero data is all-zero
        // (since feedback is IndexOf[0 ^ 0] = IndexOf[0] = Nn = sentinel → shift register path)
        var data = new int[12];
        var parity = new int[51];
        ReedSolomon63.Encode(data, parity);

        parity.Should().AllSatisfy(p => p.Should().Be(0),
            "RS(63,12) with fcr=3: zero data encodes to zero parity");
    }

    [Fact]
    public void Encode_NonZeroData_ProducesNonZeroParity()
    {
        var data = new int[12];
        data[0] = 1; // single non-zero symbol
        var parity = new int[51];
        ReedSolomon63.Encode(data, parity);

        parity.Any(p => p != 0).Should().BeTrue(
            "non-zero data must produce non-trivial parity");
    }

    [Fact]
    public void Encode_SymbolsInRange0to63_ProducesParityInRange()
    {
        var rng = new Random(42);
        var data = new int[12];
        for (int i = 0; i < 12; i++) data[i] = rng.Next(0, 64); // 6-bit symbols

        var parity = new int[51];
        ReedSolomon63.Encode(data, parity);

        parity.Should().AllSatisfy(p => p.Should().BeInRange(0, 63),
            "parity symbols must be in GF(2^6) = {0..63}");
    }

    [Fact]
    public void Encode_IsDeterministic()
    {
        var data = new int[] { 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60 };
        var p1 = new int[51]; var p2 = new int[51];
        ReedSolomon63.Encode(data, p1);
        ReedSolomon63.Encode(data, p2);
        p1.Should().BeEquivalentTo(p2);
    }

    // ── Round-trip: Decode with 0 errors ─────────────────────────────────────

    [Fact]
    public void Decode_NoErrors_Returns0AndDataUnchanged()
    {
        var data = new int[] { 3, 7, 11, 17, 22, 31, 38, 42, 50, 55, 61, 62 };
        var cw = BuildCodeword(data);
        var erasurePos = Array.Empty<int>();

        int errs = ReedSolomon63.Decode(cw, erasurePos, 0, false);

        errs.Should().Be(0, "no errors → 0 corrections");
        // First 12 symbols must be the original data
        cw.Take(12).Should().BeEquivalentTo(data,
            "data symbols must be unchanged when no errors");
    }

    [Fact]
    public void Decode_ZeroDataNoErrors_Returns0()
    {
        var data = new int[12]; // all zero
        var cw = BuildCodeword(data);
        var erasures = Array.Empty<int>();

        int errs = ReedSolomon63.Decode(cw, erasures, 0, false);
        errs.Should().Be(0);
    }

    // ── Single error correction ───────────────────────────────────────────────

    [Theory]
    [InlineData(0)]       // error in first data symbol
    [InlineData(11)]      // error in last data symbol
    [InlineData(12)]      // error in first parity symbol
    [InlineData(62)]      // error in last symbol
    [InlineData(31)]      // error in middle
    public void Decode_SingleSymbolError_CorrectsIt(int errorPos)
    {
        var data = new int[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var cw = BuildCodeword(data);

        cw[errorPos] ^= 0x17; // XOR with a non-zero pattern

        int errs = ReedSolomon63.Decode(cw, Array.Empty<int>(), 0, false);

        errs.Should().Be(1, "single error should be corrected");
        cw.Take(12).Should().BeEquivalentTo(data,
            "data symbols must be restored after single error correction");
    }

    // ── Multiple error correction (up to 25) ─────────────────────────────────

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(25)]
    public void Decode_UpTo25Errors_Corrects(int nErrors)
    {
        var data = new int[] { 63, 1, 63, 1, 63, 1, 63, 1, 63, 1, 63, 1 };
        var cw = BuildCodeword(data);

        // Introduce exactly nErrors errors at distinct positions
        for (int i = 0; i < nErrors; i++)
            cw[i] ^= (i + 1) & 0x3F; // flip distinct bits

        int errs = ReedSolomon63.Decode(cw, Array.Empty<int>(), 0, false);

        errs.Should().BeInRange(0, nErrors,
            $"RS(63,12) can correct up to 25 errors (nroots/2); attempted {nErrors}");
        cw.Take(12).Should().BeEquivalentTo(data,
            "data must be restored for correctable error count");
    }

    [Fact]
    public void Decode_26Errors_ReturnsMinusOne()
    {
        // 26 errors exceeds the error correction capability of RS(63,12)
        var data = new int[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var cw = BuildCodeword(data);

        for (int i = 0; i < 26; i++)
            cw[i] = (cw[i] ^ ((i * 7 + 3) & 0x3F)) | 1; // ensure non-zero change

        int errs = ReedSolomon63.Decode(cw, Array.Empty<int>(), 0, false);

        errs.Should().Be(-1,
            "26 errors exceeds error correction limit; decoder should return -1");
    }

    // ── With erasures ─────────────────────────────────────────────────────────

    [Fact]
    public void Decode_WithErasures_CorrectlyHandled()
    {
        var data = new int[] { 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60 };
        var cw = BuildCodeword(data);

        // Mark 3 positions as erasures (force them to 0)
        var erasurePos = new int[] { 2, 5, 8 };
        foreach (int p in erasurePos) cw[p] = 0;

        int errs = ReedSolomon63.Decode(cw, erasurePos, 3, true);

        // With 3 erasures (each counts as 1 toward limit), should correct
        errs.Should().BeGreaterThanOrEqualTo(0,
            "3 erasures within correction capability");
        cw.Take(12).Should().BeEquivalentTo(data,
            "data must be restored");
    }

    // ── Syndrome = 0 for valid codeword ──────────────────────────────────────

    [Fact]
    public void Decode_ValidCodeword_ZeroSyndrome_Returns0()
    {
        // A correctly encoded codeword has zero syndrome → returns 0 immediately
        var data = new int[] { 42, 17, 63, 0, 31, 15, 8, 3, 60, 55, 2, 1 };
        var cw = BuildCodeword(data);

        // Decode should short-circuit at syndrome check
        int errs = ReedSolomon63.Decode(cw, Array.Empty<int>(), 0, false);
        errs.Should().Be(0, "valid codeword has zero syndrome → no corrections needed");
    }

    // ── Robustness ────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_AllErrorsInParityRegion_DataPreserved()
    {
        // Errors only in parity symbols → data symbols must be correct after decode
        var data = new int[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var cw = BuildCodeword(data);

        // Corrupt 10 parity symbols
        for (int i = 12; i < 22; i++) cw[i] ^= 0x2A;

        int errs = ReedSolomon63.Decode(cw, Array.Empty<int>(), 0, false);
        errs.Should().BeGreaterThanOrEqualTo(0, "10 parity errors within correction capability");
        cw.Take(12).Should().BeEquivalentTo(data);
    }

    [Fact]
    public void Encode_ThenDecode_MultipleRandomDataSets_AllRoundTrip()
    {
        var rng = new Random(777);
        for (int trial = 0; trial < 20; trial++)
        {
            var data = new int[12];
            for (int i = 0; i < 12; i++) data[i] = rng.Next(0, 64);

            var cw = BuildCodeword(data);
            int errs = ReedSolomon63.Decode(cw, Array.Empty<int>(), 0, false);

            errs.Should().Be(0, $"trial {trial}: perfect codeword should decode with 0 corrections");
            cw.Take(12).Should().BeEquivalentTo(data, $"trial {trial}: data must round-trip");
        }
    }
}
