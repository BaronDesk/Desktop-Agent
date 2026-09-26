using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Games;
using BaronDeskAgent.ServiceCore.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BaronDeskAgent.ServiceCore.Tests.Games;

// Paths that launch a real process are covered by the mock-server walkthrough (README §5); these tests cover every
// path that must refuse before anything is started or stopped.
public sealed class GameServiceTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new();
    private TestDatabase _database = null!;
    private GameCatalogRepository _catalog = null!;
    private GameService _games = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();
        _catalog = new GameCatalogRepository(_database.Database);
        _games = new GameService(
            _catalog,
            new GameLaunchResolver(Options.Create(new AgentOptions())),
            new FakeGameLibraryLocator(),
            new InteractiveProcessLauncher(NullLogger<InteractiveProcessLauncher>.Instance),
            _time,
            NullLogger<GameService>.Instance);
    }

    public async Task DisposeAsync()
    {
        _games.Dispose();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Nothing_is_running_before_a_launch()
    {
        Assert.Null(_games.CurrentGameId);
        await _games.StopCurrentGameAsync(CancellationToken.None);   // no-op, never throws
    }

    [Fact]
    public async Task Unknown_game_is_refused()
    {
        var ex = await Assert.ThrowsAsync<GameNotFoundException>(() => _games.LaunchAsync("nope", CancellationToken.None));
        Assert.Equal("nope", ex.GameId);
    }

    [Fact]
    public async Task Missing_executable_is_refused_as_not_installed_without_a_path_in_the_message()
    {
        var path = Path.Combine(Path.GetTempPath(), $"barondesk-{Guid.NewGuid():N}", "game.exe");
        await _catalog.UpsertAsync(new GameCatalogEntity { GameId = "g1", Name = "Game", Target = path });

        var ex = await Assert.ThrowsAsync<GameNotInstalledException>(() => _games.LaunchAsync("g1", CancellationToken.None));

        Assert.DoesNotContain(path, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(_games.CurrentGameId);
    }

    [Theory]
    [InlineData(GameLaunchTypes.Steam, "730")]
    [InlineData(GameLaunchTypes.Epic, "Fortnite")]
    public async Task Launcher_game_without_its_launcher_is_refused(string launchType, string target)
    {
        await _catalog.UpsertAsync(new GameCatalogEntity { GameId = "g1", Name = "Game", LaunchType = launchType, Target = target, ProcessName = "game" });

        await Assert.ThrowsAsync<GameNotInstalledException>(() => _games.LaunchAsync("g1", CancellationToken.None));
    }
}
