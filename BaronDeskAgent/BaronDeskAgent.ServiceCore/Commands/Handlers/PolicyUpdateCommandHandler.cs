using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.Policy;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

/// <summary>
/// Handles the POLICY_UPDATE command by parsing the payload, validating operational bounds,
/// persisting to SQLite, and dynamically updating running services.
/// </summary>
public sealed class PolicyUpdateCommandHandler : ICommandHandler
{
    private readonly IPolicyStore _policyStore;
    private readonly ILogger<PolicyUpdateCommandHandler> _logger;

    public PolicyUpdateCommandHandler(
        IPolicyStore policyStore,
        ILogger<PolicyUpdateCommandHandler> logger)
    {
        _policyStore = policyStore;
        _logger = logger;
    }

    public string CommandType =>
        CommandTypes.PolicyUpdate;

    public async Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Handling POLICY_UPDATE command. CommandId={CommandId}",
            command.Id);

        try
        {
            var updatePayload = PolicyUpdatePayload.FromPayload(command.Payload);

            var updatedPolicy = await _policyStore.UpdatePolicyAsync(
                updatePayload,
                cancellationToken);

            _logger.LogInformation(
                "Successfully applied POLICY_UPDATE for CommandId={CommandId}. UpdatedAt={UpdatedAt:O}",
                command.Id,
                updatedPolicy.UpdatedAt);

            return new CommandResponse
            {
                CommandId = command.Id,
                Success = true
            };
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(
                ex,
                "Validation failed for POLICY_UPDATE CommandId={CommandId}: {Message}",
                command.Id,
                ex.Message);

            return new CommandResponse
            {
                CommandId = command.Id,
                Success = false,
                Error = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected failure applying POLICY_UPDATE CommandId={CommandId}",
                command.Id);

            return new CommandResponse
            {
                CommandId = command.Id,
                Success = false,
                Error = $"Failed to update policy: {ex.Message}"
            };
        }
    }
}
