using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Policy;

public sealed class PolicyStore : IPolicyStore
{
    private readonly PolicyRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PolicyStore> _logger;

    // Serializes read-modify-write so two updates cannot overwrite each other.
    private readonly SemaphoreSlim _updateGate = new(1, 1);

    private volatile StationPolicy _currentPolicy = new();

    public PolicyStore(PolicyRepository repository, TimeProvider timeProvider, ILogger<PolicyStore> logger)
    {
        _repository = repository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public StationPolicy CurrentPolicy => _currentPolicy;

    public event Action<StationPolicy>? PolicyUpdated;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await LoadOrSeedAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The agent must keep enforcing the lock even if SQLite is unusable.
            _logger.LogError(ex, "Could not load the station policy from SQLite; running on in-memory defaults.");
        }
    }

    private async Task LoadOrSeedAsync(CancellationToken cancellationToken)
    {
        var stored = await _repository.GetAsync(cancellationToken);

        if (stored is not null && stored.Validate() is { Count: 0 })
        {
            _currentPolicy = stored;
            _logger.LogInformation("Loaded station policy from SQLite: {Policy}", stored);
            return;
        }

        if (stored is not null)
        {
            _logger.LogError(
                "Stored station policy is out of bounds ({Errors}); falling back to defaults.",
                string.Join("; ", stored.Validate()));
        }

        var defaults = new StationPolicy { UpdatedAt = _timeProvider.GetUtcNow() };
        await _repository.UpsertAsync(defaults, cancellationToken);
        _currentPolicy = defaults;
        _logger.LogInformation("Persisted default station policy.");
    }

    public async Task<StationPolicy> UpdatePolicyAsync(PolicyUpdatePayload update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        StationPolicy candidate;
        await _updateGate.WaitAsync(cancellationToken);
        try
        {
            candidate = _currentPolicy.Apply(update, _timeProvider.GetUtcNow());

            var errors = candidate.Validate();
            if (errors.Count > 0)
            {
                throw new PolicyValidationException(errors);
            }

            await _repository.UpsertAsync(candidate, cancellationToken);
            _currentPolicy = candidate;
        }
        finally
        {
            _updateGate.Release();
        }

        _logger.LogInformation("Station policy updated: {Policy}", candidate);
        NotifySubscribers(candidate);
        return candidate;
    }

    private void NotifySubscribers(StationPolicy policy)
    {
        if (PolicyUpdated is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<StationPolicy>>())
        {
            try
            {
                handler(policy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A policy subscriber failed to apply the update.");
            }
        }
    }
}
