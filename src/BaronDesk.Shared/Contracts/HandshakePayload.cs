namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>handshake</c> payload: station metadata only. The station credential travels in the
/// WSS upgrade request (OPEN, skill §15 item 1), never in this payload.
/// </summary>
public sealed record HandshakePayload
{
    public required string SerialNumber { get; init; }

    public required string AgentVersion { get; init; }

    public required string OsVersion { get; init; }

    public required string MachineName { get; init; }
}
