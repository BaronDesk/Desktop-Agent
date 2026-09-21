using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.Commands;
using BaronDeskAgent.ServiceCore.Services.Session;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class EndSessionCommandHandler : ICommandHandler
{
    private readonly SessionService _sessionService;
    private readonly LockService _lockService;
    private readonly LeaseManager _leaseManager;
    private readonly ILogger<EndSessionCommandHandler> _logger;

    public EndSessionCommandHandler(
        SessionService sessionService,
        LockService lockService,
        LeaseManager leaseManager,
        ILogger<EndSessionCommandHandler> logger)
    {
        _sessionService = sessionService;
        _lockService = lockService;
        _leaseManager = leaseManager;
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

        await _sessionService.EndSessionAsync(reason, cancellationToken);
        await _lockService.LockAsync(command.Id, cancellationToken);
        _leaseManager.RevokeLease();

        return new CommandResponse
        {
            CommandId = command.Id,
            Success = true
        };
    }
}