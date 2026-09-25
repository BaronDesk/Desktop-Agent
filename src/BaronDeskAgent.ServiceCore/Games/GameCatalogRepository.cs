using System.Globalization;
using BaronDeskAgent.ServiceCore.Persistence;
using Microsoft.Data.Sqlite;

namespace BaronDeskAgent.ServiceCore.Games;

public sealed class GameCatalogRepository
{
    private readonly AgentDatabase _database;

    public GameCatalogRepository(AgentDatabase database)
    {
        _database = database;
    }

    public async Task UpsertAsync(GameCatalogEntity game, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO GameCatalog (GameId, Name, ExecutablePath, LaunchArguments, WorkingDirectory, CreatedAt, UpdatedAt)
            VALUES ($gameId, $name, $executablePath, $launchArguments, $workingDirectory, $createdAt, $updatedAt)
            ON CONFLICT(GameId) DO UPDATE SET
                Name = excluded.Name,
                ExecutablePath = excluded.ExecutablePath,
                LaunchArguments = excluded.LaunchArguments,
                WorkingDirectory = excluded.WorkingDirectory,
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.Parameters.AddWithValue("$gameId", game.GameId);
        command.Parameters.AddWithValue("$name", game.Name);
        command.Parameters.AddWithValue("$executablePath", game.ExecutablePath);
        command.Parameters.AddWithValue("$launchArguments", (object?)game.LaunchArguments ?? DBNull.Value);
        command.Parameters.AddWithValue("$workingDirectory", (object?)game.WorkingDirectory ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", game.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAt", game.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<GameCatalogEntity?> GetByIdAsync(string gameId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT GameId, Name, ExecutablePath, LaunchArguments, WorkingDirectory, CreatedAt, UpdatedAt
            FROM GameCatalog
            WHERE GameId = $gameId;
            """;
        command.Parameters.AddWithValue("$gameId", gameId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static GameCatalogEntity Read(SqliteDataReader reader) => new()
    {
        GameId = reader.GetString(0),
        Name = reader.GetString(1),
        ExecutablePath = reader.GetString(2),
        LaunchArguments = reader.IsDBNull(3) ? null : reader.GetString(3),
        WorkingDirectory = reader.IsDBNull(4) ? null : reader.GetString(4),
        CreatedAt = ParseTimestamp(reader.GetString(5)),
        UpdatedAt = ParseTimestamp(reader.GetString(6))
    };

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
