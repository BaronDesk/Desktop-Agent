using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Commands.Handlers;
using BaronDeskAgent.ServiceCore.Connection;

namespace BaronDeskAgent.ServiceCore.Commands;

/// <summary>
/// Executes server commands: allow-list → anti-replay → idempotency → handler → ack/nack.
/// Never throws out of the receive loop for a command failure.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly FrozenDictionary<string, ICommandHandler> _handlers;
    private readonly IServerConnection _connection;
    private readonly ReplayGuard _replayGuard;
    private readonly CommandOutcomeStore _outcomes;
    private readonly ILogger<CommandDispatcher> _logger;

    public CommandDispatcher(
        IEnumerable<ICommandHandler> handlers,
        IServerConnection connection,
        ReplayGuard replayGuard,
        CommandOutcomeStore outcomes,
        ILogger<CommandDispatcher> logger)
    {
        _handlers = handlers.ToFrozenDictionary(handler => handler.CommandType, StringComparer.Ordinal);
        _connection = connection;
        _replayGuard = replayGuard;
        _outcomes = outcomes;
        _logger = logger;

        var missing = CommandTypes.All.Except(_handlers.Keys).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"No handler registered for: {string.Join(", ", missing)}.");
        }
    }

    public async Task DispatchAsync(Envelope<JsonElement> envelope, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(envelope.Type, out var handler))
        {
            _logger.LogWarning("Rejected message type {Type} (not in the command allow-list).", envelope.Type);
            await NackAsync(envelope.Id, NackCodes.UnknownType, $"'{envelope.Type}' is not an allowed command.", cancellationToken);
            return;
        }

        var replay = _replayGuard.Validate(envelope.Seq, envelope.Ts, enforceFreshness: !handler.IsRestrictive);
        if (!replay.IsValid)
        {
            _logger.LogWarning("Rejected {Type} {Id}: {Reason}", envelope.Type, envelope.Id, replay.Reason);
            await NackAsync(envelope.Id, NackCodes.Stale, replay.Reason, cancellationToken);
            return;
        }

        if (_outcomes.WasCompleted(envelope.Id))
        {
            _logger.LogInformation("{Type} {Id} was already executed; acknowledging again.", envelope.Type, envelope.Id);
            await AckAsync(envelope.Id, cancellationToken);
            return;
        }

        _logger.LogInformation("Executing {Type} {Id}.", envelope.Type, envelope.Id);

        var command = new CommandContext(envelope.Id, envelope.Payload, envelope.Ts, async () =>
        {
            await _outcomes.MarkCompletedAsync(envelope.Id, cancellationToken);
            await AckAsync(envelope.Id, cancellationToken);
        });

        var result = await ExecuteAsync(handler, command, cancellationToken);

        if (command.WasAcknowledgedEarly)
        {
            if (!result.Succeeded)
            {
                _logger.LogError("{Type} {Id} failed after it was acknowledged: {Reason}", envelope.Type, envelope.Id, result.Reason);
            }

            return;
        }

        if (result.Succeeded)
        {
            await _outcomes.MarkCompletedAsync(envelope.Id, cancellationToken);
            await AckAsync(envelope.Id, cancellationToken);
        }
        else
        {
            _logger.LogWarning("{Type} {Id} failed ({Code}): {Reason}", envelope.Type, envelope.Id, result.Code, result.Reason);
            await NackAsync(envelope.Id, result.Code!, result.Reason, cancellationToken);
        }
    }

    private async Task<CommandResult> ExecuteAsync(ICommandHandler handler, CommandContext command, CancellationToken cancellationToken)
    {
        try
        {
            return await handler.HandleAsync(command, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing {Type} {Id}.", handler.CommandType, command.CommandId);
            return CommandResult.Failed("Unexpected error while executing the command.");
        }
    }

    private Task AckAsync(Guid commandId, CancellationToken cancellationToken) =>
        TrySendAsync(
            MessageTypes.CommandAck,
            new CommandAckPayload { CommandId = commandId },
            AgentJsonContext.Default.CommandAckPayload,
            cancellationToken);

    private Task NackAsync(Guid commandId, string code, string? reason, CancellationToken cancellationToken) =>
        TrySendAsync(
            MessageTypes.CommandNack,
            new CommandNackPayload { CommandId = commandId, Code = code, Reason = reason },
            AgentJsonContext.Default.CommandNackPayload,
            cancellationToken);

    private async Task TrySendAsync<T>(string type, T payload, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            await _connection.SendAsync(type, payload, typeInfo, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The backend redelivers unacknowledged commands; the outcome store turns that into a re-ack.
            _logger.LogWarning(ex, "Could not send {Type}.", type);
        }
    }
}
