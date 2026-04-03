namespace HamDigiSharp.Models;

/// <summary>
/// Configuration passed to a decoder at construction or before each decode call.
/// Mirrors the Set* methods on MSHV's DecoderMs / DecoderFt8 / DecoderQ65.
/// </summary>
public sealed class DecoderOptions
{
    // ── Operator identity ─────────────────────────────────────────────────────
    /// <summary>My full callsign (e.g. "LZ2HV").</summary>
    public string MyCall { get; set; } = string.Empty;

    /// <summary>My base callsign without suffix/prefix (for AP).</summary>
    public string MyBaseCall { get; set; } = string.Empty;

    /// <summary>My 4- or 6-character Maidenhead locator (e.g. "KN22").</summary>
    public string MyGrid { get; set; } = string.Empty;

    // ── QSO context (for AP decoding) ─────────────────────────────────────────
    /// <summary>Current DX station's callsign (empty = not in QSO).</summary>
    public string HisCall { get; set; } = string.Empty;

    /// <summary>DX station's grid locator.</summary>
    public string HisGrid { get; set; } = string.Empty;

    /// <summary>Current QSO exchange stage, used by AP decoding to bias candidate messages.</summary>
    public QsoProgress QsoProgress { get; set; } = QsoProgress.None;

    // ── Decoder tuning ────────────────────────────────────────────────────────
    /// <summary>LDPC decode aggressiveness — higher depth improves sensitivity at CPU cost.</summary>
    public DecoderDepth DecoderDepth { get; set; } = DecoderDepth.Normal;

    /// <summary>Maximum number of sync candidates to evaluate. Default 140 (matches ft8_lib).</summary>
    public int MaxCandidates { get; set; } = 140;

    /// <summary>Minimum sync score (dB) for a candidate to be considered. Default 4.0.</summary>
    public float MinSyncDb { get; set; } = 4.0f;

    /// <summary>Enable a priori (AP) aided decoding.</summary>
    public bool ApDecode { get; set; } = true;

    /// <summary>Frequency tolerance in Hz (search window half-width around QSO freq).</summary>
    public double FreqTolerance { get; set; } = 200.0;

    /// <summary>Frequency of the QSO partner in audio Hz (0 = no preference).</summary>
    public double QsoFrequencyHz { get; set; }

    /// <summary>TX frequency in audio Hz (used by AP subtraction).</summary>
    public double TxFrequencyHz { get; set; } = 1200.0;

    /// <summary>Minimum SNR threshold (dB) below which decodes are suppressed.</summary>
    public int MinSnrDb { get; set; } = -24;

    // ── Averaging / multi-period (Q65, FT2) ───────────────────────────────────
    /// <summary>
    /// Enable incoherent power averaging across consecutive periods (Q65/FT2).
    /// When false, each <see cref="IDigitalModeDecoder.Decode"/> call is independent.
    /// </summary>
    public bool AveragingEnabled { get; set; } = true;

    /// <summary>
    /// Number of periods to accumulate for averaging (1–5, default 3).
    /// Only used when <see cref="AveragingEnabled"/> is <see langword="true"/>
    /// and the decoder supports multi-period integration (Q65).
    /// </summary>
    public int AveragingPeriods { get; set; } = 3;

    /// <summary>Clear accumulated averaging buffer before the next decode call.</summary>
    public bool ClearAverage { get; set; }
}
