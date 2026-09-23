namespace BaronDeskAgent.ServiceCore.Telemetry;

/// <summary>
/// One sensor snapshot. Internal model; the wire shape is <see cref="BaronDesk.Shared.Contracts.TelemetryPayload"/>.
/// </summary>
public sealed class HardwareTelemetry
{
    public DateTimeOffset SampledAt { get; init; }

    public CpuTelemetry Cpu { get; } = new();

    public RamTelemetry Ram { get; } = new();

    public List<GpuTelemetry> Gpus { get; } = [];

    public List<FanTelemetry> Fans { get; } = [];
}

public sealed class CpuTelemetry
{
    public string Name { get; set; } = string.Empty;

    public double? TemperatureC { get; set; }

    public double? LoadPercent { get; set; }

    public double? CoreMaxLoadPercent { get; set; }
}

public sealed class RamTelemetry
{
    public double? UsedGb { get; set; }

    public double? AvailableGb { get; set; }

    public double? TotalGb { get; set; }

    public double? UsagePercent { get; set; }
}

public sealed class GpuTelemetry
{
    public string Name { get; set; } = string.Empty;

    public double? TemperatureC { get; set; }

    public double? HotSpotTemperatureC { get; set; }

    public double? MemoryTemperatureC { get; set; }

    public double? LoadPercent { get; set; }

    public double? MemoryUsedMb { get; set; }

    public double? MemoryFreeMb { get; set; }

    public double? MemoryTotalMb { get; set; }
}

public sealed class FanTelemetry
{
    public string Name { get; set; } = string.Empty;

    public double? SpeedRpm { get; set; }
}
