using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Policy;
using BaronDeskAgent.ServiceCore.Telemetry;
using BaronDeskAgent.ServiceCore.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace BaronDeskAgent.ServiceCore.Tests.Telemetry;

public sealed class HardwareAlertEvaluatorTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly HardwareAlertEvaluator _evaluator;

    public HardwareAlertEvaluatorTests()
    {
        var policy = new FakePolicyStore(new StationPolicy
        {
            CpuTempAlertThreshold = 85,
            CpuLoadAlertThreshold = 95,
            HardwareAlertCooldownSeconds = 60
        });
        _evaluator = new HardwareAlertEvaluator(policy, _time);
    }

    [Fact]
    public void Temperature_alert_fires_once_and_rearms_only_after_cooling_down()
    {
        Assert.Single(Evaluate(cpuTemp: 90));
        Assert.Empty(Evaluate(cpuTemp: 91));

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(Evaluate(cpuTemp: 82)); // within the 5 °C hysteresis band: still not re-armed
        Assert.Empty(Evaluate(cpuTemp: 90));

        Assert.Empty(Evaluate(cpuTemp: 70)); // re-armed
        Assert.Single(Evaluate(cpuTemp: 90));
    }

    [Fact]
    public void Temperature_far_above_threshold_is_critical()
    {
        var alert = Assert.Single(Evaluate(cpuTemp: 95));

        Assert.Equal(AlertSeverities.Critical, alert.Severity);
        Assert.Equal(AlertCategories.Hardware, alert.Category);
        Assert.Equal(AlertTypes.TemperatureWarning, alert.Type);
    }

    [Fact]
    public void Full_cpu_load_during_a_game_only_alerts_when_sustained()
    {
        for (var i = 0; i < 11; i++)
        {
            Assert.Empty(Evaluate(cpuLoad: 100));
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        _time.Advance(TimeSpan.FromSeconds(5));
        var alert = Assert.Single(Evaluate(cpuLoad: 100));
        Assert.Equal(AlertTypes.CpuUsage, alert.Type);
    }

    [Fact]
    public void A_short_load_spike_never_alerts()
    {
        Assert.Empty(Evaluate(cpuLoad: 100));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(Evaluate(cpuLoad: 40));
        _time.Advance(TimeSpan.FromSeconds(40));
        Assert.Empty(Evaluate(cpuLoad: 100));
    }

    private IReadOnlyList<AlertPayload> Evaluate(double? cpuTemp = null, double? cpuLoad = null)
    {
        var telemetry = new HardwareTelemetry { SampledAt = _time.GetUtcNow() };
        telemetry.Cpu.TemperatureC = cpuTemp;
        telemetry.Cpu.LoadPercent = cpuLoad;
        return _evaluator.Evaluate(telemetry);
    }
}
