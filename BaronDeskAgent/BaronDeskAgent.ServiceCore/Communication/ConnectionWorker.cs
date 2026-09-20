using System.Collections.Frozen;
using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Commands;
using BaronDeskAgent.ServiceCore.Configuration;
using BaronDeskAgent.ServiceCore.Security;
using BaronDeskAgent.ServiceCore.Services.Commands;
using BaronDeskAgent.ServiceCore.Services.Session;
using Microsoft.Extensions.Options;

namespace BaronDeskAgent.ServiceCore.Communication;

public sealed class ConnectionWorker : BackgroundService
{
    private static readonly FrozenSet<string> AllowedCommands =
        new[]
        {
            CommandTypes.Lock,
            CommandTypes.Unlock,
            CommandTypes.Shutdown,
            CommandTypes.LaunchGame,
            CommandTypes.EndSession,
            CommandTypes.PolicyUpdate
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly IServerConnection _connection;
    private readonly CommandService _commandService;
    private readonly ReplayGuard _replayGuard;
    private readonly IdempotencyTracker _idempotencyTracker;
    private readonly LockService _lockService;
    private readonly SessionService _sessionService;
    private readonly LeaseManager _leaseManager;
    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger<ConnectionWorker> _logger;

    public ConnectionWorker(
        IServerConnection connection,
        CommandService commandService,
        ReplayGuard replayGuard,
        IdempotencyTracker idempotencyTracker,
        LockService lockService,
        SessionService sessionService,
        LeaseManager leaseManager,
        IOptions<AgentOptions> options,
        ILogger<ConnectionWorker> logger)
    {
        _connection = connection;
        _commandService = commandService;
        _replayGuard = replayGuard;
        _idempotencyTracker = idempotencyTracker;
        _lockService = lockService;
        _sessionService = sessionService;
        _leaseManager = leaseManager;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BaronDesk ConnectionWorker started.");

        int retryAttempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _replayGuard.Reset();

                await _connection.ConnectAsync(stoppingToken);
                retryAttempt = 0;

                await SendHandshakeAsync(stoppingToken);

                await RunReceiveLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                retryAttempt++;
                var baseDelay = _options.Value.ReconnectBaseDelaySeconds;
                var maxDelay = _options.Value.ReconnectMaxDelaySeconds;
                var exponential = baseDelay * Math.Pow(1.5, Math.Min(retryAttempt, 10));
                var jitter = Random.Shared.NextDouble() * 1.5;
                var totalDelay = Math.Min(exponential + jitter, maxDelay);

                _logger.LogWarning(
                    ex,
                    "Connection lost or could not be established. Reconnecting in {Delay:F1}s (attempt {Attempt})...",
                    totalDelay,
                    retryAttempt);

                await _connection.DisconnectAsync(CancellationToken.None);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(totalDelay), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        await _connection.DisconnectAsync(CancellationToken.None);
        _logger.LogInformation("BaronDesk ConnectionWorker stopped.");
    }

    private async Task SendHandshakeAsync(CancellationToken cancellationToken)
    {
        var serial = !string.IsNullOrWhiteSpace(_options.Value.SerialNumber)
            ? _options.Value.SerialNumber
            : Environment.MachineName;

        var handshakePayload = new HandshakePayload
        {
            SerialNumber = serial,
            AgentVersion = "1.0.0",
            OsVersion = Environment.OSVersion.ToString(),
            MachineName = Environment.MachineName
        };

        var handshakeEnvelope = new Envelope<HandshakePayload>
        {
            Type = MessageTypes.Handshake,
            Id = Guid.NewGuid(),
            Ts = DateTimeOffset.UtcNow,
            Seq = _connection.NextSequence(),
            Payload = handshakePayload
        };

        await _connection.SendAsync(handshakeEnvelope, cancellationToken);
        _logger.LogInformation("Handshake sent to server. Station={Station}, MachineName={MachineName}",
            serial, Environment.MachineName);
    }

    private async Task SendStateReportAsync(CancellationToken cancellationToken)
    {
        var stateReport = new StateReportPayload
        {
            Locked = _lockService.IsLocked,
            SessionId = _sessionService.CurrentSessionId,
            RunningGameId = null,
            LeaseExpiresAt = _leaseManager.LeaseExpiresAt
        };

        var envelope = new Envelope<StateReportPayload>
        {
            Type = MessageTypes.StateReport,
            Id = Guid.NewGuid(),
            Ts = DateTimeOffset.UtcNow,
            Seq = _connection.NextSequence(),
            Payload = stateReport
        };

        await _connection.SendAsync(envelope, cancellationToken);
        _logger.LogInformation(
            "State report sent on connect. Locked={Locked}, SessionId={SessionId}, LeaseExpiresAt={ExpiresAt:u}",
            stateReport.Locked,
            stateReport.SessionId,
            stateReport.LeaseExpiresAt);
    }

    private async Task RunReceiveLoopAsync(CancellationToken stoppingToken)
    {
        while (_connection.IsConnected && !stoppingToken.IsCancellationRequested)
        {
            var envelope = await _connection.ReceiveAsync(stoppingToken);
            if (envelope is null)
            {
                _logger.LogInformation("Server closed the connection or receive stream ended.");
                break;
            }

            await ProcessInboundEnvelopeAsync(envelope, stoppingToken);
        }
    }

    private async Task ProcessInboundEnvelopeAsync(
        Envelope<JsonElement> envelope,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Processing inbound envelope. Type={Type}, Id={Id}, Seq={Seq}",
            envelope.Type, envelope.Id, envelope.Seq);

        // 1. Control frame handling
        if (envelope.Type.Equals(MessageTypes.HandshakeAck, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Handshake acknowledged by server.");
            await SendStateReportAsync(cancellationToken);
            return;
        }

        if (envelope.Type.Equals(MessageTypes.HeartbeatAck, StringComparison.OrdinalIgnoreCase))
        {
            DateTimeOffset? leaseExpiresAt = null;

            if (envelope.Payload.ValueKind == JsonValueKind.Object &&
                (envelope.Payload.TryGetProperty("leaseExpiresAt", out var prop) ||
                 envelope.Payload.TryGetProperty("LeaseExpiresAt", out prop)) &&
                prop.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(prop.GetString(), out var parsed))
            {
                leaseExpiresAt = parsed;
            }

            _leaseManager.UpdateLease(leaseExpiresAt);
            _logger.LogDebug("Heartbeat acknowledged by server. Lease updated: {ExpiresAt:u}", leaseExpiresAt);
            return;
        }

        // 2. Command allow-list enforcement
        if (!AllowedCommands.Contains(envelope.Type))
        {
            _logger.LogWarning("Rejected unauthorized or unknown message type: {Type}", envelope.Type);
            await NackAsync(envelope.Id, "UNKNOWN_TYPE", $"Type '{envelope.Type}' is not in the allowed command list.", cancellationToken);
            return;
        }

        // 3. App-level anti-replay validation (seq + ts)
        var replayCheck = _replayGuard.Validate(envelope.Seq, envelope.Ts);
        if (!replayCheck.IsValid)
        {
            _logger.LogWarning(
                "Command failed anti-replay check. Type={Type}, Id={Id}, Seq={Seq}, Code={Code}, Reason={Reason}",
                envelope.Type, envelope.Id, envelope.Seq, replayCheck.Code, replayCheck.Reason);

            await NackAsync(envelope.Id, replayCheck.Code ?? "STALE", replayCheck.Reason, cancellationToken);
            return;
        }

        // 4. Idempotency check (prevent duplicate execution on redeliveries)
        if (_idempotencyTracker.IsDuplicate(envelope.Id))
        {
            _logger.LogInformation("Command {Id} was previously processed. Re-acknowledging idempotently.", envelope.Id);
            await AckAsync(envelope.Id, cancellationToken);
            return;
        }

        // 5. Command execution
        var commandRequest = new CommandRequest
        {
            Id = envelope.Id,
            Type = envelope.Type,
            Payload = envelope.Payload
        };

        // Special handling for SHUTDOWN: Ack before executing per spec
        if (envelope.Type.Equals(CommandTypes.Shutdown, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("SHUTDOWN received: acknowledging prior to system shutdown.");
            await AckAsync(envelope.Id, cancellationToken);

            try
            {
                await _commandService.HandleAsync(commandRequest, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while initiating system shutdown.");
            }
            return;
        }

        // General command execution
        try
        {
            var result = await _commandService.HandleAsync(commandRequest, cancellationToken);

            if (result.Success)
            {
                await AckAsync(envelope.Id, cancellationToken);
            }
            else
            {
                await NackAsync(envelope.Id, "EXEC_FAILED", result.Error ?? "Execution failed.", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing command {Id} ({Type})", envelope.Id, envelope.Type);
            await NackAsync(envelope.Id, "EXEC_FAILED", "An unexpected error occurred executing the command.", cancellationToken);
        }
    }

    private async Task AckAsync(Guid commandId, CancellationToken cancellationToken)
    {
        var ack = new Envelope<CommandAckPayload>
        {
            Type = MessageTypes.CommandAck,
            Id = Guid.NewGuid(),
            Ts = DateTimeOffset.UtcNow,
            Seq = _connection.NextSequence(),
            Payload = new CommandAckPayload { CommandId = commandId }
        };

        try
        {
            await _connection.SendAsync(ack, cancellationToken);
            _logger.LogInformation("Sent command_ack for CommandId={CommandId}", commandId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transmit command_ack for CommandId={CommandId}", commandId);
        }
    }

    private async Task NackAsync(Guid commandId, string code, string? reason, CancellationToken cancellationToken)
    {
        var nack = new Envelope<CommandNackPayload>
        {
            Type = MessageTypes.CommandNack,
            Id = Guid.NewGuid(),
            Ts = DateTimeOffset.UtcNow,
            Seq = _connection.NextSequence(),
            Payload = new CommandNackPayload
            {
                CommandId = commandId,
                Code = code,
                Reason = reason
            }
        };

        try
        {
            await _connection.SendAsync(nack, cancellationToken);
            _logger.LogWarning("Sent command_nack for CommandId={CommandId}, Code={Code}, Reason={Reason}",
                commandId, code, reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transmit command_nack for CommandId={CommandId}", commandId);
        }
    }
}
