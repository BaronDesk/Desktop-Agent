using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Policy;

namespace BaronDeskAgent.ServiceCore.Telemetry;

/// <summary>
/// Turns threshold crossings into <c>alert</c>s (category <c>hardware</c>) without alert storms:
/// hysteresis re-arms an alarm only after the value drops clearly below its threshold, a cooldown limits
/// repeats, and load/RAM must stay high for a while (a game at 100 % CPU is normal, not an incident).
/// Not thread-safe: called from the hardware monitor loop only.
/// </summary>
public sealed class HardwareAlertEvaluator
{
    private const double TemperatureHysteresisC = 5.0;
    private const double UsageHysteresisPercent = 5.0;
    private const double CriticalTemperatureFactor = 1.1;
    private const double CriticalUsagePercent = 99.0;

    private static readonly TimeSpan SustainedUsageWindow = TimeSpan.FromSeconds(60);

    private readonly IPolicyStore _policyStore;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ThresholdAlarm> _alarms = new(StringComparer.Ordinal);

    public HardwareAlertEvaluator(IPolicyStore policyStore, TimeProvider timeProvider)
    {
        _policyStore = policyStore;
        _timeProvider = timeProvider;
    }

    public IReadOnlyList<AlertPayload> Evaluate(HardwareTelemetry telemetry)
    {
        var policy = _policyStore.CurrentPolicy;
        var cooldown = TimeSpan.FromSeconds(policy.HardwareAlertCooldownSeconds);
        var alerts = new List<AlertPayload>();

        void CheckTemperature(string key, string label, double? value, double threshold)
        {
            if (value is { } temperature && Alarm(key, TemperatureHysteresisC, TimeSpan.Zero).ShouldFire(temperature, threshold, cooldown))
            {
                alerts.Add(Create(
                    AlertTypes.TemperatureWarning,
                    temperature >= threshold * CriticalTemperatureFactor ? AlertSeverities.Critical : AlertSeverities.High,
                    $"{label} temperature at {temperature:F1} °C exceeded the {threshold:F1} °C threshold.",
                    telemetry.SampledAt));
            }
        }

        void CheckUsage(string key, string type, string label, double? value, double threshold)
        {
            if (value is { } usage && Alarm(key, UsageHysteresisPercent, SustainedUsageWindow).ShouldFire(usage, threshold, cooldown))
            {
                alerts.Add(Create(
                    type,
                    usage >= CriticalUsagePercent ? AlertSeverities.Critical : AlertSeverities.High,
                    $"{label} at {usage:F1} % stayed above the {threshold:F1} % threshold for {SustainedUsageWindow.TotalSeconds:F0}s.",
                    telemetry.SampledAt));
            }
        }

        CheckTemperature("cpu_temp", "CPU", telemetry.Cpu.TemperatureC, policy.CpuTempAlertThreshold);
        for (var i = 0; i < telemetry.Gpus.Count; i++)
        {
            var gpu = telemetry.Gpus[i];
            var label = string.IsNullOrWhiteSpace(gpu.Name) ? $"GPU {i}" : gpu.Name;
            CheckTemperature($"gpu_temp_{i}", label, gpu.TemperatureC, policy.GpuTempAlertThreshold);
        }

        CheckUsage("cpu_load", AlertTypes.CpuUsage, "CPU load", telemetry.Cpu.LoadPercent, policy.CpuLoadAlertThreshold);
        CheckUsage("ram_usage", AlertTypes.MemoryUsage, "RAM usage", telemetry.Ram.UsagePercent, policy.RamLoadAlertThreshold);

        return alerts;
    }

    private ThresholdAlarm Alarm(string key, double hysteresis, TimeSpan sustain)
    {
        if (!_alarms.TryGetValue(key, out var alarm))
        {
            alarm = new ThresholdAlarm(_timeProvider, hysteresis, sustain);
            _alarms[key] = alarm;
        }

        return alarm;
    }

    private static AlertPayload Create(string type, string severity, string detail, DateTimeOffset occurredAt) => new()
    {
        Category = AlertCategories.Hardware,
        Type = type,
        Severity = severity,
        Detail = detail,
        OccurredAt = occurredAt
    };

    /// <summary>
    /// Fires once when a value has been at or above its threshold for <c>sustain</c>, then stays silent until
    /// the value drops below <c>threshold − hysteresis</c> and the cooldown has passed.
    /// Timing uses the monotonic clock.
    /// </summary>
    private sealed class ThresholdAlarm(TimeProvider timeProvider, double hysteresis, TimeSpan sustain)
    {
        private bool _armed = true;
        private long? _aboveSince;
        private long? _lastFired;

        public bool ShouldFire(double value, double threshold, TimeSpan cooldown)
        {
            var now = timeProvider.GetTimestamp();

            if (value < threshold)
            {
                _aboveSince = null;
                if (value < threshold - hysteresis)
                {
                    _armed = true;
                }

                return false;
            }

            _aboveSince ??= now;

            if (!_armed ||
                timeProvider.GetElapsedTime(_aboveSince.Value, now) < sustain ||
                (_lastFired is { } last && timeProvider.GetElapsedTime(last, now) < cooldown))
            {
                return false;
            }

            _armed = false;
            _lastFired = now;
            return true;
        }
    }
}
