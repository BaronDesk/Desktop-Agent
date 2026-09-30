namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>station_credential</c>: server → agent control frame (no ack) carrying a renewed station JWT. The backend
/// sends it right after <c>handshake_ack</c> when the station's token has less than 30 days left, so a station
/// never ages out. The agent stores it and uses it from its next connect; the current connection stays as is.
/// </summary>
public sealed record StationCredentialPayload
{
    public required string StationToken { get; init; }
}
