using System.Text.Json;
using BaronDesk.Shared.Contracts;
using BaronDeskAgent.ServiceCore.Services.System;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public sealed class ShutdownCommandHandler : ICommandHandler
{
    private readonly SystemPowerService _systemPowerService;
    private readonly ILogger<ShutdownCommandHandler> _logger;

    public ShutdownCommandHandler(
        SystemPowerService systemPowerService,
        ILogger<ShutdownCommandHandler> logger)
    {
        _systemPowerService = systemPowerService;
        _logger = logger;
    }

    public string CommandType =>
        CommandTypes.Shutdown;

    public async Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Handling SHUTDOWN command. CommandId={CommandId}",
            command.Id);

        bool isRestart = false;
        int delaySeconds = 2; // Default 2s to allow TCP flush
        string? reason = null;

        if (command.Payload is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                var text = je.GetString();
                if (string.Equals(text, "restart", StringComparison.OrdinalIgnoreCase))
                {
                    isRestart = true;
                }
            }
            else if (je.ValueKind == JsonValueKind.Object)
            {
                if ((je.TryGetProperty("action", out var actionProp) || je.TryGetProperty("Action", out actionProp)) &&
                    actionProp.ValueKind == JsonValueKind.String)
                {
                    isRestart = string.Equals(actionProp.GetString(), "restart", StringComparison.OrdinalIgnoreCase);
                }

                if ((je.TryGetProperty("restart", out var restartProp) || je.TryGetProperty("Restart", out restartProp)) &&
                    restartProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    isRestart = restartProp.GetBoolean();
                }

                if ((je.TryGetProperty("delaySeconds", out var delayProp) || je.TryGetProperty("DelaySeconds", out delayProp) ||
                     je.TryGetProperty("delay", out delayProp)) &&
                    delayProp.ValueKind == JsonValueKind.Number)
                {
                    delaySeconds = delayProp.GetInt32();
                }

                if ((je.TryGetProperty("reason", out var reasonProp) || je.TryGetProperty("Reason", out reasonProp)) &&
                    reasonProp.ValueKind == JsonValueKind.String)
                {
                    reason = reasonProp.GetString();
                }
            }
        }
        else if (command.Payload is string str)
        {
            if (string.Equals(str, "restart", StringComparison.OrdinalIgnoreCase))
            {
                isRestart = true;
            }
        }

        try
        {
            if (isRestart)
            {
                await _systemPowerService.RestartAsync(
                    delaySeconds,
                    force: true,
                    reason,
                    cancellationToken);
            }
            else
            {
                await _systemPowerService.ShutdownAsync(
                    delaySeconds,
                    force: true,
                    reason,
                    cancellationToken);
            }

            return new CommandResponse
            {
                CommandId = command.Id,
                Success = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute power command for CommandId={CommandId}.", command.Id);
            return new CommandResponse
            {
                CommandId = command.Id,
                Success = false,
                Error = $"Power command execution failed: {ex.Message}"
            };
        }
    }
}