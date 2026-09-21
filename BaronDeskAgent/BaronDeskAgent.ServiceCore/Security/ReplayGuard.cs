using BaronDeskAgent.ServiceCore.Configuration;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Security;

public readonly record struct ReplayValidationResult(bool IsValid, string? Code, string? Reason)
{
    public static ReplayValidationResult Success() => new(true, null, null);
    public static ReplayValidationResult Failed(string code, string reason) => new(false, code, reason);
}

public sealed class ReplayGuard
{
    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger<ReplayGuard> _logger;
    private long _lastInboundSeq;

    public ReplayGuard(
        IOptions<AgentOptions> options,
        ILogger<ReplayGuard> logger)
    {
        _options = options;
        _logger = logger;
        _lastInboundSeq = 0;
    }

    /// <summary>
    /// Resets sequence tracker for a new connection session.
    /// Per wire protocol spec: seq scope is per-connection, reset at handshake.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _lastInboundSeq, 0);
        _logger.LogDebug("Replay guard sequence tracker reset to 0.");
    }

    /// <summary>
    /// Validates inbound message against anti-replay rules:
    /// 1. Monotonic sequence: seq must be strictly greater than last accepted seq.
    /// 2. Timestamp freshness: ts must be within acceptable drift window.
    /// </summary>
    public ReplayValidationResult Validate(long seq, DateTimeOffset ts)
    {
        var maxDrift = TimeSpan.FromSeconds(_options.Value.MaxTimestampDriftSeconds);
        var now = DateTimeOffset.UtcNow;
        var drift = (now - ts).Duration();

        if (drift > maxDrift)
        {
            _logger.LogWarning(
                "Stale message rejected. Ts={Timestamp}, Now={Now}, Drift={Drift}s, MaxAllowed={Max}s",
                ts,
                now,
                drift.TotalSeconds,
                maxDrift.TotalSeconds);

            return ReplayValidationResult.Failed("STALE", $"Timestamp drift of {drift.TotalSeconds:F1}s exceeds max allowed {maxDrift.TotalSeconds}s.");
        }

        // Check monotonic sequence
        long currentLast;
        long newSeq;
        do
        {
            currentLast = Interlocked.Read(ref _lastInboundSeq);
            if (seq <= currentLast)
            {
                _logger.LogWarning(
                    "Replay or out-of-order sequence rejected. InboundSeq={InboundSeq}, LastSeq={LastSeq}",
                    seq,
                    currentLast);

                return ReplayValidationResult.Failed("STALE", $"Sequence {seq} is less than or equal to last accepted sequence {currentLast}.");
            }
            newSeq = seq;
        } while (Interlocked.CompareExchange(ref _lastInboundSeq, newSeq, currentLast) != currentLast);

        return ReplayValidationResult.Success();
    }
}
