namespace BaronDeskAgent.ServiceCore.Data.Entities;

public sealed class OutboxMessageEntity
{
    public required Guid Id { get; init; }

    public required string Type { get; init; }

    public required string Payload { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public int Attempts { get; set; }
}