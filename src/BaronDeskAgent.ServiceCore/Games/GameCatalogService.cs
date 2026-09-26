using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Connection;

namespace BaronDeskAgent.ServiceCore.Games;

/// <summary>
/// Catalog sync: pull the backend's catalog for this machine → validate each entry → replace the local SQLite copy
/// → report which games this station can actually launch (<c>catalog_status</c>).
/// </summary>
public sealed class GameCatalogService
{
    private const int MaxGames = 2000;

    private readonly IGameCatalogClient _client;
    private readonly GameCatalogRepository _repository;
    private readonly GameLaunchResolver _resolver;
    private readonly IGameLibraryLocator _libraries;
    private readonly IServerConnection _connection;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GameCatalogService> _logger;

    public GameCatalogService(
        IGameCatalogClient client,
        GameCatalogRepository repository,
        GameLaunchResolver resolver,
        IGameLibraryLocator libraries,
        IServerConnection connection,
        TimeProvider timeProvider,
        ILogger<GameCatalogService> logger)
    {
        _client = client;
        _repository = repository;
        _resolver = resolver;
        _libraries = libraries;
        _connection = connection;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <exception cref="InvalidDataException">The response is not a usable catalog (retrying will not help).</exception>
    /// <remarks>Also throws whatever <see cref="IGameCatalogClient.FetchAsync"/> and the connection throw.</remarks>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var response = await _client.FetchAsync(cancellationToken);
        var games = response.Games ?? throw new InvalidDataException("The catalog response has no 'games' list.");
        if (games.Count > MaxGames)
        {
            throw new InvalidDataException($"The catalog has {games.Count} games; at most {MaxGames} are accepted.");
        }

        var now = _timeProvider.GetUtcNow();
        var accepted = new List<GameCatalogEntity>();
        var rejected = new List<CatalogGameStatus>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var game in games)
        {
            if (game is null)
            {
                _logger.LogWarning("Skipped a null catalog entry.");
                continue;
            }

            if (!GameCatalogValidator.TryCreate(game, now, out var entity, out var error))
            {
                // One bad entry never blocks the rest of the catalog; it is reported when it can be identified.
                _logger.LogWarning("Skipped catalog entry {GameId}: {Error}", game.GameId, error);
                if (GameCatalogValidator.TryNormalizeGameId(game.GameId, out var gameId) && seen.Add(gameId))
                {
                    rejected.Add(new CatalogGameStatus { GameId = gameId, Installed = false, Reason = $"Invalid catalog entry: {error}" });
                }

                continue;
            }

            if (!seen.Add(entity.GameId))
            {
                _logger.LogWarning("Skipped duplicate catalog entry {GameId}.", entity.GameId);
                continue;
            }

            accepted.Add(entity);
        }

        await _repository.ReplaceAllAsync(accepted, cancellationToken);

        var statuses = EvaluateInstallStatus(accepted);
        _logger.LogInformation(
            "Game catalog synced: {Count} games, {Installed} launchable here, {Rejected} rejected.",
            accepted.Count, statuses.Count(status => status.Installed), rejected.Count);

        await _connection.SendAsync(
            MessageTypes.CatalogStatus,
            new CatalogStatusPayload { Games = [.. statuses, .. rejected] },
            AgentJsonContext.Default.CatalogStatusPayload,
            cancellationToken);
    }

    /// <summary>Whether each game can be launched on this station right now.</summary>
    public IReadOnlyList<CatalogGameStatus> EvaluateInstallStatus(IEnumerable<GameCatalogEntity> games)
    {
        var libraries = _libraries.Discover();
        return games
            .Select(game =>
            {
                var resolution = _resolver.Resolve(game, libraries);
                return new CatalogGameStatus { GameId = game.GameId, Installed = resolution.IsInstalled, Reason = resolution.Reason };
            })
            .ToList();
    }
}
