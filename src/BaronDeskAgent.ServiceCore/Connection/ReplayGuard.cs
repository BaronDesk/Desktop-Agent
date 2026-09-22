using BaronDeskAgent.ServiceCore.Configuration;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Connection;

public readonly record struct ReplayCheck(bool IsValid, string? Reason)
{
    public static ReplayCheck Accepted { get; } = new(true, null);

    public static ReplayCheck Rejected(string reason) => new(false, reason);
}

/// <summary>
/// App-level anti-replay for every inbound frame: <c>seq</c> must strictly increase within a connection,
/// and <c>ts</c> must be close to the estimated server clock.
/// OPEN (skill §15 item 6): seq scope is per connection, reset at handshake.
/// </summary>
public sealed class ReplayGuard
{
    private readonly ServerClock _serverClock;
    private readonly TimeSpan _maxDrift;
    private readonly object _gate = new();
    private long _lastInboundSeq;

    public ReplayGuard(ServerClock serverClock, IOptions<AgentOptions> options)
    {
        _serverClock = serverClock;
        _maxDrift = TimeSpan.FromSeconds(options.Value.MaxTimestampDriftSeconds);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _lastInboundSeq = 0;
        }
    }

    /// <param name="enforceFreshness">
    /// False for frames where a stale timestamp is harmless (restrictive commands) or cannot be judged yet
    /// (the handshake ack that establishes the clock offset). The sequence check always applies.
    /// </param>
    public ReplayCheck Validate(long seq, DateTimeOffset ts, bool enforceFreshness = true)
    {
        if (enforceFreshness)
        {
            var drift = (_serverClock.UtcNow - ts).Duration();
            if (drift > _maxDrift)
            {
                return ReplayCheck.Rejected(
                    $"Timestamp is {drift.TotalSeconds:F1}s away from the server clock (max {_maxDrift.TotalSeconds}s).");
            }
        }

        lock (_gate)
        {
            if (seq <= _lastInboundSeq)
            {
                return ReplayCheck.Rejected($"Sequence {seq} is not greater than the last accepted sequence {_lastInboundSeq}.");
            }

            _lastInboundSeq = seq;
        }

        return ReplayCheck.Accepted;
    }
}
