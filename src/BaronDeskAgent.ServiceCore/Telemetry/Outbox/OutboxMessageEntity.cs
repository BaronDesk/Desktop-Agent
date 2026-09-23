namespace BaronDeskAgent.ServiceCore.Telemetry.Outbox;

/// <summary>
/// A buffered agent → server message. Only the payload is stored; the envelope (seq, ts) is created fresh
/// at send time, while <see cref="Id"/> stays stable so the backend can deduplicate retries.
/// </summary>
public sealed class OutboxMessageEntity
{
    public required Guid Id { get; init; }

    public required string Type { get; init; }

    public required string Payload { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public int Attempts { get; init; }
}
