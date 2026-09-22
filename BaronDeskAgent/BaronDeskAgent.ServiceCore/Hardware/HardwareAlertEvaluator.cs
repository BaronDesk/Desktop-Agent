using BaronDesk.Shared.Contracts;
using BaronDesk.Shared.Models;
using BaronDeskAgent.ServiceCore.Services.Policy;
using Microsoft.Extensions.Logging;

namespace BaronDeskAgent.ServiceCore.Hardware;

/// <summary>
/// Evaluates hardware telemetry readings against configured thresholds
/// and produces <see cref="AlertPayload"/> instances for exceeded limits.
/// Maintains per-metric cooldown state and temperature hysteresis to
/// prevent alert storms.
/// </summary>
public sealed class HardwareAlertEvaluator
{
    /// <summary>
    /// Temperature hysteresis margin in °C.
    /// After an alert fires, the temperature must fall at least this
    /// many degrees below the threshold before the alert can re-arm.
    /// </summary>
    private const double TemperatureHysteresisC = 5.0;

    /// <summary>
    /// Critical multiplier applied on top of the configured threshold.
    /// Readings above <c>threshold * CriticalFactor</c> emit CRITICAL
    /// instead of HIGH severity.
    /// </summary>
    private const double CriticalTempFactor = 1.1;

    private readonly IPolicyStore _policyStore;
    private readonly ILogger<HardwareAlertEvaluator> _logger;

    // ── Cooldown: last time each metric type fired an alert ──────────
    private readonly Dictionary<string, DateTimeOffset> _lastAlertTime =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Hysteresis: whether each temperature metric is "armed" ───────
    // True = ready to fire, False = alert already fired, waiting for
    // temperature to drop below (threshold – hysteresis).
    private readonly Dictionary<string, bool> _tempArmed =
        new(StringComparer.OrdinalIgnoreCase);

