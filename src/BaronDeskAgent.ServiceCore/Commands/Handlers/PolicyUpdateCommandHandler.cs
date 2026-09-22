using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Policy;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

/// <summary>
/// POLICY_UPDATE: validate, persist, apply live. Acked only once applied.
/// </summary>
public sealed class PolicyUpdateCommandHandler : ICommandHandler
{
    private readonly IPolicyStore _policyStore;
    private readonly ILogger<PolicyUpdateCommandHandler> _logger;

    public PolicyUpdateCommandHandler(IPolicyStore policyStore, ILogger<PolicyUpdateCommandHandler> logger)
    {
        _policyStore = policyStore;
        _logger = logger;
    }

    public string CommandType => CommandTypes.PolicyUpdate;

    public async Task<CommandResult> HandleAsync(CommandContext command, CancellationToken cancellationToken)
    {
        if (!CommandPayload.TryParse(command.Payload, AgentJsonContext.Default.PolicyUpdatePayload, out var update, out var error))
        {
            return CommandResult.Invalid(error);
        }

        if (!update.HasChanges)
        {
            return CommandResult.Invalid("The payload contains no policy fields.");
        }

        try
        {
            await _policyStore.UpdatePolicyAsync(update, cancellationToken);
            return CommandResult.Success();
        }
        catch (PolicyValidationException ex)
        {
            return CommandResult.Invalid(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not persist the policy update.");
            return CommandResult.Failed("The policy could not be saved on the station.");
        }
    }
}
