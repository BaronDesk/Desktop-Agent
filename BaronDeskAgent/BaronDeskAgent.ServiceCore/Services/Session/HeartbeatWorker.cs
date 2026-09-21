using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Communication;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Services.Commands;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Services.Session;

public sealed class HeartbeatWorker : BackgroundService
{
    private readonly IServerConnection _serverConnection;
    private readonly LockService _lockService;
    private readonly SessionService _sessionService;
    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger<HeartbeatWorker> _logger;

    public HeartbeatWorker(
        IServerConnection serverConnection,
        LockService lockService,
        SessionService sessionService,
        IOptions<AgentOptions> options,
        ILogger<HeartbeatWorker> logger)
    {
        _serverConnection = serverConnection;
        _lockService = lockService;
        _sessionService = sessionService;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HeartbeatWorker started with cadence of {Interval}s.",
            _options.Value.HeartbeatIntervalSeconds);

        var interval = TimeSpan.FromSeconds(Math.Max(1.0, _options.Value.HeartbeatIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);

                if (!_serverConnection.IsConnected)
                {
                    continue;
                }

                await SendHeartbeatAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to emit periodic heartbeat.");
            }
        }

        _logger.LogInformation("HeartbeatWorker stopped.");
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var isLocked = _lockService.IsLocked;
        var sessionId = _sessionService.CurrentSessionId;

        var heartbeatPayload = new HeartbeatPayload
        {
            Locked = isLocked,
            SessionId = sessionId
        };

        var envelope = new Envelope<HeartbeatPayload>
        {
            Type = MessageTypes.Heartbeat,
            Id = Guid.NewGuid(),
            Ts = DateTimeOffset.UtcNow,
            Seq = _serverConnection.NextSequence(),
            Payload = heartbeatPayload
        };

        await _serverConnection.SendAsync(envelope, cancellationToken);

        _logger.LogDebug(
            "Emitted presence heartbeat. Locked={Locked}, SessionId={SessionId}, Seq={Seq}",
            isLocked,
            sessionId,
            envelope.Seq);
    }
}
