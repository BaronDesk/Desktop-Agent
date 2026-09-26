using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Commands;
using BaronDeskAgent.ServiceCore.Commands.Handlers;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Games;
using BaronDeskAgent.ServiceCore.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Tests.Games;

public sealed class GameCatalogServiceTests : IAsyncLifetime
{
    private readonly FakeGameCatalogClient _client = new();
    private readonly FakeServerConnection _connection = new();
    private readonly string _root = Directory.CreateTempSubdirectory("barondesk-catalog-").FullName;
    private TestDatabase _database = null!;
    private GameCatalogRepository _repository = null!;
    private GameCatalogService _service = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();
        _repository = new GameCatalogRepository(_database.Database);
        _service = new GameCatalogService(
            _client,
            _repository,
            new GameLaunchResolver(Options.Create(new AgentOptions())),
            new FakeGameLibraryLocator(),
            _connection,
            TimeProvider.System,
            NullLogger<GameCatalogService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Sync_replaces_the_catalog_and_reports_install_status()
    {
        var installed = Path.Combine(_root, "installed.exe");
        File.WriteAllText(installed, string.Empty);
        await _repository.UpsertAsync(new GameCatalogEntity { GameId = "stale", Name = "Stale", Target = installed });
        _client.Response = new GameCatalogResponse
        {
            Games =
            [
                new CatalogGame { GameId = "here", Name = "Here", Target = installed },
                new CatalogGame { GameId = "missing", Name = "Missing", Target = Path.Combine(_root, "missing.exe") },
                new CatalogGame { GameId = "cs2", Name = "CS2", LaunchType = "steam", Target = "730", ProcessName = "cs2.exe" }
            ]
        };

        await _service.SyncAsync(CancellationToken.None);

        Assert.Equal(["cs2", "here", "missing"], (await _repository.GetAllAsync()).Select(game => game.GameId).Order());
        var statuses = SingleStatus();
        Assert.True(statuses["here"].GetProperty("installed").GetBoolean());
        Assert.False(statuses["missing"].GetProperty("installed").GetBoolean());
        Assert.False(statuses["cs2"].GetProperty("installed").GetBoolean());   // no Steam in the fake libraries
        Assert.Contains("Steam", statuses["cs2"].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Invalid_and_duplicate_entries_are_skipped_without_blocking_the_rest()
    {
        _client.Response = new GameCatalogResponse
        {
            Games =
            [
                new CatalogGame { GameId = "ok", Target = @"D:\Games\ok.exe" },
                new CatalogGame { GameId = "bad", Target = "relative.exe" },
                new CatalogGame { GameId = "ok", Target = @"D:\Games\other.exe" },
                new CatalogGame { Target = @"D:\Games\anonymous.exe" },
                null
            ]
        };

        await _service.SyncAsync(CancellationToken.None);

        var stored = Assert.Single(await _repository.GetAllAsync());
        Assert.Equal(@"D:\Games\ok.exe", stored.Target);

        var statuses = SingleStatus();
        Assert.Equal(["bad", "ok"], statuses.Keys.Order());
        Assert.StartsWith("Invalid catalog entry", statuses["bad"].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_response_without_a_games_list_is_rejected_and_the_catalog_kept()
    {
        await _repository.UpsertAsync(new GameCatalogEntity { GameId = "keep", Name = "Keep", Target = @"D:\Games\keep.exe" });
        _client.Response = new GameCatalogResponse { Games = null };

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.SyncAsync(CancellationToken.None));

        Assert.Single(await _repository.GetAllAsync());
        Assert.Empty(_connection.OfType(MessageTypes.CatalogStatus));
    }

    [Fact]
    public async Task Catalog_update_command_triggers_a_background_sync()
    {
        _client.Response = new GameCatalogResponse { Games = [new CatalogGame { GameId = "g1", Target = @"D:\Games\g1.exe" }] };
        using var worker = new GameCatalogSyncWorker(_service, _connection, TimeProvider.System, NullLogger<GameCatalogSyncWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);

        var result = await new CatalogUpdateCommandHandler(worker).HandleAsync(
            new CommandContext(Guid.NewGuid(), default, DateTimeOffset.UtcNow, () => Task.CompletedTask),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        await WaitUntilAsync(() => _connection.OfType(MessageTypes.CatalogStatus).Count > 0);
        Assert.Equal("g1", Assert.Single(await _repository.GetAllAsync()).GameId);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Connecting_triggers_a_sync()
    {
        using var worker = new GameCatalogSyncWorker(_service, _connection, TimeProvider.System, NullLogger<GameCatalogSyncWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);

        _connection.MarkReady();

        await WaitUntilAsync(() => _connection.OfType(MessageTypes.CatalogStatus).Count > 0);
        await worker.StopAsync(CancellationToken.None);
    }

    private Dictionary<string, JsonElement> SingleStatus()
    {
        var frame = Assert.Single(_connection.OfType(MessageTypes.CatalogStatus));
        return frame.Payload.GetProperty("games").EnumerateArray()
            .ToDictionary(game => game.GetProperty("gameId").GetString()!);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
