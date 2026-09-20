using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.Commands;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class UnlockCommandHandler : ICommandHandler
{
    private readonly LockService _lockService;
    private readonly ILogger<UnlockCommandHandler> _logger;

    public UnlockCommandHandler(
        LockService lockService,
        ILogger<UnlockCommandHandler> logger)
    {
        _lockService = lockService;
        _logger = logger;
    }

    public string CommandType =>
        CommandTypes.Unlock;

    public async Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Handling UNLOCK command. CommandId={CommandId}",
            command.Id);

        await _lockService.UnlockAsync(
            cancellationToken);

        return new CommandResponse
        {
            CommandId = command.Id,
            Success = true
        };
    }
}