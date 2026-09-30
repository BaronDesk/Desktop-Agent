using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaronDesk.Shared.Contracts;

/// <summary>
/// Source-generated JSON metadata for every wire type (no reflection, low allocation, trim/AOT safe).
/// Property names are camelCase; reading is case-insensitive and number handling is strict,
/// so numbers are never parsed from culture-dependent strings.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Envelope<JsonElement>))]
[JsonSerializable(typeof(JsonElement))]
// Agent → server
[JsonSerializable(typeof(HandshakePayload))]
[JsonSerializable(typeof(HeartbeatPayload))]
[JsonSerializable(typeof(StateReportPayload))]
[JsonSerializable(typeof(TelemetryPayload))]
[JsonSerializable(typeof(AlertPayload))]
[JsonSerializable(typeof(CommandAckPayload))]
[JsonSerializable(typeof(CommandNackPayload))]
[JsonSerializable(typeof(LoginRequestPayload))]
[JsonSerializable(typeof(CatalogStatusPayload))]
[JsonSerializable(typeof(InstalledGamesPayload))]
[JsonSerializable(typeof(PeripheralStatusPayload))]
// Server → agent control frames
[JsonSerializable(typeof(HandshakeAckPayload))]
[JsonSerializable(typeof(HeartbeatAckPayload))]
[JsonSerializable(typeof(LoginResultPayload))]
[JsonSerializable(typeof(SessionNoticePayload))]
[JsonSerializable(typeof(StationCredentialPayload))]
// Server → agent commands
[JsonSerializable(typeof(UnlockPayload))]
[JsonSerializable(typeof(LockPayload))]
[JsonSerializable(typeof(EndSessionPayload))]
[JsonSerializable(typeof(LaunchGamePayload))]
[JsonSerializable(typeof(ShutdownPayload))]
[JsonSerializable(typeof(PolicyUpdatePayload))]
// Enrollment (REST, before the WSS link exists)
[JsonSerializable(typeof(EnrollmentRequest))]
[JsonSerializable(typeof(EnrollmentResponse))]
// Game catalog (REST, pulled with the station credential)
[JsonSerializable(typeof(GameCatalogResponse))]
public sealed partial class AgentJsonContext : JsonSerializerContext;
