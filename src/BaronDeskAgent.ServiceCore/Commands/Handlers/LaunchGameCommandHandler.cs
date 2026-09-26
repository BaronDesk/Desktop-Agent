using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Games;
using BaronDeskAgent.ServiceCore.Session;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

/// <summary>
/// LAUNCH_GAME: resolve the id in the local catalog and start it in the gamer's session (directly, or through
/// Steam / the Epic Games Launcher). Only catalog entries are ever executed, never a path from the payload.
/// </summary>
public sealed class LaunchGameCommandHandler : ICommandHandler
{
    private const int MaxGameIdLength = 128;

    private readonly GameService _gameService;
    private readonly StationController _station;
    private readonly ILogger<LaunchGameCommandHandler> _logger;

    public LaunchGameCommandHandler(GameService gameService, StationController station, ILogger<LaunchGameCommandHandler> logger)
    {
        _gameService = gameService;
        _station = station;
        _logger = logger;
    }

    public string CommandType => CommandTypes.LaunchGame;

    public async Task<CommandResult> HandleAsync(CommandContext command, CancellationToken cancellationToken)
    {
        if (!CommandPayload.TryParse(command.Payload, AgentJsonContext.Default.LaunchGamePayload, out var payload, out var error))
        {
            return CommandResult.Invalid(error);
        }

        var gameId = payload.GameId?.Trim();
        if (string.IsNullOrEmpty(gameId) || gameId.Length > MaxGameIdLength)
        {
            return CommandResult.Invalid($"gameId is required (1-{MaxGameIdLength} characters).");
        }

        var station = _station.GetSnapshot();
        if (station.Locked || station.SessionId is null)
        {
            return CommandResult.Failed("The station must be unlocked with an active session to launch a game.");
        }

        try
        {
            await _gameService.LaunchAsync(gameId, cancellationToken);
            return CommandResult.Success();
        }
        catch (GameNotFoundException ex)
        {
            return CommandResult.Failed(ex.Message);
        }
        catch (GameNotInstalledException ex)
        {
            return CommandResult.Failed(ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning("Executable for {GameId} is missing: {Path}", gameId, ex.FileName);
            return CommandResult.Failed($"Game '{gameId}' is not installed on this station.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "Could not start {GameId}.", gameId);
            return CommandResult.Failed($"Game '{gameId}' could not be started in the player's session.");
        }
    }
}
