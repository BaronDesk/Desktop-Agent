namespace BaronDeskAgent.ServiceCore.Services.Telemetry;

public static class TelemetryMessagePolicy
{
    public static bool RequiresOutbox(string messageType)
    {
        return messageType switch
        {
            "device_event" => true,

            "session_started" => true,
            "session_ended" => true,

            "lease_changed" => true,

            "command_acknowledgement" => true,

            "agent_status_changed" => true,

            "telemetry" => false,
            "heartbeat" => false,

            _ => false
        };
    }
}