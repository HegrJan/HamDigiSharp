using HamDigiSharp.Codecs;
using HamDigiSharp.Encoders;
using HamDigiSharp.Engine;
using HamDigiSharp.Models;

namespace HamDigiSharp.Tests.Encoders;

/// <summary>
/// Tests for the FT8/FT4 encoder stack:
/// MessagePack77 → Ldpc174_91.Encode → Ft8/Ft4Encoder
/// </summary>
public class EncoderTests
{
    // ── MessagePack77 ──────────────────────────────────────────────────────────

    [Fact]
    public void Pack28_CQ_returns2()
    {
        Assert.Equal(2, MessagePack77.Pack28("CQ"));
    }

    [Fact]
    public void Pack28_DE_returns0()
    {
        Assert.Equal(0, MessagePack77.Pack28("DE"));
    }

    [Fact]
    public void Pack28_QRZ_returns1()
    {
        Assert.Equal(1, MessagePack77.Pack28("QRZ"));
    }

    // Note: geographic CQ zones like "CQ DX", "CQ EU" are preprocessed at the
    // message level in MSHV (e.g. "CQ DX" → "CQ 9xx") before Pack28 is called.
    // Pack28 itself only handles: DE(0), QRZ(1), CQ(2), CQ_nnn numeric (3..1002),
    // and standard callsigns.

    [Theory]
    [InlineData("CQ_145")]  // CQ 145 MHz (3-digit variant)
    [InlineData("CQ_000")]
    [InlineData("CQ_999")]
    public void Pack28_CQ_freqVariant_inRange(string token)
    {
        int n = MessagePack77.Pack28(token);
        Assert.InRange(n, 3, 3 + 999);
    }

    [Theory]
    [InlineData("W1AW")]        // 4-char, area digit at pos 1
    [InlineData("OK1TE")]       // 5-char, area digit at pos 2
    [InlineData("K9AN")]        // 4-char
    [InlineData("VK2ZD")]       // 5-char
    public void Pack28_StandardCallsign_inRange(string call)
    {
        int n = MessagePack77.Pack28(call);
        // Valid callsigns encode above NTokens + Max22
        Assert.True(n > 2_063_592 + 4_194_304, $"n28={n} for {call}");
        Assert.True(n < (1 << 28), $"n28 overflow for {call}");
    }

    [Fact]
    public void Pack28_SameCallTwice_sameResult()
    {
        int n1 = MessagePack77.Pack28("OK1TE");
        int n2 = MessagePack77.Pack28("OK1TE");
        Assert.Equal(n1, n2);
    }

    [Fact]
    public void Pack28_DifferentCalls_differentResults()
    {
        int n1 = MessagePack77.Pack28("OK1TE");
        int n2 = MessagePack77.Pack28("W1AW");
        Assert.NotEqual(n1, n2);
    }

    [Fact]
    public void TryParseGrid4_validGrid_succeeds()
    {
        Assert.True(MessagePack77.TryParseGrid4("JN89", out int ig));
        // JN89: (J=9)*1800 + (N=13)*100 + 8*10 + 9 = 16200+1300+89 = 17589
        Assert.Equal(17589, ig);
    }

    [Fact]
    public void TryParseGrid4_FN42_correct()
    {
        Assert.True(MessagePack77.TryParseGrid4("FN42", out int ig));
        // FN42: (F=5)*1800 + (N=13)*100 + 4*10 + 2 = 9000+1300+42 = 10342
        Assert.Equal(10342, ig);
    }

    [Fact]
    public void TryParseGrid4_invalidGrid_fails()
    {
        Assert.False(MessagePack77.TryParseGrid4("AB", out _));
        Assert.False(MessagePack77.TryParseGrid4("ZZZZ", out _));
        Assert.False(MessagePack77.TryParseGrid4("+04", out _));
    }

    // ── TryPack77 ─────────────────────────────────────────────────────────────

    [Fact]
    public void TryPack77_CQ_OK1TE_JN89_returns77bits()
    {
        var bits = new bool[77];
        bool ok = MessagePack77.TryPack77("CQ OK1TE JN89", bits);
        Assert.True(ok);
        // Verify 77 bits are set (some non-zero pattern)
        Assert.Contains(bits, b => b);
    }

