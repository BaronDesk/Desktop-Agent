using System.Threading.Channels;
using BaronDesk.Shared.Models;

namespace BaronDeskAgent.ServiceCore.Services;

public class TelemetryService : BackgroundService
{
    private readonly Channel<TelemetryEnvelope> _channel;

    private readonly ITelemetryTransport _transport;

    private readonly ILogger<TelemetryService> _logger;

    private long _sequence;

    public TelemetryService(
        ITelemetryTransport transport,
        ILogger<TelemetryService> logger)
    {
        _transport = transport;
        _logger = logger;

        _channel = Channel.CreateUnbounded<TelemetryEnvelope>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
    }

    public ValueTask PublishHardwareAsync(
        HardwareTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        TelemetryEnvelope envelope = CreateEnvelope(
            "telemetry",
            telemetry);

        return _channel.Writer.WriteAsync(
            envelope,
            cancellationToken);
    }

    public ValueTask PublishDeviceEventAsync(
        DeviceTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        TelemetryEnvelope envelope = CreateEnvelope(
            "device_event",
            telemetry);

        return _channel.Writer.WriteAsync(
            envelope,
            cancellationToken);
    }

    private TelemetryEnvelope CreateEnvelope(
        string type,
        object payload)
    {
        return new TelemetryEnvelope
        {
            Type = type,
            Id = Guid.NewGuid(),
            Ts = DateTime.UtcNow,
            Seq = Interlocked.Increment(ref _sequence),
            Payload = payload
        };
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Telemetry service started.");

        try
        {
            await foreach (
                TelemetryEnvelope envelope
                in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _transport.SendAsync(
                        envelope,
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to send telemetry.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _logger.LogInformation(
                "Telemetry service stopped.");
        }
    }
}