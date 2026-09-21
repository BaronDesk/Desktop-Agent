using System.Text.Json.Serialization;

namespace BaronDesk.Shared.Contracts;

public sealed record HeartbeatPayload
{
    [JsonPropertyName("locked")]
    public required bool Locked { get; init; }

    [JsonPropertyName("sessionId")]
    public Guid? SessionId { get; init; }
}
