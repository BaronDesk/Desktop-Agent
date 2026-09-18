using System.Management;
using BaronDeskAgent.ServiceCore.Hardware.Models;

namespace BaronDeskAgent.ServiceCore.Hardware;

public class UsbMonitorService : BackgroundService
{
    private readonly ILogger<UsbMonitorService> _logger;

    private ManagementEventWatcher? _insertWatcher;
    private ManagementEventWatcher? _removeWatcher;

    public UsbMonitorService(
        ILogger<UsbMonitorService> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        StartWatchers();

        stoppingToken.Register(StopWatchers);

        _logger.LogInformation(
            "USB monitoring started.");

        return Task.CompletedTask;
    }

    private void StartWatchers()
    {
        // We will add the Windows event watchers here.
    }

    private void StopWatchers()
    {
        _insertWatcher?.Stop();
        _removeWatcher?.Stop();

        _insertWatcher?.Dispose();
        _removeWatcher?.Dispose();

        _insertWatcher = null;
        _removeWatcher = null;

        _logger.LogInformation(
            "USB monitoring stopped.");
    }
}