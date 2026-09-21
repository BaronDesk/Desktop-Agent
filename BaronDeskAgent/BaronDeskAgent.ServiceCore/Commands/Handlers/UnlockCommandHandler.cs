using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.Commands;
using BaronDeskAgent.ServiceCore.Services.Session;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class UnlockCommandHandler : ICommandHandler
{
    private readonly LockService _lockService;
    private readonly SessionService _sessionService;
    private readonly LeaseManager _leaseManager;
    private readonly ILogger<UnlockCommandHandler> _logger;

    public UnlockCommandHandler(
        LockService lockService,
        SessionService sessionService,
        LeaseManager leaseManager,
        ILogger<UnlockCommandHandler> logger)
    {
        _lockService = lockService;
        _sessionService = sessionService;
        _leaseManager = leaseManager;
        _logger = logger;
    }

    public string CommandType =>
        CommandTypes.Unlock;

    public async Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Handling UNLOCK command. CommandId={CommandId}",
            command.Id);

        Guid sessionId = Guid.NewGuid();

        if (command.Payload is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String && Guid.TryParse(je.GetString(), out var directGuid))
            {
                sessionId = directGuid;
            }
            else if (je.ValueKind == JsonValueKind.Object)
            {
                if ((je.TryGetProperty("sessionId", out var prop) || je.TryGetProperty("SessionId", out prop)) &&
                    prop.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(prop.GetString(), out var objGuid))
                {
                    sessionId = objGuid;
                }
            }
        }
        else if (command.Payload is string str && Guid.TryParse(str, out var parsedGuid))
        {
            sessionId = parsedGuid;
        }

        await _sessionService.StartSessionAsync(sessionId, cancellationToken);
        await _lockService.UnlockAsync(cancellationToken);

        // Grant initial authorization lease on unlock
        _leaseManager.UpdateLease(null);

        return new CommandResponse
        {
            CommandId = command.Id,
            Success = true
        };
    }
}