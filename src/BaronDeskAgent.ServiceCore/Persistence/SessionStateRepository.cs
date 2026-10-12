
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
        CancellationToken cancellationToken = default,
        DateTimeOffset? contactLostAt = null,
        bool restartRecovered = false)
    {
        await using var connection =
            await _database.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SessionState
                (Id, SessionId, LockReason, LockedAt, UpdatedAt,
                 ContactLostAt, RestartRecovered)
            VALUES
                (1, $sessionId, $lockReason, $lockedAt, $updatedAt,
                 $contactLostAt, $restartRecovered)
            ON CONFLICT(Id) DO UPDATE SET
                SessionId = excluded.SessionId,
                LockReason = excluded.LockReason,
                LockedAt = excluded.LockedAt,
                UpdatedAt = excluded.UpdatedAt,
                ContactLostAt = excluded.ContactLostAt,
                RestartRecovered = excluded.RestartRecovered;
            """;

        command.Parameters.AddWithValue(
            "$sessionId", sessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$lockReason", lockReason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$lockedAt", FormatDate(lockedAt));
        command.Parameters.AddWithValue(
            "$updatedAt", DateTimeOffset.UtcNow.ToString(
                "O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$contactLostAt", FormatDate(contactLostAt));
        command.Parameters.AddWithValue(
            "$restartRecovered", restartRecovered ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PersistedSessionState?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _database.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SessionId, LockReason, LockedAt,
                   ContactLostAt, RestartRecovered
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

        var lockedAt = ParseDate(reader, 2);
        var contactLostAt = ParseDate(reader, 3);
        var restartRecovered = !reader.IsDBNull(4)
            && reader.GetInt32(4) != 0;

        return new PersistedSessionState(
            sessionId,
            lockReason,
            lockedAt,
            contactLostAt,
            restartRecovered);
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

    private static object FormatDate(DateTimeOffset? value)
    {
        if (value is null)
            return DBNull.Value;
        return value.Value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ParseDate(
        SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(
                reader.GetString(ordinal),
                CultureInfo.InvariantCulture);
}
