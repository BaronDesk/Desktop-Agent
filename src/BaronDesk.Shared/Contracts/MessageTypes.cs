namespace BaronDesk.Shared.Contracts;

/// <summary>
/// Non-command envelope types. Commands (server → agent) are listed in <see cref="CommandTypes"/>.
/// </summary>
public static class MessageTypes
{
    // Agent → server (FROZEN)
    public const string Handshake = "handshake";
    public const string Heartbeat = "heartbeat";
    public const string Telemetry = "telemetry";
    public const string Alert = "alert";
    public const string CommandAck = "command_ack";
    public const string CommandNack = "command_nack";
    public const string StateReport = "state_report";

    // OPEN (skill §15 item 2): the frozen list has no credential-relay message. Proposed shape, confirm with backend member C.
    public const string LoginRequest = "login_request";

    // OPEN (skill §15 item 9): catalog install status, confirm with backend member C (see GameCatalogContracts).
    public const string CatalogStatus = "catalog_status";

    // OPEN (skill §15 item 9): launcher games installed on the station, to suggest catalog entries. Confirm with backend member C.
    public const string InstalledGames = "installed_games";

    // OPEN (skill §15): watched USB peripherals and their connection status, confirm with backend member C.
    public const string PeripheralStatus = "peripheral_status";

    // Server → agent control frames. OPEN (skill §15 item 3): not in the frozen list yet, confirm with backend member C.
    public const string HandshakeAck = "handshake_ack";

    // Server → agent: a renewed station JWT (no ack), stored for the next connect.
    public const string StationCredential = "station_credential";
    public const string HeartbeatAck = "heartbeat_ack";
    public const string LoginResult = "login_result";

    // OPEN (skill §15): in-session warning for the gamer (low balance, booking ending), confirm with backend member C.
    public const string SessionNotice = "session_notice";
}
