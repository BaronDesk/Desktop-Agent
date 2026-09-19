using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Commands;

public sealed class CommandService
{
    private readonly ILogger<CommandService> _logger;

    public CommandService(
        ILogger<CommandService> logger)
    {
        _logger = logger;
    }

    public async Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Received command. Type={CommandType}, Id={CommandId}",
            command.Type,
            command.Id);

        return command.Type switch
        {
            CommandTypes.Lock =>
                await HandleLockAsync(
                    command,
                    cancellationToken),

            CommandTypes.Unlock =>
                await HandleUnlockAsync(
                    command,
                    cancellationToken),

            CommandTypes.EndSession =>
                await HandleEndSessionAsync(
                    command,
                    cancellationToken),

            CommandTypes.LaunchGame =>
                await HandleLaunchGameAsync(
                    command,
                    cancellationToken),

            CommandTypes.StopGame =>
                await HandleStopGameAsync(
                    command,
                    cancellationToken),

            CommandTypes.Shutdown =>
                await HandleShutdownAsync(
                    command,
                    cancellationToken),

            CommandTypes.Restart =>
                await HandleRestartAsync(
                    command,
                    cancellationToken),

            _ => CreateFailureResponse(
                command,
                $"Unknown command type: {command.Type}")
        };
    }

    private Task<CommandResponse> HandleLockAsync(
        CommandRequest command,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "LOCK command received.");

        return Task.FromResult(
            CreateSuccessResponse(command));
    }

    private Task<CommandResponse> HandleUnlockAsync(
        CommandRequest command,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "UNLOCK command received.");

        return Task.FromResult(
            CreateSuccessResponse(command));
    }

    private Task<CommandResponse> HandleEndSessionAsync(
        CommandRequest command,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "END_SESSION command received.");

        return Task.FromResult(
            CreateSuccessResponse(command));
    }

    private Task<CommandResponse> HandleLaunchGameAsync(
        CommandRequest command,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "LAUNCH_GAME command received.");

        return Task.FromResult(
            CreateSuccessResponse(command));
    }

    private Task<CommandResponse> HandleStopGameAsync(
        CommandRequest command,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "STOP_GAME command received.");

        return Task.FromResult(
            CreateSuccessResponse(command));
    }

    private Task<CommandResponse> HandleShutdownAsync(
        CommandRequest command,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "SHUTDOWN command received.");

        return Task.FromResult(
            CreateSuccessResponse(command));
    }

    private Task<CommandResponse> HandleRestartAsync(
        CommandRequest command,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "RESTART command received.");

        return Task.FromResult(
            CreateSuccessResponse(command));
    }

    private static CommandResponse CreateSuccessResponse(
        CommandRequest command)
    {
        return new CommandResponse
        {
            CommandId = command.Id,
            Success = true
        };
    }

    private static CommandResponse CreateFailureResponse(
        CommandRequest command,
        string error)
    {
        return new CommandResponse
        {
            CommandId = command.Id,
            Success = false,
            Error = error
        };
    }
}