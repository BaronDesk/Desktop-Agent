using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.Games;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class StopGameCommandHandler : ICommandHandler
{
    private readonly GameService _gameService;
    private readonly ILogger<StopGameCommandHandler> _logger;

    public StopGameCommandHandler(
        GameService gameService,
        ILogger<StopGameCommandHandler> logger)
    {
        _gameService = gameService;
        _logger = logger;
    }

    public string CommandType =>
        CommandTypes.StopGame;

    public async Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Handling STOP_GAME command. CommandId={CommandId}",
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

        await _gameService.StopGameAsync(
            gameId,
            cancellationToken);

        return new CommandResponse
        {
            CommandId = command.Id,
            Success = true
        };
    }
}