using HamDigiSharp.Abstractions;
using HamDigiSharp.Codecs;
using HamDigiSharp.Models;
using MathNet.Numerics;

namespace HamDigiSharp.Encoders;

/// <summary>
/// FT8 audio encoder. Converts a text message to 151,680 float PCM samples at 12 kHz
/// (79 symbols × 1920 samples/symbol = 12.64 s).
/// Uses GFSK modulation with BT=2.0 and 8-FSK (3 bits per symbol).
/// </summary>
public sealed class Ft8Encoder : IDigitalModeEncoder
{
    public DigitalMode Mode => DigitalMode.FT8;

    private const int NSps  = 1920;
    private const int NSym  = 79;
    private const int NWave = NSym * NSps; // 151 680

    private static readonly int[] Costas  = { 3, 1, 4, 0, 6, 5, 2 };
    private static readonly int[] GrayMap = { 0, 1, 3, 2, 5, 6, 4, 7 };

    // GFSK pulse (3×NSPS samples, centred at 1.5×NSPS, BT=2.0)
    private static readonly double[] GfskPulseData = BuildGfskPulse();

    public float[] Encode(string message, EncoderOptions options)
    {
        // 1. Pack text → 77 bits
        var c77 = new bool[77];
        if (!MessagePack77.TryPack77(message, c77))
            throw new ArgumentException($"Cannot encode message: \"{message}\"");

        // 2. LDPC encode: 77 bits → 174-bit codeword (adds CRC-14 + parity)
        var codeword = new bool[174];
        Ldpc174_91.Encode(c77, codeword);

        // 3. Map codeword bits → tone sequence (79 symbols)
        int[] tones = BuildToneSequence(codeword);

        // 4. GFSK modulate
        return Modulate(tones, options.FrequencyHz, options.Amplitude);
    }

    internal static int[] BuildToneSequence(bool[] codeword)
    {
        var tones = new int[NSym];

        // Insert Costas sync at positions 0..6, 36..42, 72..78
        for (int i = 0; i < 7; i++)
        {
            tones[i]      = Costas[i];
            tones[36 + i] = Costas[i];
            tones[72 + i] = Costas[i];
        }

        // Map 174 codeword bits → 58 3-bit symbols, fill data slots
        int k = 7; // skip first Costas block
        for (int j = 0; j < 58; j++)
        {
            if (j == 29) k += 7; // skip second Costas block at positions 36..42
            int idx = (codeword[3 * j] ? 4 : 0)
                    | (codeword[3 * j + 1] ? 2 : 0)
                    | (codeword[3 * j + 2] ? 1 : 0);
            tones[k++] = GrayMap[idx];
        }

        return tones;
    }

    private static float[] Modulate(int[] tones, double freqHz, double amplitude)
    {
        var pulse = GfskPulseData;
        double dphiPeak = 2.0 * Math.PI / NSps;
        var dphi = new double[(NSym + 2) * NSps];

        // Accumulate frequency-deviation pulses for each symbol
        for (int j = 0; j < NSym; j++)
        {
            int ib = j * NSps;
            for (int i = 0; i < 3 * NSps; i++)
                dphi[ib + i] += dphiPeak * pulse[i] * tones[j];
        }

        // Dummy symbols at boundaries to handle ISI at edges
        for (int i = 0; i < 2 * NSps; i++)
        {
            dphi[i]               += dphiPeak * tones[0]        * pulse[i + NSps];
            dphi[NSym * NSps + i] += dphiPeak * tones[NSym - 1] * pulse[i];
        }

        // Integrate phase → sine wave (output window starts at dphi[NSps])
        double ofs = 2.0 * Math.PI * freqHz / 12000.0;
        double phi = 0.0;
        var samples = new float[NWave];
        for (int k = 0; k < NWave; k++)
        {
            samples[k] = (float)(amplitude * Math.Sin(phi));
            phi = (phi + dphi[k + NSps] + ofs) % (2.0 * Math.PI);
        }

        // Amplitude ramp in/out (NSPS/8 samples)
        int nramp = NSps / 8;
        for (int i = 0; i < nramp; i++)
        {
            float w = (float)((1.0 - Math.Cos(Math.PI * i / nramp)) / 2.0);
            samples[i] *= w;
            samples[NWave - nramp + i] *= 1.0f - w;
        }

        return samples;
    }

    private static double[] BuildGfskPulse()
    {
        var pulse = new double[3 * NSps];
        for (int i = 0; i < 3 * NSps; i++)
        {
            double t = ((double)i - 1.5 * NSps) / NSps;
            pulse[i] = GfskPulse(2.0, t);
        }
        return pulse;
    }

    private static double GfskPulse(double b, double t)
    {
        double c = Math.PI * Math.Sqrt(2.0 / Math.Log(2.0));
        return 0.5 * (SpecialFunctions.Erf(c * b * (t + 0.5)) - SpecialFunctions.Erf(c * b * (t - 0.5)));
    }
}
