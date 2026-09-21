using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Services.Telemetry;

public static class TelemetryMessagePolicy
{
    public static bool RequiresOutbox(string messageType)
    {
        return messageType switch
        {
            MessageTypes.Alert => true,
            MessageTypes.CommandAck => true,
            MessageTypes.CommandNack => true,
            MessageTypes.StateReport => true,
            "device_event" => true,

            "session_started" => true,
            "session_ended" => true,
            "lease_changed" => true,
            "command_acknowledgement" => true,
            "agent_status_changed" => true,

            MessageTypes.Telemetry => false,
            MessageTypes.Heartbeat => false,

            _ => false
        };
    }
}