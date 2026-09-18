using System.Text.Json;
using BaronDesk.Shared.Models;
using BaronDeskAgent.ServiceCore.Data.Repositories;

namespace BaronDeskAgent.ServiceCore.Services.Outbox;

public sealed class OutboxWorker : BackgroundService
{
    private readonly OutboxRepository _outboxRepository;
    private readonly ITelemetryTransport _transport;
    private readonly ILogger<OutboxWorker> _logger;

    private static readonly TimeSpan RetryDelay =
        TimeSpan.FromSeconds(5);

    private const int BatchSize = 20;

    public OutboxWorker(
        OutboxRepository outboxRepository,
        ITelemetryTransport transport,
        ILogger<OutboxWorker> logger)
    {
        _outboxRepository = outboxRepository;
        _transport = transport;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox worker started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var messages =
                    await _outboxRepository.GetPendingAsync(
                        BatchSize,
                        stoppingToken);

                if (messages.Count == 0)
                {
                    _logger.LogInformation(
                        "Outbox is empty.");

                    await Task.Delay(
                        RetryDelay,
                        stoppingToken);

                    continue;
                }

                foreach (var message in messages)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await ProcessMessageAsync(
                        message,
                        stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Outbox worker stopped because of an unexpected error.");
        }

        _logger.LogInformation(
            "Outbox worker stopped.");
    }

    private async Task ProcessMessageAsync(
        Data.Entities.OutboxMessageEntity message,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope =
                JsonSerializer.Deserialize<TelemetryEnvelope>(
                    message.Payload);

            if (envelope is null)
            {
                _logger.LogError(
                    "Could not deserialize outbox message. " +
                    "Id={Id}, Type={Type}",
                    message.Id,
                    message.Type);

                await _outboxRepository.IncrementAttemptsAsync(
                    message.Id,
                    cancellationToken);

                return;
            }

            await _transport.SendAsync(
                envelope,
                cancellationToken);

            await _outboxRepository.MarkSentAsync(
                message.Id,
                cancellationToken);

            _logger.LogInformation(
                "Outbox message sent successfully. " +
                "Type={Type}, Sequence={Sequence}, Id={Id}",
                envelope.Type,
                envelope.Sequence,
                envelope.Id);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _outboxRepository.IncrementAttemptsAsync(
                message.Id,
                cancellationToken);

            _logger.LogWarning(
                ex,
                "Failed to send outbox message. " +
                "It will remain in SQLite for retry. " +
                "Id={Id}, Type={Type}, Attempts={Attempts}",
                message.Id,
                message.Type,
                message.Attempts + 1);
        }
    }
}