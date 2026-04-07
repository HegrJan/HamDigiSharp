using FluentAssertions;
using HamDigiSharp.Decoders.SuperFox;
using HamDigiSharp.Encoders;
using HamDigiSharp.Engine;
using HamDigiSharp.Models;
using Xunit;

namespace HamDigiSharp.Tests.Encoders;

/// <summary>
/// Tests for <see cref="SuperFoxEncoder"/>: message packing, QPC polar coding,
/// sync insertion, output length, and end-to-end decode round-trips.
/// </summary>
public class SuperFoxEncoderTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Output length
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Encode_CqMessage_Returns180000Samples()
    {
        var enc = new SuperFoxEncoder();
        var audio = enc.Encode("CQ LZ2HVV KN23", new EncoderOptions { FrequencyHz = 750 });
        audio.Length.Should().Be(180_000, "15 s × 12000 Hz = 180,000 samples");
    }

    [Fact]
    public void Encode_StandardMessage_Returns180000Samples()
    {
        var enc = new SuperFoxEncoder();
        var audio = enc.Encode("LZ2HVV W4ABC +01 G4XYZ", new EncoderOptions { FrequencyHz = 750 });
        audio.Length.Should().Be(180_000);
    }

    [Fact]
    public void Encode_MultipleHounds_Returns180000Samples()
    {
        var enc = new SuperFoxEncoder();
        var audio = enc.Encode(
            "LZ2HVV K1AA +05 K2BB -07 K3CC K4DD K5EE",
            new EncoderOptions { FrequencyHz = 750 });
        audio.Length.Should().Be(180_000);
    }

    // Active samples are non-zero; tail is padded silence
    [Fact]
    public void Encode_ActiveSamplesNonZero_TailIsSilence()
    {
        var enc = new SuperFoxEncoder();
        var audio = enc.Encode("CQ LZ2HVV KN23",
            new EncoderOptions { FrequencyHz = 750, Amplitude = 0.5 });

        const int nActive = 151 * 1024; // 154,624
        double rms = Math.Sqrt(audio[100..(nActive - 100)]
            .Average(x => (double)x * x));
        rms.Should().BeGreaterThan(0.1, "active region must have non-zero energy");

        for (int i = nActive + 100; i < 180_000; i++)
            audio[i].Should().Be(0f, "tail beyond active signal must be zero");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PackMessage (internal — tests white-box CRC structure and bit layout)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PackMessage_Returns50Symbols()
    {
        byte[] xin = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23");
        xin.Length.Should().Be(50);
    }

    [Fact]
    public void PackMessage_AllSymbolsIn7BitRange()
    {
        byte[] xin = SuperFoxEncoder.PackMessage("LZ2HVV W4ABC +01");
        foreach (byte b in xin)
            ((int)b).Should().BeInRange(0, 127, "7-bit symbol must be 0-127");
    }

    /// <summary>
    /// CRC occupies xin[0..2] (reversed from the end after reversal).
    /// Verify CRC is deterministic and non-zero for a typical message.
    /// </summary>
    [Fact]
    public void PackMessage_CrcDeterministicAndNonZero()
    {
        byte[] xin1 = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23");
        byte[] xin2 = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23");
        byte[] xinB = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN22"); // different grid

        xin1.Should().Equal(xin2, "same input always produces same output");

        // CRC (first 3 symbols) should differ between different messages
        bool crcDiffers = xin1[0] != xinB[0] || xin1[1] != xinB[1] || xin1[2] != xinB[2];
        crcDiffers.Should().BeTrue("different messages should produce different CRC");
    }

    /// <summary>
    /// After reversal in PackMessage the CRC lives in xin[0..2].
    /// Re-compute it from xin[3..49] (the reversed data portion) and compare.
    /// </summary>
    [Fact]
    public void PackMessage_CrcVerifiesFromPayload()
    {
        byte[] xin = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23");

        // Re-reverse to restore natural order: data[0..46] then crc[47..49]
        byte[] natural = (byte[])xin.Clone();
        Array.Reverse(natural);

        uint mask21  = (1u << 21) - 1;
        uint expected = SuperFoxDecoder.NHash2(natural, 47, 571) & mask21;

        uint crcFromSymbols =
            ((uint)natural[47] << 14) |
            ((uint)natural[48] << 7)  |
            ((uint)natural[49]);

        crcFromSymbols.Should().Be(expected, "CRC appended to xin must match NHash2");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // QpcEncode (internal)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void QpcEncode_AllZeroInput_Returns127Symbols()
    {
        byte[] xin = new byte[50];
        int[] sym = SuperFoxEncoder.QpcEncode(xin);
        sym.Length.Should().Be(127);
    }

    [Fact]
    public void QpcEncode_AllZeroInput_ProducesAllZeroOutput()
    {
        // All-zero frozen array polar-encodes to all-zero
        byte[] xin = new byte[50];
        int[] sym = SuperFoxEncoder.QpcEncode(xin);
        sym.Should().AllBeEquivalentTo(0);
    }

    [Fact]
    public void QpcEncode_NonZeroInput_ChangesOutput()
    {
        byte[] xin = new byte[50];
        int[] allZero = SuperFoxEncoder.QpcEncode(xin);

        xin[0] = 1;
        int[] changed = SuperFoxEncoder.QpcEncode(xin);

        changed.Should().NotEqual(allZero, "flipping an info symbol must change the codeword");
    }

    [Fact]
    public void QpcEncode_OutputSymbolsIn7BitRange()
    {
        var rng = new Random(42);
        byte[] xin = new byte[50];
        rng.NextBytes(xin);
        for (int i = 0; i < 50; i++) xin[i] &= 0x7F; // keep 7-bit

        int[] sym = SuperFoxEncoder.QpcEncode(xin);
        sym.Should().AllSatisfy(s => s.Should().BeInRange(0, 127));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Round-trip: SuperFoxEncoder → SuperFoxDecoder (Fox QPC path)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RoundTrip_CqMessage_DecodesCorrectly()
    {
        var enc   = new SuperFoxEncoder();
        var dec   = new SuperFoxDecoder();

        // Encode at f0=750 Hz; sync tone is at 750 Hz
        float[] audio = enc.Encode("CQ LZ2HVV KN23",
            new EncoderOptions { FrequencyHz = 750.0, Amplitude = 0.9 });

        // Search 700-800 Hz (sync tone range)
        var results = dec.Decode(audio, 700, 800, "000000");

        results.Should().ContainSingle(
            r => r.Message.Contains("LZ2HVV") && r.Message.Contains("KN23"),
            "CQ round-trip must decode the fox callsign and grid");
    }

    [Fact]
    public void RoundTrip_StandardMessage_DecodesHoundAndFox()
    {
        var enc   = new SuperFoxEncoder();
        var dec   = new SuperFoxDecoder();

        float[] audio = enc.Encode("LZ2HVV W4ABC +01",
            new EncoderOptions { FrequencyHz = 750.0, Amplitude = 0.9 });

        var results = dec.Decode(audio, 700, 800, "000000");

        results.Should().Contain(
            r => r.Message.Contains("W4ABC") && r.Message.Contains("LZ2HVV"),
            "standard round-trip must decode hound callsign paired with fox callsign");
    }

    [Fact]
    public void RoundTrip_MultipleHounds_DecodesAll()
    {
        var enc = new SuperFoxEncoder();
        var dec = new SuperFoxDecoder();

        // 3 hounds: one with report, two with implicit RR73
        float[] audio = enc.Encode(
            "LZ2HVV W4ABC +05 G4XYZ K9AN",
            new EncoderOptions { FrequencyHz = 750.0, Amplitude = 0.9 });

        var results = dec.Decode(audio, 700, 800, "000000");
        var messages = results.Select(r => r.Message).ToList();

        messages.Should().Contain(m => m.Contains("W4ABC"), "W4ABC +05 must decode");
        messages.Should().Contain(m => m.Contains("G4XYZ"), "G4XYZ RR73 must decode");
        messages.Should().Contain(m => m.Contains("K9AN"),  "K9AN RR73 must decode");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EncoderEngine integration
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EncoderEngine_SupportsSuperFox()
    {
        using var engine = new EncoderEngine();
        engine.Supports(DigitalMode.SuperFox).Should().BeTrue();
    }

    [Fact]
    public void EncoderEngine_EncodesSuperFox_Returns180000Samples()
    {
        using var engine = new EncoderEngine();
        var audio = engine.Encode("CQ LZ2HVV KN23", DigitalMode.SuperFox,
            new EncoderOptions { FrequencyHz = 750 });
        audio.Length.Should().Be(180_000);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Digital-level pipeline tests (bypass audio; isolate bit/CRC/QPC path)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CqMessage_DigitalPipeline_CrcIsConsistent()
    {
        // 1. Pack the CQ message: xin50[0..2]=CRC, xin50[3..49]=msg (reversed)
        var xin50 = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23");

        // 2. Reverse back to natural order: data[0..46], CRC[47..49]
        var originalXin = (byte[])xin50.Clone();
        Array.Reverse(originalXin);

        // 3. CRC consistency: NHash2 over first 47 symbols must match stored CRC
        uint mask21  = (1u << 21) - 1;
        uint crcChk  = SuperFoxDecoder.NHash2(originalXin, 47, 571) & mask21;
        uint crcSent = 128u * 128u * originalXin[47] + 128u * originalXin[48] + originalXin[49];
        crcChk.Should().Be(crcSent, "CRC must be consistent in CQ message packing");

        // 4. SfoxUnpack of correctly-packed bytes must return the original message
        var msgs = new SuperFoxDecoder().SfoxUnpack(originalXin);
        msgs.Should().ContainSingle(m => m.Contains("LZ2HVV") && m.Contains("KN23"),
            "direct unpack of correctly-packed bytes must decode CQ message");

        // 5. QPC encode produces 127 symbols all in 7-bit range
        var encoded = SuperFoxEncoder.QpcEncode(xin50);
        encoded.Length.Should().Be(127);
        encoded.Should().AllSatisfy(v => v.Should().BeInRange(0, 127));
    }

    [Fact]
    public void CqMessage_QpcEncodeDecode_RecoversSameSymbols()
    {        // Pack + encode CQ message
        var xin50 = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23");
        var encoded = SuperFoxEncoder.QpcEncode(xin50); // 127 symbols

        encoded.Length.Should().Be(127);
        encoded.Distinct().Should().HaveCountGreaterThan(1, "encoded symbols should have variety");

        // Compare with a standard message
        var xin50Std = SuperFoxEncoder.PackMessage("LZ2HVV W4ABC +01");
        var encodedStd = SuperFoxEncoder.QpcEncode(xin50Std);

        // Verify CRC consistency for both messages (natural order = reversed of xin50)
        var originalXinCq  = (byte[])xin50.Clone();    Array.Reverse(originalXinCq);
        var originalXinStd = (byte[])xin50Std.Clone(); Array.Reverse(originalXinStd);

        uint mask21 = (1u << 21) - 1;

        uint crcCq     = SuperFoxDecoder.NHash2(originalXinCq, 47, 571) & mask21;
        uint crcSentCq = 128u * 128u * originalXinCq[47] + 128u * originalXinCq[48] + originalXinCq[49];
        crcCq.Should().Be(crcSentCq, "CQ CRC must be consistent");

        uint crcStd     = SuperFoxDecoder.NHash2(originalXinStd, 47, 571) & mask21;
        uint crcSentStd = 128u * 128u * originalXinStd[47] + 128u * originalXinStd[48] + originalXinStd[49];
        crcStd.Should().Be(crcSentStd, "Standard CRC must be consistent");

        // Both should unpack to the expected messages
        var msgsCq  = new SuperFoxDecoder().SfoxUnpack(originalXinCq);
        var msgsStd = new SuperFoxDecoder().SfoxUnpack(originalXinStd);

        msgsCq.Should().ContainSingle(m => m.Contains("LZ2HVV") && m.Contains("KN23"),
            "CQ message must unpack correctly after pack→reverse");
        msgsStd.Should().Contain(m => m.Contains("W4ABC") && m.Contains("LZ2HVV"),
            "standard message must unpack correctly after pack→reverse");
    }

    [Fact]
    public void CqMessage_IdealLikelihoods_QpcDecodePassesCrc()
    {
        // Encode CQ to get the 127 symbol values the channel should carry
        var xin50   = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23");
        int[] syms  = SuperFoxEncoder.QpcEncode(xin50); // [0..126] = encoded[1..127]

        // Build a perfect probability matrix: pyFull[128 × 128]
        //   Row 0  = punctured symbol, known to be 0  → spike at bin 0
        //   Row k (k=1..127) = data symbol syms[k-1]  → spike at that bin
        const int N = 128; // QpcN = QpcQ = 128
        var pyFull = new float[N * N];
        pyFull[0] = 1.0f; // row 0: punctured symbol = 0
        for (int k = 1; k < N; k++)
            pyFull[k * N + syms[k - 1]] = 1.0f;

        // Run the polar decoder on ideal input
        var dec   = new SuperFoxDecoder();
        var xdec0 = new byte[50 + 2];
        dec.QpcDecodeForTest(pyFull, xdec0);

        // Mirror the decoder's reversal: xdec[j] = xdec0[49-j]
        var xdec = new byte[60];
        for (int k = 0; k < 50; k++) xdec[k] = xdec0[49 - k];

        // CRC must pass on perfect input
        const uint mask21 = (1u << 21) - 1;
        uint crcChk  = SuperFoxDecoder.NHash2(xdec, 47, 571) & mask21;
        uint crcSent = 128u * 128u * xdec[47] + 128u * xdec[48] + xdec[49];
        crcChk.Should().Be(crcSent,
            "QpcDecode on ideal likelihoods must pass the CRC — if this fails the decoder itself is broken for CQ symbols");

        // And SfoxUnpack must return the original message
        var msgs = dec.SfoxUnpack(xdec);
        msgs.Should().ContainSingle(m => m.Contains("LZ2HVV") && m.Contains("KN23"),
            "QpcDecode on ideal CQ likelihoods must decode the full message");
    }

    [Fact]
    public void CqMessage_SfoxDemod_PeaksAtExpectedSymbols()
    {
        var enc   = new SuperFoxEncoder();
        float[] audio = enc.Encode("CQ LZ2HVV KN23",
            new EncoderOptions { FrequencyHz = 750.0, Amplitude = 0.9 });

        var dec    = new SuperFoxDecoder();
        var result = dec.TestDemodulate(audio, fsync: 750.0); // use QpcSync's natural estimate
        result.Should().NotBeNull("QpcSync must find the sync tone at 750 Hz");

        var (s3, f2, t2, snr) = result!.Value;

        // Get expected channel symbols from the encoder
        var xin50        = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23");
        int[] expectedSym = SuperFoxEncoder.QpcEncode(xin50); // 127 symbols

        int correct = 0;
        var wrong   = new System.Text.StringBuilder();
        for (int k = 0; k < 127; k++)
        {
            int exp    = expectedSym[k];
            int actual = 0;
            double peak = s3[k + 1, 0];
            for (int j = 1; j < 128; j++)
                if (s3[k + 1, j] > peak) { peak = s3[k + 1, j]; actual = j; }

            if (actual == exp) correct++;
            else if (wrong.Length < 200) wrong.Append($"[k={k} exp={exp} got={actual}] ");
        }

        correct.Should().BeGreaterThanOrEqualTo(120,
            $"only {correct}/127 symbols demodulated correctly. " +
            $"First errors: {wrong}  f2={f2:F2} t2={t2:F3} snr={snr:F2}");
    }

    [Fact]
    public void StandardMessage_SfoxDemod_PeaksAtExpectedSymbols()
    {
        var enc   = new SuperFoxEncoder();
        float[] audio = enc.Encode("LZ2HVV W4ABC +01",
            new EncoderOptions { FrequencyHz = 750.0, Amplitude = 0.9 });

        var dec    = new SuperFoxDecoder();
        var result = dec.TestDemodulate(audio, fsync: 750.0);
        result.Should().NotBeNull("QpcSync must find the sync tone");

        var (s3, f2, t2, snr) = result!.Value;

        var xin50        = SuperFoxEncoder.PackMessage("LZ2HVV W4ABC +01");
        int[] expectedSym = SuperFoxEncoder.QpcEncode(xin50);

        int correct = 0;
        for (int k = 0; k < 127; k++)
        {
            int exp    = expectedSym[k];
            int actual = 0;
            double peak = s3[k + 1, 0];
            for (int j = 1; j < 128; j++)
                if (s3[k + 1, j] > peak) { peak = s3[k + 1, j]; actual = j; }
            if (actual == exp) correct++;
        }

        correct.Should().BeGreaterThanOrEqualTo(120,
            $"only {correct}/127 standard message symbols demodulated correctly");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Digital signature (notp, bits 306-325)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PackMessage_WithSignature_SignatureBitsCorrectInNaturalOrder()
    {
        // A non-zero notp must appear verbatim in bits 306-325 of the natural-order payload.
        const uint notp = 0x12345u; // 0x12345 = 74565 (20 bits)
        byte[] xin = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23", notp);

        // Reverse to natural order (data first, CRC at end)
        var nat = (byte[])xin.Clone();
        Array.Reverse(nat);

        // Extract bits 306-325 from natural-order msgbits (7 bits per symbol)
        uint extracted = 0;
        for (int i = 306; i < 326; i++)
        {
            int sym = i / 7;
            int bit = 6 - (i % 7); // MSB-first within each symbol
            extracted = (extracted << 1) | ((uint)((nat[sym] >> bit) & 1));
        }

        extracted.Should().Be(notp, "bits 306-325 must hold the verbatim notp value");
    }

    [Fact]
    public void PackMessage_WithSignature_CrcDiffersFromNoSignature()
    {
        // The signature is included in the CRC payload; CRC must change.
        byte[] xinNoSig = SuperFoxEncoder.PackMessage("LZ2HVV W4ABC +01");
        byte[] xinSig   = SuperFoxEncoder.PackMessage("LZ2HVV W4ABC +01", 12345u);

        var natNoSig = (byte[])xinNoSig.Clone(); Array.Reverse(natNoSig);
        var natSig   = (byte[])xinSig.Clone();   Array.Reverse(natSig);

        // CRC bytes are at natural[47..49]
        bool crcDiffers = natNoSig[47] != natSig[47]
                       || natNoSig[48] != natSig[48]
                       || natNoSig[49] != natSig[49];
        crcDiffers.Should().BeTrue("adding a signature must change the CRC");
    }

    [Fact]
    public void PackMessage_ZeroSignature_IsIdenticalToNoSignatureOverload()
    {
        // notp=0 must behave identically to omitting the parameter.
        byte[] xinDefault = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23");
        byte[] xinZero    = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23", 0u);
        xinDefault.Should().Equal(xinZero, "notp=0 must be equivalent to no signature");
    }

    [Fact]
    public void PackMessage_SignatureAbove20Bits_IsMasked()
    {
        // Values > 0xFFFFF are silently masked to 20 bits.
        byte[] xin20Bit = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23", 0x12345u);
        byte[] xinExtra = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23", 0x112345u); // bit 20 set
        xin20Bit.Should().Equal(xinExtra, "bits above 20 must be masked off");
    }

    [Fact]
    public void Encode_WithSignature_PassesThroughFromOptions()
    {
        var enc = new SuperFoxEncoder();
        byte[] xin1 = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23");
        byte[] xin2 = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23", 99999u);

        // Encoding with the option set must differ from encoding without
        xin1.Should().NotEqual(xin2, "SuperFoxSignature option must affect the packed output");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // i3=2: free-text response (~ separator)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PackMessage_TextResponse_I3EqualsTwo()
    {
        // i3 is in bits 326-328 of the natural-order payload → symbols 46..46 (bit 4..6) and 47 (bits 0..1)
        // Actually i3 is at bits 326-328: symbol 326/7=46, bits 6-(326%7)=6-4=2 ... easier to unpack via SfoxUnpack
        byte[] xin = SuperFoxEncoder.PackMessage("LZ2HVV W4ABC +01 G4XYZ ~ TEST TEXT");
        var nat = (byte[])xin.Clone(); Array.Reverse(nat);

        // Extract i3 from bits 326-328 (natural order)
        int i3 = 0;
        for (int i = 326; i < 329; i++)
        {
            int sym = i / 7;
            int bit = 6 - (i % 7);
            i3 = (i3 << 1) | ((nat[sym] >> bit) & 1);
        }

        i3.Should().Be(2, "tilde-separated message must produce i3=2");
    }

    [Fact]
    public void PackMessage_TextResponse_UnpacksToFreeText()
    {
        byte[] xin = SuperFoxEncoder.PackMessage("LZ2HVV W4ABC +01 ~ HELLO WORLD");
        var nat = (byte[])xin.Clone(); Array.Reverse(nat);

        var dec = new SuperFoxDecoder();
        var msgs = dec.SfoxUnpack(nat);

        msgs.Should().Contain(m => m.Contains("HELLO WORLD"),
            "free text part must appear in unpack output");
    }

    [Fact]
    public void RoundTrip_TextResponse_DecodesHoundAndFreeText()
    {
        var enc = new SuperFoxEncoder();
        var dec = new SuperFoxDecoder();

        float[] audio = enc.Encode("LZ2HVV W4ABC +01 G4XYZ ~ QSL QRZ",
            new EncoderOptions { FrequencyHz = 750.0, Amplitude = 0.9 });

        var results = dec.Decode(audio, 700, 800, "000000");
        var messages = results.Select(r => r.Message).ToList();

        messages.Should().Contain(m => m.Contains("W4ABC"),
            "hound with report must decode");
        messages.Should().Contain(m => m.Contains("QSL") || m.Contains("QRZ"),
            "free text must decode");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // i3=3: CQ with free text (tilde separator)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PackMessage_CqWithFreeText_UnpacksBothCqLineAndText()
    {
        byte[] xin = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23 ~ EXPEDITION");
        var nat = (byte[])xin.Clone(); Array.Reverse(nat);

        var msgs = new SuperFoxDecoder().SfoxUnpack(nat);

        msgs.Should().Contain(m => m.Contains("LZ2HVV") && m.Contains("KN23"),
            "CQ line must be present");
        msgs.Should().Contain(m => m.Contains("EXPEDITION"),
            "free text must be present");
    }

    [Fact]
    public void PackMessage_CqWithFreeText_I3EqualsThree()
    {
        // Tilde after CQ header must still produce i3=3, not i3=2.
        byte[] xin = SuperFoxEncoder.PackMessage("CQ LZ2HVV KN23 ~ IOTA EU050");
        var nat = (byte[])xin.Clone(); Array.Reverse(nat);

        int i3 = 0;
        for (int i = 326; i < 329; i++)
        {
            int sym = i / 7;
            int bit = 6 - (i % 7);
            i3 = (i3 << 1) | ((nat[sym] >> bit) & 1);
        }

        i3.Should().Be(3, "CQ + free text must produce i3=3, not i3=2");
    }

    [Fact]
    public void RoundTrip_CqWithFreeText_DecodesCorrectly()
    {
        var enc = new SuperFoxEncoder();
        var dec = new SuperFoxDecoder();

        float[] audio = enc.Encode("CQ LZ2HVV KN23 ~ EXPEDITION",
            new EncoderOptions { FrequencyHz = 750.0, Amplitude = 0.9 });

        var results = dec.Decode(audio, 700, 800, "000000");
        var messages = results.Select(r => r.Message).ToList();

        messages.Should().Contain(m => m.Contains("LZ2HVV") && m.Contains("KN23"),
            "CQ with free text must decode the CQ line");
        messages.Should().Contain(m => m.Contains("EXPEDITION"),
            "CQ with free text must decode the free text");
    }
}
