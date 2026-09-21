using System.Text.Json.Serialization;

namespace BaronDesk.Shared.Contracts;

public sealed record StateReportPayload
{
    [JsonPropertyName("locked")]
    public required bool Locked { get; init; }

    [JsonPropertyName("sessionId")]
    public Guid? SessionId { get; init; }

    [JsonPropertyName("runningGameId")]
    public string? RunningGameId { get; init; }

    [JsonPropertyName("leaseExpiresAt")]
    public DateTimeOffset? LeaseExpiresAt { get; init; }
}
