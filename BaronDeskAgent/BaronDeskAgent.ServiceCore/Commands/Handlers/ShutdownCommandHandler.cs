using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.System;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class ShutdownCommandHandler : ICommandHandler
{
    private readonly SystemPowerService _systemPowerService;
    private readonly ILogger<ShutdownCommandHandler> _logger;

    public ShutdownCommandHandler(
        SystemPowerService systemPowerService,
        ILogger<ShutdownCommandHandler> logger)
    {
        _systemPowerService = systemPowerService;
        _logger = logger;
    }

    public string CommandType =>
        CommandTypes.Shutdown;

    public async Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Handling SHUTDOWN command. CommandId={CommandId}",
            command.Id);

        await _systemPowerService.ShutdownAsync(
            cancellationToken);

        return new CommandResponse
        {
            CommandId = command.Id,
            Success = true
        };
    }
}