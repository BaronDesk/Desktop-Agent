using System.Globalization;
using BaronDeskAgent.ServiceCore.Persistence;
using Microsoft.Data.Sqlite;

namespace BaronDeskAgent.ServiceCore.Games;

public sealed class GameCatalogRepository
{
    private const string Columns = "GameId, Name, LaunchType, Target, LaunchArguments, WorkingDirectory, ProcessName, CreatedAt, UpdatedAt";

    private readonly AgentDatabase _database;

    public GameCatalogRepository(AgentDatabase database)
    {
        _database = database;
    }

    public async Task UpsertAsync(GameCatalogEntity game, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = CreateUpsert(connection, game);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Makes the catalog exactly <paramref name="games"/> in one transaction: missing entries are removed, the others
    /// inserted or updated (keeping their original <c>CreatedAt</c>).
    /// </summary>
    public async Task ReplaceAllAsync(IReadOnlyCollection<GameCatalogEntity> games, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        var keep = games.Select(game => game.GameId).ToHashSet(StringComparer.Ordinal);
        var stale = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT GameId FROM GameCatalog;";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var gameId = reader.GetString(0);
                if (!keep.Contains(gameId))
                {
                    stale.Add(gameId);
                }
            }
        }

        foreach (var gameId in stale)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM GameCatalog WHERE GameId = $gameId;";
            delete.Parameters.AddWithValue("$gameId", gameId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var game in games)
        {
            await using var upsert = CreateUpsert(connection, game);
            upsert.Transaction = transaction;
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<GameCatalogEntity?> GetByIdAsync(string gameId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM GameCatalog WHERE GameId = $gameId;";
        command.Parameters.AddWithValue("$gameId", gameId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<GameCatalogEntity>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM GameCatalog ORDER BY Name;";

        var games = new List<GameCatalogEntity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            games.Add(Read(reader));
        }

        return games;
    }

    private static SqliteCommand CreateUpsert(SqliteConnection connection, GameCatalogEntity game)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO GameCatalog ({Columns})
            VALUES ($gameId, $name, $launchType, $target, $launchArguments, $workingDirectory, $processName, $createdAt, $updatedAt)
            ON CONFLICT(GameId) DO UPDATE SET
                Name = excluded.Name,
                LaunchType = excluded.LaunchType,
                Target = excluded.Target,
                LaunchArguments = excluded.LaunchArguments,
                WorkingDirectory = excluded.WorkingDirectory,
                ProcessName = excluded.ProcessName,
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.Parameters.AddWithValue("$gameId", game.GameId);
        command.Parameters.AddWithValue("$name", game.Name);
        command.Parameters.AddWithValue("$launchType", game.LaunchType);
        command.Parameters.AddWithValue("$target", game.Target);
        command.Parameters.AddWithValue("$launchArguments", (object?)game.LaunchArguments ?? DBNull.Value);
        command.Parameters.AddWithValue("$workingDirectory", (object?)game.WorkingDirectory ?? DBNull.Value);
        command.Parameters.AddWithValue("$processName", (object?)game.ProcessName ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", game.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAt", game.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        return command;
    }

    private static GameCatalogEntity Read(SqliteDataReader reader) => new()
    {
        GameId = reader.GetString(0),
        Name = reader.GetString(1),
        LaunchType = reader.GetString(2),
        Target = reader.GetString(3),
        LaunchArguments = reader.IsDBNull(4) ? null : reader.GetString(4),
        WorkingDirectory = reader.IsDBNull(5) ? null : reader.GetString(5),
        ProcessName = reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedAt = ParseTimestamp(reader.GetString(7)),
        UpdatedAt = ParseTimestamp(reader.GetString(8))
    };

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
