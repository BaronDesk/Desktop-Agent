using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Services.Commands;
using BaronDeskAgent.ServiceCore.Services.Policy;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Services.Session;

public sealed class LeaseManager : IDisposable
{
    private readonly SessionService _sessionService;
    private readonly LockService _lockService;
    private readonly IPolicyStore _policyStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LeaseManager> _logger;
    private readonly object _lock = new();

    private long _leaseExpiryTimestamp;
    private DateTimeOffset? _leaseExpiresAt;
    private bool _hasActiveLease;
    private readonly ITimer _checkTimer;
    private bool _disposed;

    public LeaseManager(
        SessionService sessionService,
        LockService lockService,
        IPolicyStore policyStore,
        TimeProvider timeProvider,
        ILogger<LeaseManager> logger)
    {
        _sessionService = sessionService;
        _lockService = lockService;
        _policyStore = policyStore;
        _timeProvider = timeProvider;
        _logger = logger;

        _sessionService.OnSessionEnded += _ => RevokeLease();

        // Check lease validity every 5 second
        _checkTimer = _timeProvider.CreateTimer(
            OnCheckTimer,
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));
    }

    public bool IsLeaseValid
    {
        get
        {
            lock (_lock)
            {
                if (!_hasActiveLease)
                {
                    return false;
                }

                var now = _timeProvider.GetTimestamp();
                var remainingTicks = _leaseExpiryTimestamp - now;
                return remainingTicks > 0;
            }
        }
    }

    public DateTimeOffset? LeaseExpiresAt
    {
        get
        {
            lock (_lock)
            {
                return _hasActiveLease ? _leaseExpiresAt : null;
            }
        }
    }

    public TimeSpan? RemainingLease
    {
        get
        {
            lock (_lock)
            {
                if (!_hasActiveLease)
                {
                    return null;
                }

                var now = _timeProvider.GetTimestamp();
                var remainingTicks = _leaseExpiryTimestamp - now;
                var seconds = (double)remainingTicks / _timeProvider.TimestampFrequency;
                return TimeSpan.FromSeconds(Math.Max(0, seconds));
            }
        }
    }

    /// <summary>
    /// Grants or extends authorization lease for the specified duration using monotonic clock.
    /// </summary>
    public void GrantLease(TimeSpan duration)
    {
        lock (_lock)
        {
            var ticks = (long)(duration.TotalSeconds * _timeProvider.TimestampFrequency);
            _leaseExpiryTimestamp = _timeProvider.GetTimestamp() + ticks;
            _leaseExpiresAt = _timeProvider.GetUtcNow().Add(duration);
            _hasActiveLease = true;
        }

        _logger.LogInformation(
            "Authorization lease granted for {Duration:F1}s (expires at {ExpiresAt:u}).",
            duration.TotalSeconds,
            _leaseExpiresAt);
    }

    /// <summary>
    /// Updates lease from server heartbeat acknowledgement.
    /// </summary>
    public void UpdateLease(DateTimeOffset? serverLeaseExpiresAt)
    {
        if (serverLeaseExpiresAt.HasValue)
        {
            var diff = serverLeaseExpiresAt.Value - _timeProvider.GetUtcNow();
            if (diff > TimeSpan.Zero)
            {
                GrantLease(diff);
                return;
            }
        }

        // Fallback to default lease duration if server did not specify
        GrantLease(TimeSpan.FromSeconds(_policyStore.CurrentPolicy.DefaultLeaseDurationSeconds));
    }

    /// <summary>
    /// Revokes active lease upon session end or station lock.
    /// </summary>
    public void RevokeLease()
    {
        lock (_lock)
        {
            if (!_hasActiveLease)
            {
                return;
            }

            _hasActiveLease = false;
            _leaseExpiresAt = null;
        }

        _logger.LogDebug("Authorization lease revoked.");
    }

    private void OnCheckTimer(object? state)
    {
        bool shouldFailClosed = false;
        double elapsedPastExpiry = 0;

        lock (_lock)
        {
            if (!_hasActiveLease || !_sessionService.IsSessionActive)
            {
                return;
            }

            var now = _timeProvider.GetTimestamp();
            var diffTicks = now - _leaseExpiryTimestamp;

            if (diffTicks > 0)
            {
                elapsedPastExpiry = (double)diffTicks / _timeProvider.TimestampFrequency;
                if (elapsedPastExpiry > _policyStore.CurrentPolicy.LeaseGracePeriodSeconds)
                {
                    shouldFailClosed = true;
                    _hasActiveLease = false;
                    _leaseExpiresAt = null;
                }
            }
        }

        if (shouldFailClosed)
        {
            _logger.LogWarning(
                "Lease expired {Elapsed:F1}s ago (exceeded grace period of {Grace}s). Enforcing FAIL-CLOSED lockout.",
                elapsedPastExpiry,
                _policyStore.CurrentPolicy.LeaseGracePeriodSeconds);

            _ = EnforceFailClosedAsync();
        }
    }

    private async Task EnforceFailClosedAsync()
    {
        try
        {
            await _sessionService.EndSessionAsync("lease_expired");
            await _lockService.LockAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing fail-closed station lockout.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _checkTimer.Dispose();
    }
}
