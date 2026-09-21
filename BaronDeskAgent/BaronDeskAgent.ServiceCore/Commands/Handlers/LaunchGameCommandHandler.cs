using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.Commands;
using BaronDeskAgent.ServiceCore.Services.Games;
using BaronDeskAgent.ServiceCore.Services.Session;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class LaunchGameCommandHandler : ICommandHandler
{
    private readonly GameService _gameService;
    private readonly LockService _lockService;
    private readonly SessionService _sessionService;
    private readonly ILogger<LaunchGameCommandHandler> _logger;

    public LaunchGameCommandHandler(
        GameService gameService,
        LockService lockService,
        SessionService sessionService,
        ILogger<LaunchGameCommandHandler> logger)
    {
        _gameService = gameService;
        _lockService = lockService;
        _sessionService = sessionService;
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

        // Fail-closed check: station must be unlocked to run games
        if (_lockService.IsLocked)
        {
            _logger.LogWarning("Rejecting LAUNCH_GAME: Workstation is currently locked.");
            return new CommandResponse
            {
                CommandId = command.Id,
                Success = false,
                Error = "Workstation is locked. Station must be unlocked before launching games."
            };
        }

        string? gameId = ExtractGameId(command.Payload);

        if (string.IsNullOrWhiteSpace(gameId))
        {
            return new CommandResponse
            {
                CommandId = command.Id,
                Success = false,
                Error = "Game ID is required in payload."
            };
        }

        try
        {
            await _gameService.LaunchGameAsync(
                gameId,
                cancellationToken);

            return new CommandResponse
            {
                CommandId = command.Id,
                Success = true
            };
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "Game {GameId} not found in catalog for command {CommandId}.", gameId, command.Id);
            return new CommandResponse
            {
                CommandId = command.Id,
                Success = false,
                Error = ex.Message
            };
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Game executable missing for {GameId} in command {CommandId}.", gameId, command.Id);
            return new CommandResponse
            {
                CommandId = command.Id,
                Success = false,
                Error = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error launching game {GameId} for command {CommandId}.", gameId, command.Id);
            return new CommandResponse
            {
                CommandId = command.Id,
                Success = false,
                Error = $"Game launch failed: {ex.Message}"
            };
        }
    }

    private static string? ExtractGameId(object? payload)
    {
        if (payload is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                return je.GetString();
            }

            if (je.ValueKind == JsonValueKind.Number)
            {
                return je.GetRawText();
            }

            if (je.ValueKind == JsonValueKind.Object)
            {
                string[] possibleKeys = ["gameId", "game_id", "GameId", "id", "Id"];
                foreach (var key in possibleKeys)
                {
                    if (je.TryGetProperty(key, out var prop))
                    {
                        if (prop.ValueKind == JsonValueKind.String)
                        {
                            return prop.GetString();
                        }
                        if (prop.ValueKind == JsonValueKind.Number)
                        {
                            return prop.GetRawText();
                        }
                    }
                }
            }
        }

        return payload?.ToString();
    }
}