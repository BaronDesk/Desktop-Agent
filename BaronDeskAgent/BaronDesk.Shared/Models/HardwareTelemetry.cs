namespace BaronDesk.Shared.Models;

public class HardwareTelemetry
{
    public DateTime Timestamp { get; set; }

    public double? CpuTemperature { get; set; }

    public double? CpuLoad { get; set; }

    public double? GpuTemperature { get; set; }

    public double? GpuLoad { get; set; }

    public double? GpuMemoryUsed { get; set; }

    public double? GpuMemoryTotal { get; set; }

    public double? RamUsed { get; set; }

    public double? RamTotal { get; set; }

    public double? FanSpeed { get; set; }
}