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
            """;

        await command.ExecuteNonQueryAsync(
            cancellationToken);

        _logger.LogInformation(
            "SQLite database initialized at {DatabasePath}.",
            DatabasePaths.DatabaseFile);
    }
}