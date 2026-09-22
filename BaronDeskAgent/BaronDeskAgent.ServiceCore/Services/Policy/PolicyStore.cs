using BaronDesk.Shared.Contracts;
using BaronDesk.Shared.Models;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Data.Entities;
using BaronDeskAgent.ServiceCore.Data.Repositories;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Services.Policy;

public sealed class PolicyStore : IPolicyStore
{
    private readonly PolicyRepository _repository;
    private readonly AgentOptions _options;
    private readonly ILogger<PolicyStore> _logger;
    private readonly object _lock = new();

    private StationPolicy _currentPolicy;

    public event Action<StationPolicy>? OnPolicyUpdated;

    public StationPolicy CurrentPolicy
    {
        get
        {
            lock (_lock)
            {
                return _currentPolicy;
            }
        }
    }

    public PolicyStore(
        PolicyRepository repository,
        IOptions<AgentOptions> options,
        ILogger<PolicyStore> logger)
    {
        _repository = repository;
        _options = options.Value;
        _logger = logger;

        // Create default policy snapshot from AgentOptions until initialized from SQLite
        _currentPolicy = CreateDefaultPolicyFromOptions(_options);
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = await _repository.GetActivePolicyAsync(
                PolicyEntity.DefaultPolicyId,
                cancellationToken);

            if (entity is not null)
            {
                lock (_lock)
                {
                    _currentPolicy = entity.ToModel();
                }

                _logger.LogInformation(
                    "Loaded active station policy from SQLite. Cadence={Cadence}s, Heartbeat={Heartbeat}s, Lease={Lease}s, UsbDebounce={Debounce}s, AntiTheft={AntiTheft}",
                    _currentPolicy.TelemetryCadenceSeconds,
                    _currentPolicy.HeartbeatIntervalSeconds,
                    _currentPolicy.DefaultLeaseDurationSeconds,
                    _currentPolicy.UsbDebounceWindowSeconds,
                    _currentPolicy.EnableAntiTheftAlerts);
                return;
            }

            // If no record exists yet, persist the initial default policy
            StationPolicy defaultPolicy;
            lock (_lock)
            {
                defaultPolicy = _currentPolicy;
            }

            var newEntity = PolicyEntity.FromModel(defaultPolicy);
            await _repository.UpsertPolicyAsync(newEntity, cancellationToken);

            _logger.LogInformation(
                "Persisted initial default station policy to SQLite.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load or persist policy from SQLite during startup. Using in-memory fallback defaults.");
        }
    }

    public Task<StationPolicy> GetPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CurrentPolicy);
    }

    public async Task<StationPolicy> UpdatePolicyAsync(
        PolicyUpdatePayload update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        StationPolicy candidate;
        lock (_lock)
        {
            candidate = update.ApplyTo(_currentPolicy);
        }

        var validationErrors = candidate.Validate();
        if (validationErrors.Count > 0)
        {
            var joined = string.Join("; ", validationErrors);
            _logger.LogWarning(
                "Rejected invalid POLICY_UPDATE payload: {Errors}",
                joined);
            throw new ArgumentException($"Policy validation failed: {joined}");
        }

        var entity = PolicyEntity.FromModel(candidate);
        await _repository.UpsertPolicyAsync(entity, cancellationToken);

        lock (_lock)
        {
            _currentPolicy = candidate;
        }

        _logger.LogInformation(
            "Station policy updated live and persisted to SQLite. TelemetryCadence={Cadence}s, Heartbeat={Heartbeat}s, Lease={Lease}s, UsbDebounce={Debounce}s, AntiTheft={AntiTheft}",
            candidate.TelemetryCadenceSeconds,
            candidate.HeartbeatIntervalSeconds,
            candidate.DefaultLeaseDurationSeconds,
            candidate.UsbDebounceWindowSeconds,
            candidate.EnableAntiTheftAlerts);

        // Notify dynamic consumers
        try
        {
            OnPolicyUpdated?.Invoke(candidate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while notifying policy update consumers.");
        }

        return candidate;
    }

    private static StationPolicy CreateDefaultPolicyFromOptions(AgentOptions options)
    {
        return new StationPolicy
        {
            TelemetryCadenceSeconds = 5.0,
            HeartbeatIntervalSeconds = options.HeartbeatIntervalSeconds > 0 ? options.HeartbeatIntervalSeconds : 15.0,
            DefaultLeaseDurationSeconds = options.DefaultLeaseDurationSeconds > 0 ? options.DefaultLeaseDurationSeconds : 60.0,
            LeaseGracePeriodSeconds = options.LeaseGracePeriodSeconds > 0 ? options.LeaseGracePeriodSeconds : 10.0,
            CpuTempAlertThreshold = options.CpuTempAlertThreshold > 0 ? options.CpuTempAlertThreshold : 85.0,
            GpuTempAlertThreshold = options.GpuTempAlertThreshold > 0 ? options.GpuTempAlertThreshold : 85.0,
            CpuLoadAlertThreshold = options.CpuLoadAlertThreshold > 0 ? options.CpuLoadAlertThreshold : 95.0,
            RamLoadAlertThreshold = options.RamLoadAlertThreshold > 0 ? options.RamLoadAlertThreshold : 95.0,
            HardwareAlertCooldownSeconds = options.HardwareAlertCooldownSeconds > 0 ? options.HardwareAlertCooldownSeconds : 60.0,
            UsbDebounceWindowSeconds = options.UsbDebounceWindowSeconds > 0 ? options.UsbDebounceWindowSeconds : 5.0,
            EnableAntiTheftAlerts = options.EnableAntiTheftAlerts,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
