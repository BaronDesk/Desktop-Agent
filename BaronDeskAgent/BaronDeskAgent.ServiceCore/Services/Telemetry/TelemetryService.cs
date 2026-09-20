using BaronDesk.Shared.Models;

namespace BaronDeskAgent.ServiceCore.Services.Telemetry;

public sealed class TelemetryService
{
    private readonly ITelemetryTransport _transport;
    private readonly ILogger<TelemetryService> _logger;

    public TelemetryService(
        ITelemetryTransport transport,
        ILogger<TelemetryService> logger)
    {
        _transport = transport;
        _logger = logger;
    }

    public async Task PublishHardwareAsync(
        HardwareTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        var envelope = new TelemetryEnvelope
        {
            Type = "telemetry",
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Sequence = 0,
            Payload = telemetry
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

        var envelope = new TelemetryEnvelope
        {
            Type = "device_event",
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Sequence = 0,
            Payload = deviceTelemetry
        };

        await SendAsync(
            envelope,
            cancellationToken);
    }

    private async Task SendAsync(
        TelemetryEnvelope envelope,
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
                envelope.Sequence,
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