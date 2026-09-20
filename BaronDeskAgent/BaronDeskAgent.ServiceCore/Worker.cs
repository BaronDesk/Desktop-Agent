using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Commands;
using BaronDeskAgent.ServiceCore.Services.Commands;

namespace BaronDeskAgent.ServiceCore;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly CommandService _commandService;
    private readonly LockService _lockService;

    public Worker(
        ILogger<Worker> logger,
        CommandService commandService,
        LockService lockService)
    {
        _logger = logger;
        _commandService = commandService;
        _lockService = lockService;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "BaronDesk Agent ServiceCore started.");

        _logger.LogInformation(
            "Lock state before command: {IsLocked}",
            _lockService.IsLocked);

        var command = new CommandRequest
        {
            Id = Guid.NewGuid(),
            Type = CommandTypes.Lock
        };

        var response =
            await _commandService.HandleAsync(
                command,
                stoppingToken);

        _logger.LogInformation(
            "Command result. Id={CommandId}, Success={Success}, Error={Error}",
            response.CommandId,
            response.Success,
            response.Error);

        _logger.LogInformation(
            "Lock state after command: {IsLocked}",
            _lockService.IsLocked);

        await Task.Delay(
            Timeout.Infinite,
            stoppingToken);
    }
}