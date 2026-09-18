namespace BaronDesk.Shared.Models;

public sealed class TelemetryEnvelope
{
    public required string Type { get; init; }

    public required Guid Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required long Sequence { get; init; }

    public required object Payload { get; init; }
}