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

    /// <summary>
    /// CPU temperature threshold in Celsius for emitting hardware warning alerts.
    /// </summary>
    public double CpuTempAlertThreshold { get; set; } = 85.0;

    /// <summary>
    /// GPU temperature threshold in Celsius for emitting hardware warning alerts.
    /// </summary>
    public double GpuTempAlertThreshold { get; set; } = 85.0;

    /// <summary>
    /// CPU load percentage threshold for emitting hardware warning alerts.
    /// </summary>
    public double CpuLoadAlertThreshold { get; set; } = 95.0;

    /// <summary>
    /// RAM usage percentage threshold for emitting hardware warning alerts.
    /// </summary>
    public double RamLoadAlertThreshold { get; set; } = 95.0;

    /// <summary>
    /// Cooldown window in seconds before repeating an alert for the same hardware metric.
    /// </summary>
    public double HardwareAlertCooldownSeconds { get; set; } = 60.0;

    /// <summary>
    /// Debounce window in seconds for USB disconnects to distinguish temporary flaps from true removal.
    /// </summary>
    public double UsbDebounceWindowSeconds { get; set; } = 5.0;

    /// <summary>
    /// Whether anti-theft peripheral monitoring alerts are enabled.
    /// </summary>
    public bool EnableAntiTheftAlerts { get; set; } = true;
}
