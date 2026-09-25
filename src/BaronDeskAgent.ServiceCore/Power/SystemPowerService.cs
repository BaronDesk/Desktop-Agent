using System.Diagnostics;
using System.Globalization;

namespace BaronDeskAgent.ServiceCore.Power;

public enum PowerAction
{
    Shutdown,
    Restart
}

/// <summary>
/// Forced OS shutdown/restart through <c>shutdown.exe</c>.
/// </summary>
public sealed class SystemPowerService
{
    private const int MaxReasonLength = 256;

    private readonly ILogger<SystemPowerService> _logger;

    public SystemPowerService(ILogger<SystemPowerService> logger)
    {
        _logger = logger;
    }

    /// <exception cref="InvalidOperationException">shutdown.exe could not be started or rejected the request.</exception>
    public void Execute(PowerAction action, TimeSpan delay, string reason)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"),
            CreateNoWindow = true,
            UseShellExecute = false
        };

        // ArgumentList quotes each value, so the reason cannot inject extra switches.
        startInfo.ArgumentList.Add(action == PowerAction.Restart ? "/r" : "/s");
        startInfo.ArgumentList.Add("/f");
        startInfo.ArgumentList.Add("/t");
        startInfo.ArgumentList.Add(((int)delay.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason);

        _logger.LogWarning("Executing {Action} in {Delay}s ({Reason}).", action, (int)delay.TotalSeconds, reason);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("shutdown.exe could not be started.");

        // shutdown.exe returns immediately after scheduling; a non-zero code means it was refused.
        if (process.WaitForExit(5000) && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"shutdown.exe exited with code {process.ExitCode}.");
        }
    }
}
