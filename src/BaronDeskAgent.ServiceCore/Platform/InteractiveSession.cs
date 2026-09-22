using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace BaronDeskAgent.ServiceCore.Platform;

/// <summary>
/// Windows session helpers. In production the service runs in Session 0 (no desktop) while the gamer
/// and the LockUI helper live in the active console session.
/// </summary>
internal static class InteractiveSession
{
    private const uint NoConsoleSession = 0xFFFFFFFF;

    private static readonly Lazy<int> CurrentSessionId = new(() =>
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    });

    /// <summary>True when running as a Windows service (Session 0 isolation).</summary>
    public static bool IsServiceSession => CurrentSessionId.Value == 0;

    public static uint? GetActiveConsoleSessionId()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        return sessionId == NoConsoleSession ? null : sessionId;
    }

    /// <summary>
    /// Primary token of the user signed in on the console, or null when nobody is signed in.
    /// Requires SeTcbPrivilege (LocalSystem).
    /// </summary>
    public static SafeAccessTokenHandle? TryGetConsoleUserToken()
    {
        if (GetActiveConsoleSessionId() is not { } sessionId)
        {
            return null;
        }

        if (!WTSQueryUserToken(sessionId, out var token))
        {
            token.Dispose();
            return null;
        }

        return token;
    }

    /// <summary>SID of the console user, or null when nobody is signed in.</summary>
    public static SecurityIdentifier? TryGetConsoleUserSid()
    {
        using var token = TryGetConsoleUserToken();
        if (token is null)
        {
            return null;
        }

        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        return identity.User;
    }

    public static bool TryGetProcessSessionId(int processId, out uint sessionId) =>
        ProcessIdToSessionId((uint)processId, out sessionId);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
