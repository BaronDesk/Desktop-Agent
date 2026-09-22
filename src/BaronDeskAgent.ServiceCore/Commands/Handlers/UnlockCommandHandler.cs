using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Session;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

/// <summary>
/// UNLOCK: the backend has authorised a session. Bind it, take the lease, hide the overlay.
/// </summary>
public sealed class UnlockCommandHandler : ICommandHandler
{
    private readonly StationController _station;

    public UnlockCommandHandler(StationController station)
    {
        _station = station;
    }

    public string CommandType => CommandTypes.Unlock;

    public async Task<CommandResult> HandleAsync(CommandContext command, CancellationToken cancellationToken)
    {
        if (!CommandPayload.TryParse(command.Payload, AgentJsonContext.Default.UnlockPayload, out var payload, out var error))
        {
            return CommandResult.Invalid(error);
        }

        // The agent decides nothing: it never invents a session id.
        if (payload.SessionId is not { } sessionId || sessionId == Guid.Empty)
        {
            return CommandResult.Invalid("sessionId is required.");
        }

        var lease = LeaseManager.ResolveDuration(
            payload.LeaseSeconds,
            payload.LeaseExpiresAt,
            payload.ServerTime ?? command.ServerTimestamp);

        if (lease is { } value && value <= TimeSpan.Zero)
        {
            return CommandResult.Invalid("The granted lease has already expired.");
        }

        await _station.StartSessionAsync(sessionId, lease, cancellationToken);
        return CommandResult.Success();
    }
}