    public HardwareAlertEvaluator(
        IPolicyStore policyStore,
        ILogger<HardwareAlertEvaluator> logger)
    {
        _policyStore = policyStore;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates a telemetry snapshot and returns zero or more alerts
    /// for metrics that exceed configured thresholds.
    /// </summary>
    public IReadOnlyList<AlertPayload> Evaluate(
        HardwareTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        var alerts = new List<AlertPayload>();

        EvaluateCpuTemperature(
            telemetry.Cpu,
            alerts);

        EvaluateCpuLoad(
            telemetry.Cpu,
            alerts);

        EvaluateRamUsage(
            telemetry.Ram,
            alerts);

        EvaluateGpuTemperatures(
            telemetry.Gpus,
            alerts);

        return alerts;
    }

    // ── CPU Temperature ──────────────────────────────────────────────

    private void EvaluateCpuTemperature(
        CpuTelemetry cpu,
        List<AlertPayload> alerts)
    {
        if (!cpu.TemperatureC.HasValue)
        {
            return;
        }

        double temp = cpu.TemperatureC.Value;
        double threshold = _policyStore.CurrentPolicy.CpuTempAlertThreshold;
        const string metricKey = "cpu_temp";

        // Hysteresis: re-arm if temperature drops below threshold
        // minus the hysteresis margin.
        if (temp < threshold - TemperatureHysteresisC)
        {
            if (_tempArmed.TryGetValue(
                    metricKey,
                    out bool wasArmed) &&
                !wasArmed)
            {
                _tempArmed[metricKey] = true;

                _logger.LogDebug(
                    "CPU temperature re-armed at {Temp:F1}°C " +
                    "(below {ReArm:F1}°C).",
                    temp,
                    threshold - TemperatureHysteresisC);
            }

            return;
        }

        if (temp < threshold)
        {
            return;
        }

        // Not armed → already fired, waiting for hysteresis reset.
        if (_tempArmed.TryGetValue(
                metricKey,
                out bool armed) &&
            !armed)
        {
            return;
        }

        if (!IsCooldownExpired(metricKey))
        {
            return;
        }

        string severity = temp >= threshold * CriticalTempFactor
            ? "CRITICAL"
            : "HIGH";

        alerts.Add(new AlertPayload
        {
            Category = "hardware",
            Type = "TEMPERATURE_WARNING",
            Severity = severity,
            Detail =
                $"CPU temperature at {temp:F1}°C " +
                $"exceeded threshold ({threshold:F1}°C)."
        });

        RecordAlertFired(metricKey);
        _tempArmed[metricKey] = false;
    }

    // ── GPU Temperature(s) ───────────────────────────────────────────

    private void EvaluateGpuTemperatures(
        IReadOnlyList<GpuTelemetry> gpus,
        List<AlertPayload> alerts)
    {
        for (int i = 0; i < gpus.Count; i++)
        {
            GpuTelemetry gpu = gpus[i];

            if (!gpu.TemperatureC.HasValue)
            {
                continue;
            }

            double temp = gpu.TemperatureC.Value;
            double threshold = _policyStore.CurrentPolicy.GpuTempAlertThreshold;
            string metricKey = $"gpu_temp_{i}";

            // Hysteresis: re-arm.
            if (temp < threshold - TemperatureHysteresisC)
            {
                if (_tempArmed.TryGetValue(
                        metricKey,
                        out bool wasArmed) &&
                    !wasArmed)
                {
                    _tempArmed[metricKey] = true;

                    _logger.LogDebug(
                        "GPU[{Index}] temperature re-armed at " +
                        "{Temp:F1}°C (below {ReArm:F1}°C).",
                        i,
                        temp,
                        threshold - TemperatureHysteresisC);
                }

                continue;
            }

            if (temp < threshold)
            {
                continue;
            }

            if (_tempArmed.TryGetValue(
                    metricKey,
                    out bool armed) &&
                !armed)
            {
                continue;
            }

            if (!IsCooldownExpired(metricKey))
            {
                continue;
            }

            string severity =
                temp >= threshold * CriticalTempFactor
                    ? "CRITICAL"
                    : "HIGH";

            string gpuName =
                string.IsNullOrWhiteSpace(gpu.Name)
                    ? $"GPU {i}"
                    : gpu.Name;

            alerts.Add(new AlertPayload
            {
                Category = "hardware",
                Type = "TEMPERATURE_WARNING",
                Severity = severity,
                Detail =
                    $"{gpuName} temperature at {temp:F1}°C " +
                    $"exceeded threshold ({threshold:F1}°C)."
            });

            RecordAlertFired(metricKey);
            _tempArmed[metricKey] = false;
        }
    }

    // ── CPU Load ─────────────────────────────────────────────────────

    private void EvaluateCpuLoad(
        CpuTelemetry cpu,
        List<AlertPayload> alerts)
    {
        if (!cpu.LoadPercent.HasValue)
        {
            return;
        }

        double load = cpu.LoadPercent.Value;
        double threshold = _policyStore.CurrentPolicy.CpuLoadAlertThreshold;
        const string metricKey = "cpu_load";

        if (load < threshold)
        {
            return;
        }

        if (!IsCooldownExpired(metricKey))
        {
            return;
        }

        string severity = load >= 99.0
            ? "CRITICAL"
            : "HIGH";

        alerts.Add(new AlertPayload
        {
            Category = "hardware",
            Type = "CPU_USAGE",
            Severity = severity,
            Detail =
                $"CPU load at {load:F1}% " +
                $"exceeded threshold ({threshold:F1}%)."
        });

        RecordAlertFired(metricKey);
    }

    // ── RAM Usage ────────────────────────────────────────────────────

    private void EvaluateRamUsage(
        RamTelemetry ram,
        List<AlertPayload> alerts)
    {
        if (!ram.UsagePercent.HasValue)
        {
            return;
        }

        double usage = ram.UsagePercent.Value;
        double threshold = _policyStore.CurrentPolicy.RamLoadAlertThreshold;
        const string metricKey = "ram_usage";

        if (usage < threshold)
        {
            return;
        }

        if (!IsCooldownExpired(metricKey))
        {
            return;
        }

        string severity = usage >= 99.0
            ? "CRITICAL"
            : "HIGH";

        alerts.Add(new AlertPayload
        {
            Category = "hardware",
            Type = "MEMORY_USAGE",
            Severity = severity,
            Detail =
                $"RAM usage at {usage:F1}% " +
                $"exceeded threshold ({threshold:F1}%)."
        });

        RecordAlertFired(metricKey);
    }

    // ── Cooldown helpers ─────────────────────────────────────────────

    private bool IsCooldownExpired(string metricKey)
    {
        if (!_lastAlertTime.TryGetValue(
                metricKey,
                out DateTimeOffset lastFired))
        {
            return true;
        }

        double cooldownSeconds =
            _policyStore.CurrentPolicy.HardwareAlertCooldownSeconds;

        bool expired =
            DateTimeOffset.UtcNow - lastFired >=
            TimeSpan.FromSeconds(cooldownSeconds);

        return expired;
    }

    private void RecordAlertFired(string metricKey)
    {
        _lastAlertTime[metricKey] =
            DateTimeOffset.UtcNow;
    }
}
