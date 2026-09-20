using BaronDesk.Shared.Models;

namespace BaronDeskAgent.ServiceCore.Hardware;

public sealed class HardwareTelemetryMapper
{
    public IReadOnlyList<NodeTelemetryMetric> Map(
        HardwareTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        var metrics = new List<NodeTelemetryMetric>();

        AddCpuMetrics(
            telemetry,
            metrics);

        AddRamMetrics(
            telemetry,
            metrics);

        AddGpuMetrics(
            telemetry,
            metrics);

        AddFanMetrics(
            telemetry,
            metrics);

        return metrics;
    }

    private static void AddCpuMetrics(
        HardwareTelemetry telemetry,
        List<NodeTelemetryMetric> metrics)
    {
        if (telemetry.Cpu?.LoadPercent is double cpuLoad)
        {
            metrics.Add(
                CreateMetric(
                    "cpu.load_percent",
                    cpuLoad,
                    telemetry.Timestamp));
        }

        if (telemetry.Cpu?.TemperatureC is double cpuTemperature)
        {
            metrics.Add(
                CreateMetric(
                    "cpu.temperature_c",
                    cpuTemperature,
                    telemetry.Timestamp));
        }

        if (telemetry.Cpu?.CoreMaxLoadPercent is double coreMaxLoad)
        {
            metrics.Add(
                CreateMetric(
                    "cpu.core_max_load_percent",
                    coreMaxLoad,
                    telemetry.Timestamp));
        }
    }

    private static void AddRamMetrics(
        HardwareTelemetry telemetry,
        List<NodeTelemetryMetric> metrics)
    {
        if (telemetry.Ram?.UsedGb is double usedGb)
        {
            metrics.Add(
                CreateMetric(
                    "memory.used_gb",
                    usedGb,
                    telemetry.Timestamp));
        }

        if (telemetry.Ram?.AvailableGb is double availableGb)
        {
            metrics.Add(
                CreateMetric(
                    "memory.available_gb",
                    availableGb,
                    telemetry.Timestamp));
        }

        if (telemetry.Ram?.TotalGb is double totalGb)
        {
            metrics.Add(
                CreateMetric(
                    "memory.total_gb",
                    totalGb,
                    telemetry.Timestamp));
        }

        if (telemetry.Ram?.UsagePercent is double usagePercent)
        {
            metrics.Add(
                CreateMetric(
                    "memory.usage_percent",
                    usagePercent,
                    telemetry.Timestamp));
        }
    }

    private static void AddGpuMetrics(
        HardwareTelemetry telemetry,
        List<NodeTelemetryMetric> metrics)
    {
        if (telemetry.Gpus is null)
        {
            return;
        }

        for (var index = 0;
             index < telemetry.Gpus.Count;
             index++)
        {
            var gpu = telemetry.Gpus[index];

            var prefix =
                $"gpu.{index}";

            if (gpu.LoadPercent is double load)
            {
                metrics.Add(
                    CreateMetric(
                        $"{prefix}.load_percent",
                        load,
                        telemetry.Timestamp));
            }

            if (gpu.TemperatureC is double temperature)
            {
                metrics.Add(
                    CreateMetric(
                        $"{prefix}.temperature_c",
                        temperature,
                        telemetry.Timestamp));
            }

            if (gpu.HotSpotTemperatureC is double hotspotTemperature)
            {
                metrics.Add(
                    CreateMetric(
                        $"{prefix}.hotspot_temperature_c",
                        hotspotTemperature,
                        telemetry.Timestamp));
            }

            if (gpu.MemoryTemperatureC is double memoryTemperature)
            {
                metrics.Add(
                    CreateMetric(
                        $"{prefix}.memory_temperature_c",
                        memoryTemperature,
                        telemetry.Timestamp));
            }

            if (gpu.MemoryUsedMb is double memoryUsed)
            {
                metrics.Add(
                    CreateMetric(
                        $"{prefix}.memory_used_mb",
                        memoryUsed,
                        telemetry.Timestamp));
            }

            if (gpu.MemoryFreeMb is double memoryFree)
            {
                metrics.Add(
                    CreateMetric(
                        $"{prefix}.memory_free_mb",
                        memoryFree,
                        telemetry.Timestamp));
            }

            if (gpu.MemoryTotalMb is double memoryTotal)
            {
                metrics.Add(
                    CreateMetric(
                        $"{prefix}.memory_total_mb",
                        memoryTotal,
                        telemetry.Timestamp));
            }
        }
    }

    private static void AddFanMetrics(
        HardwareTelemetry telemetry,
        List<NodeTelemetryMetric> metrics)
    {
        if (telemetry.Fans is null)
        {
            return;
        }

        for (var index = 0;
             index < telemetry.Fans.Count;
             index++)
        {
            var fan = telemetry.Fans[index];

            if (fan.SpeedRpm is double speed)
            {
                metrics.Add(
                    CreateMetric(
                        $"fan.{index}.speed_rpm",
                        speed,
                        telemetry.Timestamp));
            }
        }
    }

    private static NodeTelemetryMetric CreateMetric(
        string metric,
        double value,
        DateTime sampledAt)
    {
        return new NodeTelemetryMetric
        {
            Metric = metric,
            Value = Math.Round(value, 4),
            SampledAt = sampledAt
        };
    }
}