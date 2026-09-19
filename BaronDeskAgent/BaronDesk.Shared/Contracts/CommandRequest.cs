namespace BaronDesk.Shared.Contracts;

public sealed class CommandRequest
{
    public required Guid Id { get; init; }

    public required string Type { get; init; }

    public object? Payload { get; init; }
}