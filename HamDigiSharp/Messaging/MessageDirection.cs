namespace HamDigiSharp.Messaging;

/// <summary>
/// Direction / initiator role of a standard exchange message.
/// </summary>
public enum MessageDirection
{
    /// <summary>Calling CQ — inviting any station to respond.</summary>
    CQ,

    /// <summary>QRZ — calling an unidentified or partially-heard station.</summary>
    QRZ,

    /// <summary>DE — identifying without a specific direction (e.g. beacons).</summary>
    DE,

    /// <summary>Point-to-point exchange between two identified stations.</summary>
    Exchange,
}
