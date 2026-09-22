using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Connection;

namespace BaronDeskAgent.ServiceCore.Telemetry.Outbox;

/// <summary>
/// Delivers buffered messages in order. Sleeps until there is work and a live connection; never busy-loops
/// while offline and never stops for good on an unexpected error.
/// </summary>
public sealed class OutboxWorker : BackgroundService
{
    private const int BatchSize = 20;

    /// <summary>Sends that fail while the connection is up; after this many the message is dead-lettered.</summary>
    internal const int MaxAttempts = 10;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly OutboxRepository _repository;
    private readonly OutboxQueue _queue;
    private readonly IServerConnection _connection;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxWorker> _logger;

    public OutboxWorker(
        OutboxRepository repository,
        OutboxQueue queue,
        IServerConnection connection,
        TimeProvider timeProvider,
        ILogger<OutboxWorker> logger)
    {
        _repository = repository;
        _queue = queue;
        _connection = connection;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _connection.WaitUntilReadyAsync(stoppingToken);

                var batch = await _repository.GetOldestAsync(BatchSize, stoppingToken);
                if (batch.Count == 0)
                {
                    await _queue.WaitForWorkAsync(stoppingToken);
                    continue;
                }

                foreach (var message in batch)
                {
                    if (!await TrySendAsync(message, stoppingToken))
                    {
                        // Keep order: stop at the first failure and retry the same message later.
                        await Task.Delay(RetryDelay, _timeProvider, stoppingToken);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox delivery failed; retrying in {Delay}s.", RetryDelay.TotalSeconds);
                try
                {
                    await Task.Delay(RetryDelay, _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <returns>True when the message is gone from the outbox (sent, corrupt, or dead-lettered).</returns>
    private async Task<bool> TrySendAsync(OutboxMessageEntity message, CancellationToken cancellationToken)
    {
        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(message.Payload);
            payload = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Dropping corrupt outbox message {Id} ({Type}).", message.Id, message.Type);
            await _repository.DeleteAsync(message.Id, cancellationToken);
            return true;
        }

        try
        {
            await _connection.SendAsync(message.Type, payload, AgentJsonContext.Default.JsonElement, cancellationToken, message.Id);
            await _repository.DeleteAsync(message.Id, cancellationToken);
            _logger.LogDebug("Delivered outbox message {Id} ({Type}).", message.Id, message.Type);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_connection.IsReady)
            {
                // Lost the connection: not the message's fault, so it does not count as an attempt.
                return false;
            }

            if (message.Attempts + 1 >= MaxAttempts)
            {
                _logger.LogError(ex, "Dead-lettering outbox message {Id} ({Type}) after {Attempts} attempts.", message.Id, message.Type, MaxAttempts);
                await _repository.DeleteAsync(message.Id, cancellationToken);
                return true;
            }

            await _repository.IncrementAttemptsAsync(message.Id, cancellationToken);
            _logger.LogWarning(ex, "Could not deliver outbox message {Id} ({Type}); will retry.", message.Id, message.Type);
            return false;
        }
    }
}
