using HamDigiSharp.Abstractions;
using HamDigiSharp.Codecs;
using HamDigiSharp.Models;

namespace HamDigiSharp.Encoders;

/// <summary>
/// WSPR audio encoder.
/// Converts a "CALL GRID dBm" message to 1,327,104 float PCM samples at 12 kHz
/// (162 symbols × 8192 samples/symbol ≈ 110.6 s).
/// Uses 4-FSK with continuous phase and the WSPR sync vector.
/// Mirrors wsprcode.f90 from WSJT-X (K1JT et al.), GPL.
/// </summary>
public sealed class WsprEncoder : IDigitalModeEncoder
{
    public DigitalMode Mode => DigitalMode.Wspr;

    private const int SymbolCount = 162;
    private const int NspsAt12k  = 8192;         // samples per symbol at 12 kHz
    private const int SampleRate = 12000;
    private const double Df      = 375.0 / 256.0; // tone spacing ≈ 1.4648 Hz

    /// <summary>
    /// Encodes a "CALL GRID dBm" message into a float PCM audio frame.
    /// </summary>
    /// <param name="message">Message in "CALLSIGN GRID POWER" format, e.g. "W1AW FN42 33".</param>
    /// <param name="options">Encoder options (frequency and amplitude).</param>
    /// <returns>1,327,104 float PCM samples at 12 kHz.</returns>
    /// <exception cref="ArgumentException">Thrown when the message cannot be encoded.</exception>
    public float[] Encode(string message, EncoderOptions options)
    {
        // 1. Pack message → 7-byte WSPR payload
        if (!WsprPack.TryEncode(message, out byte[] dat))
            throw new ArgumentException(
                $"Cannot encode WSPR message \"{message}\". " +
                "Expected format: \"CALLSIGN GRID POWER\" (e.g. \"W1AW FN42 33\").",
                nameof(message));

        // 2. Convolutional encode: 7 bytes → 162 hard-decision symbols (0/1)
        byte[] convSyms = WsprConv.Encode(dat);

        // 3. Bit-reversal interleave (in place)
        WsprConv.Interleave(convSyms);

        // 4. Build 4-FSK channel symbols: tone = 2*data + sync ∈ {0,1,2,3}
        var chanSym = new byte[SymbolCount];
        for (int i = 0; i < SymbolCount; i++)
            chanSym[i] = (byte)(2 * convSyms[i] + WsprConv.SyncVector[i]);

        // 5. Continuous-phase FSK audio synthesis
        // Convention: f0 is the CENTRE of the 4-tone group.
        // Tone frequency = f0 + (symbol - 1.5) * Df  (matches wsprsim.c convention).
        double amplitude = Math.Clamp(options.Amplitude, 0.0, 0.99);
        double f0        = options.FrequencyHz;
        int    total     = SymbolCount * NspsAt12k; // 1,327,104

        var audio = new float[total];
        double phi = 0.0;

        for (int sym = 0; sym < SymbolCount; sym++)
        {
            double freq = f0 + (chanSym[sym] - 1.5) * Df;
            double dphi = Math.Tau * freq / SampleRate;
            int    baseIdx = sym * NspsAt12k;

            for (int s = 0; s < NspsAt12k; s++)
            {
                audio[baseIdx + s] = (float)(amplitude * Math.Sin(phi));
                phi += dphi;
                if (phi >= Math.Tau) phi -= Math.Tau;
            }
        }

        // 6. Raised-cosine amplitude ramp at start and end (~50 samples)
        const int NRamp = 50;
        for (int i = 0; i < NRamp; i++)
        {
            float w = (float)((1.0 - Math.Cos(Math.PI * i / NRamp)) / 2.0);
            audio[i]             *= w;
            audio[total - 1 - i] *= 1.0f - w;
        }

        return audio;
    }
}
