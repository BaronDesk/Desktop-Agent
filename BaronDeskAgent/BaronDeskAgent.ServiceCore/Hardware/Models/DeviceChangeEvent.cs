namespace BaronDeskAgent.ServiceCore.Hardware.Models;

public class DeviceChangeEvent
{
    public string DeviceName { get; init; } = string.Empty;

    public string ProductId { get; init; } = string.Empty;

    public string DeviceType { get; init; } = string.Empty;

    public string EventType { get; init; } = string.Empty;

    public DateTime Timestamp { get; init; }
}