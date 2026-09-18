namespace BaronDesk.Shared.Models;

public class DeviceTelemetry
{
    public DateTime Timestamp { get; set; }

    public string DeviceType { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string ProductId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;
}