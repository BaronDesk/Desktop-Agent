
namespace BaronDeskAgent.ServiceCore.Connection;

/// <summary>
/// Estimate of the backend's clock, advanced using a monotonic timer.
/// </summary>
public sealed class ServerClock
{
    private readonly TimeProvider _timeProvider;
    private long _anchorUtcTicks;
    private long _anchorTimestamp;
    private int _isSynchronized;

    public ServerClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public bool IsSynchronized =>
        Volatile.Read(ref _isSynchronized) == 1;

    public TimeSpan Offset =>
        IsSynchronized
            ? UtcNow - _timeProvider.GetUtcNow()
            : TimeSpan.Zero;

    /// <summary>
    /// Current estimated server time. After synchronization, this
    /// advances using monotonic elapsed time rather than the PC clock.
    /// </summary>
    public DateTimeOffset UtcNow
    {
        get
        {
            if (!IsSynchronized)
                return _timeProvider.GetUtcNow();

            var utcTicks = Interlocked.Read(ref _anchorUtcTicks);
            var timestamp = Interlocked.Read(ref _anchorTimestamp);
            var elapsed = _timeProvider.GetElapsedTime(timestamp);

            return new DateTimeOffset(
                utcTicks + elapsed.Ticks,
                TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Records a server timestamp received on a validated frame.
    /// </summary>
    public void Synchronize(DateTimeOffset serverTime)
    {
        Interlocked.Exchange(
            ref _anchorUtcTicks,
            serverTime.UtcDateTime.Ticks);

        Interlocked.Exchange(
            ref _anchorTimestamp,
            _timeProvider.GetTimestamp());

        Volatile.Write(ref _isSynchronized, 1);
    }
}
