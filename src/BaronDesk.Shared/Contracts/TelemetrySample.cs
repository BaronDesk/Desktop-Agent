namespace BaronDesk.Shared.Contracts;

/// <summary>
/// One metric reading, e.g. <c>{ "metric": "gpu.0.temperature_c", "value": 61.5, "sampledAt": "..." }</c>.
/// </summary>
public sealed record TelemetrySample
{
    public required string Metric { get; init; }

    public required double Value { get; init; }

    public required DateTimeOffset SampledAt { get; init; }
}
