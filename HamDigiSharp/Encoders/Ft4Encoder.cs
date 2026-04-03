using HamDigiSharp.Abstractions;
using HamDigiSharp.Codecs;
using HamDigiSharp.Models;
using MathNet.Numerics;

namespace HamDigiSharp.Encoders;

/// <summary>
/// FT4 audio encoder. Converts a text message to 60,480 float PCM samples at 12 kHz
/// ((103+2) symbols × 576 samples/symbol = 5.04 s).
/// Uses GFSK modulation with BT=1.0 and 4-FSK (2 bits per symbol).
/// Mirrors MSHV's <c>GenFt4::genft4</c> at 12 kHz.
/// </summary>
public sealed class Ft4Encoder : IDigitalModeEncoder
{
    public DigitalMode Mode => DigitalMode.FT4;

    private const int NSps  = 576;
    private const int NSym  = 103;
    private const int NWave = (NSym + 2) * NSps; // 60 480

    // Four Costas sync arrays for FT4
    private static readonly int[] Icos4A = { 0, 1, 3, 2 };
    private static readonly int[] Icos4B = { 1, 0, 2, 3 };
    private static readonly int[] Icos4C = { 2, 3, 1, 0 };
    private static readonly int[] Icos4D = { 3, 2, 0, 1 };

    // Natural-binary to Gray for 4-FSK: 0→0, 1→1, 2→3, 3→2
    private static readonly int[] GrayMap4 = { 0, 1, 3, 2 };

    // 77-bit XOR scramble mask (exactly as in MSHV gen_ft4.cpp)
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

    // GFSK pulse (3×NSPS samples, centred at 1.5×NSPS, BT=1.0)
    private static readonly double[] GfskPulseData = BuildGfskPulse();

    public float[] Encode(string message, EncoderOptions options)
    {
        // 1. Pack text → 77 bits
        var c77 = new bool[77];
        if (!MessagePack77.TryPack77(message, c77))
            throw new ArgumentException($"Cannot encode message: \"{message}\"");

        // 2. Scramble message bits with rvec (FT4-specific XOR mask)
        var scrambled = new bool[77];
        for (int i = 0; i < 77; i++)
            scrambled[i] = c77[i] ^ Rvec[i];

        // 3. LDPC encode scrambled bits → 174-bit codeword (adds CRC-14 + parity)
        var codeword = new bool[174];
        Ldpc174_91.Encode(scrambled, codeword);

        // 4. Map codeword bits → tone sequence (103 symbols)
        int[] tones = BuildToneSequence(codeword);

        // 5. GFSK modulate
        return Modulate(tones, options.FrequencyHz, options.Amplitude);
    }

    internal static int[] BuildToneSequence(bool[] codeword)
    {
        var tones = new int[NSym];

        // Insert four Costas sync blocks
        for (int i = 0; i < 4; i++)
        {
            tones[i]      = Icos4A[i];
            tones[33 + i] = Icos4B[i];
            tones[66 + i] = Icos4C[i];
            tones[99 + i] = Icos4D[i];
        }

        // Map 174 codeword bits → 87 2-bit symbols, fill data slots
        // Layout: [sync4][data29][sync4][data29][sync4][data29][sync4]
        int k = 4; // start after first sync block
        for (int j = 0; j < 87; j++)
        {
            if (j == 29) k += 4; // skip second sync block (positions 33..36)
            if (j == 58) k += 4; // skip third sync block (positions 66..69)
            int isym = (codeword[2 * j] ? 2 : 0) | (codeword[2 * j + 1] ? 1 : 0);
            tones[k++] = GrayMap4[isym];
        }

        return tones;
    }

    private static float[] Modulate(int[] tones, double freqHz, double amplitude)
    {
        var pulse = GfskPulseData;
        double dphiPeak = 2.0 * Math.PI / NSps;
        var dphi = new double[NWave]; // (NSym+2)*NSps = 60 480

        // Accumulate frequency-deviation pulses for each symbol.
        // The 3×NSps pulse for symbol j=102 spans dphi[58752..60479] = dphi[NWave-1] ✓
        for (int j = 0; j < NSym; j++)
        {
            int ib = j * NSps;
            for (int i = 0; i < 3 * NSps; i++)
                dphi[ib + i] += dphiPeak * pulse[i] * tones[j];
        }

        // FT4: integrate from dphi[0] (unlike FT8 which starts at dphi[NSps])
        double ofs = 2.0 * Math.PI * freqHz / 12000.0;
        double phi = 0.0;
        var samples = new float[NWave];
        for (int k = 0; k < NWave; k++)
        {
            samples[k] = (float)(amplitude * Math.Sin(phi));
            phi = (phi + dphi[k] + ofs) % (2.0 * Math.PI);
        }

        // Amplitude ramp in: first NSps samples (0 → 1)
        for (int i = 0; i < NSps; i++)
        {
            float w = (float)((1.0 - Math.Cos(Math.PI * i / NSps)) / 2.0);
            samples[i] *= w;
        }
        // Amplitude ramp out: last NSps samples (1 → 0), starting at (NSym+1)*NSps
        int k2 = (NSym + 1) * NSps; // = 59904
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