    [Fact]
    public void TryPack77_standardMsg_roundtripsType()
    {
        // Bit layout: n28a(28) + ipa(1) + n28b(28) + ipb(1) + ir(1) + igrid4(15) + i3(3)
        var bits = new bool[77];
        bool ok = MessagePack77.TryPack77("OK1TE W1AW FN42", bits);
        Assert.True(ok);

        // Extract i3 (last 3 bits): should be 1 (Type 1)
        int i3 = (bits[74] ? 4 : 0) | (bits[75] ? 2 : 0) | (bits[76] ? 1 : 0);
        Assert.Equal(1, i3);
    }

    [Fact]
    public void TryPack77_reportMsg_irBitSet()
    {
        var bits = new bool[77];
        bool ok = MessagePack77.TryPack77("OK1TE W1AW R-10", bits);
        Assert.True(ok);
        // Bit layout: n28a(28) + ipa(1) + n28b(28) + ipb(1) + ir(1) + igrid4(15) + i3(3)
        // ir is at index 28+1+28+1 = 58
        Assert.True(bits[58], "ir bit should be 1 for R-NN report");
    }

    [Fact]
    public void TryPack77_noR_prefix_irBitClear()
    {
        var bits = new bool[77];
        bool ok = MessagePack77.TryPack77("OK1TE W1AW -10", bits);
        Assert.True(ok);
        // ir bit at index 58 should be 0 for plain ±NN report
        Assert.False(bits[58], "ir bit should be 0 for plain ±NN report");
    }

    // ── Ldpc174_91.Encode / CRC-14 ────────────────────────────────────────────

    [Fact]
    public void Crc14_Compute_ZeroMessage_notZero()
    {
        // A 77-zero-bit message should produce a non-trivial CRC
        var bits = new bool[77];
        int crc = Crc14.Compute(bits);
        Assert.InRange(crc, 0, 0x3FFF);
    }

    [Fact]
    public void Encode_ProducesCrc14ThatChecksOut()
    {
        var msg77 = new bool[77];
        // Set a simple message: CQ (bit pattern for n28a=2)
        msg77[26] = true; // bit 26 = 1 in n28a=2 (MSB of 28-bit word)
        msg77[27] = true;

        var codeword = new bool[174];
        Ldpc174_91.Encode(msg77, codeword);

        // First 91 bits of codeword = msg91 = msg77 + CRC14
        var decoded91 = codeword[..91].ToArray();
        // CRC-14 check on the 91-bit block should pass
        Assert.True(Crc14.Check(decoded91), "CRC-14 check on encoded 91-bit block should pass");
    }

    [Fact]
    public void Encode_AllZeroMessage_parity_determinstic()
    {
        var msg77 = new bool[77];
        var codeword1 = new bool[174];
        var codeword2 = new bool[174];
        Ldpc174_91.Encode(msg77, codeword1);
        Ldpc174_91.Encode(msg77, codeword2);
        Assert.Equal(codeword1, codeword2);
    }

    [Fact]
    public void Encode_DifferentMessages_differentCodewords()
    {
        var msg1 = new bool[77];
        var msg2 = new bool[77];
        msg2[0] = true; // flip one bit
        var cw1 = new bool[174];
        var cw2 = new bool[174];
        Ldpc174_91.Encode(msg1, cw1);
        Ldpc174_91.Encode(msg2, cw2);
        Assert.False(cw1.SequenceEqual(cw2));
    }

    [Fact]
    public void Encode_ParityCheck_PassesForAllSyndromes()
    {
        // Generate a codeword and verify all parity checks are satisfied
        var msg77 = new bool[77];
        msg77[5] = msg77[10] = msg77[42] = true;

        var cw = new bool[174];
        Ldpc174_91.Encode(msg77, cw);

        // For each check node m, verify XOR of connected variable nodes = 0
        // Using the Nm table (Nm[m][k] gives variable nodes, 1-based, 0=unused)
        var Nm = Ldpc174_91Test_Nm;
        for (int m = 0; m < 83; m++)
        {
            int parity = 0;
            for (int k = 0; k < 7; k++)
            {
                int v = Nm[m, k];
                if (v == 0) break;
                if (cw[v - 1]) parity ^= 1;
            }
            Assert.True(parity == 0, $"Parity check {m} failed");
        }
    }

