namespace BaronDeskAgent.ServiceCore.Configuration;

/// <summary>
/// Static agent configuration (<c>appsettings.json</c> section <c>Agent</c>).
/// Everything the backend tunes at runtime lives in <see cref="Policy.StationPolicy"/> instead.
/// Validated at startup by <see cref="AgentOptionsValidator"/>.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Backend endpoint, e.g. <c>wss://192.168.1.100:8443/agent-ws</c>. <c>ws://</c> is allowed in Development only.</summary>
    public string ServerUrl { get; set; } = "wss://127.0.0.1:8443/agent-ws";

    /// <summary>
    /// Hex SHA-256 of the server's TLS certificate (colons/spaces allowed). Required outside Development.
    /// OPEN (skill §15 item 7): delivered with the installer next to the server address.
    /// </summary>
    public string? PinnedCertificateHash { get; set; }

    /// <summary>Development only: accept any server certificate when no pin is configured. Rejected at startup elsewhere.</summary>
    public bool AllowUntrustedCertificate { get; set; }

    /// <summary>Station identity handle sent in the handshake. Defaults to the machine name.</summary>
    public string? SerialNumber { get; set; }

    /// <summary>
    /// Development only: plain-text fallback for the station JWT. Everywhere else the credential lives in DPAPI
    /// (<see cref="Credentials.DpapiStationCredentialStore"/>, provisioned with <c>--set-station-token</c>) and a
    /// value here is rejected at startup.
    /// </summary>
    public string? StationToken { get; set; }

    /// <summary>Full path of <c>BaronDesk.LockUI.exe</c>. When set, only that executable may use the IPC pipe and the service relaunches it if it dies.</summary>
    public string? LockUiExecutablePath { get; set; }

    public double ReconnectBaseDelaySeconds { get; set; } = 2.0;

    public double ReconnectMaxDelaySeconds { get; set; } = 30.0;

    public double KeepAliveIntervalSeconds { get; set; } = 30.0;

    /// <summary>Maximum tolerated difference between a frame's <c>ts</c> and the estimated server clock.</summary>
    public double MaxTimestampDriftSeconds { get; set; } = 60.0;

    public string ResolveSerialNumber() =>
        string.IsNullOrWhiteSpace(SerialNumber) ? Environment.MachineName : SerialNumber.Trim();

    /// <summary>The pinned certificate hash as bytes, or null when no pin is configured.</summary>
    /// <exception cref="FormatException">The configured value is not 32 bytes of hex.</exception>
    public byte[]? GetPinnedCertificateHash()
    {
        if (string.IsNullOrWhiteSpace(PinnedCertificateHash))
        {
            return null;
        }

        var hex = PinnedCertificateHash.Replace(":", string.Empty).Replace(" ", string.Empty);
        var bytes = Convert.FromHexString(hex);

        return bytes.Length == 32
            ? bytes
            : throw new FormatException("PinnedCertificateHash must be a SHA-256 hash (32 bytes of hex).");
    }
}
