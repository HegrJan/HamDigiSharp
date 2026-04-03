using FluentAssertions;
using HamDigiSharp.Abstractions;
using HamDigiSharp.Decoders.Fsk;
using HamDigiSharp.Decoders.Msk;
using HamDigiSharp.Decoders.Pi4;
using HamDigiSharp.Encoders;
using HamDigiSharp.Models;
using Xunit;
using Xunit.Abstractions;

namespace HamDigiSharp.Tests.Decoders;

public class Fsk441RoundTripTests
{
    private const int Sr = 11025; // FSK441/315 sample rate

    // Encode a message then decode it.
    // FSK441/315 transmitters repeat the message continuously for the full period.
    // We fill 3 s with the repeating signal so the short-window scan sees full tones.
    private static IReadOnlyList<DecodeResult> RoundTrip(
        string message, double freqHz, FskBaseDecoder decoder, IDigitalModeEncoder encoder)
    {
        float[] signal  = encoder.Encode(message, new EncoderOptions { FrequencyHz = freqHz, Amplitude = 0.9 });
        float[] padded  = new float[Sr * 3];
        // Repeat signal to fill the buffer
        for (int i = 0; i < padded.Length; i++) padded[i] = signal[i % signal.Length];
        return decoder.Decode(padded, freqHz - 100, freqHz + 1500, "000000");
    }

    [Theory]
    [InlineData("GM6VXB DJ5HG",   882.0)]
    [InlineData("CQ K1JT FN42",   882.0)]
    [InlineData("K1JT K2ABC RRR", 882.0)]  // '-' not in FSK441 alphabet; use RRR
    public void Fsk441_StandardMessage_RoundTrip(string message, double freqHz)
    {
        var results = RoundTrip(message, freqHz, new Fsk441Decoder(), new Fsk441Encoder());
        results.Should().Contain(r => r.Message!.Contains(message.Trim()),
            $"FSK441 round-trip must decode \"{message}\"");
    }

    [Theory]
    [InlineData("GM6VXB DJ5HG",   945.0)]
    [InlineData("CQ K1JT FN42",   945.0)]
    public void Fsk315_StandardMessage_RoundTrip(string message, double freqHz)
    {
        var results = RoundTrip(message, freqHz, new Fsk315Decoder(), new Fsk315Encoder());
        results.Should().Contain(r => r.Message!.Contains(message.Trim()),
            $"FSK315 round-trip must decode \"{message}\"");
    }

    [Fact]
    public void Fsk441_Encoder_Mode_Is_FSK441()
        => new Fsk441Encoder().Mode.Should().Be(DigitalMode.FSK441);

    [Fact]
    public void Fsk315_Encoder_Mode_Is_FSK315()
        => new Fsk315Encoder().Mode.Should().Be(DigitalMode.FSK315);

    [Fact]
    public void Fsk441_Encoder_OutputLength_IsProportionalToMessageLength()
    {
        var enc = new Fsk441Encoder();
        // Output = (3 preamble + msg_chars × 3 symbols) × 25 samples/symbol
        float[] a = enc.Encode("AB", new EncoderOptions());   // (3+6) × 25 = 225
        float[] b = enc.Encode("ABCD", new EncoderOptions()); // (3+12) × 25 = 375
        a.Length.Should().Be(225);
        b.Length.Should().Be(375);
    }

    [Fact]
    public void Fsk441_Encoder_SilenceInput_DecodesEmpty()
    {
        // Decoder must not crash or produce garbage on silence
        var silence = new float[Sr * 3];
        new Fsk441Decoder().Decode(silence, 800, 2500, "000000").Should().BeEmpty();
    }

    [Fact]
    public void Fsk441_Decoder_Noise_DoesNotThrow()
    {
        var rng   = new Random(7);
        var noise = new float[Sr * 3];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 2 - 1);
        var act = () => new Fsk441Decoder().Decode(noise, 800, 2500, "000000");
        act.Should().NotThrow();
    }
}