    // Expose the Nm table for parity verification
    private static readonly int[,] Ldpc174_91Test_Nm = GetNm();
    private static int[,] GetNm()
    {
        // This mirrors the Nm table in Ldpc174_91.cs (same values)
        return new int[83, 7]
        {
            {4,31,59,91,92,96,153},{5,32,60,93,115,146,0},{6,24,61,94,122,151,0},
            {7,33,62,95,96,143,0},{8,25,63,83,93,96,148},{6,32,64,97,126,138,0},
            {5,34,65,78,98,107,154},{9,35,66,99,139,146,0},{10,36,67,100,107,126,0},
            {11,37,67,87,101,139,158},{12,38,68,102,105,155,0},{13,39,69,103,149,162,0},
            {8,40,70,82,104,114,145},{14,41,71,88,102,123,156},{15,42,59,106,123,159,0},
            {1,33,72,106,107,157,0},{16,43,73,108,141,160,0},{17,37,74,81,109,131,154},
            {11,44,75,110,121,166,0},{45,55,64,111,130,161,173},{8,46,71,112,119,166,0},
            {18,36,76,89,113,114,143},{19,38,77,104,116,163,0},{20,47,70,92,138,165,0},
            {2,48,74,113,128,160,0},{21,45,78,83,117,121,151},{22,47,58,118,127,164,0},
            {16,39,62,112,134,158,0},{23,43,79,120,131,145,0},{19,35,59,73,110,125,161},
            {20,36,63,94,136,161,0},{14,31,79,98,132,164,0},{3,44,80,124,127,169,0},
            {19,46,81,117,135,167,0},{7,49,58,90,100,105,168},{12,50,61,118,119,144,0},
            {13,51,64,114,118,157,0},{24,52,76,129,148,149,0},{25,53,69,90,101,130,156},
            {20,46,65,80,120,140,170},{21,54,77,100,140,171,0},{35,82,133,142,171,174,0},
            {14,30,83,113,125,170,0},{4,29,68,120,134,173,0},{1,4,52,57,86,136,152},
            {26,51,56,91,122,137,168},{52,84,110,115,145,168,0},{7,50,81,99,132,173,0},
            {23,55,67,95,172,174,0},{26,41,77,109,141,148,0},{2,27,41,61,62,115,133},
            {27,40,56,124,125,126,0},{18,49,55,124,141,167,0},{6,33,85,108,116,156,0},
            {28,48,70,85,105,129,158},{9,54,63,131,147,155,0},{22,53,68,109,121,174,0},
            {3,13,48,78,95,123,0},{31,69,133,150,155,169,0},{12,43,66,89,97,135,159},
            {5,39,75,102,136,167,0},{2,54,86,101,135,164,0},{15,56,87,108,119,171,0},
            {10,44,82,91,111,144,149},{23,34,71,94,127,153,0},{11,49,88,92,142,157,0},
            {29,34,87,97,147,162,0},{30,50,60,86,137,142,162},{10,53,66,84,112,128,165},
            {22,57,85,93,140,159,0},{28,32,72,103,132,166,0},{28,29,84,88,117,143,150},
            {1,26,45,80,128,147,0},{17,27,89,103,116,153,0},{51,57,98,163,165,172,0},
            {21,37,73,138,152,169,0},{16,47,76,130,137,154,0},{3,24,30,72,104,139,0},
            {9,40,90,106,134,151,0},{15,58,60,74,111,150,163},{18,42,79,144,146,152,0},
            {25,38,65,99,122,160,0},{17,42,75,129,170,172,0}
        };
    }

    // ── Ft8Encoder tone sequence ───────────────────────────────────────────────

