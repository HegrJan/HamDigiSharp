using HamDigiSharp.Abstractions;
using HamDigiSharp.Codecs;
using HamDigiSharp.Models;
using MathNet.Numerics;

namespace HamDigiSharp.Encoders;

/// <summary>
/// FT2 audio encoder. Converts a text message to 30,240 float PCM samples at 12 kHz
/// ((103+2) symbols × 288 samples/symbol = 2.52 s burst within a 3.75-second window).
/// Uses GFSK modulation with BT=1.0 and 4-FSK (2 bits per symbol).
/// Identical to FT4 in codec and frame structure; only the symbol rate differs:
/// NSps=288 (vs 576 for FT4), giving double the tone spacing: 41.67 Hz vs 20.83 Hz.
/// Mirrors MSHV's <c>GenFt2::genft2</c> (MSHV runs at 48 kHz using nsps=1152=4×288;
/// this implementation operates at the standard 12 kHz decoder sample rate).
/// </summary>
public sealed class Ft2Encoder : IDigitalModeEncoder
{
    public DigitalMode Mode => DigitalMode.FT2;

    private const int NSps  = 288;           // samples/symbol at 12 kHz (= MSHV 1152/4)
    private const int NSym  = 103;
    private const int NWave = (NSym + 2) * NSps; // 30 240

    // Four Costas sync arrays (identical to FT4)
    private static readonly int[] Icos4A = { 0, 1, 3, 2 };
    private static readonly int[] Icos4B = { 1, 0, 2, 3 };
    private static readonly int[] Icos4C = { 2, 3, 1, 0 };
    private static readonly int[] Icos4D = { 3, 2, 0, 1 };

    // Natural-binary to Gray for 4-FSK: 0→0, 1→1, 2→3, 3→2
    private static readonly int[] GrayMap4 = { 0, 1, 3, 2 };

    // 77-bit XOR scramble mask (identical to FT4, from MSHV gen_ft2.cpp)
    private static readonly bool[] Rvec =
    {
        false,true ,false,false,true ,false,true ,false,false,true ,
        false,true ,true ,true ,true ,false,true ,false,false,false,
        true ,false,false,true ,true ,false,true ,true ,false,true ,
        false,false,true ,false,true ,true ,false,false,false,false,
        true ,false,false,false,true ,false,true ,false,false,true ,
        true ,true ,true ,false,false,true ,false,true ,false,true ,
        false,true ,false,true ,true ,false,true ,true ,true ,true ,
        true ,false,false,false,true ,false,true
    };

    private static readonly double[] GfskPulseData = BuildGfskPulse();

    public float[] Encode(string message, EncoderOptions options)
    {
        var c77 = new bool[77];
        if (!MessagePack77.TryPack77(message, c77))
            throw new ArgumentException($"Cannot encode message: \"{message}\"");

        var scrambled = new bool[77];
        for (int i = 0; i < 77; i++)
            scrambled[i] = c77[i] ^ Rvec[i];

        var codeword = new bool[174];
        Ldpc174_91.Encode(scrambled, codeword);

        int[] tones = BuildToneSequence(codeword);
        return Modulate(tones, options.FrequencyHz, options.Amplitude);
    }

    internal static int[] BuildToneSequence(bool[] codeword)
    {
        var tones = new int[NSym];

        for (int i = 0; i < 4; i++)
        {
            tones[i]      = Icos4A[i];
            tones[33 + i] = Icos4B[i];
            tones[66 + i] = Icos4C[i];
            tones[99 + i] = Icos4D[i];
        }

        int k = 4;
        for (int j = 0; j < 87; j++)
        {
            if (j == 29) k += 4;
            if (j == 58) k += 4;
            int isym = (codeword[2 * j] ? 2 : 0) | (codeword[2 * j + 1] ? 1 : 0);
            tones[k++] = GrayMap4[isym];
        }

        return tones;
    }

    private static float[] Modulate(int[] tones, double freqHz, double amplitude)
    {
        var pulse = GfskPulseData;
        double dphiPeak = 2.0 * Math.PI / NSps;
        var dphi = new double[NWave];

        for (int j = 0; j < NSym; j++)
        {
            int ib = j * NSps;
            for (int i = 0; i < 3 * NSps; i++)
                dphi[ib + i] += dphiPeak * pulse[i] * tones[j];
        }

        double ofs = 2.0 * Math.PI * freqHz / 12000.0;
        double phi = 0.0;
        var samples = new float[NWave];
        for (int k = 0; k < NWave; k++)
        {
            samples[k] = (float)(amplitude * Math.Sin(phi));
            phi = (phi + dphi[k] + ofs) % (2.0 * Math.PI);
        }

        // Ramp in: first NSps samples
        for (int i = 0; i < NSps; i++)
        {
            float w = (float)((1.0 - Math.Cos(Math.PI * i / NSps)) / 2.0);
            samples[i] *= w;
        }
        // Ramp out: last NSps samples starting at (NSym+1)*NSps
        int k2 = (NSym + 1) * NSps;
        for (int i = 0; i < NSps; i++)
        {
            float w = (float)((1.0 + Math.Cos(Math.PI * i / NSps)) / 2.0);
            samples[k2 + i] *= w;
        }

        return samples;
    }

    private static double[] BuildGfskPulse()
    {
        var pulse = new double[3 * NSps];
        for (int i = 0; i < 3 * NSps; i++)
        {
            double t = ((double)i - 1.5 * NSps) / NSps;
            pulse[i] = GfskPulse(1.0, t);
        }
        return pulse;
    }

    private static double GfskPulse(double b, double t)
    {
        double c = Math.PI * Math.Sqrt(2.0 / Math.Log(2.0));
        return 0.5 * (SpecialFunctions.Erf(c * b * (t + 0.5)) - SpecialFunctions.Erf(c * b * (t - 0.5)));
    }
}
