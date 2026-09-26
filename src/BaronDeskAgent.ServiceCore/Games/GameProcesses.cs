using System.Diagnostics;
using BaronDeskAgent.ServiceCore.Platform;

namespace BaronDeskAgent.ServiceCore.Games;

/// <summary>
/// Finds a game's processes by image name, limited to the gamer's session so nothing in Session 0 or another
/// user's session is ever touched.
/// </summary>
internal static class GameProcesses
{
    /// <summary>The matching processes; the caller disposes them.</summary>
    public static List<Process> FindInGamerSession(string processName)
    {
        var matches = new List<Process>();
        if (InteractiveSession.GetGamerSessionId() is not { } sessionId)
        {
            return matches;
        }

        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.SessionId == sessionId && !process.HasExited)
                {
                    matches.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited while we looked, or not accessible: not ours either way.
            }

            process.Dispose();
        }

        return matches;
    }

    public static bool IsRunning(string processName)
    {
        var matches = FindInGamerSession(processName);
        foreach (var process in matches)
        {
            process.Dispose();
        }

        return matches.Count > 0;
    }
}
