namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>telemetry</c> payload. Mirrors <c>NODE_TELEMETRY</c> (metric, value, sampledAt) and carries only
/// the samples that changed meaningfully, plus a periodic full snapshot (see the agent's delta filter).
/// </summary>
public sealed record TelemetryPayload
{
    public required IReadOnlyList<TelemetrySample> Samples { get; init; }
}
