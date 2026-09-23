namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public interface ICommandHandler
{
    /// <summary>One of <see cref="BaronDesk.Shared.Contracts.CommandTypes"/>.</summary>
    string CommandType { get; }

    /// <summary>
    /// True for commands that only restrict the station (LOCK, END_SESSION). Executing a late copy of these
    /// is always safe, so they skip the timestamp-freshness check: clock drift must never keep a station
    /// unlocked. Sequence-based replay protection still applies.
    /// </summary>
    bool IsRestrictive => false;

    /// <summary>Validates the payload and executes. Expected failures are returned, not thrown.</summary>
    Task<CommandResult> HandleAsync(CommandContext command, CancellationToken cancellationToken);
}
