using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Commands;

namespace BaronDeskAgent.ServiceCore;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly CommandService _commandService;

    public Worker(
        ILogger<Worker> logger,
        CommandService commandService)
    {
        _logger = logger;
        _commandService = commandService;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "BaronDesk Agent ServiceCore started.");

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

        await Task.Delay(
            Timeout.Infinite,
            stoppingToken);
    }
}