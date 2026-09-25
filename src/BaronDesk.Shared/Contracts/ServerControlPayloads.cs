namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>handshake_ack</c> payload. OPEN (skill §15 item 3), confirm with backend member C.
/// </summary>
public sealed record HandshakeAckPayload
{
    /// <summary>Server wall-clock time; the agent derives its clock offset from it. Falls back to the envelope <c>ts</c>.</summary>
    public DateTimeOffset? ServerTime { get; init; }
}

/// <summary>
/// <c>heartbeat_ack</c> payload: the lease renewal. OPEN (skill §15 item 3), confirm with backend member C.
/// </summary>
/// <remarks>
/// The lease length is computed from server-clock values only (<see cref="LeaseSeconds"/>, or
/// <see cref="LeaseExpiresAt"/> − <see cref="ServerTime"/>), so a drifted station clock cannot stretch it.
/// </remarks>
public sealed record HeartbeatAckPayload
{
    public double? LeaseSeconds { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }

    public DateTimeOffset? ServerTime { get; init; }
}

/// <summary>
/// <c>login_result</c> payload: the backend's verdict on a <c>login_request</c>.
/// OPEN (skill §15 item 2), confirm with backend member C.
/// </summary>
/// <remarks>
/// An accepted login is informational only; the station unlocks when the backend sends <c>UNLOCK</c>.
/// </remarks>
public sealed record LoginResultPayload
{
    /// <summary>The <c>id</c> of the <c>login_request</c> envelope this answers.</summary>
    public required Guid RequestId { get; init; }

    public required bool Accepted { get; init; }

    /// <summary>Human-readable refusal reason shown on the lock screen.</summary>
    public string? Reason { get; init; }
}
