using HamDigiSharp.Abstractions;
using HamDigiSharp.Models;

namespace HamDigiSharp.Encoders;

/// <summary>
/// PI4 encoder — π/4-QPSK-like 2-FSK with rate-1/2 K=32 convolutional code.
/// C# port of MSHV gen_pi4 (LZ2HV), GPL.
///
/// Pipeline:
///   1. Pack 8-char message (base-38) → 42-bit value
///   2. Extract 42 bits MSB-first
///   3. Rate-1/2, K=32 convolutional encode with tail → 146 bits
///   4. Interleave using 8-bit-reversal permutation (_j0)
///   5. Modulate: 146 symbols × 1000 samples @ 11025 Hz
///      Each symbol chooses f0 or f1 based on bit and PRN sequence
/// </summary>
public sealed class Pi4Encoder : IDigitalModeEncoder
{
    public DigitalMode Mode => DigitalMode.PI4;

    private const int   SampleRate = 11025;
    private const int   NSpSym     = 1000;      // samples per symbol (NSymMax/2)
    private const int   Nsym       = 146;        // symbols per PI4 frame
    private const int   NBits      = 42;         // data bits (before tail)
    // IMPORTANT: The PI4 decoder uses dt=2/SR, making its reference oscillator run at 2×nfreq.
    // The encoder therefore produces audio at 2×freq so the decoder at nfreq=682.8125 detects
    // a signal whose carrier is at 2×682.8125 ≈ 1365.6 Hz.  DefaultFreq is the decoder's
    // half-frequency parameter, not the actual audio frequency.
    private const double DefaultFreq = 682.8125; // decoder nfreq parameter (actual audio ≈ 2× this)

    private const uint Npoly1 = 0xf2d05351;
    private const uint Npoly2 = 0xe4613c47;

    // PRN sync sequence from Pi4Decoder (Npr2_pi4 in decoderpi4.cpp)
    private static readonly int[] Npr2 =
    {
        0,0,1,0,0,1,1,1,1,0,1,0,1,0,1,0,0,1,0,0,0,1,0,0,0,1,1,0,0,1,
        1,1,1,0,0,1,1,1,1,1,0,0,1,1,0,1,1,1,1,0,1,0,1,1,0,1,1,0,1,0,
        0,0,0,0,1,1,1,1,1,0,1,0,1,0,0,0,0,0,1,1,1,1,1,0,1,0,0,1,0,0,
        1,0,1,0,0,0,0,1,0,0,1,1,0,0,0,0,0,1,1,0,0,0,0,1,1,0,0,1,1,1,
        0,1,1,1,0,1,1,0,1,0,1,0,1,0,0,0,0,1,1,1,0,0,0,0,1,1
    };

    // Valid message characters: '0'-'9', 'A'-'Z', ' ', '/'  (38 total)
    private static readonly char[] ValidChars =
    {
        '0','1','2','3','4','5','6','7',
        '8','9','A','B','C','D','E','F',
        'G','H','I','J','K','L','M','N',
        'O','P','Q','R','S','T','U','V',
        'W','X','Y','Z',' ','/'
    };

    // Interleave permutation — same algorithm as Pi4Decoder.BuildInterleave()
    private static readonly int[] J0 = BuildInterleave();

    public float[] Encode(string message, EncoderOptions options)
    {
        double freq = options.FrequencyHz > 0 ? options.FrequencyHz : DefaultFreq;
        // Decoder reference runs at 2×f0; audio frequencies must also be 2× the nfreq parameter.
        double df   = 2.0 * SampleRate / 2048.0; // ≈ 10.77 Hz/bin (2× decoder df)

        // 1. Pack 8-char message → 42-bit value (big-endian base-38)
        string msg = message.ToUpper().PadRight(8).Substring(0, 8);
        long dataVal = 0;
        foreach (char ch in msg)
        {
            int idx = Array.IndexOf(ValidChars, ch);
            if (idx < 0) idx = 36; // default to space
            dataVal = dataVal * 38 + idx;
        }

        // 2. Extract 42 bits MSB-first
        int[] bits = new int[NBits];
        for (int i = 0; i < NBits; i++)
            bits[i] = (int)((dataVal >> (NBits - 1 - i)) & 1);

        // 3. Rate-1/2, K=32 convolutional encode (42 data + 31 tail = 73 bits → 146)
        int[] encoded = Convolve(bits);

        // 4. Interleave using bit-reversal permutation
        int[] interleaved = new int[Nsym];
        for (int i = 0; i < Nsym; i++)
            interleaved[J0[i]] = encoded[i];

        // 5. Modulate: continuous-phase FSK via complex phasor (2 trig calls/symbol)
        float[] samples = new float[Nsym * NSpSym];
        double pCos = 1.0, pSin = 0.0;  // phasor starts at phase=0
        for (int j = 0; j < Nsym; j++)
        {
            // Signal must be at 2× the decoder's reference frequency
            double f = interleaved[j] == 0
                ? 2.0 * freq + Npr2[j]       * df
                : 2.0 * freq + (2 + Npr2[j]) * df;
            double dphi = 2.0 * Math.PI * f / SampleRate;
            double rotCos = Math.Cos(dphi), rotSin = Math.Sin(dphi);
            int    base_ = j * NSpSym;
            for (int i = 0; i < NSpSym; i++)
            {
                double nCos = pCos * rotCos - pSin * rotSin;
                pSin = pCos * rotSin + pSin * rotCos;
                pCos = nCos;
                samples[base_ + i] = (float)pSin;
            }
        }
        return samples;
    }

    // ── Convolutional encoder ─────────────────────────────────────────────────

    private static int[] Convolve(int[] dataBits)
    {
        int total  = dataBits.Length + 31; // 42 + 31 tail bits = 73
        int[] output = new int[total * 2]; // 146 bits
        uint  shift  = 0;

        for (int i = 0; i < total; i++)
        {
            int bit = i < dataBits.Length ? dataBits[i] : 0;
            shift = (shift << 1) | (uint)bit;
            output[2 * i]     = (int)(Popcount(shift & Npoly1) & 1);
            output[2 * i + 1] = (int)(Popcount(shift & Npoly2) & 1);
        }
        return output;
    }

    private static int Popcount(uint x)
    {
        x -= (x >> 1) & 0x55555555u;
        x  = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
        x  = (x + (x >> 4)) & 0x0F0F0F0Fu;
        return (int)((x * 0x01010101u) >> 24);
    }

    // ── Build _j0 interleave table ────────────────────────────────────────────
    // Identical to Pi4Decoder.BuildInterleave(): keeps 8-bit reversed values ≤ 145.

    private static int[] BuildInterleave()
    {
        int[] j0 = new int[Nsym];
        int   k  = -1;
        for (int m = 0; m < 256; m++)
        {
            int rev = 0, tmp = m;
            for (int bit = 0; bit < 8; bit++) { rev = (rev << 1) | (tmp & 1); tmp >>= 1; }
            if (rev <= Nsym - 1) { k++; j0[k] = rev; }
        }
        return j0;
    }
}
