using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Policy;

namespace BaronDeskAgent.ServiceCore.Session;

public enum LeaseStatus
{
    /// <summary>No lease has been granted (or it was revoked).</summary>
    None,
    Valid,

    /// <summary>Expired, but still inside the policy grace period.</summary>
    InGrace,

    /// <summary>Expired beyond the grace period: the station must fail closed.</summary>
    Expired
}

/// <summary>
/// The server-granted authorization to stay unlocked. Measured on the monotonic clock
/// (<see cref="TimeProvider.GetTimestamp"/>), so changing the PC's clock cannot extend it.
/// </summary>
public sealed class LeaseManager
{
    private readonly IPolicyStore _policyStore;
    private readonly ServerClock _serverClock;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LeaseManager> _logger;
    private readonly object _gate = new();

    private long? _expiryTimestamp;

    public LeaseManager(IPolicyStore policyStore, ServerClock serverClock, TimeProvider timeProvider, ILogger<LeaseManager> logger)
    {
        _policyStore = policyStore;
        _serverClock = serverClock;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Lease length from server-supplied terms, computed from server-clock values only.
    /// Returns null when the server gave no terms, and a non-positive span when the lease has already expired.
    /// </summary>
    public static TimeSpan? ResolveDuration(double? leaseSeconds, DateTimeOffset? leaseExpiresAt, DateTimeOffset serverNow)
    {
        if (leaseSeconds is { } seconds)
        {
            return double.IsFinite(seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
        }

        return leaseExpiresAt is { } expiresAt ? expiresAt - serverNow : null;
    }

    /// <summary>
    /// Grants or renews the lease. <paramref name="requested"/> null means the policy default; any value is
    /// capped at the policy lease duration so a bogus or malicious value cannot hand out hours of free play.
    /// </summary>
    public void Grant(TimeSpan? requested)
    {
        // OPEN (skill §15 item 10): the cap is the policy lease duration, which the backend also controls.
        var maximum = TimeSpan.FromSeconds(_policyStore.CurrentPolicy.DefaultLeaseDurationSeconds);
        var duration = requested is { } value && value < maximum ? value : maximum;
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requested), "A lease must be positive; revoke it instead.");
        }

        lock (_gate)
        {
            _expiryTimestamp = _timeProvider.GetTimestamp() + ToTimestampTicks(duration);
        }

        _logger.LogDebug("Lease granted for {Seconds:F1}s.", duration.TotalSeconds);
    }

    public void Revoke()
    {
        lock (_gate)
        {
            _expiryTimestamp = null;
        }
    }

    public LeaseStatus GetStatus()
    {
        lock (_gate)
        {
            if (_expiryTimestamp is not { } expiry)
            {
                return LeaseStatus.None;
            }

            var now = _timeProvider.GetTimestamp();
            if (now < expiry)
            {
                return LeaseStatus.Valid;
            }

            var pastExpiry = _timeProvider.GetElapsedTime(expiry, now);
            return pastExpiry <= TimeSpan.FromSeconds(_policyStore.CurrentPolicy.LeaseGracePeriodSeconds)
                ? LeaseStatus.InGrace
                : LeaseStatus.Expired;
        }
    }

    /// <summary>Lease expiry on the estimated server clock (for <c>state_report</c>), or null without a lease.</summary>
    public DateTimeOffset? ExpiresAt
    {
        get
        {
            lock (_gate)
            {
                if (_expiryTimestamp is not { } expiry)
                {
                    return null;
                }

                var remaining = _timeProvider.GetElapsedTime(_timeProvider.GetTimestamp(), expiry);
                return _serverClock.UtcNow + remaining;
            }
        }
    }

    private long ToTimestampTicks(TimeSpan duration) =>
        (long)(duration.TotalSeconds * _timeProvider.TimestampFrequency);
}
