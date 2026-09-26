using BaronDeskAgent.ServiceCore.Games;

namespace BaronDeskAgent.ServiceCore.Persistence;

/// <summary>
/// Development-only sample data, so <c>LAUNCH_GAME { "gameId": "charmap" }</c> works against the mock server before
/// any catalog sync. A <c>CATALOG_UPDATE</c> replaces the whole catalog, including this entry.
/// Character Map rather than Notepad: on Windows 11 notepad.exe is a stub that exits, and the Notepad app restores the
/// developer's own documents, which a test session end must never close.
/// </summary>
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
                GameId = "charmap",
                Name = "Character Map (test game)",
                Target = Path.Combine(system, "charmap.exe"),
                WorkingDirectory = system,
                CreatedAt = now,
                UpdatedAt = now
            },
            cancellationToken);
    }
}
