namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>heartbeat</c> payload.
/// </summary>
public sealed record HeartbeatPayload
{
    public required bool Locked { get; init; }

    public Guid? SessionId { get; init; }
}
