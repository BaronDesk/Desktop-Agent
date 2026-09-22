using System.Collections.Frozen;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Commands.Handlers;

namespace BaronDeskAgent.ServiceCore.Commands;

public sealed class CommandService
{
    private readonly ILogger<CommandService> _logger;

    private readonly FrozenDictionary<string, ICommandHandler>
        _handlers;

    public CommandService(
        IEnumerable<ICommandHandler> handlers,
        ILogger<CommandService> logger)
    {
        _logger = logger;

        _handlers = handlers.ToFrozenDictionary(
            handler => handler.CommandType,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Received command. Type={CommandType}, Id={CommandId}",
            command.Type,
            command.Id);

        if (!_handlers.TryGetValue(
                command.Type,
                out var handler))
        {
            _logger.LogWarning(
                "Unknown command type. Type={CommandType}, Id={CommandId}",
                command.Type,
                command.Id);

            return new CommandResponse
            {
                CommandId = command.Id,
                Success = false,
                Error = $"Unknown command type: {command.Type}"
            };
        }

        try
        {
            return await handler.HandleAsync(
                command,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Command execution failed. " +
                "Type={CommandType}, Id={CommandId}",
                command.Type,
                command.Id);

            return new CommandResponse
            {
                CommandId = command.Id,
                Success = false,
                Error = "Command execution failed."
            };
        }
    }
}