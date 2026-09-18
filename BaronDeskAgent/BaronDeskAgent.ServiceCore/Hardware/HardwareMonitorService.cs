using BaronDesk.Shared.Models;
using BaronDeskAgent.ServiceCore.Services;

namespace BaronDeskAgent.ServiceCore.Hardware;

public sealed class HardwareMonitorService : BackgroundService
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
            "Hardware monitoring service starting.");

        _sensorReader.Start();

        try
        {
            using var timer =
                new PeriodicTimer(SampleInterval);

            while (await timer.WaitForNextTickAsync(
                stoppingToken))
            {
                try
                {
                    HardwareTelemetry telemetry =
                        _sensorReader.ReadTelemetry();

                    await _telemetryService.PublishHardwareAsync(
                        telemetry,
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
                        "Error while collecting hardware telemetry.");
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _sensorReader.Stop();

            _logger.LogInformation(
                "Hardware monitoring service stopped.");
        }
    }
}