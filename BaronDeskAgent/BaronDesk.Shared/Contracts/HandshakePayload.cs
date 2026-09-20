namespace BaronDesk.Shared.Contracts;

public sealed record HandshakePayload
{
    public string SerialNumber { get; init; } = string.Empty;
    public string AgentVersion { get; init; } = "1.0.0";
    public string OsVersion { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
}
