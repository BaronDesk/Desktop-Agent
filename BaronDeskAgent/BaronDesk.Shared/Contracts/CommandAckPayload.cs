using System.Text.Json.Serialization;

namespace BaronDesk.Shared.Contracts;

public sealed record CommandAckPayload
{
    [JsonPropertyName("commandId")]
    public required Guid CommandId { get; init; }
}
