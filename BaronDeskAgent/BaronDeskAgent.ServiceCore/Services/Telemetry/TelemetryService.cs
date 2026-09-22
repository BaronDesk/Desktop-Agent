using BaronDesk.Shared.Contracts;
using BaronDesk.Shared.Models;

namespace BaronDeskAgent.ServiceCore.Services.Telemetry;

public sealed class TelemetryService
{
    private readonly ITelemetryTransport _transport;
    private readonly ILogger<TelemetryService> _logger;
    private long _sequence;

    public TelemetryService(
        ITelemetryTransport transport,
        ILogger<TelemetryService> logger)
    {
        _transport = transport;
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
            _logger.LogError(
                ex,
                "Failed to send telemetry. Type={Type}, Id={Id}",
                envelope.Type,
                envelope.Id);

            throw;
        }
    }
}