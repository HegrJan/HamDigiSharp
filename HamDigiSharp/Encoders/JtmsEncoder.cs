using HamDigiSharp.Abstractions;
using HamDigiSharp.Models;

namespace HamDigiSharp.Encoders;

/// <summary>
/// JTMS audio encoder — 2-FSK meteor scatter at 11025 Hz.
///
/// Protocol:
/// <list type="bullet">
///   <item>Mark tone: 1155.46875 Hz (bit 0); Space tone: 1844.53125 Hz (bit 1).</item>
///   <item>8 samples/symbol → ~1378 baud.</item>
///   <item>Each character: 6 data bits (MSB-first) + 1 even-parity bit = 7 symbols.</item>
///   <item>The message is repeated to fill the full 15-second period (meteor scatter
///         practice: multiple copies maximise the chance of a meteor burst coinciding
///         with any part of the transmission).</item>
/// </list>
///
/// <para>C# port of MSHV's <c>GenMs::genjtms</c> (LZ2HV / K1JT), GPL.</para>
/// </summary>
public sealed class JtmsEncoder : IDigitalModeEncoder
{
    private const int    SampleRate = 11025;
    private const int    Nsps       = 8;
    private const double FreqMark   = 1155.46875;  // Hz — bit 0
    private const double FreqSpace  = 1844.53125;  // Hz — bit 1
    private const string CharTable  = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?@";
    private const int    PeriodSecs = 15;

    public DigitalMode Mode => DigitalMode.JTMS;

    /// <inheritdoc/>
    public float[] Encode(string message, EncoderOptions options)
    {
        double amp = options.Amplitude > 0 ? options.Amplitude : 0.9;
        string msg = PrepareMessage(message);

        // Build 7-bit symbols for one burst (6 data + 1 parity per character)
        int[] burstBits = BuildBits(msg);
        int   burstSamples = burstBits.Length * Nsps;

        // Fill the full 15-second period by repeating the burst
        int    totalSamples = SampleRate * PeriodSecs;
        float[] samples     = new float[totalSamples];

        double dphi0 = 2.0 * Math.PI * FreqMark  / SampleRate;
        double dphi1 = 2.0 * Math.PI * FreqSpace / SampleRate;
        double phi   = 0.0;
        int    k     = 0;
        int    bitIdx = 0;

        while (k < totalSamples)
        {
            double dphi = burstBits[bitIdx] == 0 ? dphi0 : dphi1;
            for (int i = 0; i < Nsps && k < totalSamples; i++, k++)
            {
                phi += dphi;
                samples[k] = (float)(amp * Math.Sin(phi));
            }
            bitIdx = (bitIdx + 1) % burstBits.Length;
        }

        return samples;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int[] BuildBits(string msg)
    {
        var bits = new int[msg.Length * 7];
        int k = 0;
        foreach (char c in msg)
        {
            int idx = CharTable.IndexOf(c);
            if (idx < 0) idx = 0;          // unmapped char → space

            int parity = 0;
            for (int bit = 5; bit >= 0; bit--)
            {
                int b = (idx >> bit) & 1;
                bits[k++] = b;
                parity   ^= b;
            }
            bits[k++] = parity;            // even parity over the 6 data bits
        }
        return bits;
    }

    private static string PrepareMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "CQ";
        var sb = new System.Text.StringBuilder(message.Length);
        foreach (char c in message.ToUpperInvariant())
            sb.Append(CharTable.Contains(c) ? c : ' ');
        return sb.ToString().TrimEnd();
    }
}
