using System.Text.Json.Serialization;

namespace BaronDesk.Shared.Contracts;

/// <summary>
/// Unified wire protocol envelope for all agent-server communication.
/// Every message on the wire follows this shape:
/// { "type": "...", "id": "uuid", "ts": "iso-8601", "seq": 42, "payload": { ... } }
/// </summary>
public sealed record Envelope
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("ts")]
    public required DateTimeOffset Ts { get; init; }

    [JsonPropertyName("seq")]
    public required long Seq { get; init; }

    [JsonPropertyName("payload")]
    public object? Payload { get; init; }
}

/// <summary>
/// Strongly-typed wire protocol envelope with a specific payload type.
/// </summary>
public sealed record Envelope<T>
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("ts")]
    public required DateTimeOffset Ts { get; init; }

    [JsonPropertyName("seq")]
    public required long Seq { get; init; }

    [JsonPropertyName("payload")]
    public T? Payload { get; init; }
}
