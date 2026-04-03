using System.Numerics;
using FluentAssertions;
using HamDigiSharp.Codecs;
using HamDigiSharp.Decoders.SuperFox;
using HamDigiSharp.Models;
using Xunit;

namespace HamDigiSharp.Tests.Decoders;

/// <summary>
/// Comprehensive tests for SuperFoxDecoder utility methods and integration.
/// Covers: NHash2, Grid4, DecodeBase38Call, Unpack28, Fwht128InPlace,
///         SfoxAna, TweakFreq2, BinToInt, SfoxUnpack, and the FT8 hound path.
/// </summary>
public class SuperFoxTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Guard / smoke
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Decode_TooShortInput_ReturnsEmpty()
    {
        var dec = new SuperFoxDecoder();
        dec.Decode(new float[100], 200, 3000, "000000").Should().BeEmpty();
    }

    /// <summary>
    /// Silence with a narrow 100 Hz search window so QpcSync iterates only twice.
    /// QpcSync immediately fails on silence (snrsync=0), making this fast.
    /// </summary>
    [Fact]
    public void Decode_NarrowBandSilence_DoesNotThrow()
    {
        var dec = new SuperFoxDecoder();
        // 45001 samples passes the guard (Nmax/4 = 45000) without full 15 s overhead.
        var silence = new float[45_001];
        var ex = Record.Exception(() => dec.Decode(silence, 1000, 1100, "000000"));
        ex.Should().BeNull("silence must not throw");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // NHash2 — Bob Jenkins nhash2 (21-bit CRC)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void NHash2_Deterministic_SameInputSameOutput()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        uint h1 = SuperFoxDecoder.NHash2(payload, payload.Length, 571);
        uint h2 = SuperFoxDecoder.NHash2(payload, payload.Length, 571);
        h1.Should().Be(h2);
    }

    [Fact]
    public void NHash2_DifferentInitVal_DifferentHash()
    {
        var payload = new byte[] { 10, 20, 30, 40, 50 };
        uint h571 = SuperFoxDecoder.NHash2(payload, payload.Length, 571);
        uint h999 = SuperFoxDecoder.NHash2(payload, payload.Length, 999);
        h571.Should().NotBe(h999, "different initval should yield different hash");
    }

    [Fact]
    public void NHash2_DifferentPayload_DifferentHash()
    {
        uint h1 = SuperFoxDecoder.NHash2(new byte[47], 47, 571);
        var p2 = new byte[47]; p2[0] = 1;
        uint h2 = SuperFoxDecoder.NHash2(p2, 47, 571);
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void NHash2_CrcRoundTrip_AllZeroPayload()
    {
        // Store CRC in bytes 47-49 of an all-zero 47-byte payload, verify the
        // stored value round-trips through the decoder's check formula.
        const uint mask21 = (1u << 21) - 1;
        var xin1 = new byte[50];
        uint crc = SuperFoxDecoder.NHash2(xin1, 47, 571) & mask21;
        xin1[47] = (byte)(crc / 16384);
        xin1[48] = (byte)((crc / 128) & 127);
        xin1[49] = (byte)(crc & 127);

        uint crcCheck  = SuperFoxDecoder.NHash2(xin1, 47, 571) & mask21;
        uint crcStored = 128u * 128u * xin1[47] + 128u * xin1[48] + xin1[49];
        crcCheck.Should().Be(crcStored, "CRC stored in bytes 47-49 must match computed CRC");
    }

    [Fact]
    public void NHash2_CrcRoundTrip_RandomPayload()
    {
        // Same round-trip check but with a non-trivial payload.
        const uint mask21 = (1u << 21) - 1;
        var xin1 = new byte[50];
        for (int i = 0; i < 47; i++) xin1[i] = (byte)(i * 13 + 7);
        uint crc = SuperFoxDecoder.NHash2(xin1, 47, 571) & mask21;
        xin1[47] = (byte)(crc / 16384);
        xin1[48] = (byte)((crc / 128) & 127);
        xin1[49] = (byte)(crc & 127);

        uint crcCheck  = SuperFoxDecoder.NHash2(xin1, 47, 571) & mask21;
        uint crcStored = 128u * 128u * xin1[47] + 128u * xin1[48] + xin1[49];
        crcCheck.Should().Be(crcStored);
    }

    [Fact]
    public void NHash2_ZeroLength_Returns_Constant()
    {
        // length=0 path returns c immediately; result is deterministic
        var buf = new byte[10];
        uint h = SuperFoxDecoder.NHash2(buf, 0, 571);
        SuperFoxDecoder.NHash2(buf, 0, 571).Should().Be(h);
    }

    [Fact]
    public void NHash2_LongPayload_Runs_MixLoop()
    {
        // Payload > 12 bytes exercises the NhashMix loop
        var big = new byte[48];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i + 1);
        uint h = SuperFoxDecoder.NHash2(big, big.Length, 571);
        // Just confirm it doesn't throw and is reproducible
        SuperFoxDecoder.NHash2(big, big.Length, 571).Should().Be(h);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Grid4 — 15-bit grid index ↔ Maidenhead string
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0,     "AA00")]
    [InlineData(19323, "KN23")]   // Prague-area grid: K=10, N=13, 2, 3
    [InlineData(17589, "JN89")]   // JN89: J=9, N=13, 8, 9 → 9*1800+13*100+80+9
    [InlineData(32399, "RR99")]   // Max valid grid: R=17, R=17, 9, 9
    [InlineData(101,   "AB01")]   // A=0, B=1, 0, 1 → 0*1800+1*100+0+1=101
    public void Grid4_KnownValues(int n, string expected)
    {
        SuperFoxDecoder.Grid4(n).Should().Be(expected);
    }

    [Fact]
    public void Grid4_NegativeInput_ReturnsQuestion()
    {
        SuperFoxDecoder.Grid4(-1).Should().Be("??");
    }

    [Fact]
    public void Grid4_Overflow_ReturnsQuestion()
    {
        // n=32400 → j1=18 which exceeds the valid range
        SuperFoxDecoder.Grid4(32400).Should().Be("??");
    }

    [Theory]
    [InlineData("AA00")]
    [InlineData("KN23")]
    [InlineData("JN89")]
    [InlineData("RR99")]
    [InlineData("FN41")]
    public void Grid4_RoundTrip_TryParseGrid4(string grid)
    {
        // TryParseGrid4 (public) and Grid4 (internal) must be inverse functions.
        MessagePack77.TryParseGrid4(grid, out int n).Should().BeTrue();
        SuperFoxDecoder.Grid4(n).Should().Be(grid);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DecodeBase38Call — 11-char base-38 decode
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DecodeBase38Call_AllZero_ReturnsEmptyString()
    {
        // n58 = 0 → 11 spaces after base-38 decode, trimmed to ""
        SuperFoxDecoder.DecodeBase38Call(0).Should().Be("");
    }

    [Theory]
    [InlineData("LZ2HVV")]
    [InlineData("OK1TE")]
    [InlineData("W1AW")]
    [InlineData("K1JT")]
    [InlineData("VK2ZD")]
    public void DecodeBase38Call_RoundTrip_ViaBase38Encode(string callsign)
    {
        // Encode the callsign to n58 using the same alphabet used by the decoder.
        const string C38 = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/";
        string call11 = (callsign + "           ")[..11];
        long n58 = 0;
        foreach (char ch in call11)
        {
            int idx = C38.IndexOf(ch);
            if (idx < 0) idx = 0;
            n58 = n58 * 38 + idx;
        }
        SuperFoxDecoder.DecodeBase38Call(n58).Should().Be(callsign);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Unpack28 — 28-bit callsign token decode
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0, "DE")]
    [InlineData(1, "QRZ")]
    [InlineData(2, "CQ")]
    public void Unpack28_SpecialTokens(int n28, string expected)
    {
        var dec = new SuperFoxDecoder();
        dec.Unpack28(n28, out string call).Should().BeTrue();
        call.Should().Be(expected);
    }

    [Theory]
    [InlineData(3,    "CQ_000")]
    [InlineData(1002, "CQ_999")]
    public void Unpack28_CqNumeric_Range(int n28, string expected)
    {
        var dec = new SuperFoxDecoder();
        dec.Unpack28(n28, out string call).Should().BeTrue();
        call.Should().Be(expected);
    }

    [Theory]
    [InlineData("OK1TE")]
    [InlineData("W1AW")]
    [InlineData("LZ2HV")]
    public void Unpack28_StandardCallsign_RoundTrip_ViaPack28(string callsign)
    {
        // MessagePack77.Pack28 → SuperFoxDecoder.Unpack28 must round-trip
        int n28 = MessagePack77.Pack28(callsign);
        var dec = new SuperFoxDecoder();
        bool ok = dec.Unpack28(n28, out string decoded);
        ok.Should().BeTrue($"Pack28 produced n28={n28} which Unpack28 must handle");
        decoded.Should().Be(callsign);
    }

    [Fact]
    public void Unpack28_Hash22Range_ReturnsPlaceholder()
    {
        // n28 in [NTokens28, NTokens28 + Max22_28) is a hash-22 callsign
        // → falls back to "<...>" per WSJT-X protocol spec
        const int NTokens28 = 2_063_592;
        var dec = new SuperFoxDecoder();
        dec.Unpack28(NTokens28, out string call).Should().BeTrue();
        call.Should().Be("<...>");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // BinToInt — bit extraction
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(new[] { true, false, true, false }, 0, 4, 10)] // 1010 = 10
    [InlineData(new[] { false, false, true }, 0, 3, 1)]
    [InlineData(new[] { true, true, true }, 0, 3, 7)]
    [InlineData(new[] { false, false, false, true, true, false }, 3, 6, 6)] // 110 = 6
    public void BinToInt_KnownValues(bool[] bits, int from, int to, int expected)
    {
        SuperFoxDecoder.BinToInt(bits, from, to).Should().Be(expected);
    }

    [Fact]
    public void BinToInt_EmptyRange_ReturnsZero()
    {
        SuperFoxDecoder.BinToInt(new bool[10], 5, 5).Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Fwht128InPlace — Fast Walsh-Hadamard Transform
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Fwht128InPlace_AllZero_RemainsZero()
    {
        var y = new float[128];
        SuperFoxDecoder.Fwht128InPlace(y);
        y.Should().AllSatisfy(v => v.Should().Be(0f));
    }

    [Fact]
    public void Fwht128InPlace_UnitVector_BecomeAllOnes()
    {
        var y = new float[128];
        y[0] = 1f;
        SuperFoxDecoder.Fwht128InPlace(y);
        y.Should().AllSatisfy(v => v.Should().BeApproximately(1f, 1e-5f));
    }

    [Fact]
    public void Fwht128InPlace_SelfInverse_ScaledBy128()
    {
        // FWHT applied twice equals N * identity (N=128)
        var original = new float[128];
        var rng = new Random(42);
        for (int i = 0; i < 128; i++) original[i] = (float)rng.NextDouble();

        var y = (float[])original.Clone();
        SuperFoxDecoder.Fwht128InPlace(y);
        SuperFoxDecoder.Fwht128InPlace(y);

        for (int i = 0; i < 128; i++)
            y[i].Should().BeApproximately(128f * original[i], 1e-3f,
                because: $"FWHT²[{i}] = 128 × original[{i}]");
    }

    [Fact]
    public void Fwht128InPlace_Linearity()
    {
        // FWHT(a + b) = FWHT(a) + FWHT(b)
        var rng = new Random(99);
        var a = new float[128]; var b = new float[128]; var ab = new float[128];
        for (int i = 0; i < 128; i++)
        {
            a[i]  = (float)rng.NextDouble();
            b[i]  = (float)rng.NextDouble();
            ab[i] = a[i] + b[i];
        }
        SuperFoxDecoder.Fwht128InPlace(a);
        SuperFoxDecoder.Fwht128InPlace(b);
        SuperFoxDecoder.Fwht128InPlace(ab);

        for (int i = 0; i < 128; i++)
            ab[i].Should().BeApproximately(a[i] + b[i], 1e-3f);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TweakFreq2 — frequency shift of analytic signal
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TweakFreq2_ZeroShift_IsIdentity()
    {
        var src = MakeSinusoid(12000, 1000.0, 12000);
        var dst = SuperFoxDecoder.TweakFreq2(src, src.Length, 12000.0, 0.0);

        for (int i = 0; i < src.Length; i++)
        {
            dst[i].Real.Should().BeApproximately(src[i].Real, 1e-9,
                because: $"zero shift real[{i}]");
            dst[i].Imaginary.Should().BeApproximately(src[i].Imaginary, 1e-9,
                because: $"zero shift imag[{i}]");
        }
    }

    [Fact]
    public void TweakFreq2_ForwardReverse_RoundTrip()
    {
        // Shift by +Δf then −Δf must cancel.
        double fShift = 200.0;
        var src  = MakeSinusoid(12000, 800.0, 12000);
        var fwd  = SuperFoxDecoder.TweakFreq2(src,  src.Length,  12000.0, +fShift);
        var back = SuperFoxDecoder.TweakFreq2(fwd,  fwd.Length,  12000.0, -fShift);

        for (int i = 0; i < src.Length; i++)
        {
            back[i].Real.Should().BeApproximately(src[i].Real, 1e-9,
                because: $"roundtrip real[{i}]");
            back[i].Imaginary.Should().BeApproximately(src[i].Imaginary, 1e-9,
                because: $"roundtrip imag[{i}]");
        }
    }

    [Fact]
    public void TweakFreq2_ShiftPreservesAmplitude()
    {
        // Frequency-shifting an analytic signal must not change sample amplitudes.
        var src = MakeSinusoid(12000, 1000.0, 12000);
        var dst = SuperFoxDecoder.TweakFreq2(src, src.Length, 12000.0, 300.0);

        for (int i = 0; i < src.Length; i++)
        {
            double ampSrc = Math.Sqrt(src[i].Real * src[i].Real + src[i].Imaginary * src[i].Imaginary);
            double ampDst = Math.Sqrt(dst[i].Real * dst[i].Real + dst[i].Imaginary * dst[i].Imaginary);
            ampDst.Should().BeApproximately(ampSrc, 1e-9, because: $"amplitude preserved at [{i}]");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SfoxAna — analytic signal (Hilbert transform)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SfoxAna_OutputLength_Is_Nmax()
    {
        // SfoxAna always returns exactly 180000 samples (15 s × 12000 Hz)
        const int Nmax = 180_000;
        var dd = new double[Nmax];
        var c = SuperFoxDecoder.SfoxAna(dd);
        c.Length.Should().Be(Nmax);
    }

    [Fact]
    public void SfoxAna_Silence_ProducesZeroSignal()
    {
        const int Nmax = 180_000;
        var c = SuperFoxDecoder.SfoxAna(new double[Nmax]);
        double energy = c.Sum(z => z.Real * z.Real + z.Imaginary * z.Imaginary);
        energy.Should().BeApproximately(0, 1e-6, "silence in → silence out");
    }

    [Fact]
    public void SfoxAna_Sinusoid_HasApproximatelyConstantEnvelope()
    {
        // The Hilbert transform of a single sinusoid produces an analytic signal
        // with nearly constant amplitude (deviations only near the edges).
        const int Nmax = 180_000;
        double freq = 1000.0;
        var dd = new double[Nmax];
        for (int i = 0; i < Nmax; i++) dd[i] = Math.Sin(2 * Math.PI * freq * i / 12000.0);

        var c = SuperFoxDecoder.SfoxAna(dd);

        // Skip first and last 5% (edge effects)
        int edge = Nmax / 20;
        double maxDev = 0;
        double refAmp = Math.Sqrt(c[Nmax / 2].Real * c[Nmax / 2].Real
                                + c[Nmax / 2].Imaginary * c[Nmax / 2].Imaginary);

        for (int i = edge; i < Nmax - edge; i++)
        {
            double amp = Math.Sqrt(c[i].Real * c[i].Real + c[i].Imaginary * c[i].Imaginary);
            maxDev = Math.Max(maxDev, Math.Abs(amp - refAmp));
        }
        maxDev.Should().BeLessThan(refAmp * 0.1,
            "analytic signal of a sinusoid has approximately constant amplitude");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SfoxUnpack — parse 50 decoded 7-bit symbols into message strings
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SfoxUnpack_AllZero_DoesNotThrow()
    {
        var dec = new SuperFoxDecoder();
        var ex = Record.Exception(() => dec.SfoxUnpack(new byte[50]));
        ex.Should().BeNull("all-zero xin1 must not throw");
    }

    [Theory]
    [InlineData("LZ2HVV", "KN23")]
    [InlineData("OK1TE",  "JN89")]
    [InlineData("K1JT",   "FN41")]
    [InlineData("W1AW",   "FN31")]
    public void SfoxUnpack_CqMessage_IsCorrectlyParsed(string callsign, string grid)
    {
        byte[] xin1 = MakeCqXin1(callsign, grid);
        var dec  = new SuperFoxDecoder();
        var msgs = dec.SfoxUnpack(xin1);

        msgs.Should().HaveCount(1, $"CQ message for {callsign} {grid}");
        msgs[0].Should().Be($"CQ {callsign} {grid}");
    }

    [Fact]
    public void SfoxUnpack_ShortBuffer_DoesNotThrow()
    {
        // Passing fewer than 50 bytes must still parse (remaining bits are zero)
        var dec = new SuperFoxDecoder();
        var ex = Record.Exception(() => dec.SfoxUnpack(new byte[10]));
        ex.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FT8 Hound path — functional coverage via Ft8Decoder
    // The FT8 hound path in SuperFoxDecoder uses the same algorithm as Ft8Decoder.
    // Round-trip correctness is verified in EncoderTests.Ft8_EncodeDecodeRoundTrip.
    // Here we verify that the Mode property is correctly set to SuperFox.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Mode_Property_IsSetToSuperFox()
    {
        var dec = new SuperFoxDecoder();
        dec.Mode.Should().Be(DigitalMode.SuperFox);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build a 50-byte xin1 packet encoding "CQ &lt;callsign&gt; &lt;grid&gt;" (i3=3).
    /// The bytes represent 47 information symbols (7 bits each, MSB first)
    /// plus 3 bytes reserved for the QPC CRC (not validated by SfoxUnpack).
    /// </summary>
    private static byte[] MakeCqXin1(string callsign, string grid)
    {
        const string C38 = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/";

        // 58-bit base-38 encoding of callsign (11 chars, right-padded with spaces)
        string call11 = (callsign.ToUpperInvariant() + "           ")[..11];
        long n58 = 0;
        foreach (char ch in call11)
        {
            int idx = C38.IndexOf(ch);
            if (idx < 0) idx = 0;
            n58 = n58 * 38 + idx;
        }

        // 15-bit grid encoding
        MessagePack77.TryParseGrid4(grid, out int n15);

        // Build 329-bit payload (47 × 7 bits)
        var bits = new bool[350];
        for (int i = 0; i < 58; i++) bits[i] = ((n58 >> (57 - i)) & 1L) != 0;
        for (int i = 0; i < 15; i++) bits[58 + i] = ((n15 >> (14 - i)) & 1) != 0;
        // bits[73..325] = 0 (hound callsign area, unused for i3=3)
        // i3 = 3 (binary 011) in bits[326..328]
        bits[326] = false;
        bits[327] = true;
        bits[328] = true;

        // Pack 7 bits per byte (MSB first), 50 bytes total
        var xin1 = new byte[50];
        int pos = 0;
        for (int i = 0; i < 50; i++)
        {
            int val = 0;
            for (int b = 6; b >= 0 && pos < bits.Length; b--)
                val |= (bits[pos++] ? 1 : 0) << b;
            xin1[i] = (byte)val;
        }
        return xin1;
    }

    /// <summary>Complex sinusoid at the given frequency, useful for TweakFreq2 tests.</summary>
    private static Complex[] MakeSinusoid(int nSamples, double freqHz, double sampleRate)
    {
        var c = new Complex[nSamples];
        double dphi = 2.0 * Math.PI * freqHz / sampleRate;
        for (int i = 0; i < nSamples; i++)
            c[i] = new Complex(Math.Cos(i * dphi), Math.Sin(i * dphi));
        return c;
    }
}
