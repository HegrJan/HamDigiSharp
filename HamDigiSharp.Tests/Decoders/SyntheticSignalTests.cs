using HamDigiSharp.Abstractions;
using FluentAssertions;
using HamDigiSharp.Decoders.Fsk;
using HamDigiSharp.Decoders.Ft8;
using HamDigiSharp.Decoders.Ft4;
using HamDigiSharp.Decoders.Ft2;
using HamDigiSharp.Decoders.Jt65;
using HamDigiSharp.Decoders.Jt6m;
using HamDigiSharp.Decoders.Msk;
using HamDigiSharp.Decoders.Jtms;
using HamDigiSharp.Decoders.Iscat;
using HamDigiSharp.Decoders.Pi4;
using HamDigiSharp.Decoders.Q65;
using HamDigiSharp.Decoders.SuperFox;
using HamDigiSharp.Encoders;
using HamDigiSharp.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HamDigiSharp.Tests.Decoders;

/// <summary>
/// Synthetic signal tests: synthesize known signals and verify round-trip decoding.
///
/// FSK441/FSK315 tests synthesize exact integer-cycle tones (no spectral leakage)
/// and verify a known message can be decoded from the first window.
/// Other modes use silence / noise smoke tests to verify robustness.
/// </summary>
public class SyntheticSignalTests
{
    // ── FSK441 round-trip ─────────────────────────────────────────────────────

