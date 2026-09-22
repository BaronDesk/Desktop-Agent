using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Session;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

/// <summary>
/// END_SESSION: lock, stop the game the agent launched, forget the session, revoke the lease.
/// OPEN (skill §15 item 12): exact semantics still to confirm with the backend.
/// </summary>
public sealed class EndSessionCommandHandler : ICommandHandler
{
    private const string DefaultReason = "end_session";

    private readonly StationController _station;
    private readonly ILogger<EndSessionCommandHandler> _logger;

    public EndSessionCommandHandler(StationController station, ILogger<EndSessionCommandHandler> logger)
    {
        _station = station;
        _logger = logger;
    }

    public string CommandType => CommandTypes.EndSession;

    public bool IsRestrictive => true;

    public async Task<CommandResult> HandleAsync(CommandContext command, CancellationToken cancellationToken)
    {
        Guid? sessionId = null;
        var reason = DefaultReason;

        if (CommandPayload.TryParse(command.Payload, AgentJsonContext.Default.EndSessionPayload, out var payload, out var error))
        {
            sessionId = payload.SessionId;
            reason = string.IsNullOrWhiteSpace(payload.Reason) ? DefaultReason : payload.Reason;
        }
        else
        {
            // Ending the session and locking is always safe, so a malformed payload does not stop it.
            _logger.LogWarning("END_SESSION payload ignored ({Error}); ending the active session anyway.", error);
        }

        return await _station.EndSessionAsync(sessionId, reason, cancellationToken) switch
        {
            EndSessionResult.Ended or EndSessionResult.StaleSessionIgnored => CommandResult.Success(),
            _ => CommandResult.Failed("Session ended and station state is locked, but the lock screen did not confirm it is visible.")
        };
    }
}
