using System.Text.Json;
using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Commands;

public readonly record struct CommandResult(bool Succeeded, string? Code, string? Reason)
{
    public static CommandResult Success() => new(true, null, null);

    /// <summary>The payload is missing or malformed (<see cref="NackCodes.InvalidPayload"/>).</summary>
    public static CommandResult Invalid(string reason) => new(false, NackCodes.InvalidPayload, reason);

    /// <summary>The command was valid but could not be carried out (<see cref="NackCodes.ExecFailed"/>).</summary>
    public static CommandResult Failed(string reason) => new(false, NackCodes.ExecFailed, reason);
}

/// <summary>
/// One command being executed.
/// </summary>
public sealed class CommandContext
{
    private readonly Func<Task> _acknowledgeEarly;

    public CommandContext(Guid commandId, JsonElement payload, DateTimeOffset serverTimestamp, Func<Task> acknowledgeEarly)
    {
        CommandId = commandId;
        Payload = payload;
        ServerTimestamp = serverTimestamp;
        _acknowledgeEarly = acknowledgeEarly;
    }

    /// <summary>The command envelope's id.</summary>
    public Guid CommandId { get; }

    public JsonElement Payload { get; }

    /// <summary>The envelope <c>ts</c>, on the server clock.</summary>
    public DateTimeOffset ServerTimestamp { get; }

    public bool WasAcknowledgedEarly { get; private set; }

    /// <summary>
    /// Sends <c>command_ack</c> now, before execution finishes. For commands whose execution prevents a later
    /// ack from ever leaving (SHUTDOWN). The dispatcher then sends nothing else for this command.
    /// </summary>
    public async Task AcknowledgeEarlyAsync()
    {
        if (WasAcknowledgedEarly)
        {
            return;
        }

        WasAcknowledgedEarly = true;
        await _acknowledgeEarly();
    }
}
