using System.Threading.Channels;
using BaronDesk.Shared.Models;

namespace BaronDeskAgent.ServiceCore.Services;

public sealed class TelemetryService : BackgroundService
{
    private readonly ITelemetryTransport _transport;
    private readonly ILogger<TelemetryService> _logger;

    private readonly Channel<TelemetryEnvelope> _channel;

    private long _sequence;

    public TelemetryService(
        ITelemetryTransport transport,
        ILogger<TelemetryService> logger)
    {
        _transport = transport;
        _logger = logger;

        _channel = Channel.CreateBounded<TelemetryEnvelope>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,

                // Important for a lightweight agent.
                // If the consumer temporarily falls behind,
                // don't allow unlimited memory growth.
                FullMode = BoundedChannelFullMode.DropOldest
            });
    }

    public ValueTask PublishHardwareAsync(
        HardwareTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        return PublishAsync(
            "telemetry",
            telemetry,
            cancellationToken);
    }

    public ValueTask PublishDeviceAsync(
        DeviceTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        return PublishAsync(
            "device_event",
            telemetry,
            cancellationToken);
    }

    private ValueTask PublishAsync<T>(
        string type,
        T payload,
        CancellationToken cancellationToken)
    {
        var envelope = new TelemetryEnvelope
        {
            Type = type,
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Sequence = Interlocked.Increment(ref _sequence),
            Payload = payload!
        };

        return _channel.Writer.WriteAsync(
            envelope,
            cancellationToken);
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Telemetry service started.");

        try
        {
            await foreach (
                var envelope in _channel.Reader.ReadAllAsync(
                    stoppingToken))
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
                        "Failed to send telemetry. " +
                        "Type={Type}, Sequence={Sequence}, Id={Id}",
                        envelope.Type,
                        envelope.Sequence,
                        envelope.Id);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        _logger.LogInformation(
            "Telemetry service stopped.");
    }
}