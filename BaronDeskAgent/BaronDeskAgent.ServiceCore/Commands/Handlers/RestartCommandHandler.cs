using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.System;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class RestartCommandHandler : ICommandHandler
{
    private readonly SystemPowerService _systemPowerService;
    private readonly ILogger<RestartCommandHandler> _logger;

    public RestartCommandHandler(
        SystemPowerService systemPowerService,
        ILogger<RestartCommandHandler> logger)
    {
        _systemPowerService = systemPowerService;
        _logger = logger;
    }

    public string CommandType =>
        CommandTypes.Restart;

    public async Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Handling RESTART command. CommandId={CommandId}",
            command.Id);

        await _systemPowerService.RestartAsync(
            cancellationToken);

        return new CommandResponse
        {
            CommandId = command.Id,
            Success = true
        };
    }
}