using System.Threading.Channels;
using BaronDeskAgent.ServiceCore.Connection;

namespace BaronDeskAgent.ServiceCore.Telemetry.Outbox;

/// <summary>
/// Entry point of the outbox: persists a message and wakes the <see cref="OutboxWorker"/>, which otherwise
/// sleeps (no polling).
/// </summary>
public sealed class OutboxQueue
{
    /// <summary>Bound on buffered messages; the oldest are dropped first when an outage lasts long.</summary>
    internal const int Capacity = 1000;

    private readonly OutboxRepository _repository;
    private readonly ServerClock _serverClock;
    private readonly ILogger<OutboxQueue> _logger;

    // Holds at most one pending wake-up; extra signals collapse into it.
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public OutboxQueue(OutboxRepository repository, ServerClock serverClock, ILogger<OutboxQueue> logger)
    {
        _repository = repository;
        _serverClock = serverClock;
        _logger = logger;
    }

    public async Task EnqueueAsync(string type, string payloadJson, CancellationToken cancellationToken)
    {
        var message = new OutboxMessageEntity
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = payloadJson,
            CreatedAt = _serverClock.UtcNow
        };

        var dropped = await _repository.InsertAsync(message, Capacity, cancellationToken);
        if (dropped > 0)
        {
            _logger.LogWarning("Outbox full: dropped the {Count} oldest buffered message(s).", dropped);
        }

        _signal.Writer.TryWrite(true);
    }

    /// <summary>Completes when a message was enqueued since the last call.</summary>
    public async Task WaitForWorkAsync(CancellationToken cancellationToken) =>
        await _signal.Reader.ReadAsync(cancellationToken);
}
