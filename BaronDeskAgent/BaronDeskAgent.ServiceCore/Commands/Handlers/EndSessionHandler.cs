using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.Session;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class EndSessionCommandHandler : ICommandHandler
{
    private readonly SessionService _sessionService;
    private readonly ILogger<EndSessionCommandHandler> _logger;

    public EndSessionCommandHandler(
        SessionService sessionService,
        ILogger<EndSessionCommandHandler> logger)
    {
        _sessionService = sessionService;
        _logger = logger;
    }

    public string CommandType =>
        CommandTypes.EndSession;

    public async Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Handling END_SESSION command. CommandId={CommandId}",
            command.Id);

        await _sessionService.EndSessionAsync(
            cancellationToken);

        return new CommandResponse
        {
            CommandId = command.Id,
            Success = true
        };
    }
}