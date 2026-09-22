using System.Text.Json.Serialization;

namespace BaronDesk.Shared.Contracts;

/// <summary>
/// Telemetry alert payload conforming to backend specifications (Prisma TelemetryAlert model
/// and Step 0 Frozen Contracts §6.4).
/// Wire type: "alert"
/// Categories: "hardware" | "anti_theft" | "security_violation"
/// Types: "TEMPERATURE_WARNING" | "CPU_USAGE" | "MEMORY_USAGE" | "HARDWARE_FAILURE"
/// Severities: "LOW" | "MEDIUM" | "HIGH" | "CRITICAL"
/// </summary>
public sealed record AlertPayload
{
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("detail")]
    public required string Detail { get; init; }

    [JsonPropertyName("occurredAt")]
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
