namespace BaronDesk.Shared.Models;

public class HardwareTelemetry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public CpuTelemetry Cpu { get; set; } = new();

    public RamTelemetry Ram { get; set; } = new();

    public List<GpuTelemetry> Gpus { get; set; } = [];

    public List<FanTelemetry> Fans { get; set; } = [];
}

public class CpuTelemetry
{
    public string Name { get; set; } = string.Empty;

    public string Vendor { get; set; } = string.Empty;

    public double? TemperatureC { get; set; }

    public double? LoadPercent { get; set; }

    public double? CoreMaxLoadPercent { get; set; }
}

public class RamTelemetry
{
    public double? UsedGb { get; set; }

    public double? AvailableGb { get; set; }

    public double? TotalGb { get; set; }

    public double? UsagePercent { get; set; }
}

public class GpuTelemetry
{
    public string Name { get; set; } = string.Empty;

    public string Vendor { get; set; } = string.Empty;

    public double? TemperatureC { get; set; }

    public double? HotSpotTemperatureC { get; set; }

    public double? MemoryTemperatureC { get; set; }

    public double? LoadPercent { get; set; }

    public double? MemoryUsedMb { get; set; }

    public double? MemoryFreeMb { get; set; }

    public double? MemoryTotalMb { get; set; }
}

public class FanTelemetry
{
    public string Name { get; set; } = string.Empty;

    public double? SpeedRpm { get; set; }
}