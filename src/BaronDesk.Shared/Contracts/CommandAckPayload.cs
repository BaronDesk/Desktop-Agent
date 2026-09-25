namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>command_ack</c> payload. <see cref="CommandId"/> is the command envelope's <c>id</c>.
/// </summary>
public sealed record CommandAckPayload
{
    public required Guid CommandId { get; init; }
}
