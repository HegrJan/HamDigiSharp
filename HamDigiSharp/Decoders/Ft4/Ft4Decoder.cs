using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Ft4;

/// <summary>
/// FT4 decoder — 7.5-second period, 4-FSK, 103 symbols, LDPC(174,91).
/// C# port of MSHV's <c>DecoderFt4</c> (LZ2HV) and WSJT-X FT4 (K1JT et al.), GPL.
///
/// Frame structure (103 symbols):
///   [0..3]    Costas-A  {0,1,3,2}
///   [4..32]   29 data symbols
///   [33..36]  Costas-B  {1,0,2,3}
///   [37..65]  29 data symbols
///   [66..69]  Costas-C  {2,3,1,0}
///   [70..98]  29 data symbols
///   [99..102] Costas-D  {3,2,0,1}
///   87 data symbols × 2 bits = 174 LDPC codeword bits
///
/// All decode logic is implemented in <see cref="Ft4x2DecoderBase"/>; this class
/// supplies the mode-specific constants (period length, tone spacing, Costas threshold).
/// Multi-period LLR averaging is supported via <see cref="Models.DecoderOptions.AveragingEnabled"/>.
/// </summary>
public sealed class Ft4Decoder : Ft4x2DecoderBase
{
    // Nsps=576, NDown=18, nMax=72576 (6.048 s at 12 kHz), Nfft1=1152, Nss=32,
    // tone spacing=20.83 Hz.  RealTimeDecoder always sends _samplesPerPeriod+_guardSamples
    // (76 176) so DT coverage is preserved even though nMax stays at 72576.
    public Ft4Decoder() : base(nsps: 576, nDown: 18, nMax: 72576, nfft1: 1152) { }

    public override DigitalMode Mode => DigitalMode.FT4;

    protected override int MinCostasMatches => 4;
}

