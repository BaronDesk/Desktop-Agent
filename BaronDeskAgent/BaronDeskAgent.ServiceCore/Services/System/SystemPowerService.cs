namespace BaronDeskAgent.ServiceCore.Services.System;

public sealed class SystemPowerService
{
    private readonly ILogger<SystemPowerService> _logger;

    public SystemPowerService(
        ILogger<SystemPowerService> logger)
    {
        _logger = logger;
    }

    public Task ShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "System shutdown requested.");

        // Actual Windows shutdown will be implemented later.

        return Task.CompletedTask;
    }

    public Task RestartAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "System restart requested.");

        // Actual Windows restart will be implemented later.

        return Task.CompletedTask;
    }
}