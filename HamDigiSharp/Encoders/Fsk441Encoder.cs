using HamDigiSharp.Abstractions;
using HamDigiSharp.Models;

namespace HamDigiSharp.Encoders;

/// <summary>
/// FSK441 audio encoder — 4-FSK meteor scatter mode at 11025 Hz.
/// Generates continuous-phase FSK at 441-Hz spacing, 441 baud (25 samples/symbol).
/// Ported from MSHV's <c>GenMs::abc441</c> and <c>gen441</c> (LZ2HV / K1JT), GPL.
/// </summary>
public sealed class Fsk441Encoder : FskBaseEncoder
{
    public Fsk441Encoder() : base(nsps: 25, toneStep: 441.0, lTone: 2, mode: DigitalMode.FSK441) { }
}

/// <summary>
/// FSK315 audio encoder — 4-FSK meteor scatter mode at 11025 Hz.
/// 315-Hz spacing, 315 baud (35 samples/symbol), lowest tone = 3 × 315 = 945 Hz.
/// </summary>
public sealed class Fsk315Encoder : FskBaseEncoder
{
    public Fsk315Encoder() : base(nsps: 35, toneStep: 315.0, lTone: 3, mode: DigitalMode.FSK315) { }
}

/// <summary>
/// Shared base encoder for FSK441 and FSK315 meteor scatter modes.
/// Both modes use the same 92-entry lookup table and 48-character alphabet from MSHV.
/// </summary>
public abstract class FskBaseEncoder : IDigitalModeEncoder
{
    private const int SampleRate = 11025;

    // From MSHV config_msg_all.h: lookup_FSK441_TXRX[92].
    // Maps ASCII code (indices 0–91) to a 6-bit tone code (0–63).
    // Indices 0–91 cover control chars through '['.  Only codes < 48 are valid characters.
    private static readonly int[] LookupTxRx =
    {
        13, 15, 17, 46, 47, 45, 44, 12, 11, 14,
         1,  2,  3,  4,  5,  6,  7,  8,  9, 10,
        16, 48, 18, 19, 20, 21, 22, 23, 24, 25,
        26, 27, 15, 47, 30, 14, 16, 42, 46, 35,
        36, 37, 21,  0, 11, 41, 10, 13, 43,  1,
         2,  3,  4,  5,  6,  7,  8,  9, 49, 56,
        52, 55, 54, 12, 63, 17, 18, 19, 20, 44,
        22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        32, 33, 34, 35, 36, 37, 38, 39, 40, 41,
        45, 63,
    };

    private static readonly int SpaceCode = LookupTxRx[32]; // = 15

    private readonly int _nsps;
    private readonly double _toneStep;
    private readonly int _lTone;
    private readonly DigitalMode _mode;

    protected FskBaseEncoder(int nsps, double toneStep, int lTone, DigitalMode mode)
    {
        _nsps = nsps; _toneStep = toneStep; _lTone = lTone; _mode = mode;
    }

    public DigitalMode Mode => _mode;

    /// <summary>
    /// Encode <paramref name="message"/> into continuous-phase FSK PCM at 11025 Hz.
    /// <para>
    /// <see cref="EncoderOptions.FrequencyHz"/> sets the lowest tone frequency
    /// (default: <c>lTone × toneStep</c> — 882 Hz for FSK441, 945 Hz for FSK315).
    /// </para>
    /// </summary>
    public float[] Encode(string message, EncoderOptions options)
    {
        string msg = message.ToUpperInvariant();
        if (msg.Length > 46) msg = msg[..46];

        double lowestTone = options.FrequencyHz > 0
            ? options.FrequencyHz
            : _lTone * _toneStep;
        double amp = options.Amplitude > 0 ? options.Amplitude : 0.9;

        // Convert each character to 3 tone indices (0–3, MSB first): n = 16·d0 + 4·d1 + d2
        var dits = new List<int>(msg.Length * 3 + 3);
        // FSK441 protocol: 3 sync preamble symbols (tone 0) precede the message.
        // jsync = 3 in the decoder skips these preamble symbols and starts decoding
        // the actual message at symbol position 3.
        dits.Add(0); dits.Add(0); dits.Add(0);
        foreach (char ch in msg)
        {
            int j = (int)ch;
            if (j < 0 || j > 91) j = 32;      // substitute illegal char with space
            int n = LookupTxRx[j];
            if (n >= 48) n = SpaceCode;         // substitute unmappable code with space

            dits.Add(n / 16);                   // tone index 0–3 for symbol 1
            dits.Add((n / 4) % 4);              // tone index for symbol 2
            dits.Add(n % 4);                    // tone index for symbol 3
        }

        // Generate continuous-phase FSK: freq per tone = lowestTone + dit × toneStep
        float[] samples = new float[dits.Count * _nsps];
        double pCos = 1.0, pSin = 0.0;         // running phasor (continuous phase)

        for (int s = 0; s < dits.Count; s++)
        {
            double freq    = lowestTone + dits[s] * _toneStep;
            double dphi    = 2.0 * Math.PI * freq / SampleRate;
            double rotCos  = Math.Cos(dphi);
            double rotSin  = Math.Sin(dphi);
            int    offset  = s * _nsps;
            for (int i = 0; i < _nsps; i++)
            {
                double nCos = pCos * rotCos - pSin * rotSin;
                pSin = pCos * rotSin + pSin * rotCos;
                pCos = nCos;
                samples[offset + i] = (float)(amp * pSin);
            }
        }

        return samples;
    }
}
