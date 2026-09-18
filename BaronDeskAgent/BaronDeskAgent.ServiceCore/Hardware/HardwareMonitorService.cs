using BaronDesk.Shared.Models;

namespace BaronDeskAgent.ServiceCore.Hardware;

public class HardwareMonitorService : BackgroundService
{
    private readonly HardwareSensorReader _sensorReader;
    private readonly ILogger<HardwareMonitorService> _logger;

    public HardwareMonitorService(
        HardwareSensorReader sensorReader,
        ILogger<HardwareMonitorService> logger)
    {
        _sensorReader = sensorReader;
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
            while (!stoppingToken.IsCancellationRequested)
            {
                HardwareTelemetry telemetry =
                    _sensorReader.ReadTelemetry();

                _logger.LogInformation(
                    "CPU: {CpuTemp}°C | GPU: {GpuTemp}°C | CPU Load: {CpuLoad}% | GPU Load: {GpuLoad}%",
                    telemetry.CpuTemperature,
                    telemetry.GpuTemperature,
                    telemetry.CpuLoad,
                    telemetry.GpuLoad);

                await Task.Delay(
                    TimeSpan.FromSeconds(5),
                    stoppingToken);
            }
        }
        finally
        {
            _sensorReader.Stop();

            _logger.LogInformation(
                "Hardware monitoring stopped.");
        }
    }
}