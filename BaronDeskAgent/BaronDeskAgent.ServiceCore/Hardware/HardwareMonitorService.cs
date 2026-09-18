using BaronDeskAgent.ServiceCore.Services;
using BaronDesk.Shared.Models;

namespace BaronDeskAgent.ServiceCore.Hardware;

public class HardwareMonitorService : BackgroundService
{
    private readonly HardwareSensorReader _sensorReader;
    private readonly TelemetryService _telemetryService;
    private readonly ILogger<HardwareMonitorService> _logger;

    private static readonly TimeSpan SampleInterval =
        TimeSpan.FromSeconds(5);

    public HardwareMonitorService(
        HardwareSensorReader sensorReader,
        TelemetryService telemetryService,
        ILogger<HardwareMonitorService> logger)
    {
        _sensorReader = sensorReader;
        _telemetryService = telemetryService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Starting hardware monitoring...");

        try
        {
            _sensorReader.Start();

            _logger.LogInformation(
                "Hardware monitoring started successfully.");

            await PublishTelemetryAsync(
                stoppingToken);

            using var timer =
                new PeriodicTimer(SampleInterval);

            while (await timer.WaitForNextTickAsync(
                       stoppingToken))
            {
                await PublishTelemetryAsync(
                    stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Hardware monitoring failed.");
        }
        finally
        {
            _sensorReader.Stop();

            _logger.LogInformation(
                "Hardware monitoring stopped.");
        }
    }

    private async Task PublishTelemetryAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            HardwareTelemetry telemetry =
                _sensorReader.ReadTelemetry();

            await _telemetryService.PublishHardwareAsync(
                telemetry,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to collect hardware telemetry.");
        }
    }
}