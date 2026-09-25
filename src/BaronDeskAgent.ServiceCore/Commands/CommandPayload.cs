using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace BaronDeskAgent.ServiceCore.Commands;

internal static class CommandPayload
{
    /// <summary>
    /// Parses a payload whose fields are all optional: a missing or <c>null</c> payload yields an empty instance.
    /// Handlers enforce their own required fields. Anything that is not a well-formed object fails.
    /// </summary>
    public static bool TryParse<T>(
        JsonElement payload,
        JsonTypeInfo<T> typeInfo,
        [NotNullWhen(true)] out T? value,
        [NotNullWhen(false)] out string? error)
        where T : class, new() =>
        TryParseCore(payload, typeInfo, static () => new T(), out value, out error);

    /// <summary>Parses a payload that must be present (e.g. one with <c>required</c> members).</summary>
    public static bool TryParseRequired<T>(
        JsonElement payload,
        JsonTypeInfo<T> typeInfo,
        [NotNullWhen(true)] out T? value,
        [NotNullWhen(false)] out string? error)
        where T : class =>
        TryParseCore(payload, typeInfo, whenMissing: null, out value, out error);

    private static bool TryParseCore<T>(
        JsonElement payload,
        JsonTypeInfo<T> typeInfo,
        Func<T>? whenMissing,
        [NotNullWhen(true)] out T? value,
        [NotNullWhen(false)] out string? error)
        where T : class
    {
        value = null;

        if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            value = whenMissing?.Invoke();
            error = value is null ? "Payload is required." : null;
            return value is not null;
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "Payload must be a JSON object.";
            return false;
        }

        try
        {
            value = payload.Deserialize(typeInfo) ?? whenMissing?.Invoke();
            error = value is null ? "Payload is required." : null;
            return value is not null;
        }
        catch (JsonException ex)
        {
            error = $"Invalid payload: {ex.Message}";
            return false;
        }
    }
}
