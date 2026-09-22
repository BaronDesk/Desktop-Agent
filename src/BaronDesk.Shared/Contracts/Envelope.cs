namespace BaronDesk.Shared.Contracts;

/// <summary>
/// Frozen wire envelope used by every frame on <c>/agent-ws</c>:
/// <c>{ "type": "...", "id": "uuid", "ts": "iso-8601", "seq": 42, "payload": { ... } }</c>.
/// <c>seq</c> + <c>ts</c> give app-level anti-replay on top of TLS.
/// </summary>
/// <remarks>
/// Only inbound frames are deserialized through this type (as <c>Envelope&lt;JsonElement&gt;</c>).
/// Outbound frames are written by the connection itself, which stamps <c>seq</c> and <c>ts</c>
/// under its send lock so they always reach the wire in order.
/// </remarks>
public sealed record Envelope<T>
{
    public required string Type { get; init; }

    public required Guid Id { get; init; }

    public required DateTimeOffset Ts { get; init; }

    public required long Seq { get; init; }

    public T? Payload { get; init; }
}
