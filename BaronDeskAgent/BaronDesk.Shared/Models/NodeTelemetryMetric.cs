namespace BaronDesk.Shared.Models;

public sealed class NodeTelemetryMetric
{
    public required string Metric { get; init; }

    public required double Value { get; init; }

    public required DateTime SampledAt { get; init; }
}