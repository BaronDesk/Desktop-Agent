namespace BaronDesk.Shared.Contracts;

public static class MessageTypes
{
    // Agent -> Server
    public const string Handshake = "handshake";
    public const string Heartbeat = "heartbeat";
    public const string Telemetry = "telemetry";
    public const string Alert = "alert";
    public const string CommandAck = "command_ack";
    public const string CommandNack = "command_nack";
    public const string StateReport = "state_report";

    // Server -> Agent
    public const string HandshakeAck = "handshake_ack";
    public const string HeartbeatAck = "heartbeat_ack";
    public const string Command = "command";
    public const string StateRequest = "state_request";
    public const string PolicyPush = "policy_push";
}