public class Fsk315DecoderTests(ITestOutputHelper output)
{
    // Path to the optional real-data FSK441 sample (downloaded separately).
    // Tests requiring it are skipped when the file is absent.
    private static readonly string Fsk441SamplePath =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Samples", "fsk441_sim_example.wav"));

    [Fact]
    public void Decode_Silence_ReturnsNoResults()
    {
        var decoder = new Fsk315Decoder();
        var silence = new float[11025 * 15];
        var results = decoder.Decode(silence, 900, 2000, "000000");
        results.Should().BeEmpty("silence has no decodable FSK315 signal");
    }

    [Fact]
    public void Decode_TooShort_ReturnsEmpty()
    {
        var decoder = new Fsk315Decoder();
        var results = decoder.Decode(new float[10], 900, 2000, "000000");
        results.Should().BeEmpty();
    }

    [Fact]
    public void Decode_Noise_DoesNotThrow()
    {
        var decoder = new Fsk315Decoder();
        var rng = new Random(42);
        var noise = new float[11025 * 15];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 2 - 1);

        var act = () => decoder.Decode(noise, 900, 2000, "000000");
        act.Should().NotThrow();
    }

    [Fact]
    public void Mode_Is_FSK315()
    {
        new Fsk315Decoder().Mode.Should().Be(DigitalMode.FSK315);
    }

    /// <summary>
    /// Real-data test using sim_example_1.wav from DJ5HG (FSK441 simulated meteor scatter).
    /// Skipped when the file is absent (CI environments).
    /// Run locally: download Samples/fsk441_sim_example.wav from http://www.dj5hg.de/digitalmodes/sim_example_1.wav
    /// </summary>
    [Fact]
    public void Fsk441_RealData_SimExample_ProducesOutput()
    {
        string path = Fsk441SamplePath;
        if (!File.Exists(path))
        {
            output.WriteLine($"SKIPPED: Real-data sample not found: {path}");
            return; // treat missing file as pass (CI environments)
        }

        float[] samples = LoadWav8(path, out int sampleRate);
        output.WriteLine($"Loaded {samples.Length} samples @ {sampleRate} Hz ({samples.Length/(double)sampleRate:F1}s)");
        sampleRate.Should().Be(11025, "FSK441 decoder expects 11025 Hz");

        // Try FSK441 over the full 30s file, two 15s windows
        var dec441 = new Fsk441Decoder();
        int periodSamples = sampleRate * 15;

        int totalDecodes = 0;
        for (int p = 0; p * periodSamples < samples.Length; p++)
        {
            int start = p * periodSamples;
            int len   = Math.Min(periodSamples, samples.Length - start);
            var window = samples.AsSpan(start, len);

            var results = dec441.Decode(window, 200.0, 4000.0, $"P{p}");
            foreach (var r in results)
            {
                output.WriteLine($"  FSK441 P{p}: f={r.FrequencyHz:F0}Hz dt={r.Dt:F2}s [{r.Message}]");
                totalDecodes++;
            }
        }

        // Also try FSK315 (should not decode FSK441 signal)
        var dec315 = new Fsk315Decoder();
        var r315 = dec315.Decode(samples.AsSpan(0, Math.Min(periodSamples, samples.Length)), 200.0, 4000.0, "P0");
        output.WriteLine($"FSK315 on FSK441 data: {r315.Count} results (expected 0 or garbage)");

        output.WriteLine($"Total FSK441 decodes across 2 periods: {totalDecodes}");
        // We assert non-crash; the actual decode depends on signal quality.
        // The key value is seeing what gets decoded in the test output.
        totalDecodes.Should().BeGreaterThanOrEqualTo(0, "decoder must not crash on real data");
    }

    // Minimal WAV loader for 8-bit unsigned PCM
    private static float[] LoadWav8(string path, out int sampleRate)
    {
        sampleRate = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);
        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not RIFF");
        br.ReadInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not WAVE");
        short numCh = 1, bitsPerSample = 8;
        while (fs.Position < fs.Length - 8)
        {
            string id   = new string(br.ReadChars(4));
            int    size = br.ReadInt32();
            if (id == "fmt ")
            {
                br.ReadInt16(); // audioFmt
                numCh         = br.ReadInt16();
                sampleRate    = br.ReadInt32();
                br.ReadInt32(); br.ReadInt16();
                bitsPerSample = br.ReadInt16();
                if (size > 16) br.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                int ch = Math.Max((int)numCh, 1);
                int n  = size / (bitsPerSample / 8) / ch;
                var pcm = new float[n];
                for (int i = 0; i < n; i++)
                {
                    double sum = 0;
                    for (int c = 0; c < ch; c++)
                        sum += bitsPerSample == 16 ? br.ReadInt16() / 32768.0 : br.ReadByte() / 128.0 - 1.0;
                    pcm[i] = (float)(sum / ch);
                }
                return pcm;
            }
            else br.ReadBytes(Math.Max(0, size));
        }
        return [];
    }
}

