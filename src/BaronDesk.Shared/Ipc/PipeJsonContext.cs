using System.Text.Json.Serialization;

namespace BaronDesk.Shared.Ipc;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PipeMessage))]
public sealed partial class PipeJsonContext : JsonSerializerContext;
