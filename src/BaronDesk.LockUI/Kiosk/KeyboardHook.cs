using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BaronDesk.LockUI.Kiosk;

/// <summary>
/// Low-level keyboard hook that swallows the shell escape keys while the station is locked:
/// Windows key, Alt+Tab, Alt+Esc, Alt+F4, Ctrl+Esc and Ctrl+Shift+Esc.
/// </summary>
/// <remarks>
/// Honest limits: Ctrl+Alt+Del (secure attention sequence) and Win+L cannot be intercepted from user mode.
/// Windows silently removes a low-level hook whose callback is too slow, so the UI thread must stay responsive.
/// </remarks>
public sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int LlkhfAltDown = 0x20;
    private const int FlagsOffset = 8; // KBDLLHOOKSTRUCT: vkCode, scanCode, flags, …

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    private const int VkTab = 0x09;
    private const int VkShift = 0x10;
    private const int VkEscape = 0x1B;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkF4 = 0x73;
    private const int VkF12 = 0x7B;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;

    // Kept in a field so the delegate handed to Windows is never garbage collected.
    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hook;

    public KeyboardHook()
    {
        _callback = OnKey;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Trace.TraceError($"Keyboard hook could not be installed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    public void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void Dispose() => Uninstall();

#if DEBUG
    /// <summary>
    /// Debug builds only: Ctrl+Alt+Shift+F12 was pressed while the hook is installed. Raised on the hook's
    /// thread (the UI thread), so handlers must return quickly.
    /// </summary>
    public event Action? EmergencyExitRequested;
#endif

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var virtualKey = Marshal.ReadInt32(data);
            var flags = Marshal.ReadInt32(data, FlagsOffset);

#if DEBUG
            if (IsEmergencyExit((int)message, virtualKey))
            {
                EmergencyExitRequested?.Invoke();
                return 1;
            }
#endif

            if (ShouldBlock(virtualKey, flags))
            {
                return 1; // Swallow both key-down and key-up.
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

#if DEBUG
    // Development escape hatch: a kiosk overlay whose service died cannot otherwise be left without signing out.
    // Compiled out of Release builds, where it would let a gamer end the lock screen.
    // Alt is read from the key state: Windows leaves LLKHF_ALTDOWN clear while Ctrl is also held.
    private static bool IsEmergencyExit(int message, int virtualKey) =>
        message is WmKeyDown or WmSysKeyDown &&
        virtualKey == VkF12 &&
        (GetAsyncKeyState(VkControl) & 0x8000) != 0 &&
        (GetAsyncKeyState(VkMenu) & 0x8000) != 0 &&
        (GetAsyncKeyState(VkShift) & 0x8000) != 0;
#endif

    private static bool ShouldBlock(int virtualKey, int flags)
    {
        if (virtualKey is VkLeftWindows or VkRightWindows)
        {
            return true;
        }

        var altDown = (flags & LlkhfAltDown) != 0;
        if (altDown && virtualKey is VkTab or VkEscape or VkF4)
        {
            return true;
        }

        var controlDown = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
        return controlDown && virtualKey == VkEscape;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
