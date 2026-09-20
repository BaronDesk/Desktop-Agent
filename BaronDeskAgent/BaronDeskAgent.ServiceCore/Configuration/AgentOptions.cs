namespace BaronDeskAgent.ServiceCore.Configuration;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>
    /// WebSocket server URL (e.g., wss://192.168.1.100:8443/agent-ws).
    /// </summary>
    public string ServerUrl { get; set; } = "wss://127.0.0.1:8443/agent-ws";

    /// <summary>
    /// Hex-encoded SHA-256 fingerprint of the server's TLS certificate.
    /// Used for certificate pinning over LAN.
    /// </summary>
    public string? PinnedCertificateHash { get; set; }

    /// <summary>
    /// When true and no pinned certificate hash is configured, bypasses certificate validation (development only).
    /// </summary>
    public bool AllowUntrustedCertificate { get; set; }

    /// <summary>
    /// Machine serial number or identifier. Defaults to Environment.MachineName if not set.
    /// </summary>
    public string? SerialNumber { get; set; }

    /// <summary>
    /// Station JWT credential for WSS upgrade authentication.
    /// </summary>
    public string? StationToken { get; set; }

    /// <summary>
    /// One-time bootstrap token for initial station enrollment.
    /// Provided by the admin dashboard. Cleared after successful enrollment.
    /// </summary>
    public string? BootstrapToken { get; set; }

    /// <summary>
    /// Override path for the DPAPI-protected credential file.
    /// Defaults to %ProgramData%\BaronDesk\station.dat if not set.
    /// </summary>
    public string? StationDataPath { get; set; }

    /// <summary>
    /// Initial delay before reconnection attempt in seconds.
    /// </summary>
    public double ReconnectBaseDelaySeconds { get; set; } = 2.0;

    /// <summary>
    /// Maximum ceiling for reconnection exponential backoff in seconds.
    /// </summary>
    public double ReconnectMaxDelaySeconds { get; set; } = 30.0;

    /// <summary>
    /// Timeout for server handshake response in seconds.
    /// </summary>
    public double HandshakeTimeoutSeconds { get; set; } = 10.0;

    /// <summary>
    /// WebSocket ping/keep-alive interval in seconds.
    /// </summary>
    public double KeepAliveIntervalSeconds { get; set; } = 30.0;

    /// <summary>
    /// Maximum allowable clock drift between agent and server in seconds for anti-replay check.
    /// </summary>
    public double MaxTimestampDriftSeconds { get; set; } = 60.0;

    /// <summary>
    /// Cadence in seconds at which the agent sends presence heartbeats to the server.
    /// </summary>
    public double HeartbeatIntervalSeconds { get; set; } = 15.0;

    /// <summary>
    /// Default lease duration in seconds granted on unlock or heartbeat ack.
    /// </summary>
    public double DefaultLeaseDurationSeconds { get; set; } = 60.0;

    /// <summary>
    /// Grace window in seconds after lease expiration before enforcing fail-closed station lockout.
    /// </summary>
    public double LeaseGracePeriodSeconds { get; set; } = 10.0;
}
