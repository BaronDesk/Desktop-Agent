using System.Threading.Channels;
using BaronDeskAgent.ServiceCore.Connection;

namespace BaronDeskAgent.ServiceCore.Games;

/// <summary>
/// Runs catalog syncs off the receive loop, so a slow catalog download never delays LOCK or heartbeat acks.
/// A sync is requested on every (re)connect and by <c>CATALOG_UPDATE</c>; requests that arrive while one is pending
/// are coalesced. A failed sync is retried with backoff until it succeeds.
/// </summary>
public sealed class GameCatalogSyncWorker : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)
    ];

    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly GameCatalogService _catalog;
    private readonly IServerConnection _connection;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GameCatalogSyncWorker> _logger;

    public GameCatalogSyncWorker(
        GameCatalogService catalog,
        IServerConnection connection,
        TimeProvider timeProvider,
        ILogger<GameCatalogSyncWorker> logger)
    {
        _catalog = catalog;
        _connection = connection;
        _timeProvider = timeProvider;
        _logger = logger;

        _connection.ReadyChanged += OnReadyChanged;
    }

    /// <summary>Queues a sync; never blocks.</summary>
    public void RequestSync() => _requests.Writer.TryWrite(true);

    public override void Dispose()
    {
        _connection.ReadyChanged -= OnReadyChanged;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var _ in _requests.Reader.ReadAllAsync(stoppingToken))
            {
                await SyncWithRetryAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SyncWithRetryAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _connection.WaitUntilReadyAsync(stoppingToken);
            try
            {
                await _catalog.SyncAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException ex)
            {
                // The backend sent something unusable; asking again returns the same thing. The next request retries.
                _logger.LogError("Rejected the game catalog: {Reason}", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
                _logger.LogWarning(ex, "Game catalog sync failed; retrying in {Delay}.", delay);
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
        }
    }

    private void OnReadyChanged(bool ready)
    {
        if (ready)
        {
            RequestSync();
        }
    }
}
