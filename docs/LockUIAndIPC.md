# BaronDesk Agent : Lock UI & Named-Pipe IPC Architecture

This document describes the WPF lock screen overlay (`BaronDesk.LockUI`), the named-pipe IPC connecting it to `BaronDeskAgent.ServiceCore`, and the login relay that forwards the gamer's PIN to the backend.

---

## Architecture Overview

```text
┌─────────────────────────────────────────────────────────────────────────────────┐
│                           Session 0 (SYSTEM Service)                            │
│                                                                                 │
│   BaronDeskAgent.ServiceCore                                                    │
│   ├── StationController / LockService (authoritative state, starts LOCKED)      │
│   ├── LoginRelay (relays the PIN to the backend; never verifies it)             │
│   └── PipeServer (ILockScreen) on "\\.\pipe\BaronDeskAgentPipe"                  │
│       ├── ACL: service account + console user only (no CreateNewInstance)      │
│       ├── FirstPipeInstance (squatting detection)                               │
│       ├── client verification (session id, optional image path)                │
│       ├── watchdog: alert + relaunch when the helper is missing                 │
│       └── Task Manager policy for the console user (written as LocalSystem)     │
└────────────────────────────────────────┬────────────────────────────────────────┘
                                         │
                   Named-Pipe IPC (newline-delimited JSON, ≤ 4 KiB/message)
                                         │
┌────────────────────────────────────────┴────────────────────────────────────────┐
│                       Session 1+ (Interactive User Desktop)                      │
│                                                                                 │
│   BaronDesk.LockUI (single instance per session)                                 │
│   ├── MainWindow (borderless topmost window over the whole virtual screen,      │
│   │              or a normal window in the Debug-only --windowed test mode)    │
│   ├── Kiosk/KeyboardHook (blocks Win, Alt-Tab, Alt-Esc, Alt-F4, Ctrl-Esc, …)     │
│   └── Ipc/PipeClient (verifies the server, auto-reconnects, keep-alive)          │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### Why a Two-Process Architecture?

1. **Session 0 isolation:** a Windows Service has no desktop and cannot draw a window the gamer sees.
2. **Interactive UI in the user session:** the LockUI renders the fullscreen overlay and installs the keyboard hook where the gamer is.
3. **No authority in the helper:** the LockUI shows what the service asks for and forwards what the gamer types. It never unlocks by itself. The only decision it makes alone is to **lock**, when it loses the service.

---

## Folder Structure

```text
Desktop-Agent
│
└── src
    ├── BaronDesk.Shared
    │   └── Ipc
    │       ├── PipeConfig.cs         (pipe name, 4 KiB message limit)
    │       ├── PipeMessage.cs        (PipeMessageKind, LoginOutcome, SessionNoticeKind, PipeMessage)
    │       ├── PipeJsonContext.cs    (source-generated JSON, string enums)
    │       └── PipeLineReader.cs     (bounded line reader, both sides)
    │
    ├── BaronDesk.LockUI
    │   ├── App.xaml(.cs)             (single-instance mutex, crash logging)
    │   ├── MainWindow.xaml(.cs)
    │   ├── NoticeWindow.xaml(.cs)    (in-session corner notice: low balance, booking ending)
    │   ├── Ipc
    │   │   └── PipeClient.cs
    │   └── Kiosk
    │       └── KeyboardHook.cs
    │
    └── BaronDeskAgent.ServiceCore
        ├── Ipc
        │   ├── PipeServer.cs
        │   └── PipeClientVerifier.cs
        ├── Platform
        │   └── TaskManagerPolicy.cs  (DisableTaskMgr in the console user's hive)
        └── Session
            ├── ILockScreen.cs
            ├── SessionNotices.cs     (session_notice validation, ISessionNotifier)
            ├── LockService.cs
            └── LoginRelay.cs
```

---

## 1. IPC Wire Protocol

### `PipeConfig`

- **Pipe name:** `BaronDeskAgentPipe` (`\\.\pipe\BaronDeskAgentPipe`)
- **Direction:** bidirectional (`PipeDirection.InOut`), one instance at a time
- **Framing:** UTF-8 (no BOM) newline-delimited JSON, read with `PipeLineReader` (max **4096** characters; a longer line drops the connection instead of buffering it)
- **Serialization:** `PipeJsonContext` (source-generated, camelCase, enums as strings)

### `PipeMessageKind`

| Kind | Sender &rarr; Receiver | Purpose |
|---|---|---|
| `ShowLock` | ServiceCore &rarr; LockUI | Show the overlay. Carries a `correlationId`. |
| `HideLock` | ServiceCore &rarr; LockUI | Hide the overlay. Carries a `correlationId`. |
| `LockShown` | LockUI &rarr; ServiceCore | Echoes the `correlationId` once the overlay and hooks are active. |
| `LockHidden` | LockUI &rarr; ServiceCore | Echoes the `correlationId` once the overlay is hidden. |
| `HelperAlive` | LockUI &rarr; ServiceCore | Keep-alive every 15 s. |
| `SubmitCredential` | LockUI &rarr; ServiceCore | PIN typed by the gamer, to be relayed to the backend. Never logged. |
| `LoginResult` | ServiceCore &rarr; LockUI | Outcome of the relay (`LoginOutcome` + `detail`). |
| `ServerStatus` | ServiceCore &rarr; LockUI | Whether the backend is reachable (`serverOnline`). |
| `SessionNotice` | ServiceCore &rarr; LockUI | Show or clear the in-session notice (`noticeKind`, `remainingSeconds`, optional `detail`). See §4b. |

### `LoginOutcome`

| Outcome | Meaning | Lock screen text |
|---|---|---|
| `Accepted` | Backend accepted; `UNLOCK` follows | "PIN accepted — unlocking…" |
| `Rejected` | Backend refused (`detail` = reason) | reason, or "Incorrect PIN. Please try again." |
| `Unavailable` | No backend connection: no new sessions offline | "Service unavailable — please ask the staff for help." |
| `Timeout` | No `login_result` within 15 s | "The server did not answer. Please try again." |
| `RateLimited` | 5 rejections → 30 s lockout (`detail` = seconds) | "Too many attempts. Try again in Ns." |
| `Busy` | A previous login is still pending | "Please wait…" |

The backend's refusal reasons (`login_result.reason`) are: `invalid_pin`, `pin_used`, `pin_expired`,
`too_many_attempts`, `no_pending_session` (no booking has started on this PC), `insufficient_funds` (the balance
does not cover the minimum play time), `unsupported_method` and `unavailable`. The lock screen shows the reason
it receives.

### Message Format

```json
{ "kind": "ShowLock", "correlationId": "d3b07384-d113-463e-b816-6411516e4544" }
```

```json
{ "kind": "LoginResult", "loginOutcome": "Rejected", "detail": "Incorrect PIN. Please try again." }
```

```json
{ "kind": "ServerStatus", "serverOnline": true }
```

```json
{ "kind": "SessionNotice", "noticeKind": "LowBalance", "remainingSeconds": 180 }
```

---

## 2. Named-Pipe Security

The pipe drives the lock screen of a paid station, so it is locked down on both ends: an open pipe would let any local user drive the lock screen, or even host a fake instance of it.

### Service Side (`PipeServer`)

```csharp
var security = new PipeSecurity();

// The service account (LocalSystem in production) hosts the pipe.
security.AddAccessRule(new PipeAccessRule(service, PipeAccessRights.FullControl, AccessControlType.Allow));

// Only the user signed in on the console may read/write. No CreateNewInstance: nobody else can host it.
security.AddAccessRule(new PipeAccessRule(consoleUser, PipeAccessRights.ReadWrite, AccessControlType.Allow));

NamedPipeServerStreamAcl.Create(
    PipeConfig.Name, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
```

| Protection | How |
|---|---|
| Only the console user can connect | ACL built from `WTSQueryUserToken` of the active console session |
| New sign-in / user switch | While idle the pipe is re-created every 10 s with a fresh ACL |
| Pipe-name squatting | `FirstPipeInstance` fails if another process owns the name → CRITICAL log + `security_violation` alert |
| Client verification | `GetNamedPipeClientProcessId` → client must be in the active console session (as a service) and, when `Agent:LockUiExecutablePath` is set, be that exact executable |
| Unbounded input | 4 KiB per line (`PipeLineReader`) |
| Credential leakage | Malformed lines and `SubmitCredential` contents are never logged |

### LockUI Side (`PipeClient`)

In Release builds the client checks, with `GetNamedPipeServerProcessId` + `ProcessIdToSessionId`, that the pipe server runs in **Session 0**. Only a service can, so a local impostor hosting the pipe name cannot drive the overlay. Debug builds skip this so the agent can run as a console app during development.

---

## 3. Overlay Confirmation (`ILockScreen`)

`PipeServer` implements `ILockScreen` for `LockService`:

```text
LockService.LockAsync()
      │
      ▼
PipeServer.ShowAsync()  →  OverlayResult
      │
      ├── helper not connected ──────────────────────────► HelperNotConnected
      │
      ├── send ShowLock { correlationId }
      │
      ├── LockShown { same correlationId } within 5 s ────► Confirmed
      │
      └── timeout / disconnect ───────────────────────────► NotConfirmed
```

- The station is locked in every case (the flag flips first). Only the **acknowledgement** depends on the result.
- `LOCK` and `END_SESSION` are acked only for `Confirmed`. Otherwise they are nacked `EXEC_FAILED` with a reason that names the cause:
  - `HelperNotConnected`: "The station is now locked, but the lock screen app (LockUI) is not running or not connected, so nothing covers the screen."
  - `NotConfirmed`: "The station is now locked, but the lock screen app did not confirm that the overlay is visible."
- The service remembers the **desired** state (starts locked) and replays it to every newly connected helper, together with the current `ServerStatus`.
- The read loop never blocks: logins are relayed in the background, so confirmations keep flowing while the backend decides.

---

## 4. Booking PIN Login Flow

The agent **never verifies a PIN**. The PIN is relayed to the backend, which checks it, and only an `UNLOCK` from the backend unlocks the station. A PIN never travels in `UNLOCK`, and nothing on the station can turn a typed PIN into an unlocked screen by itself.

```text
LockUI (User Session)            ServiceCore (SYSTEM)                      Backend Server
     │                                  │                                         │
     │ Gamer types PIN, presses Enter   │                                         │
     │── SubmitCredential { "1234" } ──►│                                         │
     │                                  ├── LoginRelay                            │
     │                                  │   ├── offline? → Unavailable            │
     │                                  │   ├── locked out? → RateLimited         │
     │                                  │   └── in flight? → Busy                 │
     │                                  │── login_request { method, credential } ►│
     │                                  │                                         ├── verify (Argon2id),
     │                                  │                                         │   balance, reservation
     │                                  │◄─ login_result { requestId, accepted } ─│
     │◄─ LoginResult { Accepted } ──────│                                         │
     │                                  │◄─ UNLOCK { sessionId, leaseSeconds } ───│
     │                                  ├── StationController.StartSessionAsync   │
     │◄─ HideLock { correlationId } ────│                                         │
     │── LockHidden { correlationId } ─►│── command_ack ─────────────────────────►│
     ├── Overlay hides, gamer plays     │                                         │
```

### Wire Messages

```json
{
  "type": "login_request",
  "id": "8e1f2c34-5a6b-4c7d-8e9f-0a1b2c3d4e5f",
  "ts": "2026-09-22T22:10:00.000Z",
  "seq": 14,
  "payload": { "method": "pin", "credential": "1234" }
}
```

```json
{
  "type": "login_result",
  "id": "a9b8c7d6-e5f4-4321-9876-543210fedcba",
  "ts": "2026-09-22T22:10:00.080Z",
  "seq": 131,
  "payload": { "requestId": "8e1f2c34-5a6b-4c7d-8e9f-0a1b2c3d4e5f", "accepted": true, "reason": null }
}
```

- `requestId` is the `id` of the `login_request` envelope.
- `login_request` is sent only on a ready connection and never buffered in the outbox.
- `login_result` passes the replay guard like every inbound frame.
- The mock server accepts PIN `1234` and answers with `login_result` + `UNLOCK`.

---

## 4b. In-Session Notice (low balance, booking ending)

While the gamer plays, the backend can ask the station to show a small warning before it locks: the balance is
running out, or the booked time ends soon. The notice is **informational only**: the backend still decides and
locks with `LOCK` / `END_SESSION` at run-out.

### Wire Message

The backend sends `LOW_BALANCE` when its run-out warning fires (`endsAt` = when the station locks), `TIME_LEFT`
10 minutes before the booking ends (`SESSION_ENDING_NOTICE_MINUTES`, once per session), and `CLEAR` after a
top-up or an extend. The backend sends `message: null`, so the station shows its default text. The gamer's app
gets the same notices.

Server &rarr; agent control frame (not a command: no ack), replay-checked like every inbound frame:

```json
{
  "type": "session_notice",
  "payload": {
    "sessionId": "6adf0ba1-4ed1-4bf6-a4bc-c3c9a661a499",
    "kind": "LOW_BALANCE",
    "endsAt": "2026-09-27T21:03:00Z",
    "message": null
  }
}
```

| `kind` | Shown as |
|---|---|
| `LOW_BALANCE` | Amber box "Low balance", countdown to `endsAt` |
| `TIME_LEFT` | Blue box "Session ending soon", countdown to `endsAt` |
| `CLEAR` | Hides the notice (e.g. after a top-up) |

### Flow

```text
backend ── session_notice ──▶ ConnectionWorker
                                 │ replay guard, required fields
                                 │ SessionNotices.TryCreate: kind known? sessionId = active session?
                                 │ remaining = endsAt − ServerClock.UtcNow   (station clock drift has no effect)
                                 ▼
                              PipeServer (ISessionNotifier) ── SessionNotice {noticeKind, remainingSeconds} ──▶ LockUI
                                 │ keeps the notice; a relaunched helper gets it back with the elapsed time removed
                                 ▼
                              NoticeWindow: bottom-right corner, 1 s countdown on the monotonic clock
```

- A notice for another session (stale) or with an unknown `kind` is dropped and logged.
- `message` replaces the default text, trimmed to 200 characters.
- Cleared on every `ShowLock` and `HideLock`: a lock ends it, and a new or resumed session starts clean.
- The box is a **no-activate tool window** (`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`): it never takes focus, so
  typing and the game keep working, and it has no taskbar button.
- Honest limit: a game in **exclusive fullscreen** draws over every window, so the notice only shows in windowed
  or borderless mode. The run-out lock still happens on time.
- Mock server: `w` sends `LOW_BALANCE` with 3 minutes left (after `2`, UNLOCK), `k` sends `CLEAR`.

---

## 5. Lock UI Security & Kiosk Hardening

### Multi-Monitor Virtual Screen Bounds

The window covers every monitor through the virtual screen, and is re-sized when displays change (`SystemEvents.DisplaySettingsChanged`), so a monitor plugged in while locked is covered too:

```csharp
Left = SystemParameters.VirtualScreenLeft;
Top = SystemParameters.VirtualScreenTop;
Width = SystemParameters.VirtualScreenWidth;
Height = SystemParameters.VirtualScreenHeight;
```

While locked, the window re-asserts `Topmost` and re-activates itself when it loses focus.

### Low-Level Keyboard Hook (`WH_KEYBOARD_LL`)

`Kiosk/KeyboardHook` swallows (key-down and key-up):

- **Windows keys:** `VK_LWIN` (`0x5B`), `VK_RWIN` (`0x5C`)
- **Alt combinations:** `Alt + Tab`, `Alt + Esc`, `Alt + F4` (Alt read from the hook's own `LLKHF_ALTDOWN` flag)
- **Ctrl combinations:** `Ctrl + Esc`, `Ctrl + Shift + Esc`

```csharp
private static bool ShouldBlock(int virtualKey, int flags)
{
    if (virtualKey is VkLeftWindows or VkRightWindows) return true;

    var altDown = (flags & LlkhfAltDown) != 0;
    if (altDown && virtualKey is VkTab or VkEscape or VkF4) return true;

    var controlDown = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
    return controlDown && virtualKey == VkEscape;
}
```

The callback delegate is kept in a field (never garbage collected), and installation failures are logged.

**Honest limits:** `Ctrl + Alt + Del` (secure attention sequence) and `Win + L` cannot be intercepted from user mode. Windows silently removes a low-level hook whose callback is too slow, so the UI thread must stay responsive (pipe events use `Dispatcher.BeginInvoke`, never `Invoke`).

### Task Manager Policy (set by the service)

Standard users only have **read** access to their own `HKCU\Software\Microsoft\Windows\CurrentVersion\Policies` key; only SYSTEM and Administrators can write it. The LockUI runs as the gamer, so it cannot disable Task Manager itself. The first implementation tried to, and its write always failed silently.

`Platform/TaskManagerPolicy` in the **service** (LocalSystem) writes the value into the console user's hive:

```text
HKEY_USERS\<console user SID>\Software\Microsoft\Windows\CurrentVersion\Policies\System
    DisableTaskMgr = 1        (ShowLock, and on every helper connection while locked)
    (value removed)           (HideLock)
```

- Applied in `PipeServer.ShowAsync` / `HideAsync`, and re-applied whenever a helper connects (a new sign-in means a new user hive).
- If it cannot be written (agent running as a standard user during development, nobody signed in), one warning is logged: `Task Manager policy could not be applied for the console user (…)`.
- If the service stops while locked the value stays set until the next unlock. The station is locked in that case anyway.

### Window Close Prevention

```csharp
protected override void OnClosing(CancelEventArgs e)
{
    if (_isLocked)
    {
        e.Cancel = true;
        return;
    }

    base.OnClosing(e);
}
```


### Windowed Test Mode (Debug Builds Only)

```powershell
dotnet run --project src/BaronDesk.LockUI -- --windowed
```

| | Kiosk mode (default) | Windowed test mode |
|---|---|---|
| Window | Borderless, topmost, whole virtual screen | Normal 560×640 window, title `BaronDesk LockUI — TEST MODE — LOCKED/UNLOCKED` |
| Keyboard hook | Installed while locked | Not installed |
| When unlocked | Hidden | Stays visible, banner shows **UNLOCKED** |
| Close button | Refused while locked | Allowed |
| IPC, PIN relay, confirmations, fail-closed | Same | Same |

The flag is compiled out of **Release** builds (`#if DEBUG`). Otherwise a gamer could start a windowed instance first, take the single-instance slot, and receive the service's "lock" in a window they can move away.

For the real kiosk mode, the mock server's `MOCK_AUTO_RELOCK_SECONDS` sends `LOCK` automatically after every acknowledged `UNLOCK`, so the lock/unlock cycle can be tested without typing in the covered terminal (see `README.md`, step 6B).

---

## 6. Resilience & Fail-Closed Behaviour

| Situation | Behaviour |
|---|---|
| LockUI starts | Shows the lock screen immediately, before the pipe connects |
| Second LockUI instance | Exits (`Local\BaronDesk.LockUI` mutex), so a relaunch never stacks overlays |
| Pipe to the service lost while **unlocked** | After 15 s the LockUI locks itself: without the service nothing enforces the lease |
| Pipe lost while a login is pending | Pending login cancelled, "Service unavailable" shown |
| Backend offline (`ServerStatus: false`) | PIN box and button disabled, "Service unavailable — please ask the staff for help." |
| No `LoginResult` within 20 s | "No answer from the service. Please try again." |
| Helper missing while locked for 30 s | Service raises a `security_violation` alert (once per outage, rate-limited) |
| Helper missing (any state) | Service relaunches it every 30 s via `InteractiveProcessLauncher`, when running as a service and `LockUiExecutablePath` is set |
| Keep-alive loop per connection | Cancelled when that connection ends, so reconnects never pile up loops |
| Unexpected error in the pipe client | Logged via `Trace`; the client keeps reconnecting, so an unexpected error can never leave the LockUI stuck on "Service unavailable" |
| Service gone while locked (development) | Debug builds only: **Ctrl+Alt+Shift+F12**, caught by the keyboard hook, closes the LockUI. Compiled out of Release builds |
| Unhandled UI exception | Logged via `Trace`; the overlay keeps running |

---

## 7. Implementation Summary

- Bidirectional named-pipe IPC on `\\.\pipe\BaronDeskAgentPipe` with source-generated JSON
- Pipe ACL restricted to the service and the console user, no `CreateNewInstance`
- `FirstPipeInstance` squatting detection with a `security_violation` alert
- Client verification (console session, optional executable path); server verification in the LockUI (Session 0)
- 4 KiB message limit on both sides (`PipeLineReader`)
- Correlated `ShowLock`/`HideLock` confirmations; `LOCK`/`END_SESSION` acked only when confirmed
- PIN relayed to the backend (`login_request` / `login_result`); **no PIN verification in the agent**
- Offline login refused ("no new sessions offline"); 5 failures → 30 s lockout
- Fullscreen topmost overlay over the virtual screen, re-sized on display changes
- Keyboard hook for Win, Alt-Tab, Alt-Esc, Alt-F4, Ctrl-Esc, Ctrl-Shift-Esc
- Task Manager disabled by the service in the console user's hive (the gamer cannot write that policy)
- Debug-only windowed test mode; mock auto-relock for hands-free kiosk tests
- Debug-only emergency exit (Ctrl+Alt+Shift+F12) for a kiosk overlay whose service stopped
- LockUI single instance; locks itself 15 s after losing the service
- Service watchdog: alert and relaunch when the helper is missing
- `dotnet build` succeeds with 0 warnings (warnings as errors) across all projects
- In-session notice (`session_notice` → `SessionNotice` → corner box with countdown), never takes focus
- `login_request` / `login_result` and `session_notice` wire messages as implemented by the backend
