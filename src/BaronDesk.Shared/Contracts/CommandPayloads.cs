namespace BaronDesk.Shared.Contracts;

// Payloads of the server → agent commands. Each command's payload carries { sessionId?, gameId?, reason? }
// as relevant (frozen), plus the proposed lease fields on UNLOCK.

/// <summary>
/// <c>UNLOCK</c> payload. The backend has already authorised the session; the agent only obeys.
/// </summary>
public sealed record UnlockPayload
{
    /// <summary>Required: the agent never invents a session id.</summary>
    public Guid? SessionId { get; init; }

    public double? LeaseSeconds { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }

    public DateTimeOffset? ServerTime { get; init; }
}

/// <summary>
/// <c>LOCK</c> payload. Also what a billing run-out looks like.
/// </summary>
public sealed record LockPayload
{
    public string? Reason { get; init; }
}

/// <summary>
/// <c>END_SESSION</c> payload. When <see cref="SessionId"/> is present and does not match the
/// active session, the command is stale and is acknowledged without touching the current session.
/// </summary>
public sealed record EndSessionPayload
{
    public Guid? SessionId { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// <c>LAUNCH_GAME</c> payload. Only catalog ids are accepted, never executable paths.
/// </summary>
public sealed record LaunchGamePayload
{
    public string? GameId { get; init; }
}

/// <summary>
/// <c>SHUTDOWN</c> payload.
/// </summary>
public sealed record ShutdownPayload
{
    /// <summary><c>"shutdown"</c> (default) or <c>"restart"</c>.</summary>
    public string? Action { get; init; }

    public int? DelaySeconds { get; init; }

    public string? Reason { get; init; }
}
