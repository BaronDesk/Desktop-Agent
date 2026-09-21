namespace BaronDeskAgent.ServiceCore.Services.Games;

public sealed class GameService
{
    private readonly ILogger<GameService> _logger;

    public GameService(
        ILogger<GameService> logger)
    {
        _logger = logger;
    }

    public Task LaunchGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Launch game requested. GameId={GameId}",
            gameId);

        // Actual game launching will be implemented later.

        return Task.CompletedTask;
    }

    public Task StopGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Stop game requested. GameId={GameId}",
            gameId);

        // Actual game stopping will be implemented later.

        return Task.CompletedTask;
    }
}