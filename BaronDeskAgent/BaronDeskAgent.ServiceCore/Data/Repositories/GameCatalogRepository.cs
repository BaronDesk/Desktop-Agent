using BaronDeskAgent.ServiceCore.Data.Database;
using BaronDeskAgent.ServiceCore.Data.Entities;
using Microsoft.Data.Sqlite;

namespace BaronDeskAgent.ServiceCore.Data.Repositories;

public sealed class GameCatalogRepository
{
    private readonly AgentDatabase _database;

    public GameCatalogRepository(AgentDatabase database)
    {
        _database = database;
    }

    public async Task UpsertAsync(
        GameCatalogEntity game,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO GameCatalog
            (
                GameId,
                Name,
                ExecutablePath,
                LaunchArguments,
                WorkingDirectory,
                CreatedAt,
                UpdatedAt
            )
            VALUES
            (
                $gameId,
                $name,
                $executablePath,
                $launchArguments,
                $workingDirectory,
                $createdAt,
                $updatedAt
            )
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
        command.Parameters.AddWithValue("$createdAt", game.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", game.UpdatedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<GameCatalogEntity?> GetByIdAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                GameId,
                Name,
                ExecutablePath,
                LaunchArguments,
                WorkingDirectory,
                CreatedAt,
                UpdatedAt
            FROM GameCatalog
            WHERE GameId = $gameId
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$gameId", gameId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadEntity(reader);
    }

    public async Task<IReadOnlyList<GameCatalogEntity>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var list = new List<GameCatalogEntity>();

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                GameId,
                Name,
                ExecutablePath,
                LaunchArguments,
                WorkingDirectory,
                CreatedAt,
                UpdatedAt
            FROM GameCatalog
            ORDER BY Name ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(ReadEntity(reader));
        }

        return list;
    }

    public async Task<bool> DeleteAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM GameCatalog
            WHERE GameId = $gameId;
            """;

        command.Parameters.AddWithValue("$gameId", gameId);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    private static GameCatalogEntity ReadEntity(SqliteDataReader reader)
    {
        return new GameCatalogEntity
        {
            GameId = reader.GetString(0),
            Name = reader.GetString(1),
            ExecutablePath = reader.GetString(2),
            LaunchArguments = reader.IsDBNull(3) ? null : reader.GetString(3),
            WorkingDirectory = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(6))
        };
    }
}
