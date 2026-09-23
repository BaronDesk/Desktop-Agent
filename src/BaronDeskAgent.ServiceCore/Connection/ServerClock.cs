namespace BaronDeskAgent.ServiceCore.Connection;

/// <summary>
/// Estimate of the backend's clock, which is authoritative.
/// </summary>
/// <remarks>
/// Venue PCs run without internet, so Windows cannot sync time over NTP and station clocks drift.
/// Freshness checks and outbound timestamps use this estimate instead of the local wall clock;
/// otherwise a few minutes of drift would make the agent reject every command as stale.
/// </remarks>
public sealed class ServerClock
{
    private readonly TimeProvider _timeProvider;
    private long _offsetTicks;
    private int _isSynchronized;

    public ServerClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public bool IsSynchronized => Volatile.Read(ref _isSynchronized) == 1;

    /// <summary>Server clock minus local clock.</summary>
    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    /// <summary>Current server time (the local clock until the first synchronization).</summary>
    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow() + Offset;

    /// <summary>Records a server timestamp that was just received on a validated frame.</summary>
    public void Synchronize(DateTimeOffset serverTime)
    {
        Interlocked.Exchange(ref _offsetTicks, (serverTime - _timeProvider.GetUtcNow()).Ticks);
        Volatile.Write(ref _isSynchronized, 1);
    }
}
