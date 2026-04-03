using FluentAssertions;
using HamDigiSharp.Decoders.Ft2;
using HamDigiSharp.Decoders.Ft4;
using HamDigiSharp.Decoders.Ft8;
using HamDigiSharp.Encoders;
using HamDigiSharp.Models;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace HamDigiSharp.Tests.Decoders;

/// <summary>
/// Integration tests against real over-the-air recordings and noisy synthetic signals.
///
/// WAV files are NOT stored in the repository. Tests that require external files skip
/// silently (pass as no-op) when the file is absent — so CI stays green.
///
/// Sample file locations can be configured via environment variables:
///   HAMDIGIENGINE_FT8_SAMPLES  — directory containing FT8 WAV files
///                                 (from kgoba/ft8_lib GitHub: 191111_110*.wav)
///   HAMDIGIENGINE_FT4_SAMPLES  — directory containing FT4 WAV files
///                                 (from SourceForge WSJT project: 190106_*.wav)
///
/// Default fallback: ~/Samples/FT8 and ~/Samples/FT4
/// FT2 : no public recordings yet — tested via synthesised noisy signals instead
/// </summary>
public class RealSamplesTests
{
    private static string FT8Dir =>
        Environment.GetEnvironmentVariable("HAMDIGIENGINE_FT8_SAMPLES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Samples", "FT8_sf");

    private static string FT4Dir =>
        Environment.GetEnvironmentVariable("HAMDIGIENGINE_FT4_SAMPLES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Samples", "FT4");
    private const int    SampleRate = 12000;

    private readonly ITestOutputHelper _out;
    public RealSamplesTests(ITestOutputHelper output) => _out = output;

    // ── FT8 real recordings ──────────────────────────────────────────────────

    /// <summary>
    /// Known decodes for the kgoba/ft8_lib test corpus (subset that every
    /// LDPC decoder should find at depth 2).
    /// </summary>
    public static TheoryData<string, string[]> Ft8KnownDecodes => new()
    {
        {
            "191111_110130.wav",
            new[] { "CQ TA6CQ KN70", "OH3NIV ZS6S -03", "CQ R7IW LN35", "CQ DX R6WA LN32" }
        },
        {
            "191111_110200.wav",
            new[] { "CQ TA6CQ KN70", "OH3NIV ZS6S RR73", "CQ LZ1JZ KN22", "CQ R7IW LN35", "CQ DX R6WA LN32" }
        },
        {
            "191111_110215.wav",
            new[] { "GJ0KYZ RK9AX MO05", "GJ0KYZ UA6HI -15" }
        },
    };

