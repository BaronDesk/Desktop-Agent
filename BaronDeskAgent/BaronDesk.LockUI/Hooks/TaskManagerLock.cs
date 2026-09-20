using Microsoft.Win32;

namespace BaronDesk.LockUI.Hooks
{
    public static class TaskManagerLock
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";

        public static void Disable()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key?.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"TaskManagerLock.Disable failed: {ex.Message}");
            }
        }

        public static void Enable()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key?.SetValue("DisableTaskMgr", 0, RegistryValueKind.DWord);
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"TaskManagerLock.Enable failed: {ex.Message}");
            }
        }
    }
}