    /// <summary>
    /// Synthesizes the message "ABIJ" as a clean FSK441 signal and verifies
    /// the decoder recovers it.
    ///
    /// Algorithm:
    ///   SampleRate = 11025, NSPS = 25, ToneStep = 441 Hz
    ///   f0 = 882 Hz (lTone=2, baud=441, so f1=2*441=882 at dfx=0)
    ///   Integer cycles: 882*25/11025=2, 1323*25/11025=3, etc. → no spectral leakage.
    ///   Symbol sequence: 3 preamble (tone 0) + 4×3 message tones.
    ///   No tone-3 anywhere → jsync=3, message starts at symbol offset 3.
    /// </summary>
    [Fact]
    public void Fsk441_SynthesizedMessage_DecodesCorrectly()
    {
        // Character encoding: nc = 16*d0 + 4*d1 + d2
        // 'A'=17(1,0,1), 'B'=18(1,0,2), 'I'=25(1,2,1), 'J'=26(1,2,2) — no tone-3.
        int[] pattern =
        {
            0, 0, 0,          // preamble: 3×tone-0
            1, 0, 1,          // 'A' nc=17
            1, 0, 2,          // 'B' nc=18
            1, 2, 1,          // 'I' nc=25
            1, 2, 2           // 'J' nc=26
        };

        const int SampleRate = 11025;
        const int Nsps = 25;
        const double F0 = 882.0;  // base tone frequency
        const double ToneStep = 441.0;

        // Provide 2 seconds so the outer loop runs at least once
        int numSamples = SampleRate * 2;
        var samples = new float[numSamples];

        // Fill by repeating the 15-symbol pattern (375 samples per cycle)
        int symbolCount = numSamples / Nsps; // 882 symbols
        for (int sym = 0; sym < symbolCount; sym++)
        {
            int tone = pattern[sym % pattern.Length];
            double freq = F0 + tone * ToneStep;
            for (int s = 0; s < Nsps; s++)
            {
                int idx = sym * Nsps + s;
                if (idx < numSamples)
                    samples[idx] = (float)Math.Sin(2.0 * Math.PI * freq * s / SampleRate);
            }
        }

        var decoder = new Fsk441Decoder();
        var results = decoder.Decode(samples, 700, 3000, "000000");

        results.Should().NotBeEmpty("a clean FSK441 signal with pattern 'ABIJ' must be decoded");
        results.Any(r => r.Message.Contains("ABIJ")).Should().BeTrue(
            $"at least one result should contain 'ABIJ'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void Fsk441_SynthesizedMessage_HasLetterContent()
    {
        // Same synthesis, lighter assertion: any letter must be present.
        const int SampleRate = 11025;
        const int Nsps = 25;
        const double F0 = 882.0;
        const double ToneStep = 441.0;

        int[] pattern = { 0, 0, 0, 1, 0, 1, 1, 0, 2, 1, 2, 1, 1, 2, 2 };

        var samples = new float[SampleRate * 2];
        int symbolCount = samples.Length / Nsps;
        for (int sym = 0; sym < symbolCount; sym++)
        {
            int tone = pattern[sym % pattern.Length];
            double freq = F0 + tone * ToneStep;
            for (int s = 0; s < Nsps; s++)
            {
                int idx = sym * Nsps + s;
                if (idx < samples.Length)
                    samples[idx] = (float)(0.9 * Math.Sin(2.0 * Math.PI * freq * s / SampleRate));
            }
        }

        var decoder = new Fsk441Decoder();
        var results = decoder.Decode(samples, 700, 3000, "000000");

        results.Should().NotBeEmpty();
        results.All(r => r.Message.Any(char.IsLetter)).Should().BeTrue(
            "every result must contain at least one letter");
        results.All(r => r.Mode == DigitalMode.FSK441).Should().BeTrue();
    }

    [Fact]
    public void Fsk441_SynthesizedMessage_FrequencyReportedCorrectly()
    {
        // The reported FrequencyHz should be within the decoder's search range near 882 Hz.
        // The dfx step is 6 Hz, so the closest candidate is within ±441 Hz of the base.
        // We only verify the frequency is in the expected search band [441, 1323] Hz.
        const int SampleRate = 11025;
        const int Nsps = 25;
        const double F0 = 882.0;
        const double ToneStep = 441.0;

        int[] pattern = { 0, 0, 0, 1, 0, 1, 1, 0, 2, 1, 2, 1, 1, 2, 2 };
        var samples = new float[SampleRate * 2];
        int symbolCount = samples.Length / Nsps;
        for (int sym = 0; sym < symbolCount; sym++)
        {
            int tone = pattern[sym % pattern.Length];
            double freq = F0 + tone * ToneStep;
            for (int s = 0; s < Nsps; s++)
            {
                int idx = sym * Nsps + s;
                if (idx < samples.Length)
                    samples[idx] = (float)Math.Sin(2.0 * Math.PI * freq * s / SampleRate);
            }
        }

        var decoder = new Fsk441Decoder();
        var results = decoder.Decode(samples, 700, 3000, "000000");

        results.Should().NotBeEmpty();
        // Frequency reported by the decoder is the f1 (tone-0 base) at the dfx value
        // that gave the best decode. It must be within the legal search band.
        var freqHzValues = results.Select(r => r.FrequencyHz).ToList();
        freqHzValues.All(f => f >= 441 && f <= 3000 - 3 * 441).Should().BeTrue(
            $"All reported frequencies must be in valid search range; got: [{string.Join(", ", freqHzValues)}]");
    }

    // ── ISCAT round-trip ─────────────────────────────────────────────────────

    /// <summary>
    /// Generates a synthetic ISCAT-B signal with message "HAMMRINGS" (msgLen=9) and
    /// verifies the decoder recovers it.
    ///
    /// Design constraints satisfied by msgLen=9 / "HAMMRINGS" / search 400–900 Hz:
    ///
    ///   1. NDat(18) % 9 == 0  → every block maps each character to the SAME fold
    ///      slot — no cross-block phase mixing that would dilute the argmax.
    ///
    ///   2. Length[0] bin = i0 + 2×9 = 48; search range gives ibS = 41.
    ///      The Costas search accesses bins fi+{0,2,4,6} for fi ∈ [19,41],
    ///      so max reachable bin = 47 < 48.  Neither the Length[0] nor Length[1]
    ///      tones lie inside the Costas-search window, which prevents a false
    ///      Costas score driven by the isolated high-normFac Length bins overwhelming
    ///      the true Costas score.
    ///
    ///   3. "HAMMRINGS" avoids chars '9' (ci=9, offset=18 = Length[0]) and
    ///      'E' (ci=14, offset=28 = Length[1]), so no data tone aliases a sync tone.
    ///
    ///   4. Small white noise: pure integer-cycle tones give exact-zero DFT power
    ///      at non-signal bins (orthogonality), so smax2=0 → rr=0 → no decode.
    ///      Noise at −54 dB makes smax2 > 0 and leaves rr ≫ 2.
    /// </summary>
    [Fact]
    public void IscatB_SynthesizedMessage_DecodesCorrectly()
    {
        const string Message    = "HAMMRINGS";  // 9 chars; 18%9==0; no '9' or 'E'
        const int    SampleRate = 11025;
        const double Fsample    = 3100.78125;
        const int    NspsOrig   = 256;          // ISCAT-B: 72 DS × 32/9
        const int    NfftDown   = 144;
        const int    NBlk       = 24;
        double df   = Fsample / NfftDown;       // ≈ 21.533 Hz/bin
        int    i0   = 30;                       // base bin ≈ 646 Hz

        const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ /.?@-";
        var toneFor = new int[256];
        for (int ci = 0; ci < Alphabet.Length; ci++) toneFor[Alphabet[ci]] = ci;

        int[] icos   = { 0, 1, 3, 2 };
        int   msgLen = Message.Length;   // 9

        var blockTone = new int[NBlk];
        for (int n = 0; n < 4; n++)  blockTone[n]   = 2 * icos[n];
        blockTone[4] = 2 * msgLen;                           // offset 18, bin 48 > ibS=41
        blockTone[5] = 2 * msgLen + 10;                      // offset 28, bin 58 > 47
        for (int d = 0; d < 18; d++) blockTone[6 + d] = 2 * toneFor[Message[d % msgLen]];

        int    totalSamples = SampleRate * 30;
        var    samples      = new float[totalSamples];
        double dtOrig       = 1.0 / SampleRate;
        int    totalSymbols = totalSamples / NspsOrig;
        for (int sym = 0; sym < totalSymbols; sym++)
        {
            int    pos  = sym % NBlk;
            double freq = (i0 + blockTone[pos]) * df;   // integer cycles → no leakage
            int    start = sym * NspsOrig;
            for (int s = 0; s < NspsOrig && start + s < totalSamples; s++)
                samples[start + s] = (float)(0.5 * Math.Sin(2.0 * Math.PI * freq * (start + s) * dtOrig));
        }

        // Noise at −54 dB makes smax2 > 0 (needed for rr); see design note above.
        var rng = new Random(42);
        for (int i = 0; i < totalSamples; i++)
            samples[i] += (float)(rng.NextDouble() * 2e-3 - 1e-3);

        var decoder = new IscatDecoder(DigitalMode.IscatB);
        // ibS = 30 + ⌊250/21.53⌋ = 41 < 48 = Length[0] bin → no false Costas
        var results = decoder.Decode(samples, 400, 900, "000000");

        results.Should().NotBeEmpty("a clean ISCAT-B signal at +54 dB SNR must decode");
        results.Any(r => r.Message.Contains("HAMMRINGS")).Should().BeTrue(
            $"message should contain 'HAMMRINGS'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
        results.All(r => r.Mode == DigitalMode.IscatB).Should().BeTrue();
    }

    /// <summary>
    /// Synthetic ISCAT-A signal with message "HAMMRINGS" (msgLen=9).
    /// Search range 900–1100 Hz gives ibS ≈ 101; Length[0] bin = 94+18 = 112 > 107
    /// (max reachable = ibS+6 = 107), so the isolation constraint holds for ISCAT-A too.
    /// </summary>
    [Fact]
    public void IscatA_SynthesizedMessage_DecodesCorrectly()
    {
        const string Message    = "HAMMRINGS";  // 9 chars; 18%9==0; no '9' or 'E'
        const int    SampleRate = 11025;
        const double Fsample    = 3100.78125;
        const int    NspsOrig   = 512;          // ISCAT-A: 144 DS × 32/9
        const int    NfftDown   = 288;
        const int    NBlk       = 24;
        double df   = Fsample / NfftDown;       // ≈ 10.766 Hz/bin
        int    i0   = 94;                       // base bin ≈ 1012 Hz

        const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ /.?@-";
        var toneFor = new int[256];
        for (int ci = 0; ci < Alphabet.Length; ci++) toneFor[Alphabet[ci]] = ci;

        int[] icos   = { 0, 1, 3, 2 };
        int   msgLen = Message.Length;   // 9

        var blockTone = new int[NBlk];
        for (int n = 0; n < 4; n++)  blockTone[n]   = 2 * icos[n];
        blockTone[4] = 2 * msgLen;                           // offset 18, bin 112 > ibS+6=107
        blockTone[5] = 2 * msgLen + 10;
        for (int d = 0; d < 18; d++) blockTone[6 + d] = 2 * toneFor[Message[d % msgLen]];

        int    totalSamples = SampleRate * 30;
        var    samples      = new float[totalSamples];
        double dtOrig       = 1.0 / SampleRate;
        int    totalSymbols = totalSamples / NspsOrig;
        for (int sym = 0; sym < totalSymbols; sym++)
        {
            int    pos  = sym % NBlk;
            double freq = (i0 + blockTone[pos]) * df;
            int    start = sym * NspsOrig;
            for (int s = 0; s < NspsOrig && start + s < totalSamples; s++)
                samples[start + s] = (float)(0.5 * Math.Sin(2.0 * Math.PI * freq * (start + s) * dtOrig));
        }

        var rng = new Random(42);
        for (int i = 0; i < totalSamples; i++)
            samples[i] += (float)(rng.NextDouble() * 2e-3 - 1e-3);

        var decoder = new IscatDecoder(DigitalMode.IscatA);
        // fCenter=1000 Hz → i0_calc=92; ibS=92+⌊100/10.77⌋=101; max bin=107 < 112
        var results = decoder.Decode(samples, 900, 1100, "000000");

        results.Should().NotBeEmpty("a clean ISCAT-A signal at +54 dB SNR must decode");
        results.Any(r => r.Message.Contains("HAMMRINGS")).Should().BeTrue(
            $"message should contain 'HAMMRINGS'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
        results.All(r => r.Mode == DigitalMode.IscatA).Should().BeTrue();
    }

    [Fact]
    public void IscatB_Mode_IsCorrect()
        => new IscatDecoder(DigitalMode.IscatB).Mode.Should().Be(DigitalMode.IscatB);

    [Fact]
    public void IscatA_Mode_IsCorrect()
        => new IscatDecoder(DigitalMode.IscatA).Mode.Should().Be(DigitalMode.IscatA);

    [Fact]
    public void IscatB_Silence_ReturnsEmpty()
    {
        var silence = new float[11025 * 30];
        new IscatDecoder(DigitalMode.IscatB)
            .Decode(silence, 200, 3000, "000000")
            .Should().BeEmpty("silence has no ISCAT signal");
    }

    [Fact]
    public void IscatB_Encoder_RoundTrip_DecodesCorrectly()
    {
        var encoder = new IscatEncoder(DigitalMode.IscatB);
        float[] samples = encoder.Encode("HAMMRINGS",
            new EncoderOptions { FrequencyHz = 645.996, Amplitude = 0.5 });

        // Add tiny noise so smax2 > 0 (needed for rr computation)
        var rng = new Random(7);
        for (int i = 0; i < samples.Length; i++)
            samples[i] += (float)(rng.NextDouble() * 2e-3 - 1e-3);

        var results = new IscatDecoder(DigitalMode.IscatB).Decode(samples, 400, 900, "000000");
        results.Should().NotBeEmpty("IscatEncoder output must be decodable");
        results.Any(r => r.Message.Contains("HAMMRINGS")).Should().BeTrue(
            $"got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    [Fact]
    public void IscatA_Encoder_RoundTrip_DecodesCorrectly()
    {
        var encoder = new IscatEncoder(DigitalMode.IscatA);
        float[] samples = encoder.Encode("HAMMRINGS",
            new EncoderOptions { FrequencyHz = 1011.475, Amplitude = 0.5 });

        var rng = new Random(7);
        for (int i = 0; i < samples.Length; i++)
            samples[i] += (float)(rng.NextDouble() * 2e-3 - 1e-3);

        var results = new IscatDecoder(DigitalMode.IscatA).Decode(samples, 900, 1100, "000000");
        results.Should().NotBeEmpty("IscatEncoder output must be decodable");
        results.Any(r => r.Message.Contains("HAMMRINGS")).Should().BeTrue(
            $"got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    /// <summary>
    /// FSK315: NSPS=35, ToneStep=315 Hz, lTone=3 → f1 = 3×315 = 945 Hz at dfx=0.
    /// Integer cycles: 945*35/11025=3, 1260*35/11025=4, etc. → no leakage.
    /// </summary>
    [Fact]
    public void Fsk315_SynthesizedMessage_DecodesCorrectly()
    {
        int[] pattern =
        {
            0, 0, 0,          // preamble
            1, 0, 1,          // 'A'
            1, 0, 2,          // 'B'
            1, 2, 1,          // 'I'
            1, 2, 2           // 'J'
        };

        const int SampleRate = 11025;
        const int Nsps = 35;
        const double F0 = 945.0;   // base: lTone=3, baud=315 → 3*315=945 Hz
        const double ToneStep = 315.0;

        var samples = new float[SampleRate * 2];
        int symbolCount = samples.Length / Nsps;
        for (int sym = 0; sym < symbolCount; sym++)
        {
            int tone = pattern[sym % pattern.Length];
            double freq = F0 + tone * ToneStep;
            for (int s = 0; s < Nsps; s++)
            {
                int idx = sym * Nsps + s;
                if (idx < samples.Length)
                    samples[idx] = (float)Math.Sin(2.0 * Math.PI * freq * s / SampleRate);
            }
        }

        var decoder = new Fsk315Decoder();
        var results = decoder.Decode(samples, 700, 3000, "000000");

        results.Should().NotBeEmpty("clean FSK315 signal must be decoded");
        results.Any(r => r.Message.Contains("ABIJ")).Should().BeTrue(
            $"message should contain 'ABIJ'; got: [{string.Join(", ", results.Select(r => r.Message))}]");
    }

    // ── Mode property verification ────────────────────────────────────────────

    [Fact]
    public void Fsk441_Mode_IsCorrect()
        => new Fsk441Decoder().Mode.Should().Be(DigitalMode.FSK441);

    [Fact]
    public void Fsk315_Mode_IsCorrect()
        => new Fsk315Decoder().Mode.Should().Be(DigitalMode.FSK315);

    // ── Decoder smoke tests: no crash on silence and random noise ─────────────

    [Theory]
    [InlineData("FT8", 12000, 15)]
    [InlineData("JT65A", 11025, 60)]
    [InlineData("MSK144", 12000, 1)]
    [InlineData("JTMS", 11025, 2)]
    [InlineData("FSK441", 11025, 1)]
    [InlineData("FSK315", 11025, 1)]
    [InlineData("PI4", 11025, 30)]
    public void Decoder_Silence_DoesNotCrash(string modeName, int sampleRate, int periodSeconds)
    {
        IReadOnlyList<DecodeResult> results = null!;
        var silence = new float[sampleRate * periodSeconds];
        var ex = Record.Exception(() =>
        {
            results = CreateDecoder(modeName).Decode(silence, 200, 3000, "000000");
        });
        ex.Should().BeNull($"{modeName} decoder must not throw on silence");
        results.Should().NotBeNull();
    }

    [Theory]
    [InlineData("FT8", 12000, 15)]
    [InlineData("JT65A", 11025, 60)]
    [InlineData("MSK144", 12000, 1)]
    [InlineData("JTMS", 11025, 2)]
    [InlineData("FSK441", 11025, 2)]
    [InlineData("FSK315", 11025, 2)]
    [InlineData("PI4", 11025, 15)]
    public void Decoder_RandomNoise_DoesNotCrash(string modeName, int sampleRate, int periodSeconds)
    {
        var rng = new Random(42);
        var noise = new float[sampleRate * periodSeconds];
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)(rng.NextDouble() * 2 - 1);

        var ex = Record.Exception(() =>
        {
            CreateDecoder(modeName).Decode(noise, 200, 3000, "000000");
        });
        ex.Should().BeNull($"{modeName} decoder must not throw on random noise");
    }

    [Theory]
    [InlineData("FT8", 12000, 15)]
    [InlineData("JT65A", 11025, 60)]
    [InlineData("JTMS", 11025, 2)]
    [InlineData("FSK441", 11025, 2)]
    public void Decoder_PureSineWave_DoesNotCrash(string modeName, int sampleRate, int periodSeconds)
    {
        // A pure 1000 Hz sine — likely won't decode but must not crash.
        var samples = new float[sampleRate * periodSeconds];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000.0 * i / sampleRate));

        var ex = Record.Exception(() =>
            CreateDecoder(modeName).Decode(samples, 200, 3000, "000000"));
        ex.Should().BeNull($"{modeName} must not crash on a pure sine wave");
    }

    [Theory]
    [InlineData("FT8", 12000)]
    [InlineData("JT65A", 11025)]
    [InlineData("MSK144", 12000)]
    [InlineData("FSK441", 11025)]
    public void Decoder_TooShort_ReturnsEmpty(string modeName, int sampleRate)
    {
        var veryShort = new float[10]; // way too short for any mode
        var results = CreateDecoder(modeName).Decode(veryShort, 200, 3000, "000000");
        results.Should().BeEmpty($"{modeName} should return empty for 10-sample input");
        _ = sampleRate; // parameter used in InlineData for documentation
    }

    // ── FSK441 signal properties ──────────────────────────────────────────────

    [Fact]
    public void Fsk441_Silence_ReturnsNoResults()
    {
        var decoder = new Fsk441Decoder();
        var silence = new float[11025 * 2];
        var results = decoder.Decode(silence, 700, 3000, "000000");
        results.Should().BeEmpty("silence has no FSK441 signal");
    }

    [Fact]
    public void Fsk441_TwoIdenticalWindows_DoesNotDuplicateResults()
    {
        // The decoder has a deduplication HashSet — identical windows should give 1 result, not 2.
        int[] pattern = { 0, 0, 0, 1, 0, 1, 1, 0, 2, 1, 2, 1, 1, 2, 2 };
        const int SampleRate = 11025;
        const int Nsps = 25;
        const double F0 = 882.0;
        const double ToneStep = 441.0;

        var samples = new float[SampleRate * 3]; // 3 seconds: triggers 4 windows
        int symbolCount = samples.Length / Nsps;
        for (int sym = 0; sym < symbolCount; sym++)
        {
            int tone = pattern[sym % pattern.Length];
            double freq = F0 + tone * ToneStep;
            for (int s = 0; s < Nsps; s++)
            {
                int idx = sym * Nsps + s;
                if (idx < samples.Length)
                    samples[idx] = (float)Math.Sin(2.0 * Math.PI * freq * s / SampleRate);
            }
        }

        var decoder = new Fsk441Decoder();
        var results = decoder.Decode(samples, 700, 3000, "000000");

        // Deduplication means each unique message appears only once
        var messages = results.Select(r => r.Message).ToList();
        messages.Should().OnlyHaveUniqueItems("decoder deduplication must prevent identical messages");
    }

    [Fact]
    public void Fsk441_FrequencyOutOfRange_ReturnsEmpty()
    {
        // Signal at 882 Hz, but we restrict to [2000, 3000] — should not be found.
        int[] pattern = { 0, 0, 0, 1, 0, 1, 1, 0, 2, 1, 2, 1, 1, 2, 2 };
        const int SampleRate = 11025;
        const int Nsps = 25;
        const double F0 = 882.0;
        const double ToneStep = 441.0;

        var samples = new float[SampleRate * 2];
        int symbolCount = samples.Length / Nsps;
        for (int sym = 0; sym < symbolCount; sym++)
        {
            int tone = pattern[sym % pattern.Length];
            double freq = F0 + tone * ToneStep;
            for (int s = 0; s < Nsps; s++)
            {
                int idx = sym * Nsps + s;
                if (idx < samples.Length)
                    samples[idx] = (float)Math.Sin(2.0 * Math.PI * freq * s / SampleRate);
            }
        }

        var decoder = new Fsk441Decoder();
        var results = decoder.Decode(samples, 2000, 3000, "000000");
        results.Should().BeEmpty("signal at 882 Hz should not be found in [2000,3000] search range");
    }

    // ── Smoke tests for all remaining modes ─────────────────────────────────

    /// <summary>
    /// Quick smoke test using inputs too short to pass each decoder's minimum-length
    /// guard. The decoder must early-return cleanly without throwing. Verifies
    /// constructor + guard code are safe on any input.
    /// </summary>
    [Theory]
    [InlineData("Q65A",     12000)]
    [InlineData("Q65B",     12000)]
    [InlineData("Q65C",     12000)]
    [InlineData("Q65D",     12000)]
    [InlineData("JT65B",    11025)]
    [InlineData("JT65C",    11025)]
    [InlineData("JT6M",     11025)]
    [InlineData("FT2",      12000)]
    [InlineData("FT4",      12000)]
    [InlineData("MSKMS",    12000)]
    [InlineData("IscatA",   11025)]
    [InlineData("IscatB",   11025)]
    [InlineData("SuperFox", 12000)]
    public void AllRemainingModes_ShortSilence_DoesNotCrash(string modeName, int sampleRate)
    {
        // 500 ms is always below every decoder's minimum viable sample count.
        var tiny = new float[sampleRate / 2];
        var ex = Record.Exception(() =>
            CreateDecoderFull(modeName).Decode(tiny, 200, 3000, "000000"));
        ex.Should().BeNull($"{modeName} must not throw on a short silence input");
    }

    /// <summary>
    /// Full-path smoke test: enough samples to pass the decoder's guard, but with
    /// a narrow 200 Hz frequency search window (1000–1200 Hz) to keep the number
    /// of candidate FFT bins small and the test fast. Still exercises the FFT,
    /// spectrogram, and inner decode loops on random-noise data.
    /// </summary>
    [Theory]
    // Modes whose guards are small — use enough samples to pass:
    [InlineData("FT2",      12000)]   // guard = 11251
    [InlineData("FT4",      20000)]   // guard = 18145
    [InlineData("MSKMS",     2000)]   // guard =   961
    [InlineData("IscatB",    5000)]   // guard varies; 5000 exercises inner path
    [InlineData("SuperFox", 50000)]   // guard = 45001
    // Modes with large guards — narrow freq. range keeps run-time manageable:
    [InlineData("Q65A",    294000)]  // guard = 293761; freq 1000–1200 Hz = ~115 bins
    [InlineData("Q65B",    200000)]
    [InlineData("Q65C",    100000)]
    [InlineData("Q65D",     50000)]
    [InlineData("JT65B",   332000)]  // guard = 330751
    [InlineData("JT65C",   332000)]
    [InlineData("JT6M",      5000)]  // delegates to JT65 → early-return on 5000
    public void AllRemainingModes_RandomNoise_DoesNotCrash(
        string modeName, int nSamples)
    {
        var rng   = new Random(0xDEAD);
        var noise = new float[nSamples];
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)(rng.NextDouble() * 2 - 1);

        // Narrow search window keeps large-period decoders fast on silence/noise.
        var ex = Record.Exception(() =>
            CreateDecoderFull(modeName).Decode(noise, 1000, 1200, "000000"));
        ex.Should().BeNull($"{modeName} decoder must not throw on random noise");
    }

    [Theory]
    [InlineData("Q65A",  DigitalMode.Q65A)]
    [InlineData("Q65B",  DigitalMode.Q65B)]
    [InlineData("Q65C",  DigitalMode.Q65C)]
    [InlineData("Q65D",  DigitalMode.Q65D)]
    [InlineData("JT65B", DigitalMode.JT65B)]
    [InlineData("JT65C", DigitalMode.JT65C)]
    [InlineData("JT6M",  DigitalMode.JT6M)]
    [InlineData("FT2",   DigitalMode.FT2)]
    [InlineData("FT4",   DigitalMode.FT4)]
    public void AllRemainingModes_ModeProperty_IsCorrect(string modeName, DigitalMode expected)
    {
        CreateDecoderFull(modeName).Mode.Should().Be(expected,
            $"{modeName} decoder must report the correct DigitalMode");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static IDigitalModeDecoder CreateDecoder(string name) => name switch
    {
        "FT8"    => new Ft8Decoder(),
        "JT65A"  => new Jt65Decoder(DigitalMode.JT65A),
        "MSK144" => new Msk144Decoder(),
        "JTMS"   => new JtmsDecoder(),
        "FSK441" => new Fsk441Decoder(),
        "FSK315" => new Fsk315Decoder(),
        "PI4"    => new Pi4Decoder(),
        _        => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    private static IDigitalModeDecoder CreateDecoderFull(string name) => name switch
    {
        "Q65A"     => new Q65Decoder(DigitalMode.Q65A),
        "Q65B"     => new Q65Decoder(DigitalMode.Q65B),
        "Q65C"     => new Q65Decoder(DigitalMode.Q65C),
        "Q65D"     => new Q65Decoder(DigitalMode.Q65D),
        "JT65B"    => new Jt65Decoder(DigitalMode.JT65B),
        "JT65C"    => new Jt65Decoder(DigitalMode.JT65C),
        "JT6M"     => new Jt6mDecoder(),
        "FT2"      => new Ft2Decoder(),
        "FT4"      => new Ft4Decoder(),
        "MSKMS"    => new Msk40Decoder(),
        "IscatA"   => new IscatDecoder(DigitalMode.IscatA),
        "IscatB"   => new IscatDecoder(DigitalMode.IscatB),
        "SuperFox" => new SuperFoxDecoder(),
        _          => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };
}