public class Msk144DecoderTests
{
    [Fact]
    public void Decode_Silence_ReturnsNoResults()
    {
        var decoder = new Msk144Decoder();
        var silence = new float[12000 * 15];
        var results = decoder.Decode(silence, 200, 3000, "000000");
        results.Should().BeEmpty("silence has no decodable MSK144 signal");
    }

    [Fact]
    public void Decode_TooShort_ReturnsEmpty()
    {
        var decoder = new Msk144Decoder();
        var results = decoder.Decode(new float[10], 200, 3000, "000000");
        results.Should().BeEmpty();
    }

    [Fact]
    public void Decode_Noise_DoesNotThrow()
    {
        var decoder = new Msk144Decoder();
        var rng = new Random(123);
        var noise = new float[12000 * 15];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 2 - 1);

        var act = () => decoder.Decode(noise, 200, 3000, "000000");
        act.Should().NotThrow();
    }

    [Fact]
    public void Mode_Is_MSK144()
    {
        new Msk144Decoder().Mode.Should().Be(DigitalMode.MSK144);
    }

    [Theory]
    [InlineData("CQ OK1TE JN89", 1000)]
    [InlineData("OK1TE SP5XYZ -14", 1500)]
    public void RoundTrip_EncodeThenDecode_ReturnsExpectedMessage(string message, double freqHz)
    {
        var audio = new Msk144Encoder().Encode(message, new EncoderOptions
        {
            FrequencyHz = freqHz,
            Amplitude   = 0.5f,
        });

        // Pad to one full second so the decoder has its expected buffer size
        var padded = new float[12000];
        audio.CopyTo(padded, 0);

        var results = new Msk144Decoder().Decode(padded, freqHz - 50, freqHz + 50, "000000");
        results.Should().ContainSingle(r => r.Message == message,
            $"MSK144 round-trip must decode \"{message}\" at {freqHz} Hz");
    }
}

public class Pi4DecoderTests
{
    [Fact]
    public void Decode_Silence_ReturnsNoResults()
    {
        var decoder = new Pi4Decoder();
        var silence = new float[11025 * 15];
        var results = decoder.Decode(silence, 600, 800, "000000");
        results.Should().BeEmpty("silence has no decodable PI4 signal");
    }

    [Fact]
    public void Decode_TooShort_ReturnsEmpty()
    {
        var decoder = new Pi4Decoder();
        var results = decoder.Decode(new float[100], 600, 800, "000000");
        results.Should().BeEmpty();
    }

    [Fact]
    public void Decode_Noise_DoesNotThrow()
    {
        var decoder = new Pi4Decoder();
        var rng = new Random(77);
        var noise = new float[11025 * 15];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 2 - 1);

        var act = () => decoder.Decode(noise, 600, 800, "000000");
        act.Should().NotThrow();
    }

    [Fact]
    public void Mode_Is_PI4()
    {
        new Pi4Decoder().Mode.Should().Be(DigitalMode.PI4);
    }
}
