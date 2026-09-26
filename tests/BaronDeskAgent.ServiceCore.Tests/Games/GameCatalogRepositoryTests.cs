using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Games;
using BaronDeskAgent.ServiceCore.Persistence;
using BaronDeskAgent.ServiceCore.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BaronDeskAgent.ServiceCore.Tests.Games;

public sealed class GameCatalogRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Earlier = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    private TestDatabase _database = null!;
    private GameCatalogRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();
        _repository = new GameCatalogRepository(_database.Database);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task Launcher_fields_round_trip()
    {
        await _repository.UpsertAsync(new GameCatalogEntity
        {
            GameId = "cs2",
            Name = "Counter-Strike 2",
            LaunchType = GameLaunchTypes.Steam,
            Target = "730",
            LaunchArguments = "-novid",
            ProcessName = "cs2",
            CreatedAt = Earlier,
            UpdatedAt = Earlier
        });

        var stored = await _repository.GetByIdAsync("cs2");

        Assert.NotNull(stored);
        Assert.Equal(GameLaunchTypes.Steam, stored.LaunchType);
        Assert.Equal("730", stored.Target);
        Assert.Equal("-novid", stored.LaunchArguments);
        Assert.Equal("cs2", stored.ProcessName);
        Assert.Null(stored.WorkingDirectory);
    }

    [Fact]
    public async Task Replace_all_removes_missing_games_and_keeps_created_at()
    {
        await _repository.ReplaceAllAsync([Game("keep", Earlier), Game("drop", Earlier)]);

        await _repository.ReplaceAllAsync([Game("keep", Later, target: @"D:\New\game.exe"), Game("new", Later)]);

        var games = (await _repository.GetAllAsync()).ToDictionary(game => game.GameId);
        Assert.Equal(["keep", "new"], games.Keys.Order());
        Assert.Equal(@"D:\New\game.exe", games["keep"].Target);
        Assert.Equal(Earlier, games["keep"].CreatedAt);
        Assert.Equal(Later, games["keep"].UpdatedAt);
    }

    [Fact]
    public async Task Replace_all_with_an_empty_list_clears_the_catalog()
    {
        await _repository.ReplaceAllAsync([Game("g1", Earlier)]);

        await _repository.ReplaceAllAsync([]);

        Assert.Empty(await _repository.GetAllAsync());
    }

    [Fact]
    public async Task Migration_v3_keeps_existing_entries_as_exe_games()
    {
        var path = Path.Combine(Path.GetTempPath(), $"barondesk-test-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE GameCatalog
                    (
                        GameId TEXT PRIMARY KEY, Name TEXT NOT NULL, ExecutablePath TEXT NOT NULL,
                        LaunchArguments TEXT, WorkingDirectory TEXT, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL
                    );
                    INSERT INTO GameCatalog VALUES ('old', 'Old game', 'D:\Games\old.exe', '-x', NULL, '2026-09-01T00:00:00.0000000+00:00', '2026-09-01T00:00:00.0000000+00:00');
                    PRAGMA user_version = 2;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var database = new AgentDatabase(path);
            await new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

            var migrated = await new GameCatalogRepository(database).GetByIdAsync("old");

            Assert.NotNull(migrated);
            Assert.Equal(GameLaunchTypes.Exe, migrated.LaunchType);
            Assert.Equal(@"D:\Games\old.exe", migrated.Target);
            Assert.Equal("-x", migrated.LaunchArguments);
            Assert.Null(migrated.ProcessName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    private static GameCatalogEntity Game(string gameId, DateTimeOffset at, string target = @"D:\Games\game.exe") => new()
    {
        GameId = gameId,
        Name = gameId,
        Target = target,
        CreatedAt = at,
        UpdatedAt = at
    };
}
