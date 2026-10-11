
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace BaronDeskAgent.ServiceCore.Persistence;

public sealed class SessionStateRepository
{
    private readonly AgentDatabase _database;

    public SessionStateRepository(AgentDatabase database)
    {
        _database = database;
    }

    public async Task SaveAsync(
        Guid? sessionId,
        string? lockReason,
        DateTimeOffset? lockedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _database.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SessionState
                (Id, SessionId, LockReason, LockedAt, UpdatedAt)
            VALUES
                (1, $sessionId, $lockReason, $lockedAt, $updatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                SessionId = excluded.SessionId,
                LockReason = excluded.LockReason,
                LockedAt = excluded.LockedAt,
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.Parameters.AddWithValue(
            "$sessionId", sessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$lockReason", lockReason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$lockedAt", lockedAt?.ToString("O", CultureInfo.InvariantCulture)
                ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$updatedAt", DateTimeOffset.UtcNow.ToString(
                "O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PersistedSessionState?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _database.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SessionId, LockReason, LockedAt
            FROM SessionState
            WHERE Id = 1;
            """;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var sessionId = reader.IsDBNull(0)
            ? (Guid?)null
            : Guid.Parse(reader.GetString(0));

        var lockReason = reader.IsDBNull(1)
            ? null
            : reader.GetString(1);

        var lockedAt = reader.IsDBNull(2)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(
                reader.GetString(2), CultureInfo.InvariantCulture);

        return new PersistedSessionState(sessionId, lockReason, lockedAt);
    }

    public async Task ClearAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _database.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SessionState WHERE Id = 1;";

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
