namespace BaronDeskAgent.ServiceCore.Hardware.Models;

public class DeviceChangeEvent
{
    public string DeviceName { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; }
}