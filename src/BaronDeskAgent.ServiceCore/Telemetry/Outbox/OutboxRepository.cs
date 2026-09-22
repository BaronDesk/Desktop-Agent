using System.Globalization;
using BaronDeskAgent.ServiceCore.Persistence;

namespace BaronDeskAgent.ServiceCore.Telemetry.Outbox;

/// <summary>
/// SQLite storage for <see cref="OutboxMessageEntity"/>. Rows are ordered by insertion (<c>rowid</c>).
/// </summary>
public sealed class OutboxRepository
{
    private readonly AgentDatabase _database;

    public OutboxRepository(AgentDatabase database)
    {
        _database = database;
    }

    /// <summary>Inserts a message and trims the oldest rows beyond <paramref name="capacity"/>.</summary>
    /// <returns>How many old rows were dropped to respect the capacity.</returns>
    public async Task<int> InsertAsync(OutboxMessageEntity message, int capacity, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO OutboxMessages (Id, Type, Payload, CreatedAt, Attempts)
            VALUES ($id, $type, $payload, $createdAt, 0);
            """;
        insert.Parameters.AddWithValue("$id", message.Id.ToString());
        insert.Parameters.AddWithValue("$type", message.Type);
        insert.Parameters.AddWithValue("$payload", message.Payload);
        insert.Parameters.AddWithValue("$createdAt", message.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(cancellationToken);

        await using var trim = connection.CreateCommand();
        trim.Transaction = transaction;
        trim.CommandText =
            """
            DELETE FROM OutboxMessages
            WHERE rowid NOT IN (SELECT rowid FROM OutboxMessages ORDER BY rowid DESC LIMIT $capacity);
            """;
        trim.Parameters.AddWithValue("$capacity", capacity);
        var dropped = await trim.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return dropped;
    }

    public async Task<IReadOnlyList<OutboxMessageEntity>> GetOldestAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, Type, Payload, CreatedAt, Attempts
            FROM OutboxMessages
            ORDER BY rowid
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var messages = new List<OutboxMessageEntity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new OutboxMessageEntity
            {
                Id = Guid.Parse(reader.GetString(0)),
                Type = reader.GetString(1),
                Payload = reader.GetString(2),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Attempts = reader.GetInt32(4)
            });
        }

        return messages;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        ExecuteAsync("DELETE FROM OutboxMessages WHERE Id = $id;", id, cancellationToken);

    public Task IncrementAttemptsAsync(Guid id, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE OutboxMessages SET Attempts = Attempts + 1 WHERE Id = $id;", id, cancellationToken);

    private async Task ExecuteAsync(string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
