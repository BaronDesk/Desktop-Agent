using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Session;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

/// <summary>
/// LOCK: show the overlay now (also what a billing run-out looks like). Acked only once the overlay is confirmed.
/// </summary>
public sealed class LockCommandHandler : ICommandHandler
{
    private readonly StationController _station;
    private readonly ILogger<LockCommandHandler> _logger;

    public LockCommandHandler(StationController station, ILogger<LockCommandHandler> logger)
    {
        _station = station;
        _logger = logger;
    }

    public string CommandType => CommandTypes.Lock;

    public bool IsRestrictive => true;

    public async Task<CommandResult> HandleAsync(CommandContext command, CancellationToken cancellationToken)
    {
        // Locking is always safe, so a malformed payload (it only carries an optional reason) does not stop it.
        if (CommandPayload.TryParse(command.Payload, AgentJsonContext.Default.LockPayload, out var payload, out var error))
        {
            _logger.LogInformation("LOCK requested ({Reason}).", payload.Reason ?? "no reason given");
        }
        else
        {
            _logger.LogWarning("LOCK payload ignored ({Error}); locking anyway.", error);
        }

        var overlay = await _station.LockAsync(cancellationToken);
        return overlay == OverlayResult.Confirmed
            ? CommandResult.Success()
            : CommandResult.Failed(overlay.DescribeLockFailure());
    }
}
