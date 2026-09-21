using System.Text.Json.Serialization;

namespace BaronDesk.Shared.Contracts;

public sealed record CommandNackPayload
{
    [JsonPropertyName("commandId")]
    public required Guid CommandId { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
