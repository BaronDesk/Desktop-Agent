
namespace BaronDeskAgent.ServiceCore.Persistence;

public sealed record PersistedSessionState(
    Guid? SessionId,
    string? LockReason,
    DateTimeOffset? LockedAt,
    DateTimeOffset? ContactLostAt = null,
    bool RestartRecovered = false);
