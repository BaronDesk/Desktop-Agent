using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Telemetry;
using Microsoft.Extensions.Time.Testing;

namespace BaronDeskAgent.ServiceCore.Tests.Telemetry;

public sealed class TelemetryDeltaFilterTests
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new();
    private readonly TelemetryDeltaFilter _filter;

    public TelemetryDeltaFilterTests()
    {
        _filter = new TelemetryDeltaFilter(_time);
    }

    [Fact]
    public void First_sample_is_a_full_snapshot_then_only_meaningful_changes_are_sent()
    {
        Assert.Equal(2, _filter.Filter(Samples(60.0, 40.0), Cadence).Count);

        _time.Advance(Cadence);
        Assert.Empty(_filter.Filter(Samples(60.4, 41.0), Cadence));

        _time.Advance(Cadence);
        var changed = Assert.Single(_filter.Filter(Samples(62.0, 41.0), Cadence));
        Assert.Equal("cpu.temperature_c", changed.Metric);
    }

    [Fact]
    public void Everything_is_resent_before_the_backend_cache_could_expire()
    {
        _filter.Filter(Samples(60.0, 40.0), Cadence);

        var sentAgain = false;
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromSeconds(30); elapsed += Cadence)
        {
            _time.Advance(Cadence);
            sentAgain |= _filter.Filter(Samples(60.0, 40.0), Cadence).Count == 2;
        }

        Assert.True(sentAgain);
    }

    [Fact]
    public void Reset_forces_a_full_snapshot()
    {
        _filter.Filter(Samples(60.0, 40.0), Cadence);
        _filter.Reset();

        Assert.Equal(2, _filter.Filter(Samples(60.0, 40.0), Cadence).Count);
    }

    private IReadOnlyList<TelemetrySample> Samples(double temperature, double load) =>
    [
        new TelemetrySample { Metric = "cpu.temperature_c", Value = temperature, SampledAt = _time.GetUtcNow() },
        new TelemetrySample { Metric = "cpu.load_percent", Value = load, SampledAt = _time.GetUtcNow() }
    ];
}
