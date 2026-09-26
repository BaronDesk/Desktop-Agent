using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Commands;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Enrollment;
using BaronDeskAgent.ServiceCore.Session;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Connection;

/// <summary>
/// Owns the connection lifecycle: enroll (first run only) → connect → handshake → state report → receive loop → back off → reconnect.
/// </summary>
public sealed class ConnectionWorker : BackgroundService
{
    /// <summary>A connection that lasted this long resets the reconnect backoff.</summary>
    private static readonly TimeSpan StableConnectionThreshold = TimeSpan.FromSeconds(30);

    internal static readonly string AgentVersion =
        typeof(ConnectionWorker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    private readonly IServerConnection _connection;
    private readonly CommandDispatcher _dispatcher;
    private readonly ReplayGuard _replayGuard;
    private readonly ServerClock _serverClock;
    private readonly StationController _station;
    private readonly LoginRelay _loginRelay;
    private readonly EnrollmentService _enrollment;
    private readonly AgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConnectionWorker> _logger;

    public ConnectionWorker(
        IServerConnection connection,
        CommandDispatcher dispatcher,
        ReplayGuard replayGuard,
        ServerClock serverClock,
        StationController station,
        LoginRelay loginRelay,
        EnrollmentService enrollment,
        IOptions<AgentOptions> options,
        TimeProvider timeProvider,
        ILogger<ConnectionWorker> logger)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _replayGuard = replayGuard;
        _serverClock = serverClock;
        _station = station;
        _loginRelay = loginRelay;
        _enrollment = enrollment;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // First run only: obtain the station credential before the first connect (no-op once enrolled).
            await _enrollment.EnsureEnrolledAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            long? connectedAt = null;
            try
            {
                _replayGuard.Reset();
                await _connection.ConnectAsync(stoppingToken);
                connectedAt = _timeProvider.GetTimestamp();

                await SendHandshakeAsync(stoppingToken);

                // Sent on every (re)connect, not gated on a handshake_ack the frozen contract does not define.
                await SendStateReportAsync(stoppingToken);
                _connection.MarkReady();

                await RunReceiveLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connection to the server failed.");
            }
            finally
            {
                await _connection.DisconnectAsync(CancellationToken.None);
            }

            // Always back off, also after a clean close: a server that rejects this station (not enrolled,
            // revoked) must not be hammered in a tight loop. Only a stable connection resets the backoff.
            attempt = connectedAt is { } start && _timeProvider.GetElapsedTime(start) >= StableConnectionThreshold
                ? 1
                : attempt + 1;

            var delay = ReconnectBackoff.Compute(
                attempt,
                TimeSpan.FromSeconds(_options.ReconnectBaseDelaySeconds),
                TimeSpan.FromSeconds(_options.ReconnectMaxDelaySeconds),
                Random.Shared);

            _logger.LogInformation("Reconnecting in {Delay:F1}s (attempt {Attempt}).", delay.TotalSeconds, attempt);

            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendHandshakeAsync(CancellationToken cancellationToken)
    {
        var payload = new HandshakePayload
        {
            SerialNumber = _options.ResolveSerialNumber(),
            AgentVersion = AgentVersion,
            OsVersion = Environment.OSVersion.VersionString,
            MachineName = Environment.MachineName
        };

        await _connection.SendAsync(MessageTypes.Handshake, payload, AgentJsonContext.Default.HandshakePayload, cancellationToken);
        _logger.LogInformation("Handshake sent (serial {SerialNumber}, agent {Version}).", payload.SerialNumber, payload.AgentVersion);
    }

    private async Task SendStateReportAsync(CancellationToken cancellationToken)
    {
        var snapshot = _station.GetSnapshot();
        var report = new StateReportPayload
        {
            Locked = snapshot.Locked,
            SessionId = snapshot.SessionId,
            RunningGameId = snapshot.RunningGameId,
            LeaseExpiresAt = snapshot.LeaseExpiresAt
        };

        await _connection.SendAsync(MessageTypes.StateReport, report, AgentJsonContext.Default.StateReportPayload, cancellationToken);
        _logger.LogInformation("State report sent: {Report}", report);
    }

    private async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await _connection.ReceiveAsync(cancellationToken);
            switch (result.Status)
            {
                case ReceiveStatus.Closed:
                    return;

                case ReceiveStatus.Malformed:
                    // Ignore the frame, keep the connection (skill §6.3: ignore anything malformed).
                    _logger.LogWarning("Ignored a malformed frame: {Error}", result.Error);
                    continue;

                default:
                    await ProcessAsync(result.Envelope!, cancellationToken);
                    break;
            }
        }
    }

    private async Task ProcessAsync(Envelope<JsonElement> envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case MessageTypes.HandshakeAck:
                HandleHandshakeAck(envelope);
                return;

            case MessageTypes.HeartbeatAck:
                HandleHeartbeatAck(envelope);
                return;

            case MessageTypes.LoginResult:
                HandleLoginResult(envelope);
                return;

            default:
                // Commands, and anything unknown (answered with UNKNOWN_TYPE so the backend's command loop closes).
                await _dispatcher.DispatchAsync(envelope, cancellationToken);
                return;
        }
    }

    private void HandleHandshakeAck(Envelope<JsonElement> envelope)
    {
        // Freshness cannot be judged yet: this frame is what establishes the clock offset.
        if (!IsSequenceValid(envelope, enforceFreshness: false))
        {
            return;
        }

        var ack = TryParse(envelope, AgentJsonContext.Default.HandshakeAckPayload);
        _serverClock.Synchronize(ack?.ServerTime ?? envelope.Ts);
        _logger.LogInformation("Handshake acknowledged; server clock offset {Offset}.", _serverClock.Offset);
    }

    private void HandleHeartbeatAck(Envelope<JsonElement> envelope)
    {
        // A replayed ack would otherwise keep extending the lease.
        if (!IsSequenceValid(envelope, enforceFreshness: true) ||
            TryParse(envelope, AgentJsonContext.Default.HeartbeatAckPayload) is not { } ack)
        {
            return;
        }

        if (ack.ServerTime is { } serverTime)
        {
            _serverClock.Synchronize(serverTime);
        }

        // OPEN (skill §15 item 3): an ack without lease terms renews the policy default lease.
        _station.RenewLease(LeaseManager.ResolveDuration(ack.LeaseSeconds, ack.LeaseExpiresAt, ack.ServerTime ?? envelope.Ts));
    }

    private void HandleLoginResult(Envelope<JsonElement> envelope)
    {
        if (!IsSequenceValid(envelope, enforceFreshness: true))
        {
            return;
        }

        if (CommandPayload.TryParseRequired(envelope.Payload, AgentJsonContext.Default.LoginResultPayload, out var result, out var error))
        {
            _loginRelay.Complete(result);
        }
        else
        {
            _logger.LogWarning("Dropped {Type} {Id}: {Error}", envelope.Type, envelope.Id, error);
        }
    }

    private bool IsSequenceValid(Envelope<JsonElement> envelope, bool enforceFreshness)
    {
        var check = _replayGuard.Validate(envelope.Seq, envelope.Ts, enforceFreshness);
        if (!check.IsValid)
        {
            _logger.LogWarning("Dropped {Type} {Id}: {Reason}", envelope.Type, envelope.Id, check.Reason);
        }

        return check.IsValid;
    }

    private T? TryParse<T>(Envelope<JsonElement> envelope, JsonTypeInfo<T> typeInfo)
        where T : class, new()
    {
        if (CommandPayload.TryParse(envelope.Payload, typeInfo, out var value, out var error))
        {
            return value;
        }

        _logger.LogWarning("Dropped {Type} {Id}: {Error}", envelope.Type, envelope.Id, error);
        return null;
    }
}
