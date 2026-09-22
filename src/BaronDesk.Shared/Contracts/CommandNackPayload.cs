namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>command_nack</c> payload. <see cref="Code"/> is one of <see cref="NackCodes"/>.
/// </summary>
public sealed record CommandNackPayload
{
    public required Guid CommandId { get; init; }

    public required string Code { get; init; }

    public string? Reason { get; init; }
}
