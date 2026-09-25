using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Policy;

/// <summary>
/// The active <see cref="StationPolicy"/>: cached in memory, persisted in SQLite, updated live.
/// </summary>
public interface IPolicyStore
{
    StationPolicy CurrentPolicy { get; }

    /// <summary>Raised after an update is persisted. Each subscriber is isolated from the others' failures.</summary>
    event Action<StationPolicy>? PolicyUpdated;

    /// <summary>Loads the persisted policy (or persists the defaults). Call once at startup, before workers start.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Merges, validates, persists and publishes an update.</summary>
    /// <exception cref="PolicyValidationException">The merged policy is out of bounds; nothing is changed.</exception>
    Task<StationPolicy> UpdatePolicyAsync(PolicyUpdatePayload update, CancellationToken cancellationToken = default);
}

public sealed class PolicyValidationException : Exception
{
    public PolicyValidationException(IReadOnlyList<string> errors)
        : base($"Policy validation failed: {string.Join("; ", errors)}")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
