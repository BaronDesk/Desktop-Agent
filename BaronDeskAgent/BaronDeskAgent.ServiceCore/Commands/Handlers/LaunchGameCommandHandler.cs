using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.Games;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class LaunchGameCommandHandler : ICommandHandler
{
    private readonly GameService _gameService;
    private readonly ILogger<LaunchGameCommandHandler> _logger;

    public LaunchGameCommandHandler(
        GameService gameService,
        ILogger<LaunchGameCommandHandler> logger)
    {
        _gameService = gameService;
        _logger = logger;
    }

    public string CommandType =>
        CommandTypes.LaunchGame;

    public async Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Handling LAUNCH_GAME command. CommandId={CommandId}",
            command.Id);

        var gameId =
            command.Payload?.ToString();

        if (string.IsNullOrWhiteSpace(gameId))
        {
            return new CommandResponse
            {
                CommandId = command.Id,
                Success = false,
                Error = "Game ID is required."
            };
        }

        await _gameService.LaunchGameAsync(
            gameId,
            cancellationToken);

        return new CommandResponse
        {
            CommandId = command.Id,
            Success = true
        };
    }
}