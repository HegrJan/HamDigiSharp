using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Fsk;

/// <summary>
/// FSK315 decoder — 4-FSK meteor scatter, 15-second period, 315 baud.
/// Uses the same algorithm as FSK441 via <see cref="FskBaseDecoder"/>.
/// From MSHV config_msg_all.h: NSPD=35, LTONE=3 (vs FSK441: NSPD=25, LTONE=2).
/// </summary>
public sealed class Fsk315Decoder : FskBaseDecoder
{
    public Fsk315Decoder() : base(35, 315.0, DigitalMode.FSK315) { }
}
