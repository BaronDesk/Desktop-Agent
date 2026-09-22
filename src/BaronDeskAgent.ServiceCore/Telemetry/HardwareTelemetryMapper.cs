using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Telemetry;

/// <summary>
/// Flattens a <see cref="HardwareTelemetry"/> snapshot into <c>NODE_TELEMETRY</c>-shaped samples.
/// </summary>
public sealed class HardwareTelemetryMapper
{
    public IReadOnlyList<TelemetrySample> Map(HardwareTelemetry telemetry)
    {
        var samples = new List<TelemetrySample>(24);

        void Add(string metric, double? value)
        {
            if (value is { } v && double.IsFinite(v))
            {
                samples.Add(new TelemetrySample { Metric = metric, Value = Math.Round(v, 4), SampledAt = telemetry.SampledAt });
            }
        }

        Add("cpu.load_percent", telemetry.Cpu.LoadPercent);
        Add("cpu.temperature_c", telemetry.Cpu.TemperatureC);
        Add("cpu.core_max_load_percent", telemetry.Cpu.CoreMaxLoadPercent);

        Add("memory.used_gb", telemetry.Ram.UsedGb);
        Add("memory.available_gb", telemetry.Ram.AvailableGb);
        Add("memory.total_gb", telemetry.Ram.TotalGb);
        Add("memory.usage_percent", telemetry.Ram.UsagePercent);

        for (var i = 0; i < telemetry.Gpus.Count; i++)
        {
            var gpu = telemetry.Gpus[i];
            Add($"gpu.{i}.load_percent", gpu.LoadPercent);
            Add($"gpu.{i}.temperature_c", gpu.TemperatureC);
            Add($"gpu.{i}.hotspot_temperature_c", gpu.HotSpotTemperatureC);
            Add($"gpu.{i}.memory_temperature_c", gpu.MemoryTemperatureC);
            Add($"gpu.{i}.memory_used_mb", gpu.MemoryUsedMb);
            Add($"gpu.{i}.memory_free_mb", gpu.MemoryFreeMb);
            Add($"gpu.{i}.memory_total_mb", gpu.MemoryTotalMb);
        }

        for (var i = 0; i < telemetry.Fans.Count; i++)
        {
            Add($"fan.{i}.speed_rpm", telemetry.Fans[i].SpeedRpm);
        }

        return samples;
    }
}