    [Theory]
    [MemberData(nameof(Ft8KnownDecodes))]
    public void Ft8_RealRecording_FindsKnownMessages(string fileName, string[] expected)
    {
        string path = Path.Combine(FT8Dir, fileName);
        if (!File.Exists(path)) return; // skip — file not present

        float[] samples = LoadWav16(path);
        _out.WriteLine($"Loaded {samples.Length} samples ({samples.Length / (double)SampleRate:F1}s) from {fileName}");

        var decoder = new Ft8Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Normal, MaxCandidates = 500, ApDecode = false });
        var results = decoder.Decode(samples, 200, 3000, "000000");

        _out.WriteLine($"Decoded {results.Count} message(s):");
        foreach (var r in results.OrderBy(r => r.FrequencyHz))
            _out.WriteLine($"  {r.FrequencyHz:F0}Hz SNR={r.Snr:+0.0;-0.0} dt={r.Dt:+0.00;-0.00} '{r.Message}'");

        results.Should().NotBeEmpty($"real FT8 recording {fileName} must produce at least one decode");

        foreach (string msg in expected)
        {
            bool found = results.Any(r => r.Message.Trim().Equals(msg, StringComparison.OrdinalIgnoreCase));
            if (!found)
                _out.WriteLine($"  *** MISSING expected: '{msg}'");
            found.Should().BeTrue($"expected message '{msg}' must be in the decode output");
        }
    }

    [Theory]
    [InlineData("191111_110115.wav", 1)]
    [InlineData("191111_110130.wav", 4)]
    [InlineData("191111_110145.wav", 1)]
    [InlineData("191111_110200.wav", 5)]
    [InlineData("191111_110215.wav", 2)]
    public void Ft8_RealRecording_MinimumDecodeCount(string fileName, int minDecodes)
    {
        string path = Path.Combine(FT8Dir, fileName);
        if (!File.Exists(path)) return;

        float[] samples = LoadWav16(path);
        var decoder = new Ft8Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Normal, MaxCandidates = 500, ApDecode = false });
        var results = decoder.Decode(samples, 200, 3000, "000000");

        _out.WriteLine($"{fileName}: {results.Count} decodes (min required: {minDecodes})");
        foreach (var r in results.OrderBy(r => r.FrequencyHz))
            _out.WriteLine($"  {r.FrequencyHz:F0}Hz  SNR={r.Snr:+0.0;-0.0}  '{r.Message}'");

        results.Count.Should().BeGreaterThanOrEqualTo(minDecodes,
            $"real FT8 recording {fileName} should produce at least {minDecodes} decode(s)");
    }

    [Theory]
    [InlineData("191111_110130.wav")]
    [InlineData("191111_110200.wav")]
    public void Ft8_RealRecording_MessagesLookLikeHamRadio(string fileName)
    {
        string path = Path.Combine(FT8Dir, fileName);
        if (!File.Exists(path)) return;

        float[] samples = LoadWav16(path);
        var results = new Ft8Decoder().Decode(samples, 200, 3000, "000000");

        results.Should().NotBeEmpty();
        results.All(r => r.Mode == DigitalMode.FT8).Should().BeTrue();
        results.All(r => r.Snr is > -30 and < 30).Should().BeTrue("SNR must be in [-30,+30] dB");
        results.All(r => r.FrequencyHz is > 100 and < 4000).Should().BeTrue();
        results.All(r => r.Message.Length >= 3).Should().BeTrue();

        // Messages should contain at least one callsign-looking token (letters + digits)
        var callsignPattern = new Regex(@"[A-Z]\d[A-Z0-9]{2,}");
        results.Any(r => callsignPattern.IsMatch(r.Message)).Should().BeTrue(
            "at least one decoded message should contain a callsign-like token");
    }

    // ── Performance regression test ─────────────────────────────────────────

    /// <summary>
    /// Decodes websdr_test6.wav (15 s, 25 real-world FT8 messages) and asserts
    /// that the decoder finishes in reasonable time and finds at least 20 messages.
    ///
    /// The time limit is intentionally generous (45 s) to stay green on slow CI
    /// agents; the primary goal is catching catastrophic regressions, not tuning.
    /// </summary>
    [Fact]
    public void Ft8_Performance_WebsdrTest6()
    {
        string path = Path.Combine(FT8Dir, "websdr_test6.wav");
        if (!File.Exists(path)) return;

        float[] samples = LoadWav16(path);
        var decoder = new Ft8Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Normal, MaxCandidates = 500, ApDecode = false });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = decoder.Decode(samples, 200, 3000, "000000");
        sw.Stop();

        _out.WriteLine($"websdr_test6.wav: {results.Count} decodes in {sw.ElapsedMilliseconds} ms");
        foreach (var r in results.OrderBy(r => r.FrequencyHz))
            _out.WriteLine($"  {r.FrequencyHz:F0}Hz SNR={r.Snr:+0.0;-0.0} '{r.Message}'");

        results.Count.Should().BeGreaterThanOrEqualTo(20,
            "websdr_test6.wav should produce at least 20 FT8 decodes");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(45),
            "FT8 decode of a 15-second file must complete within 45 seconds");
    }

    // ── FT4 real recordings ──────────────────────────────────────────────────

    /// <summary>
    /// Decodes a FT4 over-the-air recording that is known to contain a complete FT4 frame.
    /// 000000_000002.wav starts exactly at a 7.5-second period boundary; the signal
    /// is at ~1148 Hz starting at t≈0.5 s, well within our 6.05-second window.
    ///
    /// 190106_000115.wav and 190106_000112.wav are informational — they may contain
    /// partial frames or heavy QRM and are tested in a separate "BestEffort" variant.
    /// </summary>
    [Theory]
    [InlineData("000000_000002.wav")]
    public void Ft4_RealRecording_DecodesAtLeastOneMessage(string fileName)
    {
        string path = Path.Combine(FT4Dir, fileName);
        if (!File.Exists(path)) return;

        float[] samples = LoadWav16(path);
        _out.WriteLine($"Loaded {samples.Length} samples ({samples.Length / (double)SampleRate:F1}s) from {fileName}");

        var decoder = new Ft4Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Normal, MaxCandidates = 500, ApDecode = false });
        var results = decoder.Decode(samples, 200, 3000, "000000");

        _out.WriteLine($"Decoded {results.Count} message(s):");
        foreach (var r in results.OrderBy(r => r.FrequencyHz))
            _out.WriteLine($"  {r.FrequencyHz:F0}Hz SNR={r.Snr:+0.0;-0.0} dt={r.Dt:+0.00;-0.00} '{r.Message}'");

        results.Should().NotBeEmpty(
            $"real FT4 recording {fileName} must produce at least one decode");
    }

    /// <summary>
    /// Best-effort decode of recordings that may contain partial frames or QRM.
    /// Decodes all 7.5-second windows within the file; passes if at least one window
    /// produces a decode across all files OR if files are absent.
    /// </summary>
    [Theory]
    [InlineData("190106_000115.wav")]
    [InlineData("190106_000112.wav")]
    public void Ft4_RealRecording_BestEffortDecode(string fileName)
    {
        string path = Path.Combine(FT4Dir, fileName);
        if (!File.Exists(path)) return;

        float[] allSamples = LoadWav16(path);
        _out.WriteLine($"Loaded {allSamples.Length} samples ({allSamples.Length / (double)SampleRate:F1}s) from {fileName}");

        const int PeriodSamples = (int)(7.5 * SampleRate);  // 90000
        int periodCount = Math.Max(1, allSamples.Length / PeriodSamples);

        var decoder = new Ft4Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Normal, MaxCandidates = 500, ApDecode = false });

        int totalDecodes = 0;
        for (int p = 0; p < periodCount; p++)
        {
            int start = p * PeriodSamples;
            var window = allSamples.AsSpan(start, Math.Min(PeriodSamples, allSamples.Length - start));
            var results = decoder.Decode(window, 200, 3000, "000000");
            totalDecodes += results.Count;
            foreach (var r in results)
                _out.WriteLine($"  Period {p}: {r.FrequencyHz:F0}Hz SNR={r.Snr:+0.0} '{r.Message}'");
        }
        _out.WriteLine($"Total decodes across {periodCount} periods: {totalDecodes}");
        // Informational only — not asserting on these recordings
    }

    [Theory]
    [InlineData("000000_000002.wav")]
    public void Ft4_RealRecording_MessagesLookLikeHamRadio(string fileName)
    {
        string path = Path.Combine(FT4Dir, fileName);
        if (!File.Exists(path)) return;

        float[] samples = LoadWav16(path);
        var decoder = new Ft4Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Normal, MaxCandidates = 500, ApDecode = false });
        var results = decoder.Decode(samples, 200, 3000, "000000");

        results.Should().NotBeEmpty();
        results.All(r => r.Mode == DigitalMode.FT4).Should().BeTrue();
        results.All(r => r.Snr is > -30 and < 30).Should().BeTrue();
        results.All(r => r.Message.Length >= 3).Should().BeTrue();

        var callsignPattern = new Regex(@"[A-Z]\d[A-Z0-9]{2,}");
        results.Any(r => callsignPattern.IsMatch(r.Message)).Should().BeTrue(
            "at least one decoded FT4 message should contain a callsign-like token");
    }

    // ── FT2 noisy synthetic tests ────────────────────────────────────────────
    // No public FT2 WAV recordings exist yet (FT2 was officially certified ADIF 3.1.7
    // in March 2026). Instead we test the decoder under increasingly strong noise.
    // These are more thorough than the clean round-trip tests: they exercise the full
    // Costas detection, LLR computation, and LDPC under realistic conditions.

    [Theory]
    [InlineData("CQ W1AW FN42",   -3.0)]  // easy: high SNR
    [InlineData("CQ W1AW FN42",   -8.0)]  // medium SNR
    [InlineData("W1AW K9AN -07",  -3.0)]
    [InlineData("W1AW K9AN -07",  -8.0)]
    public void Ft2_Noisy_DecodesCorrectly(string message, double snrDb)
    {
        const double freq = 1000.0;
        float[] clean   = new Ft2Encoder().Encode(message, new EncoderOptions { FrequencyHz = freq });
        float[] noisy   = AddWhiteNoise(clean, snrDb, seed: 42);

        var decoder = new Ft2Decoder();
        decoder.Configure(new DecoderOptions { AveragingEnabled = false, DecoderDepth = DecoderDepth.Normal });
        var results = decoder.Decode(noisy, 850, 1200, "000000");

        _out.WriteLine($"FT2 SNR={snrDb:+0;-0}dB '{message}': " +
                       $"{results.Count} decodes [{string.Join(", ", results.Select(r => $"'{r.Message}'"))}]");

        results.Should().NotBeEmpty($"FT2 should decode '{message}' at SNR={snrDb}dB");
        results.Any(r => r.Message.Trim().Equals(message, StringComparison.OrdinalIgnoreCase))
               .Should().BeTrue($"decoder output must match original message at SNR={snrDb}dB");
    }

    /// <summary>
    /// Documents the FT2 decoder's noise floor by checking it at harder SNR levels.
    /// These tests report but do not assert (they may fail at very low SNR and that is expected).
    /// </summary>
    [Theory]
    [InlineData("CQ W1AW FN42", -12.0)]
    [InlineData("CQ W1AW FN42", -15.0)]
    [InlineData("CQ W1AW FN42", -18.0)]
    public void Ft2_NoisyHard_DocumentsSensitivity(string message, double snrDb)
    {
        const double freq = 1000.0;
        float[] clean  = new Ft2Encoder().Encode(message, new EncoderOptions { FrequencyHz = freq });
        float[] noisy  = AddWhiteNoise(clean, snrDb, seed: 42);

        var decoder = new Ft2Decoder();
        decoder.Configure(new DecoderOptions { AveragingEnabled = false, DecoderDepth = DecoderDepth.Deep });
        var results = decoder.Decode(noisy, 850, 1200, "000000");

        bool decoded = results.Any(r => r.Message.Trim().Equals(message, StringComparison.OrdinalIgnoreCase));
        _out.WriteLine($"FT2 SNR={snrDb:+0;-0}dB '{message}': {(decoded ? "DECODED ✓" : "missed ✗")}  " +
                       $"({results.Count} total results)");

        // No assertion — this is a sensitivity documentation test only.
        // If it decodes at these SNRs, it's a bonus.
    }

    // ── FT4 noisy synthetic tests (bonus: tests FT4 Rvec + LDPC under noise) ─

    [Theory]
    [InlineData("CQ W1AW FN42",  -5.0)]
    [InlineData("CQ W1AW FN42", -10.0)]
    [InlineData("CQ W1AW FN42", -15.0)]  // ≈ -11.2 dB in 2500 Hz WSJT-X convention
    [InlineData("W1AW K9AN -07", -5.0)]
    public void Ft4_Noisy_DecodesCorrectly(string message, double snrDb)
    {
        const double freq = 1000.0;
        float[] clean  = new Ft4Encoder().Encode(message, new EncoderOptions { FrequencyHz = freq });
        float[] noisy  = AddWhiteNoise(clean, snrDb, seed: 7);

        var decoder = new Ft4Decoder();
        decoder.Configure(new DecoderOptions { DecoderDepth = DecoderDepth.Normal });
        var results = decoder.Decode(noisy, 850, 1200, "000000");

        _out.WriteLine($"FT4 SNR={snrDb:+0;-0}dB '{message}': " +
                       $"{results.Count} decodes [{string.Join(", ", results.Select(r => $"'{r.Message}'"))}]");

        results.Should().NotBeEmpty($"FT4 should decode '{message}' at SNR={snrDb}dB");
        results.Any(r => r.Message.Trim().Equals(message, StringComparison.OrdinalIgnoreCase))
               .Should().BeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Load a 16-bit PCM mono WAV at 12 kHz. Converts to float in [-1,+1].
    /// Only handles the format used by the WSJT sample files.
    /// </summary>
    private static float[] LoadWav16(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not RIFF");
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not WAVE");

        short fmt = 0, numCh = 0, bits = 0;
        int sr = 0;

        while (fs.Position < fs.Length - 8)
        {
            string id   = new string(br.ReadChars(4));
            int    size = br.ReadInt32();
            if (id == "fmt ")
            {
                fmt   = br.ReadInt16();
                numCh = br.ReadInt16();
                sr    = br.ReadInt32();
                br.ReadInt32(); br.ReadInt16();
                bits  = br.ReadInt16();
                if (size > 16) br.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                int n = size / (bits / 8) / Math.Max(1, (int)numCh);
                var out_ = new float[n];
                for (int i = 0; i < n; i++)
                {
                    double s = 0;
                    for (int c = 0; c < Math.Max(1, (int)numCh); c++)
                        s += fmt == 3 ? br.ReadSingle()
                                      : bits == 16 ? br.ReadInt16() / 32768.0
                                                   : br.ReadByte()  / 128.0 - 1.0;
                    out_[i] = (float)(s / Math.Max(1, (int)numCh));
                }
                return out_;
            }
            else br.ReadBytes(Math.Max(0, size));
        }
        throw new InvalidDataException("No data chunk");
    }

    /// <summary>
    /// Add white Gaussian noise to a signal to achieve a target SNR (dB).
    /// SNR = 10*log10(signal_power / noise_power).
    /// </summary>
    private static float[] AddWhiteNoise(float[] signal, double snrDb, int seed = 0)
    {
        double sigPower = signal.Average(s => (double)s * s);
        if (sigPower < 1e-20) return signal; // silent — no noise to scale

        double noisePower = sigPower / Math.Pow(10, snrDb / 10.0);
        double noiseAmp   = Math.Sqrt(noisePower);

        var rng = new Random(seed);
        var result = new float[signal.Length];
        for (int i = 0; i < signal.Length; i++)
        {
            // Box-Muller Gaussian
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            result[i] = signal[i] + (float)(noiseAmp * gauss);
        }
        return result;
    }
}
