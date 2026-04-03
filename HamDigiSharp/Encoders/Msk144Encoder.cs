using HamDigiSharp.Abstractions;
using HamDigiSharp.Codecs;
using HamDigiSharp.Models;

namespace HamDigiSharp.Encoders;

/// <summary>
/// Shared MSK encoder for MSK144 and MSKMS (MSK40) — only the sync word differs.
/// </summary>
public abstract class MskEncoderBase : IDigitalModeEncoder
{
    private const int NSym  = 144;
    private const int NSps  = 6;
    private const int NWave = NSym * NSps;  // 864 samples @ 12 kHz = 72 ms

    private readonly int[] _sync8;

    protected MskEncoderBase(int[] sync8) => _sync8 = sync8;

    public abstract DigitalMode Mode { get; }

    public float[] Encode(string message, EncoderOptions options)
    {
        bool[] msg77 = new bool[77];
        if (!MessagePack77.TryPack77(message, msg77))
            throw new ArgumentException($"Cannot encode message: \"{message}\"");

        bool[] codeword = new bool[128];
        Ldpc128_90.Encode(msg77, codeword);

        // Frame: [sync8] [codeword 0..47] [sync8] [codeword 48..127] [sentinel=0]
        int[] bitseq = new int[145];
        for (int i = 0; i < 8;  i++) bitseq[i]      = _sync8[i];
        for (int i = 0; i < 48; i++) bitseq[8  + i] = codeword[i]      ? 1 : 0;
        for (int i = 0; i < 8;  i++) bitseq[56 + i] = _sync8[i];
        for (int i = 0; i < 80; i++) bitseq[64 + i] = codeword[48 + i] ? 1 : 0;
        // bitseq[144] = 0 — wrap-around sentinel for differential encoding

        // Map 0/1 → −1/+1
        for (int i = 0; i < 144; i++) bitseq[i] = 2 * bitseq[i] - 1;

        // MSK differential encoding → tone indices 0/1
        int[] i4tone = new int[144];
        for (int i = 0; i < 72; i++)
        {
            i4tone[2 * i]     = (bitseq[2 * i + 1] * bitseq[2 * i] + 1) / 2;
            i4tone[2 * i + 1] = -(bitseq[2 * i + 1] * bitseq[(2 * i + 1) % 144 + 1] - 1) / 2;
        }
        for (int i = 0; i < 144; i++) i4tone[i] = -i4tone[i] + 1;

        // Continuous-phase FSK synthesis
        double f0    = options.FrequencyHz > 0 ? options.FrequencyHz : 1000.0;
        double df    = 1000.0;
        double twoPi = 2.0 * Math.PI;
        float[] wave = new float[NWave];
        double pCos = 1.0, pSin = 0.0;
        int k = 0;

        for (int m = 0; m < NSym; m++)
        {
            double dpha   = twoPi * (f0 + i4tone[m] * df) / 12000.0;
            double rotCos = Math.Cos(dpha), rotSin = Math.Sin(dpha);
            for (int i = 0; i < NSps; i++)
            {
                double nCos = pCos * rotCos - pSin * rotSin;
                pSin = pCos * rotSin + pSin * rotCos;
                pCos = nCos;
                wave[k++] = (float)(options.Amplitude * pSin);
            }
        }

        return wave;
    }
}

/// <summary>
/// MSK144 v2 audio encoder (864 float samples @ 12 kHz = 72 ms).
/// Mirrors MSHV's <c>GenMsk::genmsk144</c> (v2 path, non-MSKMS).
/// </summary>
public sealed class Msk144Encoder : MskEncoderBase
{
    private static readonly int[] Sync8 = { 0, 1, 1, 1, 0, 0, 1, 0 };

    public Msk144Encoder() : base(Sync8) { }

    public override DigitalMode Mode => DigitalMode.MSK144;
}

/// <summary>
/// MSKMS (MSK40) audio encoder — identical to MSK144 except the sync word.
/// Uses sync <c>{1,0,1,1,0,0,0,1}</c> matching MSHV's <c>genmsk40</c>.
/// </summary>
public sealed class Msk40Encoder : MskEncoderBase
{
    private static readonly int[] Sync8 = { 1, 0, 1, 1, 0, 0, 0, 1 };

    public Msk40Encoder() : base(Sync8) { }

    public override DigitalMode Mode => DigitalMode.MSKMS;
}
