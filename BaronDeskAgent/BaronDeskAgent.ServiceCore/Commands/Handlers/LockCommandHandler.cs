using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class LockCommandHandler : ICommandHandler
{
    private readonly ILogger<LockCommandHandler> _logger;

    public LockCommandHandler(
        ILogger<LockCommandHandler> logger)
    {
        _logger = logger;
    }

    public string CommandType =>
        CommandTypes.Lock;

    public Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Handling LOCK command. CommandId={CommandId}",
            command.Id);

        return Task.FromResult(
            new CommandResponse
            {
                CommandId = command.Id,
                Success = true
            });
    }
}