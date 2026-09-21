# BaronDesk Agent : Lock UI & Named-Pipe IPC Architecture

This document describes the implementation and architecture of the WPF Lock Screen overlay (`BaronDesk.LockUI`) and the Named-Pipe Inter-Process Communication (IPC) system connecting it to `BaronDeskAgent.ServiceCore`.

---

## Architecture Overview

```text
┌─────────────────────────────────────────────────────────────────────────────────┐
│                           Session 0 (SYSTEM Service)                           │
│                                                                                 │
│   BaronDeskAgent.ServiceCore                                                    │
│   ├── LockService (Authoritative State: IsLocked = true by default)             │
│   ├── SessionService (Stores ExpectedPin & Validates via FixedTimeEquals)       │
│   └── PipeServer (NamedPipeServerStream on "\\.\pipe\BaronDeskAgentPipe")       │
└────────────────────────────────────────┬────────────────────────────────────────┘
                                         │
                         Named-Pipe IPC (Bidirectional)
                                         │
┌────────────────────────────────────────┴────────────────────────────────────────┐
│                        Session 1 (Interactive User Desktop)                     │
│                                                                                 │
│   BaronDesk.LockUI                                                              │
│   ├── MainWindow (Borderless Topmost Kiosk Window covering Virtual Screen)     │
│   ├── KeyboardHook (Low-Level Hook blocking Alt-Tab, WinKey, Alt-F4, Ctrl-Esc) │
│   └── PipeClient (Connects to pipe, auto-reconnects, handles Show/Hide/Pin)     │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### Why a Two-Process Architecture?

1. **Session 0 Isolation:**
   Windows Services execute in Session 0 without direct access to the interactive desktop or user GUI. They cannot directly render windows on user displays.
2. **Interactive UI in User Session:**
   `BaronDesk.LockUI` executes in the active user session (Session 1+), where it can render fullscreen WPF windows, install low-level keyboard hooks, and interact with monitors.
3. **Fail-Closed Security:**
   The service holds the authoritative state. If the UI process drops, the service detects disconnect; if the service is unreachable, the LockUI helper defaults to staying locked.

---

## 1. IPC Wire Protocol

The named-pipe IPC exchanges JSON messages defined in `BaronDesk.Shared.Contracts`:

### `PipeConfig`

- **Pipe Name:** `BaronDeskAgentPipe` (`\\.\pipe\BaronDeskAgentPipe`)
- **Direction:** Bidirectional (`PipeDirection.InOut`)
- **Transmission Mode:** UTF-8 newline-delimited JSON (`StreamReader.ReadLineAsync` / `StreamWriter.WriteLineAsync`)

### `PipeMessageKind`

| Kind | Sender &rarr; Receiver | Purpose |
|---|---|---|
| `ShowLock` | ServiceCore &rarr; LockUI | Command to show and activate the lock screen overlay. |
| `HideLock` | ServiceCore &rarr; LockUI | Command to hide and deactivate the lock screen overlay. |
| `LockShown` | LockUI &rarr; ServiceCore | Confirmation from LockUI that the screen is locked and hooks are installed. |
| `LockHidden` | LockUI &rarr; ServiceCore | Confirmation from LockUI that the screen is hidden and hooks are uninstalled. |
| `HelperAlive` | LockUI &rarr; ServiceCore | Periodic heartbeat (every 15s) from LockUI to confirm it is alive. |
| `SubmitPin` | LockUI &rarr; ServiceCore | Relays user-entered PIN to ServiceCore for validation. |
| `PinResult` | ServiceCore &rarr; LockUI | Returns validation result (`SUCCESS` or `INVALID_PIN`). |

### Message Format

```json
{
  "Kind": 5,
  "CommandId": "d3b07384-d113-463e-b816-6411516e4544",
  "Timestamp": "2026-09-21T22:30:00.0000000Z",
  "Payload": "482917"
}
```

---

## 2. Lock UI Security & Kiosk Hardening

### Multi-Monitor Virtual Screen Bounds

Gaming stations often feature dual or ultra-wide monitors. Standard WPF `WindowState="Maximized"` only covers the primary monitor. `MainWindow` covers all connected monitors by binding to the virtual screen:

```csharp
Left = SystemParameters.VirtualScreenLeft;
Top = SystemParameters.VirtualScreenTop;
Width = SystemParameters.VirtualScreenWidth;
Height = SystemParameters.VirtualScreenHeight;
```

### Low-Level Keyboard Hook (`WH_KEYBOARD_LL`)

`BaronDesk.LockUI.Hooks.KeyboardHook` intercepts and blocks escape keys before they reach the shell:

- **Windows Keys:** `VK_LWIN` (`0x5B`) and `VK_RWIN` (`0x5C`)
- **Task Switching:** `Alt + Tab` (`VK_TAB` with `VK_MENU`)
- **System Menus & Escapes:** `Alt + Esc`, `Ctrl + Esc`
- **Application Kill:** `Alt + F4` (`VK_F4` with `VK_MENU`)
- **Task Manager Shortcut:** `Ctrl + Shift + Esc`

```csharp
if ((isWinKey && isKeyEvent) || blockAltCombo || blockCtrlCombo)
{
    return (IntPtr)1; // Suppress keystroke
}
```

### Window Close Prevention

In addition to suppressing Alt-F4 in the keyboard hook, `OnClosing` cancels closing attempts while in the locked state:

```csharp
protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
{
    if (_isLocked)
    {
        e.Cancel = true;
        return;
    }
    base.OnClosing(e);
}
```

---

## 3. Booking PIN Verification Flow

When a station is unlocked with a booking PIN, the flow proceeds as follows:

```text
Backend Server               ServiceCore (SYSTEM)                     LockUI (User Session)
     │                               │                                         │
     │── UNLOCK { sessionId, pin } ─▶│                                         │
     │                               ├── StartSessionAsync(sessionId, pin)     │
     │                               └── Keeps station LOCKED                  │
     │                                                                         │
     │                                              Customer types PIN         │
     │                                              and clicks Unlock          │
     │                                                                         │
     │                               │◀── SubmitPin { Payload: "482917" } ─────│
     │                               │                                         │
     │                               ├── ValidatePin("482917")                 │
     │                               │   FixedTimeEquals(expected, entered)    │
     │                               │                                         │
     │                               │─── PinResult { Payload: "SUCCESS" } ───▶│
     │                               │─── HideLock ───────────────────────────▶│
     │                               │                                         │
     │                               ├── UnlockAsync()                         │
     │                               └── UpdateLease()                         ├── Screen Hides
     │                                                                         └── User Plays
