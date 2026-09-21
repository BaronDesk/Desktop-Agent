using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.Commands;
using BaronDeskAgent.ServiceCore.Services.Games;
using BaronDeskAgent.ServiceCore.Services.Session;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class EndSessionCommandHandler : ICommandHandler
{
    private readonly SessionService _sessionService;
    private readonly LockService _lockService;
    private readonly LeaseManager _leaseManager;
    private readonly GameService _gameService;
    private readonly ILogger<EndSessionCommandHandler> _logger;

    public EndSessionCommandHandler(
        SessionService sessionService,
        LockService lockService,
        LeaseManager leaseManager,
        GameService gameService,
        ILogger<EndSessionCommandHandler> logger)
    {
        _sessionService = sessionService;
        _lockService = lockService;
        _leaseManager = leaseManager;
        _gameService = gameService;
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

        string reason = "normal";

        if (command.Payload is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                reason = je.GetString() ?? "normal";
            }
            else if (je.ValueKind == JsonValueKind.Object &&
                     (je.TryGetProperty("reason", out var prop) || je.TryGetProperty("Reason", out prop)) &&
                     prop.ValueKind == JsonValueKind.String)
            {
                reason = prop.GetString() ?? "normal";
            }
        }
        else if (command.Payload is string str)
        {
            reason = str;
        }

        try
        {
            // 1. Terminate any active game process tree
            try
            {
                await _gameService.StopCurrentGameAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while stopping current game during session end (CommandId={CommandId}).", command.Id);
            }

            // 2. Mark session state as ended
            await _sessionService.EndSessionAsync(reason, cancellationToken);
        }
        finally
        {
            // 3. Guaranteed fail-closed station lockout and lease revocation
            try
            {
                await _lockService.LockAsync(command.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error locking station during session end (CommandId={CommandId}).", command.Id);
            }

            _leaseManager.RevokeLease();
        }

        return new CommandResponse
        {
            CommandId = command.Id,
            Success = true
        };
    }
}