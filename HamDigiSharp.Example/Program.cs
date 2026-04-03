// HamDigiSharp.Example — decodes FT8 periods from a WAV file.
//
// Usage:
//   HamDigiSharp.Example <wav> [freqLow=200] [freqHigh=3000] [depth=2] [maxCand=500] [minSync=2.5] [startUtc=HHMMSS]
//
//   wav        : Any sample rate, mono or stereo.  Automatically resampled to 12 kHz.
//   freqLow    : Lower edge of decode window in Hz (default 200).
//   freqHigh   : Upper edge of decode window in Hz (default 3000).
//   depth      : LDPC OSD depth 1-4 (default 2).
//   maxCand    : Maximum sync candidates per period (default 500).
//   minSync    : Minimum sync score in dB (default 2.5).
//   startUtc   : UTC start time of the recording as HHMMSS, e.g. 231800.
//                Used to label each period; optional.

using HamDigiSharp.Decoders.Ft8;
using HamDigiSharp.Dsp;
using HamDigiSharp.Models;

const int Ft8SampleRate  = 12000;
const int Ft8PeriodSec   = 15;
const int Ft8PeriodSamples = Ft8SampleRate * Ft8PeriodSec; // 180 000

// ── Parse arguments ────────────────────────────────────────────────────────────
string wavPath  = args.Length > 0 ? args[0] : "";
double freqLow  = args.Length > 1 ? double.Parse(args[1]) : 200;
double freqHigh = args.Length > 2 ? double.Parse(args[2]) : 3000;
int    rawDepth = args.Length > 3 ? int.Parse(args[3])    : 2;
DecoderDepth depth = rawDepth switch { 1 => DecoderDepth.Fast, 3 => DecoderDepth.Deep, _ => DecoderDepth.Normal };
int    maxCand  = args.Length > 4 ? int.Parse(args[4])    : 500;
float  minSync  = args.Length > 5 ? float.Parse(args[5])  : 2.5f;
string? startArg = args.Length > 6 ? args[6] : null;

if (string.IsNullOrEmpty(wavPath) || !File.Exists(wavPath))
{
    Console.WriteLine("Usage: HamDigiSharp.Example <wav> [freqLow=200] [freqHigh=3000] [depth=2] [maxCand=500] [minSync=2.5] [startUtc=HHMMSS]");
    Console.WriteLine();
    Console.WriteLine("  wav       Any sample rate / channel count WAV.  Resampled to 12 kHz automatically.");
    Console.WriteLine("  startUtc  UTC timestamp of the first sample, e.g. 231800 = 23:18:00.");
    Console.WriteLine("            When given, each 15-second period is labelled with its UTC time.");
    return 1;
}

// ── Load WAV ───────────────────────────────────────────────────────────────────
float[] rawSamples;
int     srcRate;
if (!LoadWav(wavPath, out rawSamples, out srcRate))
{
    Console.Error.WriteLine($"Cannot read WAV: {wavPath}");
    return 2;
}
Console.WriteLine($"Loaded {rawSamples.Length} samples @ {srcRate} Hz  ({rawSamples.Length / (double)srcRate:F1} s)  from {Path.GetFileName(wavPath)}");

// ── Resample to 12 kHz if needed ──────────────────────────────────────────────
float[] samples;
if (srcRate == Ft8SampleRate)
{
    samples = rawSamples;
}
else
{
    Console.WriteLine($"Resampling {srcRate} Hz → {Ft8SampleRate} Hz …");
    var resampler = new Resampler(srcRate, Ft8SampleRate);
    samples = resampler.Process(rawSamples.AsSpan());
}

// ── Split into 15-second periods and decode each ───────────────────────────────
// Use Floor so a file that is slightly shorter than N full periods doesn't produce
// a tiny zero-padded tail period that always shows "(no decodes)".
// Signals that extend across the boundary are still found because the decoder
// searches a DT window of ±2.5 s around the nominal period start.
int totalPeriods = Math.Max(1, (int)Math.Floor((double)samples.Length / Ft8PeriodSamples));
TimeSpan? startTime = ParseUtcTime(startArg);

Console.WriteLine($"Decoding {totalPeriods} × 15 s period(s)  {freqLow}–{freqHigh} Hz");
Console.WriteLine();

