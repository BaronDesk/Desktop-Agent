using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Telemetry;

/// <summary>
/// Sends only meaningful changes. The backend keeps just the latest reading in Redis with a 30 s TTL,
/// so a full snapshot still goes out before any metric could expire there.
/// </summary>
public sealed class TelemetryDeltaFilter
{
    private static readonly TimeSpan BackendCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, double> _lastSent = new(StringComparer.Ordinal);
    private long? _lastFullSnapshot;

    public TelemetryDeltaFilter(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <param name="interval">Time until the next sample; used to decide whether this is the last chance before the TTL.</param>
    public IReadOnlyList<TelemetrySample> Filter(IReadOnlyList<TelemetrySample> samples, TimeSpan interval)
    {
        var now = _timeProvider.GetTimestamp();
        var fullSnapshotDue = _lastFullSnapshot is not { } last ||
                              _timeProvider.GetElapsedTime(last, now) + interval >= BackendCacheTtl - SafetyMargin;

        if (fullSnapshotDue)
        {
            _lastFullSnapshot = now;
            foreach (var sample in samples)
            {
                _lastSent[sample.Metric] = sample.Value;
            }

            return samples;
        }

        var changed = new List<TelemetrySample>();
        foreach (var sample in samples)
        {
            if (!_lastSent.TryGetValue(sample.Metric, out var previous) ||
                Math.Abs(sample.Value - previous) >= SignificantChange(sample.Metric))
            {
                _lastSent[sample.Metric] = sample.Value;
                changed.Add(sample);
            }
        }

        return changed;
    }

    /// <summary>Forget what was sent (after a disconnect), so the next sample is a full snapshot.</summary>
    public void Reset()
    {
        _lastSent.Clear();
        _lastFullSnapshot = null;
    }

    private static double SignificantChange(string metric) => metric switch
    {
        _ when metric.EndsWith("_c", StringComparison.Ordinal) => 1.0,
        _ when metric.EndsWith("_percent", StringComparison.Ordinal) => 2.0,
        _ when metric.EndsWith("_rpm", StringComparison.Ordinal) => 50.0,
        _ when metric.EndsWith("_gb", StringComparison.Ordinal) => 0.1,
        _ when metric.EndsWith("_mb", StringComparison.Ordinal) => 64.0,
        _ => 0.0
    };
}
