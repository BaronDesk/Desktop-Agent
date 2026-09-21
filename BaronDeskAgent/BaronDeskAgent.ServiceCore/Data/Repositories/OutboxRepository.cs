using BaronDeskAgent.ServiceCore.Data.Database;
using BaronDeskAgent.ServiceCore.Data.Entities;

namespace BaronDeskAgent.ServiceCore.Data.Repositories;

public sealed class OutboxRepository
{
    private readonly AgentDatabase _database;

    public OutboxRepository(AgentDatabase database)
    {
        _database = database;
    }

    public async Task InsertAsync(
        OutboxMessageEntity message,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            _database.CreateConnection();

        await connection.OpenAsync(cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO OutboxMessages
            (
                Id,
                Type,
                Payload,
                CreatedAt,
                Attempts
            )
            VALUES
            (
                $id,
                $type,
                $payload,
                $createdAt,
                $attempts
            );
            """;

        command.Parameters.AddWithValue(
            "$id",
            message.Id.ToString());

        command.Parameters.AddWithValue(
            "$type",
            message.Type);

        command.Parameters.AddWithValue(
            "$payload",
            message.Payload);

        command.Parameters.AddWithValue(
            "$createdAt",
            message.CreatedAt.ToString("O"));

        command.Parameters.AddWithValue(
            "$attempts",
            message.Attempts);

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxMessageEntity>> GetPendingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var messages =
            new List<OutboxMessageEntity>();

        await using var connection =
            _database.CreateConnection();

        await connection.OpenAsync(cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                Id,
                Type,
                Payload,
                CreatedAt,
                Attempts
            FROM OutboxMessages
            ORDER BY CreatedAt ASC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue(
            "$limit",
            limit);

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
            cancellationToken))
        {
            messages.Add(
                new OutboxMessageEntity
                {
                    Id = Guid.Parse(
                        reader.GetString(0)),

                    Type = reader.GetString(1),

                    Payload = reader.GetString(2),

                    CreatedAt = DateTimeOffset.Parse(
                        reader.GetString(3)),

                    Attempts = reader.GetInt32(4)
                });
        }

        return messages;
    }

    public async Task MarkSentAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            _database.CreateConnection();

        await connection.OpenAsync(cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            DELETE FROM OutboxMessages
            WHERE Id = $id;
            """;

        command.Parameters.AddWithValue(
            "$id",
            id.ToString());

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    public async Task IncrementAttemptsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            _database.CreateConnection();

        await connection.OpenAsync(cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            UPDATE OutboxMessages
            SET Attempts = Attempts + 1
            WHERE Id = $id;
            """;

        command.Parameters.AddWithValue(
            "$id",
            id.ToString());

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    public async Task<int> GetCountAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            _database.CreateConnection();

        await connection.OpenAsync(cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText =
            """
            SELECT COUNT(*)
            FROM OutboxMessages;
            """;

        var result =
            await command.ExecuteScalarAsync(
                cancellationToken);

        return Convert.ToInt32(result);
    }
}