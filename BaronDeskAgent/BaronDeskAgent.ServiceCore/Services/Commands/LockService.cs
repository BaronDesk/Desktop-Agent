namespace BaronDeskAgent.ServiceCore.Services.Commands;

public sealed class LockService
{
    private readonly ILogger<LockService> _logger;

    private int _isLocked;

    public LockService(
        ILogger<LockService> logger)
    {
        _logger = logger;
        // Default to locked state on startup (fail-closed)
        _isLocked = 1;
    }

    public bool IsLocked =>
        Interlocked.CompareExchange(
            ref _isLocked,
            0,
            0) == 1;

    public Task LockAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsLocked)
        {
            _logger.LogInformation(
                "Agent is already locked.");

            return Task.CompletedTask;
        }

        Interlocked.Exchange(
            ref _isLocked,
            1);

        _logger.LogInformation(
            "Agent lock state changed to LOCKED.");

        return Task.CompletedTask;
    }

    public Task UnlockAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsLocked)
        {
            _logger.LogInformation(
                "Agent is already unlocked.");

            return Task.CompletedTask;
        }

        Interlocked.Exchange(
            ref _isLocked,
            0);

        _logger.LogInformation(
            "Agent lock state changed to UNLOCKED.");

        return Task.CompletedTask;
    }
}