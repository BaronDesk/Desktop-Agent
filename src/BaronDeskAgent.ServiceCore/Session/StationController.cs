using BaronDeskAgent.ServiceCore.Games;
using BaronDeskAgent.ServiceCore.Policy;

namespace BaronDeskAgent.ServiceCore.Session;

public sealed record StationSnapshot(bool Locked, Guid? SessionId, string? RunningGameId, DateTimeOffset? LeaseExpiresAt);

public enum EndSessionResult
{
    Ended,

    /// <summary>The command named a different session than the active one (late redelivery); nothing changed.</summary>
    StaleSessionIgnored,

    /// <summary>The session ended and the station is locked, but the helper did not confirm the overlay.</summary>
    LockNotConfirmed
}

/// <summary>
/// Single owner of the session, lease, lock and game transitions. Every transition runs under one gate,
/// so commands, lease expiry and shutdown cannot interleave.
/// </summary>
/// <remarks>
/// Enforced invariant: <b>unlocked ⇒ valid lease</b>. A timer checks it every few seconds and fails closed
/// otherwise, which covers lease expiry while offline and any path that unlocks without a lease.
/// </remarks>
public sealed class StationController : IDisposable
{
    private static readonly TimeSpan LeaseCheckInterval = TimeSpan.FromSeconds(5);

    private readonly LockService _lockService;
    private readonly SessionService _sessionService;
    private readonly LeaseManager _leaseManager;
    private readonly GameService _gameService;
    private readonly IPolicyStore _policyStore;
    private readonly ILogger<StationController> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ITimer _leaseCheckTimer;
    private int _failClosedRunning;

    public StationController(
        LockService lockService,
        SessionService sessionService,
        LeaseManager leaseManager,
        GameService gameService,
        IPolicyStore policyStore,
        TimeProvider timeProvider,
        ILogger<StationController> logger)
    {
        _lockService = lockService;
        _sessionService = sessionService;
        _leaseManager = leaseManager;
        _gameService = gameService;
        _policyStore = policyStore;
        _logger = logger;

        _leaseCheckTimer = timeProvider.CreateTimer(_ => CheckLease(), null, LeaseCheckInterval, LeaseCheckInterval);
    }

    public StationSnapshot GetSnapshot() => new(
        _lockService.IsLocked,
        _sessionService.CurrentSessionId,
        _gameService.CurrentGameId,
        _leaseManager.ExpiresAt);

    /// <summary>UNLOCK: binds the session, grants the lease, then hides the overlay.</summary>
    public async Task StartSessionAsync(Guid sessionId, TimeSpan? leaseDuration, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessionService.CurrentSessionId is { } previous && previous != sessionId)
            {
                _logger.LogWarning("UNLOCK for session {SessionId} replaces active session {PreviousId}.", sessionId, previous);
                await StopGameSafelyAsync();
                _sessionService.End("replaced_by_new_session");
            }

            _sessionService.Start(sessionId);

            // Granted before unlocking, so the station is never unlocked without a lease.
            _leaseManager.Grant(leaseDuration);
            await _lockService.UnlockAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>END_SESSION: locks, stops the game the agent launched, forgets the session and revokes the lease.</summary>
    public async Task<EndSessionResult> EndSessionAsync(Guid? expectedSessionId, string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (expectedSessionId is { } expected &&
                _sessionService.CurrentSessionId is { } active &&
                expected != active)
            {
                _logger.LogWarning(
                    "Ignoring END_SESSION for session {Expected}: the active session is {Active}.",
                    expected,
                    active);
                return EndSessionResult.StaleSessionIgnored;
            }

            return await EndAndLockCoreAsync(reason)
                ? EndSessionResult.Ended
                : EndSessionResult.LockNotConfirmed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// LOCK (e.g. billing run-out): locks and revokes the lease. The session stays bound so the backend can
    /// UNLOCK again after a top-up. Returns true when the overlay was confirmed.
    /// </summary>
    public async Task<bool> LockAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _leaseManager.Revoke();
            var shown = await _lockService.LockAsync(CancellationToken.None);

            if (_policyStore.CurrentPolicy.StopGameOnLock)
            {
                await StopGameSafelyAsync();
            }

            return shown;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Renews the lease from a heartbeat ack. Ignored when no session is bound.</summary>
    public void RenewLease(TimeSpan? duration)
    {
        if (!_sessionService.IsSessionActive)
        {
            return;
        }

        if (duration is { } value && value <= TimeSpan.Zero)
        {
            _logger.LogWarning("The server reports the lease already expired; not renewing it.");
            return;
        }

        _leaseManager.Grant(duration);
    }

    /// <summary>Ends the session and locks before a power action, so a failed shutdown leaves the station locked.</summary>
    public async Task PrepareForShutdownAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await EndAndLockCoreAsync("shutdown");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _leaseCheckTimer.Dispose();

    private void CheckLease()
    {
        if (!_lockService.IsLocked && !IsLeaseUsable())
        {
            _ = FailClosedAsync();
        }
    }

    private async Task FailClosedAsync()
    {
        if (Interlocked.Exchange(ref _failClosedRunning, 1) == 1)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                // Re-check under the gate: an UNLOCK may have granted a fresh lease in the meantime.
                if (_lockService.IsLocked || IsLeaseUsable())
                {
                    return;
                }

                _logger.LogWarning(
                    "Station is unlocked without a valid lease ({Status}). Failing closed.",
                    _leaseManager.GetStatus());

                await EndAndLockCoreAsync("lease_expired");
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fail-closed lockout failed.");
        }
        finally
        {
            Volatile.Write(ref _failClosedRunning, 0);
        }
    }

    private bool IsLeaseUsable() => _leaseManager.GetStatus() is LeaseStatus.Valid or LeaseStatus.InGrace;

    /// <summary>Caller holds the gate. Locks first so the screen is covered immediately, then cleans up.</summary>
    private async Task<bool> EndAndLockCoreAsync(string reason)
    {
        _leaseManager.Revoke();
        var shown = await _lockService.LockAsync(CancellationToken.None);
        await StopGameSafelyAsync();
        _sessionService.End(reason);
        return shown;
    }

    private async Task StopGameSafelyAsync()
    {
        try
        {
            await _gameService.StopCurrentGameAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not stop the running game.");
        }
    }
}
