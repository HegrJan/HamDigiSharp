using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Ft2;

/// <summary>
/// FT2 decoder — 3.75-second period, 4-FSK, 103 symbols, LDPC(174,91).
/// FT2 was created by IU8LMC (Martino) for ARI Caserta; adapted into MSHV by LZ2HV, GPL.
/// Official ADIF submode SUBMODE=FT2, certified 22:0 by ADIF Development Group (Mar 2026).
///
/// Timing: 2.47 s burst (103+2 symbols × 288 sps) within a 3.75-second window.
/// Two stations alternate in 7.5-second TX/RX cycles (each holds one 3.75-second slot).
///
/// Frame structure: identical to FT4 (same Costas groups, GrayMap, LDPC, Rvec scramble) but
/// Nsps=288 (half of FT4 = twice the symbol rate), giving a 41.67 Hz tone spacing.
/// Coherent averaging across consecutive periods improves sensitivity for weak signals.
///
/// All decode logic is implemented in <see cref="Ft4x2DecoderBase"/>; this class
/// supplies the mode-specific constants (period length, tone spacing, Costas threshold).
/// </summary>
public sealed class Ft2Decoder : Ft4x2DecoderBase
{
    // Nsps=288, NDown=9, nMax=48600 (4.05 s at 12 kHz, includes 300 ms guard band),
    // Nfft1=1152, Nss=32, tone spacing=41.67 Hz.

    public Ft2Decoder()
        : base(nsps: 288, nDown: 9, nMax: 48600, nfft1: 1152) { }

    public override DigitalMode Mode => DigitalMode.FT2;

    // FT2 requires more Costas matches because its wider tone spacing makes
    // random noise slightly more likely to mimic a Costas pattern.
    protected override int MinCostasMatches => 6;
}
