using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.Commands;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class LockCommandHandler : ICommandHandler
{
    private readonly LockService _lockService;
    private readonly ILogger<LockCommandHandler> _logger;

    public LockCommandHandler(
        LockService lockService,
        ILogger<LockCommandHandler> logger)
    {
        _lockService = lockService;
        _logger = logger;
    }

    public string CommandType =>
        CommandTypes.Lock;

    public async Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Handling LOCK command. CommandId={CommandId}",
            command.Id);

        await _lockService.LockAsync(
            command.Id,
            cancellationToken);

        return new CommandResponse
        {
            CommandId = command.Id,
            Success = true
        };
    }
}