using HamDigiSharp.Models;

namespace HamDigiSharp.Decoders.Msk;

/// <summary>
/// MSK40 decoder (MSKMS mode in MSHV) — 40m band variant of MSK144.
/// Same physical layer as MSK144 but different sync preamble:
///   sync = {1,0,1,1,0,0,0,1}  (bitwise complement of MSK144's {0,1,1,1,0,0,1,0})
/// C# port of MSHV's decodermsk40.cpp (LZ2HV), GPL.
/// </summary>
public sealed class Msk40Decoder : Msk144Decoder
{
    private static readonly int[] Msk40SyncSeq = { 1, 0, 1, 1, 0, 0, 0, 1 };

    public Msk40Decoder() : base(DigitalMode.MSKMS, Msk40SyncSeq) { }
}
