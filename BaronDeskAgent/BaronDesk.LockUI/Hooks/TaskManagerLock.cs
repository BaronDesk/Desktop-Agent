using Microsoft.Win32;

namespace BaronDesk.LockUI.Hooks
{
    public static class TaskManagerLock
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";

        public static void Disable()
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
        }

        public static void Enable()
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue("DisableTaskMgr", 0, RegistryValueKind.DWord);
        }
    }
}