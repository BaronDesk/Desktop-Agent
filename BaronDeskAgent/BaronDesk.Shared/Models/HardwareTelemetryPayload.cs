namespace BaronDesk.Shared.Models;

public sealed class HardwareTelemetryPayload
{
    public required DateTimeOffset Timestamp { get; init; }

    public required IReadOnlyList<NodeTelemetryMetric> Metrics { get; init; }
}