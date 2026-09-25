namespace BaronDeskAgent.ServiceCore.Session;

/// <summary>
/// Lock state of the station. Starts locked (fail closed). State changes and the matching overlay
/// updates are serialized, so a concurrent lock and unlock cannot leave the overlay hidden while locked.
/// </summary>
public sealed class LockService
{
    private readonly ILockScreen _lockScreen;
    private readonly ILogger<LockService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _isLocked = true;

    public LockService(ILockScreen lockScreen, ILogger<LockService> logger)
    {
        _lockScreen = lockScreen;
        _logger = logger;
    }

    public bool IsLocked => _isLocked;

    /// <summary>
    /// Locks the station. The state flips before anything is awaited, so the station is locked even if the
    /// overlay call is cancelled. Always re-sends the overlay command, which heals a helper that restarted.
    /// </summary>
    /// <returns>Whether the helper confirmed the overlay is visible.</returns>
    public async Task<OverlayResult> LockAsync(CancellationToken cancellationToken)
    {
        _isLocked = true;

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            _isLocked = true;
            var overlay = await _lockScreen.ShowAsync(cancellationToken);
            _logger.LogInformation("Station LOCKED (overlay: {Overlay}).", overlay);
            return overlay;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns>Whether the helper confirmed the overlay is hidden.</returns>
    public async Task<OverlayResult> UnlockAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _isLocked = false;
            var overlay = await _lockScreen.HideAsync(cancellationToken);
            _logger.LogInformation("Station UNLOCKED (overlay: {Overlay}).", overlay);
            return overlay;
        }
        finally
        {
            _gate.Release();
        }
    }
}
