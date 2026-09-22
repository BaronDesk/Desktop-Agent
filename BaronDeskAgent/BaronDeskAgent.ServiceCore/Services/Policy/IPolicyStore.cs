using BaronDesk.Shared.Contracts;
using BaronDesk.Shared.Models;

namespace BaronDeskAgent.ServiceCore.Services.Policy;

/// <summary>
/// Authoritative station policy store maintaining cached active policy and SQLite persistence.
/// Fires dynamic change notifications to running services upon policy update.
/// </summary>
public interface IPolicyStore
{
    /// <summary>
    /// Gets the current active workstation policy.
    /// </summary>
    StationPolicy CurrentPolicy { get; }

    /// <summary>
    /// Event fired whenever the workstation policy is updated live.
    /// Consumers (heartbeat, telemetry, alert evaluator, lease manager, device monitor)
    /// subscribe to dynamically adapt their operation without restarting.
    /// </summary>
    event Action<StationPolicy>? OnPolicyUpdated;

    /// <summary>
    /// Initializes policy from SQLite storage during startup, or falls back to defaults.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current policy, reloading from SQLite if necessary.
    /// </summary>
    Task<StationPolicy> GetPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates, merges, persists to SQLite, and live-applies a policy update.
    /// </summary>
    Task<StationPolicy> UpdatePolicyAsync(PolicyUpdatePayload update, CancellationToken cancellationToken = default);
}
