using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDesk.Shared.Models;
using BaronDeskAgent.ServiceCore.Data.Entities;
using BaronDeskAgent.ServiceCore.Data.Repositories;

namespace BaronDeskAgent.ServiceCore.Services.Telemetry;

public sealed class TelemetryService
{
    private readonly ITelemetryTransport _transport;
    private readonly OutboxRepository _outboxRepository;
    private readonly ILogger<TelemetryService> _logger;
    private long _sequence;

    public TelemetryService(
        ITelemetryTransport transport,
        OutboxRepository outboxRepository,
        ILogger<TelemetryService> logger)
    {
        _transport = transport;
        _outboxRepository = outboxRepository;
        _logger = logger;
    }

    public async Task PublishHardwareAsync(
        HardwareTelemetryPayload telemetryPayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetryPayload);

        var envelope = new Envelope
        {
            Type = MessageTypes.Telemetry,
            Id = Guid.NewGuid(),
            Ts = DateTimeOffset.UtcNow,
            Seq = Interlocked.Increment(ref _sequence),
            Payload = telemetryPayload
        };

        await SendAsync(
            envelope,
            cancellationToken);
    }

    public async Task PublishDeviceAsync(
        DeviceTelemetry deviceTelemetry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceTelemetry);

        var envelope = new Envelope
        {
            Type = "device_event",
            Id = Guid.NewGuid(),
            Ts = DateTimeOffset.UtcNow,
            Seq = Interlocked.Increment(ref _sequence),
            Payload = deviceTelemetry
        };

        await SendAsync(
            envelope,
            cancellationToken);
    }

    public async Task PublishAlertAsync(
        AlertPayload alertPayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alertPayload);

        var envelope = new Envelope
        {
            Type = MessageTypes.Alert,
            Id = Guid.NewGuid(),
            Ts = DateTimeOffset.UtcNow,
            Seq = Interlocked.Increment(ref _sequence),
            Payload = alertPayload
        };

        await SendAsync(
            envelope,
            cancellationToken);
    }

    private async Task SendAsync(
        Envelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            await _transport.SendAsync(
                envelope,
                cancellationToken);

            _logger.LogInformation(
                "Telemetry sent: Type={Type}, Sequence={Sequence}, Id={Id}",
                envelope.Type,
                envelope.Seq,
                envelope.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (TelemetryMessagePolicy.RequiresOutbox(envelope.Type))
            {
                _logger.LogWarning(
                    ex,
                    "Failed to transmit {Type} envelope over transport. Buffering in outbox for retry. Id={Id}",
                    envelope.Type,
                    envelope.Id);

                try
                {
                    var serializedPayload = JsonSerializer.Serialize(
                        envelope,
                        AgentJsonContext.Default.Envelope);

                    var outboxEntity = new OutboxMessageEntity
                    {
                        Id = envelope.Id,
                        Type = envelope.Type,
                        Payload = serializedPayload,
                        CreatedAt = envelope.Ts,
                        Attempts = 0
                    };

                    await _outboxRepository.InsertAsync(
                        outboxEntity,
                        cancellationToken);

                    _logger.LogInformation(
                        "Enqueued message to outbox: Type={Type}, Id={Id}",
                        envelope.Type,
                        envelope.Id);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception outboxEx)
                {
                    _logger.LogError(
                        outboxEx,
                        "Failed to persist message to outbox: Type={Type}, Id={Id}",
                        envelope.Type,
                        envelope.Id);
                }
            }
            else
            {
                _logger.LogDebug(
                    ex,
                    "Ephemeral telemetry send failed (no outbox buffering required). Type={Type}, Id={Id}",
                    envelope.Type,
                    envelope.Id);
            }
        }
    }
}