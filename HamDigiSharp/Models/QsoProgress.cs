namespace HamDigiSharp.Models;

/// <summary>
/// Tracks the stage of an ongoing QSO, used by AP (a-priori) decoding to bias the decoder
/// toward messages that are likely at each exchange step.
/// </summary>
public enum QsoProgress
{
    /// <summary>Not in a QSO — sending or listening for CQ.</summary>
    None = 0,

    /// <summary>Called a DX station — sent call + grid locator.</summary>
    Called = 1,

    /// <summary>Received a signal report from the DX station.</summary>
    ReportReceived = 2,

    /// <summary>Received RRR — exchange acknowledged.</summary>
    RrrReceived = 3,

    /// <summary>QSO complete — 73 exchanged.</summary>
    Completed = 4,
}
