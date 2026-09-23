using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace BaronDeskAgent.ServiceCore.Platform;

/// <summary>
/// Disables Task Manager for the console user while the station is locked.
/// </summary>
/// <remarks>
/// Standard users only have read access to their own <c>…\CurrentVersion\Policies</c> key, so the LockUI
/// (running as the gamer) cannot set this value itself. The service, running as LocalSystem, writes it into
/// the signed-in user's hive under <c>HKEY_USERS\&lt;SID&gt;</c>. Takes effect for Task Manager started afterwards.
/// </remarks>
internal static class TaskManagerPolicy
{
    private const string PolicyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ValueName = "DisableTaskMgr";

    /// <returns>False when the value could not be written (nobody signed in, or not LocalSystem / administrator).</returns>
    public static bool TryApply(bool disabled, out string? error)
    {
        SecurityIdentifier? user;
        if (InteractiveSession.IsServiceSession)
        {
            user = InteractiveSession.TryGetConsoleUserSid();
        }
        else
        {
            using var identity = WindowsIdentity.GetCurrent();
            user = identity.User;
        }

        if (user is null)
        {
            error = "nobody is signed in on the console";
            return false;
        }

        try
        {
            using var key = Registry.Users.CreateSubKey($@"{user.Value}\{PolicyPath}", writable: true);
            if (disabled)
            {
                key.SetValue(ValueName, 1, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }
}