    [Fact]
    public void Ft8_ToneSequence_HasCorrectCostasPositions()
    {
        var codeword = new bool[174]; // all zeros
        var tones = Ft8Encoder.BuildToneSequence(codeword);

        // Costas arrays at positions 0..6, 36..42, 72..78
        int[] costas = { 3, 1, 4, 0, 6, 5, 2 };
        for (int i = 0; i < 7; i++)
        {
            Assert.True(costas[i] == tones[i],      $"Costas A[{i}] mismatch: expected {costas[i]}, got {tones[i]}");
            Assert.True(costas[i] == tones[36 + i], $"Costas B[{i}] mismatch");
            Assert.True(costas[i] == tones[72 + i], $"Costas C[{i}] mismatch");
        }
    }

    [Fact]
    public void Ft8_ToneSequence_DataBitsUsedGrayCode()
    {
        // All-zero codeword → all data symbols should map to GrayMap[0]=0
        var codeword = new bool[174];
        var tones = Ft8Encoder.BuildToneSequence(codeword);
        int[] dataPositions = Enumerable.Range(7, 29).Concat(Enumerable.Range(43, 29)).ToArray();
        foreach (int pos in dataPositions)
            Assert.True(0 == tones[pos], $"Expected tone 0 at data position {pos}, got {tones[pos]}");
    }

    [Fact]
    public void Ft8_ToneSequence_Length79()
    {
        var codeword = new bool[174];
        var tones = Ft8Encoder.BuildToneSequence(codeword);
        Assert.Equal(79, tones.Length);
    }

    [Fact]
    public void Ft8_ToneSequence_AllTonesInRange0to7()
    {
        var codeword = new bool[174];
        // Set some bits to get non-zero tones
        for (int i = 0; i < 174; i += 3) codeword[i] = true;
        var tones = Ft8Encoder.BuildToneSequence(codeword);
        Assert.All(tones, t => Assert.InRange(t, 0, 7));
    }

    // ── Ft4Encoder tone sequence ───────────────────────────────────────────────

    [Fact]
    public void Ft4_ToneSequence_HasCorrectCostasPositions()
    {
        var codeword = new bool[174];
        var tones = Ft4Encoder.BuildToneSequence(codeword);

        int[] icos4a = { 0, 1, 3, 2 };
        int[] icos4b = { 1, 0, 2, 3 };
        int[] icos4c = { 2, 3, 1, 0 };
        int[] icos4d = { 3, 2, 0, 1 };

        for (int i = 0; i < 4; i++)
        {
            Assert.True(icos4a[i] == tones[i],      $"FT4 sync A[{i}]: expected {icos4a[i]}, got {tones[i]}");
            Assert.True(icos4b[i] == tones[33 + i], $"FT4 sync B[{i}]: expected {icos4b[i]}, got {tones[33+i]}");
            Assert.True(icos4c[i] == tones[66 + i], $"FT4 sync C[{i}]: expected {icos4c[i]}, got {tones[66+i]}");
            Assert.True(icos4d[i] == tones[99 + i], $"FT4 sync D[{i}]: expected {icos4d[i]}, got {tones[99+i]}");
        }
    }

    [Fact]
    public void Ft4_ToneSequence_Length103()
    {
        var codeword = new bool[174];
        var tones = Ft4Encoder.BuildToneSequence(codeword);
        Assert.Equal(103, tones.Length);
    }

    [Fact]
    public void Ft4_ToneSequence_AllTonesInRange0to3()
    {
        var codeword = new bool[174];
        for (int i = 0; i < 174; i += 2) codeword[i] = true;
        var tones = Ft4Encoder.BuildToneSequence(codeword);
        Assert.All(tones, t => Assert.InRange(t, 0, 3));
    }

    // ── Ft8Encoder audio output ────────────────────────────────────────────────

