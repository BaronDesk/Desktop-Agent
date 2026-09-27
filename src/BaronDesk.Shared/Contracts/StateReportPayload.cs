namespace BaronDesk.Shared.Contracts;

/// <summary>
/// <c>state_report</c> payload, sent on every (re)connect so the backend can reconcile.
/// </summary>
public sealed record StateReportPayload
{
    public required bool Locked { get; init; }

    public Guid? SessionId { get; init; }

    public string? RunningGameId { get; init; }

    /// <summary>Lease expiry on the (estimated) server clock, or null when there is no lease.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>
    /// Watched venue peripherals and whether each is connected (same list as <c>peripheral_status</c>).
    /// OPEN (skill §15): agent proposal, confirm with backend member C.
    /// </summary>
    public IReadOnlyList<PeripheralState> Peripherals { get; init; } = [];
}
