using BaronDeskAgent.ServiceCore.Data.Database;
using BaronDeskAgent.ServiceCore.Data.Entities;

namespace BaronDeskAgent.ServiceCore.Data.Repositories;

public sealed class OutboxRepository
{
    private readonly AgentDatabase _database;

    public OutboxRepository(
        AgentDatabase database)
    {
        _database = database;
    }

    public async Task InsertAsync(
        OutboxMessageEntity message,
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

    public async Task<int> GetCountAsync(
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
            SELECT COUNT(*)
            FROM OutboxMessages;
            """;

        var result =
            await command.ExecuteScalarAsync(
                cancellationToken);

        return Convert.ToInt32(result);
    }
}