var decoder = new Ft8Decoder();
decoder.Configure(new DecoderOptions
{
    DecoderDepth  = depth,
    MaxCandidates = maxCand,
    MinSyncDb     = minSync,
    ApDecode      = false,
});

int totalDecodes = 0;
for (int p = 0; p < totalPeriods; p++)
{
    int start  = p * Ft8PeriodSamples;
    int length = Math.Min(Ft8PeriodSamples, samples.Length - start);

    // Pad to a full period if the last chunk is short
    float[] period;
    if (length == Ft8PeriodSamples)
    {
        period = samples[start..(start + length)];
    }
    else
    {
        period = new float[Ft8PeriodSamples];
        samples.AsSpan(start, length).CopyTo(period);
    }

    // Compute UTC label for this period
    string utcLabel;
    if (startTime.HasValue)
    {
        var t = startTime.Value + TimeSpan.FromSeconds(p * Ft8PeriodSec);
        t = TimeSpan.FromSeconds(t.TotalSeconds % 86400); // wrap midnight
        utcLabel = $"{t.Hours:D2}{t.Minutes:D2}{t.Seconds:D2}";
    }
    else
    {
        utcLabel = $"P{p:D3}";
    }

    var results = decoder.Decode(period, freqLow, freqHigh, utcLabel);
    totalDecodes += results.Count;

    foreach (var r in results.OrderBy(r => r.FrequencyHz))
        Console.WriteLine($"  {r}");
}

Console.WriteLine();
Console.WriteLine($"Total decodes: {totalDecodes}");
return 0;

// ── Helpers ────────────────────────────────────────────────────────────────────

static TimeSpan? ParseUtcTime(string? s)
{
    if (s is null || s.Length != 6) return null;
    if (!int.TryParse(s[..2], out int h) ||
        !int.TryParse(s[2..4], out int m) ||
        !int.TryParse(s[4..6], out int sec)) return null;
    return new TimeSpan(h, m, sec);
}

/// <summary>
/// Loads a WAV file (PCM-16, PCM-32f, or PCM-8u; mono or stereo).
/// Stereo is mixed to mono by averaging channels.
/// Returns false if the file cannot be parsed.
/// </summary>
static bool LoadWav(string path, out float[] samples, out int sampleRate)
{
    samples    = [];
    sampleRate = 0;
    try
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        if (new string(br.ReadChars(4)) != "RIFF") return false;
        br.ReadInt32(); // chunk size
        if (new string(br.ReadChars(4)) != "WAVE") return false;

        short audioFmt = 0, numCh = 0, bitsPerSample = 0;

        while (fs.Position < fs.Length - 8)
        {
            string id   = new string(br.ReadChars(4));
            int    size = br.ReadInt32();

            if (id == "fmt ")
            {
                audioFmt      = br.ReadInt16(); // 1=PCM, 3=IEEE float
                numCh         = br.ReadInt16();
                sampleRate    = br.ReadInt32();
                br.ReadInt32(); // byte rate
                br.ReadInt16(); // block align
                bitsPerSample = br.ReadInt16();
                if (size > 16) br.ReadBytes(size - 16);

                if (audioFmt != 1 && audioFmt != 3)
                {
                    Console.Error.WriteLine($"Unsupported WAV format {audioFmt} (need PCM=1 or IEEE-float=3).");
                    return false;
                }
            }
            else if (id == "data")
            {
                int bytesPerSample = bitsPerSample / 8;
                int ch = Math.Max((int)numCh, 1);
                int nSamples = size / bytesPerSample / ch;

                var pcm = new float[nSamples];
                for (int i = 0; i < nSamples; i++)
                {
                    double sum = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        double v = audioFmt == 3
                            ? br.ReadSingle()
                            : bitsPerSample == 16
                                ? br.ReadInt16() / 32768.0
                                : br.ReadByte()  / 128.0 - 1.0; // 8-bit unsigned
                        sum += v;
                    }
                    pcm[i] = (float)(sum / ch); // mix to mono
                }
                samples = pcm;
                return true;
            }
            else
            {
                br.ReadBytes(Math.Max(0, size));
            }
        }
        return false;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"WAV error: {ex.Message}");
        return false;
    }
}
