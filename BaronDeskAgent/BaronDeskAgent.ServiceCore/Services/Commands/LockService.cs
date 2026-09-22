namespace BaronDeskAgent.ServiceCore.Services.Commands;

public sealed class LockService
{
    private readonly ILogger<LockService> _logger;

    private int _isLocked;

    /// <summary>
    /// Event triggered when lock state changes: (isLocked, commandId, cancellationToken).
    /// </summary>
    public event Func<bool, Guid?, CancellationToken, Task>? OnLockStateChanged;

    public LockService(
        ILogger<LockService> logger)
    {
        _logger = logger;
        // Default to locked state on startup (fail-closed)
        _isLocked = 1;
    }

    public bool IsLocked =>
        Volatile.Read(ref _isLocked) == 1;

    public async Task LockAsync(
        Guid? commandId,
        CancellationToken cancellationToken = default)
    {
        if (IsLocked)
        {
            _logger.LogInformation("Agent is already locked.");
            return;
        }

        Interlocked.Exchange(ref _isLocked, 1);
        _logger.LogInformation("Agent lock state changed to LOCKED (CommandId={CommandId}).", commandId);

        if (OnLockStateChanged is not null)
        {
            await OnLockStateChanged.Invoke(true, commandId, cancellationToken);
        }
    }

    public Task LockAsync(CancellationToken cancellationToken = default)
    {
        return LockAsync(null, cancellationToken);
    }

    public async Task UnlockAsync(
        Guid? commandId,
        CancellationToken cancellationToken = default)
    {
        if (!IsLocked)
        {
            _logger.LogInformation("Agent is already unlocked.");
            return;
        }

        Interlocked.Exchange(ref _isLocked, 0);
        _logger.LogInformation("Agent lock state changed to UNLOCKED (CommandId={CommandId}).", commandId);

        if (OnLockStateChanged is not null)
        {
            await OnLockStateChanged.Invoke(false, commandId, cancellationToken);
        }
    }

    public Task UnlockAsync(CancellationToken cancellationToken = default)
    {
        return UnlockAsync(null, cancellationToken);
    }
}