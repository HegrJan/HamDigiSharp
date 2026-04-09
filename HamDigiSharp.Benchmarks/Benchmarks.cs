using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using HamDigiSharp.Codecs;
using HamDigiSharp.Decoders.Ft8;
using HamDigiSharp.Decoders.Ft4;
using HamDigiSharp.Decoders.Ft2;
using HamDigiSharp.Encoders;
using HamDigiSharp.Models;
using System.Diagnostics;

// Quick mode: args = ["quick"] → Stopwatch timing (10 warm-up + 10 measure)
// Full mode:  no args → BenchmarkDotNet full statistical run
if (args.Length > 0 && args[0] == "quick")
{
    RunQuick();
}
else
{
    BenchmarkSwitcher.FromAssembly(typeof(DecodeBenchmarks).Assembly).RunAll();
}

static void RunQuick()
{
    var b = new DecodeBenchmarks();
    b.Setup();

    static long MeasureMs(Action fn, int warmup = 5, int runs = 10)
    {
        for (int i = 0; i < warmup; i++) fn();
        long best = long.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            fn();
            sw.Stop();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        return best;
    }

    Console.WriteLine("=== Quick timing (best of 10 runs) ===");
    Console.WriteLine($"FT8  decode: {MeasureMs(b.Ft8Decode),6} ms");
    Console.WriteLine($"FT4  decode: {MeasureMs(b.Ft4Decode),6} ms");
    Console.WriteLine($"FT2  decode: {MeasureMs(b.Ft2Decode),6} ms");
    Console.WriteLine($"LDPC BP    : {MeasureMs(b.LdpcBpDecode),6} ms");
    Console.WriteLine($"LDPC OSD-1 : {MeasureMs(b.LdpcOsdDecode),6} ms");
}

[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 5)]
public class DecodeBenchmarks
{
    private float[] _ft8Samples  = [];
    private float[] _ft4Samples  = [];
    private float[] _ft2Samples  = [];
    private bool[]  _llr174Msg   = new bool[77];
    private bool[]  _llr174Cw    = new bool[174];
    private double[] _llrInput   = new double[174];
    private bool[]   _apMask     = new bool[174];

    [GlobalSetup]
    public void Setup()
    {
        const double FreqHz = 1000.0;
        var opts = new EncoderOptions { FrequencyHz = FreqHz };

        float[] ft8Signal = new Ft8Encoder().Encode("CQ W1AW FN42", opts);
        _ft8Samples = new float[12000 * 15];
        ft8Signal.AsSpan(0, Math.Min(ft8Signal.Length, _ft8Samples.Length)).CopyTo(_ft8Samples);
        AddNoise(_ft8Samples, snrDb: 6, seed: 1);

        float[] ft4Signal = new Ft4Encoder().Encode("CQ W1AW FN42", opts);
        _ft4Samples = new float[90000];
        ft4Signal.AsSpan(0, Math.Min(ft4Signal.Length, _ft4Samples.Length)).CopyTo(_ft4Samples);
        AddNoise(_ft4Samples, snrDb: 3, seed: 2);

        float[] ft2Signal = new Ft2Encoder().Encode("CQ W1AW FN42",
            new EncoderOptions { FrequencyHz = 882.0 });
        _ft2Samples = new float[45000];
        ft2Signal.AsSpan(0, Math.Min(ft2Signal.Length, _ft2Samples.Length)).CopyTo(_ft2Samples);
        AddNoise(_ft2Samples, snrDb: 0, seed: 3);

        var rng = new Random(42);
        for (int i = 0; i < 174; i++)
            _llrInput[i] = (rng.NextDouble() * 2.0 - 1.0) * 4.0;
    }

    [Benchmark(Description = "FT8 Decode (200 cand)")]
    public void Ft8Decode()
    {
        var dec = new Ft8Decoder();
        dec.Configure(new DecoderOptions { MaxCandidates = 200 });
        dec.Decode(_ft8Samples, 800, 1200, "000000");
    }

    [Benchmark(Description = "FT4 Decode (200 cand)")]
    public void Ft4Decode()
    {
        var dec = new Ft4Decoder();
        dec.Configure(new DecoderOptions { MaxCandidates = 200 });
        dec.Decode(_ft4Samples, 800, 1200, "000000");
    }

    [Benchmark(Description = "FT2 Decode (200 cand)")]
    public void Ft2Decode()
    {
        var dec = new Ft2Decoder();
        dec.Configure(new DecoderOptions { MaxCandidates = 200 });
        dec.Decode(_ft2Samples, 680, 1080, "000000");
    }

    [Benchmark(Description = "LDPC(174,91) BP decode x1000")]
    public void LdpcBpDecode()
    {
        for (int i = 0; i < 1000; i++)
            Ldpc174_91.BpDecode(_llrInput, _apMask, _llr174Msg, _llr174Cw, out _);
    }

    [Benchmark(Description = "LDPC(174,91) OSD order-1 x1000")]
    public void LdpcOsdDecode()
    {
        for (int i = 0; i < 1000; i++)
            Ldpc174_91.OsdDecode(_llrInput, _apMask, 1, _llr174Msg, _llr174Cw, out _, out _);
    }

    private static void AddNoise(float[] buf, double snrDb, int seed)
    {
        double sigPow = 0;
        foreach (var s in buf) sigPow += s * s;
        sigPow /= buf.Length;
        if (sigPow < 1e-20) return;
        double noiseAmp = Math.Sqrt(sigPow / Math.Pow(10, snrDb / 10.0));
        var rng = new Random(seed);
        for (int i = 0; i < buf.Length; i++)
            buf[i] += (float)(noiseAmp * (rng.NextDouble() * 2 - 1) * Math.Sqrt(3));
    }
}
