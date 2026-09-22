using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Power;
using BaronDeskAgent.ServiceCore.Session;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

/// <summary>
/// SHUTDOWN: acknowledged before executing, otherwise the ack never leaves the machine.
/// </summary>
public sealed class ShutdownCommandHandler : ICommandHandler
{
    private const int DefaultDelaySeconds = 2;
    private const int MaxDelaySeconds = 600;
    private const string DefaultReason = "BaronDesk remote shutdown";

    private readonly SystemPowerService _power;
    private readonly StationController _station;
    private readonly ILogger<ShutdownCommandHandler> _logger;

    public ShutdownCommandHandler(SystemPowerService power, StationController station, ILogger<ShutdownCommandHandler> logger)
    {
        _power = power;
        _station = station;
        _logger = logger;
    }

    public string CommandType => CommandTypes.Shutdown;

    public async Task<CommandResult> HandleAsync(CommandContext command, CancellationToken cancellationToken)
    {
        if (!CommandPayload.TryParse(command.Payload, AgentJsonContext.Default.ShutdownPayload, out var payload, out var error))
        {
            return CommandResult.Invalid(error);
        }

        PowerAction action;
        switch (payload.Action?.Trim().ToLowerInvariant())
        {
            case null or "" or "shutdown":
                action = PowerAction.Shutdown;
                break;
            case "restart":
                action = PowerAction.Restart;
                break;
            default:
                return CommandResult.Invalid("action must be \"shutdown\" or \"restart\".");
        }

        var delaySeconds = payload.DelaySeconds ?? DefaultDelaySeconds;
        if (delaySeconds is < 0 or > MaxDelaySeconds)
        {
            return CommandResult.Invalid($"delaySeconds must be between 0 and {MaxDelaySeconds}.");
        }

        await command.AcknowledgeEarlyAsync();

        // If the power action fails, the station must not be left unlocked.
        await _station.PrepareForShutdownAsync();

        try
        {
            _power.Execute(action, TimeSpan.FromSeconds(delaySeconds), string.IsNullOrWhiteSpace(payload.Reason) ? DefaultReason : payload.Reason);
            return CommandResult.Success();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "{Action} could not be executed.", action);
            return CommandResult.Failed($"{action} could not be executed.");
        }
    }
}
