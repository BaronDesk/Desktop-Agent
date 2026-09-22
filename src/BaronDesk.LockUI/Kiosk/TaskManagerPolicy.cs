using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace BaronDesk.LockUI.Kiosk;

/// <summary>
/// Disables Task Manager for the signed-in user while the station is locked (per-user policy value).
/// </summary>
/// <remarks>
/// If the helper is killed while locked the value stays set until the helper next unlocks; the helper always
/// starts locked, so the next unlock clears it.
/// </remarks>
internal static class TaskManagerPolicy
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ValueName = "DisableTaskMgr";

    public static void SetDisabled(bool disabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (disabled)
            {
                key.SetValue(ValueName, 1, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Trace.TraceWarning($"Could not update the Task Manager policy: {ex.Message}");
        }
    }
}
