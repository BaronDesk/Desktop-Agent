using System.Text.Json;
using System.Text.Json.Serialization;
using BaronDesk.Shared.Models;

namespace BaronDesk.Shared.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(Envelope<JsonElement>))]
[JsonSerializable(typeof(Envelope<HardwareTelemetryPayload>))]
[JsonSerializable(typeof(Envelope<DeviceTelemetry>))]
[JsonSerializable(typeof(Envelope<HandshakePayload>))]
[JsonSerializable(typeof(Envelope<CommandAckPayload>))]
[JsonSerializable(typeof(Envelope<CommandNackPayload>))]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResponse))]
[JsonSerializable(typeof(HandshakePayload))]
[JsonSerializable(typeof(CommandAckPayload))]
[JsonSerializable(typeof(CommandNackPayload))]
[JsonSerializable(typeof(Envelope<HeartbeatPayload>))]
[JsonSerializable(typeof(Envelope<HeartbeatAckPayload>))]
[JsonSerializable(typeof(Envelope<StateReportPayload>))]
[JsonSerializable(typeof(HeartbeatPayload))]
[JsonSerializable(typeof(HeartbeatAckPayload))]
[JsonSerializable(typeof(StateReportPayload))]
[JsonSerializable(typeof(HardwareTelemetry))]
[JsonSerializable(typeof(HardwareTelemetryPayload))]
[JsonSerializable(typeof(DeviceTelemetry))]
[JsonSerializable(typeof(NodeTelemetryMetric))]
[JsonSerializable(typeof(AlertPayload))]
[JsonSerializable(typeof(Envelope<AlertPayload>))]
[JsonSerializable(typeof(StationPolicy))]
[JsonSerializable(typeof(PolicyUpdatePayload))]
[JsonSerializable(typeof(Envelope<PolicyUpdatePayload>))]
[JsonSerializable(typeof(Envelope<StationPolicy>))]
public partial class AgentJsonContext : JsonSerializerContext
{
}
