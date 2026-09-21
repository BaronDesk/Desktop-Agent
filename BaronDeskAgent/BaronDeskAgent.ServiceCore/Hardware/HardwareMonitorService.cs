using BaronDesk.Shared.Models;
using BaronDeskAgent.ServiceCore.Services.Telemetry;

namespace BaronDeskAgent.ServiceCore.Hardware;

public sealed class HardwareMonitorService : BackgroundService
{
    private readonly HardwareSensorReader _sensorReader;
    private readonly HardwareTelemetryMapper _telemetryMapper;
    private readonly TelemetryService _telemetryService;
    private readonly ILogger<HardwareMonitorService> _logger;

    private static readonly TimeSpan SampleInterval =
        TimeSpan.FromSeconds(5);

    public HardwareMonitorService(
        HardwareSensorReader sensorReader,
        HardwareTelemetryMapper telemetryMapper,
        TelemetryService telemetryService,
        ILogger<HardwareMonitorService> logger)
    {
        _sensorReader = sensorReader;
        _telemetryMapper = telemetryMapper;
        _telemetryService = telemetryService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _sensorReader.Start();

        _logger.LogInformation(
            "Hardware monitoring started.");

        try
        {
            using var timer =
                new PeriodicTimer(SampleInterval);

            while (await timer.WaitForNextTickAsync(
                       stoppingToken))
            {
                HardwareTelemetry telemetry =
                    _sensorReader.ReadTelemetry();

                var metrics =
                    _telemetryMapper.Map(telemetry);

                var payload = new HardwareTelemetryPayload
                {
                    Timestamp = telemetry.Timestamp,
                    Metrics = metrics
                };

                await _telemetryService.PublishHardwareAsync(
                    payload,
                    stoppingToken);
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
                "Hardware monitoring stopped because of an unexpected error.");
        }
        finally
        {
            _sensorReader.Stop();

            _logger.LogInformation(
                "Hardware monitoring stopped.");
        }
    }
}