using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Connection;
using BaronDeskAgent.ServiceCore.Policy;

namespace BaronDeskAgent.ServiceCore.Session;

/// <summary>
/// Sends <c>heartbeat</c> frames. The backend marks the node offline on missed heartbeats and renews the
/// lease in its <c>heartbeat_ack</c>.
/// </summary>
public sealed class HeartbeatWorker : BackgroundService
{
    private readonly IServerConnection _connection;
    private readonly StationController _station;
    private readonly IPolicyStore _policyStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HeartbeatWorker> _logger;

    public HeartbeatWorker(
        IServerConnection connection,
        StationController station,
        IPolicyStore policyStore,
        TimeProvider timeProvider,
        ILogger<HeartbeatWorker> logger)
    {
        _connection = connection;
        _station = station;
        _policyStore = policyStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(IntervalOf(_policyStore.CurrentPolicy), _timeProvider);

        // Applied immediately: waiting out the old interval could outlast a shorter new lease.
        void OnPolicyUpdated(StationPolicy policy) => timer.Period = IntervalOf(policy);

        // Renew the lease right after a reconnect instead of up to one interval later: after a long blip the
        // lease may be about to run out.
        void OnReadyChanged(bool ready)
        {
            if (ready)
            {
                _ = SendHeartbeatAsync(stoppingToken);
            }
        }

        _policyStore.PolicyUpdated += OnPolicyUpdated;
        _connection.ReadyChanged += OnReadyChanged;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (_connection.IsReady)
                {
                    await SendHeartbeatAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _connection.ReadyChanged -= OnReadyChanged;
            _policyStore.PolicyUpdated -= OnPolicyUpdated;
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = _station.GetSnapshot();
            await _connection.SendAsync(
                MessageTypes.Heartbeat,
                new HeartbeatPayload { Locked = snapshot.Locked, SessionId = snapshot.SessionId },
                AgentJsonContext.Default.HeartbeatPayload,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send a heartbeat.");
        }
    }

    private static TimeSpan IntervalOf(StationPolicy policy) => TimeSpan.FromSeconds(policy.HeartbeatIntervalSeconds);
}
