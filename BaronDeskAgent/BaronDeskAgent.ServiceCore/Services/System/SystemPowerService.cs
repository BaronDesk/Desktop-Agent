using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BaronDeskAgent.ServiceCore.Services.System;

public sealed class SystemPowerService
{
    private readonly ILogger<SystemPowerService> _logger;

    public SystemPowerService(
        ILogger<SystemPowerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initiates an OS shutdown.
    /// Default delay of 2 seconds allows the network stack to transmit command_ack before shutdown.
    /// </summary>
    public Task ShutdownAsync(
        int delaySeconds = 2,
        bool force = true,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Executing system shutdown. Delay={Delay}s, Force={Force}, Reason={Reason}",
            delaySeconds, force, reason);

        ExecuteShutdownCommand(
            isRestart: false,
            delaySeconds: Math.Max(0, delaySeconds),
            force: force,
            reason: reason ?? "BaronDesk Agent remote shutdown");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Initiates an OS reboot.
    /// </summary>
    public Task RestartAsync(
        int delaySeconds = 2,
        bool force = true,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Executing system restart. Delay={Delay}s, Force={Force}, Reason={Reason}",
            delaySeconds, force, reason);

        ExecuteShutdownCommand(
            isRestart: true,
            delaySeconds: Math.Max(0, delaySeconds),
            force: force,
            reason: reason ?? "BaronDesk Agent remote restart");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Aborts a pending system shutdown or restart.
    /// </summary>
    public Task CancelShutdownAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Cancelling pending system shutdown/restart.");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/a",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cancel shutdown.");
            }
        }

        return Task.CompletedTask;
    }

    private void ExecuteShutdownCommand(
        bool isRestart,
        int delaySeconds,
        bool force,
        string reason)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogWarning("Power commands are currently only implemented for Windows.");
            return;
        }

        var actionFlag = isRestart ? "/r" : "/s";
        var forceFlag = force ? "/f" : string.Empty;
        var sanitizedReason = reason.Replace("\"", "'");

        var arguments = $"{actionFlag} {forceFlag} /t {delaySeconds} /c \"{sanitizedReason}\"".Trim();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false
            };

            _logger.LogInformation("Starting shutdown process: shutdown.exe {Arguments}", arguments);

            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogError("Failed to start shutdown.exe process.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while invoking shutdown.exe with args: {Arguments}", arguments);
            throw;
        }
    }
}