using System.Globalization;
using BaronDeskAgent.ServiceCore.Persistence;

namespace BaronDeskAgent.ServiceCore.Commands;

/// <summary>
/// Ids of recently executed commands, kept in memory and persisted in SQLite, so a command the backend
/// redelivers (BullMQ retry, reconnect, agent restart) is re-acknowledged instead of executed twice.
/// </summary>
/// <remarks>
/// Only successes are recorded. A failed command may be retried with the same id and is executed again:
/// acknowledging a retry of something that never ran would lie to the backend.
/// </remarks>
public sealed class CommandOutcomeStore
{
    internal const int Capacity = 256;

    private readonly AgentDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CommandOutcomeStore> _logger;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _completed = [];
    private readonly Queue<Guid> _order = new();

    public CommandOutcomeStore(AgentDatabase database, TimeProvider timeProvider, ILogger<CommandOutcomeStore> logger)
    {
        _database = database;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CommandId FROM HandledCommands ORDER BY Seq DESC LIMIT $capacity;";
        command.Parameters.AddWithValue("$capacity", Capacity);

        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        lock (_gate)
        {
            // Oldest first, so eviction order matches the database.
            for (var i = ids.Count - 1; i >= 0; i--)
            {
                Remember(ids[i]);
            }
        }
    }

    public bool WasCompleted(Guid commandId)
    {
        lock (_gate)
        {
            return _completed.Contains(commandId);
        }
    }

    public async Task MarkCompletedAsync(Guid commandId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!Remember(commandId))
            {
                return;
            }
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR IGNORE INTO HandledCommands (CommandId, HandledAt) VALUES ($id, $handledAt);
                DELETE FROM HandledCommands WHERE Seq <= (SELECT MAX(Seq) FROM HandledCommands) - $capacity;
                """;
            command.Parameters.AddWithValue("$id", commandId.ToString());
            command.Parameters.AddWithValue("$handledAt", _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$capacity", Capacity);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // In-memory dedup still works; only protection across an agent restart is lost.
            _logger.LogWarning(ex, "Could not persist handled command {CommandId}.", commandId);
        }
    }

    /// <returns>False when the id was already known.</returns>
    private bool Remember(Guid commandId)
    {
        if (!_completed.Add(commandId))
        {
            return false;
        }

        _order.Enqueue(commandId);
        while (_order.Count > Capacity)
        {
            _completed.Remove(_order.Dequeue());
        }

        return true;
    }
}
