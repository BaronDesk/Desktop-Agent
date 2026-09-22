namespace BaronDesk.Shared.Ipc;

public enum PipeMessageKind
{
    /// <summary>Service → helper: show the overlay. Answered with <see cref="LockShown"/>.</summary>
    ShowLock,

    /// <summary>Service → helper: hide the overlay. Answered with <see cref="LockHidden"/>.</summary>
    HideLock,

    LockShown,
    LockHidden,

    /// <summary>Helper → service keep-alive.</summary>
    HelperAlive,

    /// <summary>Helper → service: a credential typed by the gamer, relayed to the backend.</summary>
    SubmitCredential,

    /// <summary>Service → helper: the outcome of a <see cref="SubmitCredential"/>.</summary>
    LoginResult,

    /// <summary>Service → helper: whether the backend is reachable (drives "service unavailable").</summary>
    ServerStatus
}

public enum LoginOutcome
{
    /// <summary>The backend accepted the credential; <c>UNLOCK</c> follows.</summary>
    Accepted,
    Rejected,

    /// <summary>No backend connection: no new sessions offline.</summary>
    Unavailable,
    Timeout,
    RateLimited,

    /// <summary>A previous login is still waiting for the backend.</summary>
    Busy
}

/// <summary>
/// One newline-delimited JSON message on the service ↔ LockUI named pipe.
/// </summary>
public sealed record PipeMessage
{
    public required PipeMessageKind Kind { get; init; }

    /// <summary>Pairs <see cref="PipeMessageKind.ShowLock"/>/<see cref="PipeMessageKind.HideLock"/> with their confirmation.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary><see cref="PipeMessageKind.SubmitCredential"/> only. Never logged.</summary>
    public string? Credential { get; init; }

    public LoginOutcome? LoginOutcome { get; init; }

    /// <summary>Human-readable detail (refusal reason, lockout seconds).</summary>
    public string? Detail { get; init; }

    public bool? ServerOnline { get; init; }

    public override string ToString() =>
        $"PipeMessage {{ Kind = {Kind}, CorrelationId = {CorrelationId}, LoginOutcome = {LoginOutcome}, ServerOnline = {ServerOnline} }}";
}
