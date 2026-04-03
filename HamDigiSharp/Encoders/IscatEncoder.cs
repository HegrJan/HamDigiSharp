using HamDigiSharp.Abstractions;
using HamDigiSharp.Models;

namespace HamDigiSharp.Encoders;

/// <summary>
/// ISCAT-A / ISCAT-B audio encoder at 11025 Hz.
///
/// Each 30-second period consists of repeating 24-symbol blocks:
/// <list type="bullet">
///   <item>Symbols 0–3: Costas synchronisation tones.</item>
///   <item>Symbols 4–5: Message-length identification tones.</item>
///   <item>Symbols 6–23: 18 data tones cycling through the message characters.</item>
/// </list>
///
/// Each symbol is a continuous-phase sine wave at bin × (SampleRate / 2·NSps) Hz
/// (integer-cycle per symbol window), so the signal is self-coherent and
/// compatible with the MSHV/Decodium coherent ISCAT decoder.
///
/// <para>C# port of MSHV gen_iscat.cpp (LZ2HV, GPL).</para>
/// </summary>
public sealed class IscatEncoder : IDigitalModeEncoder
{
    private const int    SampleRate = 11025;
    private const int    NBlk       = 24;
    private const string CharTable  = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ /.?@-";

    private static readonly int[] Icos = { 0, 1, 3, 2 }; // Costas tone offsets ÷ 2

    public DigitalMode Mode { get; }

    public IscatEncoder(DigitalMode mode) => Mode = mode;

    /// <inheritdoc/>
    public float[] Encode(string message, EncoderOptions options)
    {
        int    nspsOrig = Mode == DigitalMode.IscatA ? 512 : 256;
        int    i0       = FreqToI0(options.FrequencyHz, nspsOrig);
        double amp      = options.Amplitude > 0 ? options.Amplitude : 0.9;

        string msg    = PrepareMessage(message);
        int    msgLen = msg.Length;

        // Build block-level tone offsets (relative to i0, in units of 2)
        var blockTone = new int[NBlk];
        for (int n = 0; n < 4; n++) blockTone[n] = 2 * Icos[n];
        blockTone[4] = 2 * msgLen;           // Length[0]
        blockTone[5] = 2 * msgLen + 10;      // Length[1]
        for (int d = 0; d < 18; d++)
        {
            int ci = CharTable.IndexOf(msg[d % msgLen]);
            if (ci < 0) ci = CharTable.IndexOf(' ');
            blockTone[6 + d] = 2 * ci;
        }

        int     totalSamples = SampleRate * 30;
        var     samples      = new float[totalSamples];
        double  dt           = 1.0 / SampleRate;
        int     totalSymbols = totalSamples / nspsOrig;

        for (int sym = 0; sym < totalSymbols; sym++)
        {
            // f = bin × SampleRate / (2 × nspsOrig)  → integer cycles per symbol window
            double freq  = (i0 + blockTone[sym % NBlk]) * (double)SampleRate / (2 * nspsOrig);
            int    start = sym * nspsOrig;
            for (int s = 0; s < nspsOrig && start + s < totalSamples; s++)
                samples[start + s] = (float)(amp * Math.Sin(2.0 * Math.PI * freq * (start + s) * dt));
        }

        return samples;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string PrepareMessage(string message)
    {
        string msg = message.ToUpperInvariant().TrimEnd();
        if (string.IsNullOrEmpty(msg)) return " ";

        // Replace characters not in CharTable with space
        var sb = new System.Text.StringBuilder(msg.Length);
        foreach (char c in msg)
            sb.Append(CharTable.Contains(c) ? c : ' ');
        return sb.ToString();
    }

    /// <summary>
    /// Converts a user-supplied centre frequency to the STFT base bin i0.
    /// Falls back to MSHV defaults (30 for ISCAT-B, 94 for ISCAT-A) when not specified.
    /// </summary>
    private int FreqToI0(double freqHz, int nspsOrig)
    {
        if (freqHz > 0)
            return (int)Math.Round(freqHz * 2 * nspsOrig / SampleRate);

        return Mode == DigitalMode.IscatA ? 94 : 30;
    }
}
