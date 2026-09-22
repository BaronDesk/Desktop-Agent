namespace BaronDesk.Shared.Models;

public sealed class DeviceTelemetry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public string DeviceType { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string ProductId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;
}