```

1. Server sends `UNLOCK` with `pin: "482917"`.
2. `UnlockCommandHandler` stores the PIN in `SessionService` and keeps the station locked.
3. The customer types the PIN into `BaronDesk.LockUI`.
4. `PipeClient` sends `SubmitPin` with the PIN payload over the named pipe.
5. `PipeServer` invokes `SessionService.ValidatePin` which checks equality in constant time (`CryptographicOperations.FixedTimeEquals`).
6. On success, `PipeServer` calls `LockService.UnlockAsync()`, which sends `HideLock` over the pipe to hide the lock screen.

---

## 4. ServiceCore Named-Pipe Security (Windows Service Support)

To allow a standard desktop user process (in Session 1) to connect to a named pipe created by a Windows Service running as `SYSTEM` in Session 0, `PipeServer` configures access control rules using `PipeSecurity`:

```csharp
var pipeSecurity = new PipeSecurity();

// Allow current service identity (SYSTEM/Admin) Full Control
pipeSecurity.AddAccessRule(new PipeAccessRule(
    WindowsIdentity.GetCurrent().User ?? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
    PipeAccessRights.FullControl,
    AccessControlType.Allow));

// Allow interactive users Read/Write access so LockUI can connect
pipeSecurity.AddAccessRule(new PipeAccessRule(
    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
    AccessControlType.Allow));
```

---

## 5. Verification Checklist

- [x] Bidirectional named-pipe IPC between ServiceCore and LockUI on `\\.\pipe\BaronDeskAgentPipe`.
- [x] Fullscreen topmost kiosk window covering virtual screen across all monitors.
- [x] Low-level keyboard hook blocking Windows Key, Alt-Tab, Alt-F4, Alt-Esc, Ctrl-Esc, and Ctrl-Shift-Esc.
- [x] Thread-safe writes with `SemaphoreSlim` in both `PipeServer` and `PipeClient`.
- [x] Synchronized lock state: when LockUI connects, `PipeServer` immediately pushes the current `_lockService.IsLocked` state.
- [x] PIN verification integration: PIN entered in LockUI is validated by ServiceCore in constant time.
- [x] Direct unlock vs booking PIN unlock: admin dashboard unlocks immediately, while booking unlock waits for user PIN entry on LockUI.
- [x] `dotnet build` succeeds with 0 warnings and 0 errors across all projects.
