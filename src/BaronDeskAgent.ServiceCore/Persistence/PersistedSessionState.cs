
namespace BaronDeskAgent.ServiceCore.Persistence;

public sealed record PersistedSessionState(
    Guid? SessionId,
    string? LockReason,
    DateTimeOffset? LockedAt);
