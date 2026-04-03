namespace HamDigiSharp.Models;

/// <summary>
/// Controls how aggressively the LDPC decoder searches for a valid codeword.
/// Higher depth improves sensitivity at the cost of CPU time.
/// </summary>
public enum DecoderDepth
{
    /// <summary>
    /// Belief propagation only — fastest path, ~1 dB less sensitive than Normal.
    /// Equivalent to WSJT-X fast-decode mode.
    /// </summary>
    Fast = 1,

    /// <summary>
    /// BP followed by OSD order-1 (91 test vectors) — balances speed and sensitivity.
    /// Default. Matches WSJT-X depth 2.
    /// </summary>
    Normal = 2,

    /// <summary>
    /// BP followed by OSD order-2 (4096 test vectors) — most sensitive, slowest.
    /// Use for weak-signal work where decode latency is acceptable.
    /// </summary>
    Deep = 3,
}
