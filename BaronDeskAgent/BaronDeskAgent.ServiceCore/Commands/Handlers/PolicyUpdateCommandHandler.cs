using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class PolicyUpdateCommandHandler : ICommandHandler
{
    private readonly ILogger<PolicyUpdateCommandHandler> _logger;

    public PolicyUpdateCommandHandler(
        ILogger<PolicyUpdateCommandHandler> logger)
    {
        _logger = logger;
    }

    public string CommandType =>
        CommandTypes.PolicyUpdate;

    public Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Handling POLICY_UPDATE command. CommandId={CommandId}",
            command.Id);

        // Policy persistence will be implemented in feature/policy-store.
        return Task.FromResult(new CommandResponse
        {
            CommandId = command.Id,
            Success = true
        });
    }
}
