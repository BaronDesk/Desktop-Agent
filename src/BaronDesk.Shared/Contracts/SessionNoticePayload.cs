namespace BaronDesk.Shared.Contracts;

/// <summary><c>session_notice.kind</c> values.</summary>
public static class SessionNoticeKinds
{
    /// <summary>The balance runs out soon; the backend locks the station at <c>endsAt</c>.</summary>
    public const string LowBalance = "LOW_BALANCE";

    /// <summary>The booked time (or pass) ends soon.</summary>
    public const string TimeLeft = "TIME_LEFT";

    /// <summary>Hide the notice (e.g. after a top-up).</summary>
    public const string Clear = "CLEAR";
}

/// <summary>
/// <c>session_notice</c>: server → agent control frame asking the station to show the gamer a small warning
/// during play. Informational only: the backend still locks with <c>LOCK</c> / <c>END_SESSION</c>.
/// OPEN (skill §15): not in the frozen list, agent proposal, confirm with backend member C.
/// </summary>
public sealed record SessionNoticePayload
{
    /// <summary>A notice for any other session than the active one is stale and ignored.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>One of <see cref="SessionNoticeKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>When the balance or booking runs out, on the server clock. Drives the countdown.</summary>
    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>Optional text to show instead of the default one (trimmed to 200 characters).</summary>
    public string? Message { get; init; }
}
