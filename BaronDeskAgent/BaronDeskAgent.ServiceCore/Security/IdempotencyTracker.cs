using System.Collections.Concurrent;

namespace BaronDeskAgent.ServiceCore.Security;

/// <summary>
/// Maintains a bounded cache of recently processed command IDs
/// to ensure idempotency when the server redelivers commands.
/// </summary>
public sealed class IdempotencyTracker
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _processed = new();
    private readonly ConcurrentQueue<Guid> _order = new();
    private readonly int _capacity;
    private readonly object _lock = new();

    public IdempotencyTracker(int capacity = 256)
    {
        _capacity = capacity;
    }

    /// <summary>
    /// Checks whether a command has already been processed.
    /// If not seen before, registers it and returns false.
    /// If seen before, returns true.
    /// </summary>
    public bool IsDuplicate(Guid commandId)
    {
        if (_processed.ContainsKey(commandId))
        {
            return true;
        }

        lock (_lock)
        {
            if (_processed.ContainsKey(commandId))
            {
                return true;
            }

            if (_processed.TryAdd(commandId, DateTimeOffset.UtcNow))
            {
                _order.Enqueue(commandId);

                while (_order.Count > _capacity && _order.TryDequeue(out var oldest))
                {
                    _processed.TryRemove(oldest, out _);
                }
            }
        }

        return false;
    }
}
