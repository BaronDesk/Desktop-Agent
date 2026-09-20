using System.Text.Json.Serialization;

namespace BaronDesk.Shared.Contracts;

public sealed record HeartbeatAckPayload
{
    [JsonPropertyName("leaseExpiresAt")]
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    [JsonPropertyName("serverTime")]
    public DateTimeOffset? ServerTime { get; init; }
}
