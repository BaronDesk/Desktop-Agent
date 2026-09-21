using Microsoft.Data.Sqlite;

namespace BaronDeskAgent.ServiceCore.Data.Database;

public sealed class DatabaseInitializer
{
    private readonly AgentDatabase _database;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        AgentDatabase database,
        ILogger<DatabaseInitializer> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            _database.CreateConnection();

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            PRAGMA journal_mode = WAL;

            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS OutboxMessages
            (
                Id TEXT PRIMARY KEY,

                Type TEXT NOT NULL,

                Payload TEXT NOT NULL,

                CreatedAt TEXT NOT NULL,

                Attempts INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS
                IX_OutboxMessages_CreatedAt
            ON OutboxMessages (CreatedAt);

            CREATE TABLE IF NOT EXISTS GameCatalog
            (
                GameId TEXT PRIMARY KEY,

                Name TEXT NOT NULL,

                ExecutablePath TEXT NOT NULL,

                LaunchArguments TEXT,

                WorkingDirectory TEXT,

                CreatedAt TEXT NOT NULL,

                UpdatedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS
                IX_GameCatalog_Name
            ON GameCatalog (Name);
            """;

        await command.ExecuteNonQueryAsync(
            cancellationToken);

        // Seed default test game if catalog is empty
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(1) FROM GameCatalog;";
        var count = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(cancellationToken));

        if (count == 0)
        {
            var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var notepadPath = System.IO.Path.Combine(systemRoot, "notepad.exe");
            var nowIso = DateTimeOffset.UtcNow.ToString("O");

            await using var seedCmd = connection.CreateCommand();
            seedCmd.CommandText =
                """
                INSERT INTO GameCatalog (GameId, Name, ExecutablePath, LaunchArguments, WorkingDirectory, CreatedAt, UpdatedAt)
                VALUES ('notepad', 'Notepad (Test Game)', $path, NULL, $workDir, $now, $now);
                """;
            seedCmd.Parameters.AddWithValue("$path", notepadPath);
            seedCmd.Parameters.AddWithValue("$workDir", systemRoot);
            seedCmd.Parameters.AddWithValue("$now", nowIso);
            await seedCmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Seeded default test game entry ('notepad') into GameCatalog.");
        }

        _logger.LogInformation(
            "SQLite database initialized at {DatabasePath}.",
            DatabasePaths.DatabaseFile);
    }
}