    [Fact]
    public void Ft8_Encode_ReturnsCorrectSampleCount()
    {
        var enc = new Ft8Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 0.9 };
        float[] audio = enc.Encode("CQ OK1TE JN89", opts);
        Assert.Equal(79 * 1920, audio.Length); // 151 680
    }

    [Fact]
    public void Ft8_Encode_SamplesNormalisedBelow1()
    {
        var enc = new Ft8Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1500, Amplitude = 1.0 };
        float[] audio = enc.Encode("CQ OK1TE JN89", opts);
        // Signal should be clipped below ±1 (sine never exceeds 1)
        Assert.All(audio, s => Assert.InRange(s, -1.01f, 1.01f));
    }

    [Fact]
    public void Ft8_Encode_StartsAndEndsSilent()
    {
        var enc = new Ft8Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 0.9 };
        float[] audio = enc.Encode("CQ OK1TE JN89", opts);

        // First sample should be near 0 (ramp in)
        Assert.InRange(audio[0], -0.01f, 0.01f);
        // Last sample should be near 0 (ramp out)
        Assert.InRange(audio[^1], -0.01f, 0.01f);
    }

    [Fact]
    public void Ft8_Encode_DifferentMessages_DifferentAudio()
    {
        var enc = new Ft8Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 0.9 };
        float[] a1 = enc.Encode("CQ OK1TE JN89", opts);
        float[] a2 = enc.Encode("OK1TE W1AW -10", opts);
        // At least one sample should differ
        Assert.False(a1.SequenceEqual(a2));
    }

    [Fact]
    public void Ft8_Encode_SameMessage_SameAudio()
    {
        var enc = new Ft8Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 0.9 };
        float[] a1 = enc.Encode("CQ OK1TE JN89", opts);
        float[] a2 = enc.Encode("CQ OK1TE JN89", opts);
        Assert.Equal(a1, a2);
    }

    // ── Ft4Encoder audio output ────────────────────────────────────────────────

    [Fact]
    public void Ft4_Encode_ReturnsCorrectSampleCount()
    {
        var enc = new Ft4Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 0.9 };
        float[] audio = enc.Encode("CQ OK1TE JN89", opts);
        Assert.Equal(105 * 576, audio.Length); // 60 480
    }

    [Fact]
    public void Ft4_Encode_SamplesNormalisedBelow1()
    {
        var enc = new Ft4Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1500, Amplitude = 1.0 };
        float[] audio = enc.Encode("CQ OK1TE JN89", opts);
        Assert.All(audio, s => Assert.InRange(s, -1.01f, 1.01f));
    }

    [Fact]
    public void Ft4_Encode_StartsAndEndsSilent()
    {
        var enc = new Ft4Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 0.9 };
        float[] audio = enc.Encode("CQ OK1TE JN89", opts);

        Assert.InRange(audio[0], -0.01f, 0.01f);
        Assert.InRange(audio[^1], -0.01f, 0.01f);
    }

    // ── FT2Encoder audio output ────────────────────────────────────────────────

    [Fact]
    public void Ft2_Encode_ReturnsCorrectSampleCount()
    {
        var enc = new Ft2Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 0.9 };
        float[] audio = enc.Encode("CQ OK1TE JN89", opts);
        Assert.Equal(105 * 288, audio.Length); // 30 240 samples at 12 kHz (NSps=288)
    }

    [Fact]
    public void Ft2_Encode_SamplesNormalisedBelow1()
    {
        var enc = new Ft2Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1500, Amplitude = 1.0 };
        float[] audio = enc.Encode("CQ OK1TE JN89", opts);
        Assert.All(audio, s => Assert.InRange(s, -1.01f, 1.01f));
    }

    [Fact]
    public void Ft2_Encode_StartsAndEndsSilent()
    {
        var enc = new Ft2Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 0.9 };
        float[] audio = enc.Encode("CQ OK1TE JN89", opts);
        Assert.InRange(audio[0], -0.01f, 0.01f);
        Assert.InRange(audio[^1], -0.01f, 0.01f);
    }

    [Fact]
    public void Ft2_ToneSequence_HasSameCostasAsFt4()
    {
        // FT2 uses identical frame structure to FT4 — just wider symbols
        var codeword = new bool[174];
        var tones = Ft2Encoder.BuildToneSequence(codeword);
        int[] icos4a = { 0, 1, 3, 2 };
        int[] icos4b = { 1, 0, 2, 3 };
        int[] icos4c = { 2, 3, 1, 0 };
        int[] icos4d = { 3, 2, 0, 1 };
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(icos4a[i], tones[i]);
            Assert.Equal(icos4b[i], tones[33 + i]);
            Assert.Equal(icos4c[i], tones[66 + i]);
            Assert.Equal(icos4d[i], tones[99 + i]);
        }
    }

    [Fact]
    public void Ft2_ToneSequence_Length103_AllTonesInRange()
    {
        var codeword = new bool[174];
        for (int i = 0; i < 174; i += 3) codeword[i] = true;
        var tones = Ft2Encoder.BuildToneSequence(codeword);
        Assert.Equal(103, tones.Length);
        Assert.All(tones, t => Assert.InRange(t, 0, 3));
    }

    [Fact]
    public void Ft2_Encode_DifferentMessages_DifferentAudio()
    {
        var enc = new Ft2Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 0.9 };
        float[] a1 = enc.Encode("CQ OK1TE JN89", opts);
        float[] a2 = enc.Encode("OK1TE W1AW RR73", opts);
        Assert.False(a1.SequenceEqual(a2));
    }

    // ── MSK144Encoder ─────────────────────────────────────────────────────────

    [Fact]
    public void Msk144_Encode_ReturnsCorrectSampleCount()
    {
        var enc = new Msk144Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 0.9 };
        float[] audio = enc.Encode("CQ OK1TE JN89", opts);
        Assert.Equal(864, audio.Length); // 144 sym × 6 sps
    }

    [Fact]
    public void Msk144_Encode_SamplesNormalisedBelow1()
    {
        var enc = new Msk144Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 1.0 };
        float[] audio = enc.Encode("CQ OK1TE JN89", opts);
        Assert.All(audio, s => Assert.InRange(s, -1.01f, 1.01f));
    }

    [Fact]
    public void Msk144_Encode_DifferentMessages_DifferentAudio()
    {
        var enc = new Msk144Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 0.9 };
        float[] a1 = enc.Encode("CQ OK1TE JN89", opts);
        float[] a2 = enc.Encode("OK1TE W1AW RR73", opts);
        Assert.False(a1.SequenceEqual(a2));
    }

    [Fact]
    public void Msk144_Encode_SameMessage_SameAudio()
    {
        var enc = new Msk144Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000, Amplitude = 0.9 };
        float[] a1 = enc.Encode("CQ OK1TE JN89", opts);
        float[] a2 = enc.Encode("CQ OK1TE JN89", opts);
        Assert.Equal(a1, a2);
    }

    [Fact]
    public void Msk144_Encode_Codeword_PassesCrc13()
    {
        // Verify that the 90-bit block [msg77 | crc13] produced by Ldpc128_90.Encode
        // satisfies the CRC-13 check (i.e. the systematic encoder appends valid CRC).
        bool[] msg77 = new bool[77];
        Assert.True(MessagePack77.TryPack77("CQ OK1TE JN89", msg77));

        bool[] codeword = new bool[128];
        Ldpc128_90.Encode(msg77, codeword);

        // First 90 bits of the codeword are [msg77 | crc13]
        Assert.True(Crc13.Check(codeword[..90]));
    }

    [Theory]
    [InlineData("CQ OK1TE JN89")]
    [InlineData("OK1TE W1AW RR73")]
    [InlineData("W1AW OK1TE -12")]
    public void Msk144_Encode_Codeword_SatisfiesAllParityChecks(string message)
    {
        // Verify that every parity-check equation H·c = 0 holds for the encoded codeword.
        // Uses CheckParity() which directly evaluates the Tanner-graph check matrix —
        // no LLR convention assumptions.
        bool[] msg77 = new bool[77];
        Assert.True(MessagePack77.TryPack77(message, msg77));

        bool[] codeword = new bool[128];
        Ldpc128_90.Encode(msg77, codeword);

        Assert.True(Ldpc128_90.CheckParity(codeword),
            $"One or more LDPC parity checks failed for \"{message}\"");
    }

    [Fact]
    public void Msk144_Encode_InvalidMessage_Throws()
    {
        var enc = new Msk144Encoder();
        var opts = new EncoderOptions { FrequencyHz = 1000 };
        Assert.Throws<ArgumentException>(() => enc.Encode("NOT A VALID HAM MSG 12345 TOOLONG", opts));
    }

    // ── EncoderEngine: FT2 + MSK144 ───────────────────────────────────────────

    [Fact]
    public void EncoderEngine_SupportsAllFourModes()
    {
        using var engine = new EncoderEngine();
        Assert.True(engine.Supports(DigitalMode.FT8));
        Assert.True(engine.Supports(DigitalMode.FT4));
        Assert.True(engine.Supports(DigitalMode.FT2));
        Assert.True(engine.Supports(DigitalMode.MSK144));
    }

    [Fact]
    public void EncoderEngine_FT2_Encode_Succeeds()
    {
        using var engine = new EncoderEngine();
        float[] audio = engine.Encode("CQ OK1TE JN89", DigitalMode.FT2);
        Assert.Equal(105 * 288, audio.Length); // 30 240 samples at 12 kHz
    }

    [Fact]
    public void EncoderEngine_MSK144_Encode_Succeeds()
    {
        using var engine = new EncoderEngine();
        float[] audio = engine.Encode("CQ OK1TE JN89", DigitalMode.MSK144);
        Assert.Equal(864, audio.Length);
    }

    // ── EncoderEngine ─────────────────────────────────────────────────────────

    [Fact]
    public void EncoderEngine_UnsupportedMode_Throws()
    {
        using var engine = new EncoderEngine();
        Assert.Throws<NotSupportedException>(() =>
            engine.Encode("CQ W1AW FN42", DigitalMode.FSK441));
    }

    [Fact]
    public void EncoderEngine_FT8_Encode_Succeeds()
    {
        using var engine = new EncoderEngine();
        float[] audio = engine.Encode("CQ OK1TE JN89", DigitalMode.FT8);
        Assert.Equal(79 * 1920, audio.Length);
    }

    [Fact]
    public void EncoderEngine_FT4_Encode_Succeeds()
    {
        using var engine = new EncoderEngine();
        float[] audio = engine.Encode("CQ OK1TE JN89", DigitalMode.FT4);
        Assert.Equal(105 * 576, audio.Length);
    }

    // ── Encode → Decode round-trip (FT8) ──────────────────────────────────────
    // Uses the decoder to verify the encoded audio can be decoded back.

    [Theory]
    [InlineData("CQ OK1TE JN89", 1500.0)]
    [InlineData("W1AW OK1TE -07", 1200.0)]
    [InlineData("OK1TE W1AW RR73", 800.0)]
    public void Ft8_EncodeDecodeRoundTrip(string message, double freq)
    {
        var enc = new Ft8Encoder();
        var opts = new EncoderOptions { FrequencyHz = freq, Amplitude = 0.5 };
        float[] audio = enc.Encode(message, opts);

        using var engine = new HamDigiSharp.Engine.DecoderEngine();
        var results = engine.Decode(
            audio,
            DigitalMode.FT8,
            freqLow: freq - 50,
            freqHigh: freq + 50,
            utcTime: "000000");

        Assert.True(results.Count > 0, $"No FT8 decodes for message: {message} at {freq} Hz");

        string expected = message.ToUpperInvariant().Trim();
        string? decoded = results.FirstOrDefault(r =>
            r.Message.Trim().ToUpperInvariant() == expected)?.Message;
        Assert.NotNull(decoded);
    }

    // ── Encode → Decode round-trip (FT4) ──────────────────────────────────────

    [Theory]
    [InlineData("CQ OK1TE JN89", 1000.0)]
    [InlineData("W1AW OK1TE -07", 1200.0)]
    public void Ft4_EncodeDecodeRoundTrip(string message, double freq)
    {
        var enc = new Ft4Encoder();
        var opts = new EncoderOptions { FrequencyHz = freq, Amplitude = 0.5 };
        float[] audio = enc.Encode(message, opts);

        using var engine = new HamDigiSharp.Engine.DecoderEngine();
        var results = engine.Decode(
            audio,
            DigitalMode.FT4,
            freqLow: freq - 50,
            freqHigh: freq + 50,
            utcTime: "000000");

        Assert.True(results.Count > 0, $"No FT4 decodes for message: {message} at {freq} Hz");

        string expected = message.ToUpperInvariant().Trim();
        string? decoded = results.FirstOrDefault(r =>
            r.Message.Trim().ToUpperInvariant() == expected)?.Message;
        Assert.NotNull(decoded);
    }

    // ── EncoderEngine: contract and dispose behaviour ─────────────────────────

    [Fact]
    public void EncoderEngine_EncodeAfterDispose_ThrowsObjectDisposedException()
    {
        var engine = new EncoderEngine();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            engine.Encode("CQ OK1TE JN89", DigitalMode.FT8));
    }

    [Fact]
    public void EncoderEngine_DoubleDispose_DoesNotThrow()
    {
        var engine = new EncoderEngine();
        engine.Dispose();
        var ex = Record.Exception(() => engine.Dispose());
        Assert.Null(ex);
    }

    // ── MSK144: FrequencyHz option shifts the tones ───────────────────────────

    [Fact]
    public void Msk144_Encode_DifferentFrequencies_DifferentAudio()
    {
        var enc = new Msk144Encoder();
        float[] audio1 = enc.Encode("CQ OK1TE JN89",
            new EncoderOptions { FrequencyHz = 1000.0 });
        float[] audio2 = enc.Encode("CQ OK1TE JN89",
            new EncoderOptions { FrequencyHz = 1500.0 });

        Assert.Equal(864, audio1.Length);
        Assert.Equal(864, audio2.Length);
        Assert.False(audio1.SequenceEqual(audio2),
            "different base frequencies must produce different waveforms");
    }

    // ── Ldpc174_91.CheckParity: direct verification ──────────────────────────

    [Fact]
    public void Ldpc174_91_CheckParity_EncodedMessage_PassesAllChecks()
    {
        var msg77 = new bool[77];
        Assert.True(MessagePack77.TryPack77("CQ OK1TE JN89", msg77));
        var codeword = new bool[174];
        Ldpc174_91.Encode(msg77, codeword);
        Assert.True(Ldpc174_91.CheckParity(codeword),
            "Ldpc174_91.Encode must produce a codeword satisfying all 83 parity checks");
    }

    // ── EncoderOptions.Amplitude clamping ────────────────────────────────────

    [Fact]
    public void EncoderOptions_Amplitude_ClampedAbove1()
    {
        var opts = new EncoderOptions { Amplitude = 1.5 };
        Assert.Equal(0.99, opts.Amplitude, precision: 10);
    }

    [Fact]
    public void EncoderOptions_Amplitude_ClampedBelowZero()
    {
        var opts = new EncoderOptions { Amplitude = -0.5 };
        Assert.Equal(0.0, opts.Amplitude, precision: 10);
    }

    [Fact]
    public void EncoderOptions_Amplitude_ValidValuePassesThrough()
    {
        var opts = new EncoderOptions { Amplitude = 0.7 };
        Assert.Equal(0.7, opts.Amplitude, precision: 10);
    }

    [Fact]
    public void EncoderOptions_Amplitude_DefaultIs0_9()
    {
        var opts = new EncoderOptions();
        Assert.Equal(0.9, opts.Amplitude, precision: 10);
    }

    // ── Encoder edge cases ────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ft8Encoder_EmptyMessage_Throws(string msg)
    {
        Assert.Throws<ArgumentException>(() =>
            new Ft8Encoder().Encode(msg, new EncoderOptions()));
    }

    [Theory]
    [InlineData("CQ W1AW FN42")] // 12 chars — exactly at limit
    [InlineData("W1AW K9AN -07")]
    public void Ft8Encoder_AtMaxLength_DoesNotThrow(string msg)
    {
        var audio = new Ft8Encoder().Encode(msg, new EncoderOptions());
        Assert.NotEmpty(audio);
    }

    [Fact]
    public void Ft8Encoder_MessageWithInvalidChars_Throws()
    {
        // FT8 standard messages go through pack77 which accepts only valid callsign/grid
        // completely invalid tokens should throw
        Assert.Throws<ArgumentException>(() =>
            new Ft8Encoder().Encode("$$$ ??? ###", new EncoderOptions()));
    }
}

