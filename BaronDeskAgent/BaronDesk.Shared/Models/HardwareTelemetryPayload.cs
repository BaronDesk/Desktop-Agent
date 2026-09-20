namespace BaronDesk.Shared.Models;

public sealed class HardwareTelemetryPayload
{
    public required DateTime Timestamp { get; init; }

    public required IReadOnlyList<NodeTelemetryMetric> Metrics { get; init; }
}