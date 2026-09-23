using BaronDeskAgent.ServiceCore.Games;

namespace BaronDeskAgent.ServiceCore.Persistence;

/// <summary>
/// Development-only sample data, so <c>LAUNCH_GAME { "gameId": "notepad" }</c> works against the mock server.
/// </summary>
/// <remarks>
/// OPEN (skill §15 item 9): production catalog delivery (e.g. <c>GET /games</c> with the station credential)
/// is not defined yet; <see cref="GameCatalogRepository.UpsertAsync"/> is the entry point for it.
/// </remarks>
public sealed class DevelopmentDataSeeder
{
    private readonly GameCatalogRepository _catalog;
    private readonly TimeProvider _timeProvider;

    public DevelopmentDataSeeder(GameCatalogRepository catalog, TimeProvider timeProvider)
    {
        _catalog = catalog;
        _timeProvider = timeProvider;
    }

    public Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var now = _timeProvider.GetUtcNow();

        return _catalog.UpsertAsync(
            new GameCatalogEntity
            {
                GameId = "notepad",
                Name = "Notepad (test game)",
                ExecutablePath = Path.Combine(system, "notepad.exe"),
                WorkingDirectory = system,
                CreatedAt = now,
                UpdatedAt = now
            },
            cancellationToken);
    }
}
