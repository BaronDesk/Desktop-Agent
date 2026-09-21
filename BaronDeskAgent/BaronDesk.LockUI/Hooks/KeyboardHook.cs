using System.Runtime.InteropServices;

namespace BaronDesk.LockUI.Hooks
{
    public class KeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_TAB = 0x09;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12; // Alt
        private const int VK_F4 = 0x73;

        private IntPtr _hookId = IntPtr.Zero;
        private readonly LowLevelKeyboardProc _proc;

        public KeyboardHook()
        {
            _proc = HookCallback;
        }

        public void Install()
        {
            if (_hookId != IntPtr.Zero) return;
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
                GetModuleHandle(curModule.ModuleName), 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                bool isWinKey = vkCode == VK_LWIN || vkCode == VK_RWIN;
                bool isKeyEvent = wParam == WM_KEYDOWN || wParam == WM_KEYUP
                                || wParam == WM_SYSKEYDOWN || wParam == WM_SYSKEYUP;

                bool isAlt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                bool isCtrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

                bool blockAltCombo =
                    (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN) &&
                    isAlt &&
                    (vkCode == VK_TAB || vkCode == VK_ESCAPE || vkCode == VK_F4);

                bool blockCtrlCombo =
                    (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN) &&
                    isCtrl &&
                    vkCode == VK_ESCAPE;

                if ((isWinKey && isKeyEvent) || blockAltCombo || blockCtrlCombo)
                    return (IntPtr)1; // swallow it, both down AND up
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}