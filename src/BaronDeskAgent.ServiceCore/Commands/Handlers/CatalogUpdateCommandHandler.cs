using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Games;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

/// <summary>
/// CATALOG_UPDATE: the station's catalog changed on the backend. Acked once a sync is queued; the result follows as
/// <c>catalog_status</c>. The payload is ignored: the catalog itself is pulled over HTTPS, since it can exceed the
/// WebSocket frame limit.
/// </summary>
public sealed class CatalogUpdateCommandHandler : ICommandHandler
{
    private readonly GameCatalogSyncWorker _sync;

    public CatalogUpdateCommandHandler(GameCatalogSyncWorker sync)
    {
        _sync = sync;
    }

    public string CommandType => CommandTypes.CatalogUpdate;

    public Task<CommandResult> HandleAsync(CommandContext command, CancellationToken cancellationToken)
    {
        _sync.RequestSync();
        return Task.FromResult(CommandResult.Success());
    }
}
