# BaronDesk Agent : Session Commands, Game Catalog & OS System Control Architecture

This document describes the complete implementation and architecture of session commands, SQLite cached game catalog resolution, game process execution and tree tracking, clean session teardown, and OS power management in `BaronDeskAgent.ServiceCore`.

---

## Folder Structure

```text
BaronDeskAgent
│
├── BaronDesk.Shared
│   └── Contracts
│       ├── CommandTypes.cs
│       ├── CommandRequest.cs
│       ├── CommandResponse.cs
│       ├── StateReportPayload.cs
│       └── PipeMessage.cs
│
├── BaronDesk.LockUI
│   └── Ipc
│       └── PipeClient.cs
│
└── BaronDeskAgent.ServiceCore
    │
    ├── Commands
    │   ├── CommandService.cs
    │   └── Handlers
    │       ├── LaunchGameCommandHandler.cs
    │       ├── EndSessionHandler.cs
    │       └── ShutdownCommandHandler.cs
    │
    ├── Communication
    │   └── ConnectionWorker.cs
    │
    ├── Data
    │   ├── Database
    │   │   └── DatabaseInitializer.cs
    │   ├── Entities
    │   │   └── GameCatalogEntity.cs
    │   └── Repositories
    │       └── GameCatalogRepository.cs
    │
    └── Services
        ├── Games
        │   ├── GameService.cs
        │   └── InteractiveProcessLauncher.cs
        └── System
            └── SystemPowerService.cs
```

---

## 1. High-Level Architecture Flow

```text
Backend Server
      │
      ├── LAUNCH_GAME { gameId }
      │         │
      │         ▼
      │   LaunchGameCommandHandler
      │         │
      │         ├── Check Station Lock State (Fail-closed if locked)
      │         ├── GameCatalogRepository.GetByIdAsync(gameId)
      │         ├── InteractiveProcessLauncher.Launch(exe, args, workDir)
      │         └── GameService tracks PID & process tree
      │
      ├── END_SESSION { reason } (or Fail-Closed Lease Expiry)
      │         │
      │         ▼
      │   EndSessionCommandHandler
      │         │
      │         ├── 1. GameService.StopCurrentGameAsync() (CloseMainWindow -> Kill tree)
      │         ├── 2. SessionService.EndSessionAsync(reason)
      │         └── 3. (Finally) LockService.LockAsync() + LeaseManager.RevokeLease()
      │
      └── SHUTDOWN { action, delaySeconds, reason }
                │
                ▼
          ShutdownCommandHandler
                │
                ├── TCP Ack flushed before machine power off
                └── SystemPowerService.ShutdownAsync() / RestartAsync() via shutdown.exe
```

---

## 2. SQLite Game Catalog Architecture

Local game metadata is persisted in the shared SQLite database (`agent.sqlite` with WAL mode and normal synchronous pragma).

### Schema: `GameCatalog`

```sql
CREATE TABLE IF NOT EXISTS GameCatalog
(
    GameId TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    ExecutablePath TEXT NOT NULL,
    LaunchArguments TEXT,
    WorkingDirectory TEXT,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_GameCatalog_Name ON GameCatalog (Name);
```

### Repository: `GameCatalogRepository`

- `GetByIdAsync(gameId, cancellationToken)`: Returns the game entry or `null`.
- `GetAllAsync(cancellationToken)`: Returns all registered games ordered by Name.
- `UpsertAsync(gameEntity, cancellationToken)`: Inserts or updates existing record on conflict.
- `DeleteAsync(gameId, cancellationToken)`: Deletes an entry.

The database initialization automatically verifies the table existence and seeds a default verification game (`notepad`) pointing to `%SystemRoot%\notepad.exe`.

---

## 3. Session 0 vs. Session 1 Process Launching

### The Challenge

`BaronDeskAgent.ServiceCore` runs as a Windows Service in **Session 0** (isolated, non-interactive). Launching GUI applications or games with standard `Process.Start` from Session 0 spawns them in Session 0 where they cannot access the user desktop, graphics display, or mouse/keyboard input.

### The Solution: `InteractiveProcessLauncher`

`InteractiveProcessLauncher` bridges this boundary:

1. **Session Detection:** Inspects `Process.GetCurrentProcess().SessionId`.
2. **Service Mode (Session 0):**
   - Queries the active interactive user session using `WTSGetActiveConsoleSessionId()`.
   - Obtains the primary token of the active user using `WTSQueryUserToken(sessionId)`.
   - Duplicates the token with `DuplicateTokenEx` (`TOKEN_ALL_ACCESS`, `SecurityImpersonation`).
   - Generates the user's environment block using `CreateEnvironmentBlock`.
   - Populates `STARTUPINFO` with `lpDesktop = @"winsta0\default"`.
   - Launches the executable using `CreateProcessAsUser`.
3. **User Mode / Fallback:**
   - If running in an interactive session (e.g. during developer testing or local execution), or if token extraction is unavailable, it seamlessly falls back to standard `Process.Start`.

---

## 4. Game Process Tracking & Lifecycle

`GameService` acts as the single source of truth for running game processes:

- **State Tracking:**
  - `IsGameRunning`: Returns `true` if the tracked process is alive.
  - `CurrentRunningGameId`: Identifies the currently running game.
  - `CurrentProcessId`: Returns the PID of the tracked game.
  - `RunningDuration`: Time elapsed since launch.
- **Natural Exit Detection:**
  - Sets `process.EnableRaisingEvents = true`.
  - Subscribes to `process.Exited`: when a game is closed by the player or crashes, `GameService` logs the exit code, resets `_currentRunningGameId` to `null`, and emits `OnGameExited`.
- **Double Launch Protection:**
  - If a launch request arrives for the game that is already active, `GameService` recognizes this and avoids redundant launches.
  - If a different game is launched, `GameService` gracefully terminates the previous game before launching the new game.
- **Process Termination:**
  - `StopCurrentGameAsync()`:
    1. First attempts graceful exit using `process.CloseMainWindow()`.
    2. Waits up to 3 seconds for exit.
    3. If still alive, enforces `process.Kill(entireProcessTree: true)` so that launchers, crash handlers, and sub-processes are completely cleared.
    4. Disposes the process handle and resets tracked state.

---

## 5. Clean Session Teardown & Fail-Closed Integration

### Teardown Sequencing

When a customer's session ends:

1. `EndSessionCommandHandler` executes:
   - Invokes `await _gameService.StopCurrentGameAsync()`.
   - Notifies `SessionService.EndSessionAsync(reason)`.
2. Guaranteed Lockout:
   - Station lockout (`LockService.LockAsync`) and lease revocation (`LeaseManager.RevokeLease()`) are executed inside a `finally` block.
   - Even if process termination throws an unhandled exception, the station is guaranteed to fail-closed (screen locked and lease revoked).
3. Fail-Closed Lease Expiration:
   - In `Program.cs`, `GameService` is hooked into `SessionService.OnSessionEnded`.
   - If the station fails closed due to lease expiration (e.g., player walks away or network outage beyond grace period), running games are automatically terminated.

---

## 6. Real OS Power Execution

`SystemPowerService` coordinates Windows power operations:

- **Shutdown:** Executes `shutdown.exe /s /f /t {delaySeconds} /c "{reason}"`.
- **Reboot:** Executes `shutdown.exe /r /f /t {delaySeconds} /c "{reason}"`.
- **Abort:** Executes `shutdown.exe /a`.
- **Network Flush Grace Delay:**
  - When the server sends a `SHUTDOWN` command, `ConnectionWorker` immediately responds with `command_ack`.
  - A default delay of 2 seconds is applied in `shutdown.exe` so the operating system does not immediately terminate the network adapter before the TCP ACK packet leaves the network interface card.

---

## 7. Edge Cases Handled

| Component | Edge Case | Handling / Resolution |
|---|---|---|
| `LaunchGameCommandHandler` | Game launched while screen is locked | Rejected with fail-closed check (`Station is locked. Station must be unlocked before launching games.`). |
| `LaunchGameCommandHandler` | Malformed / polymorphic JSON payloads | Safely checks `gameId`, `game_id`, `id`, numbers, and strings without `InvalidOperationException`. |
| `LaunchGameCommandHandler` | Executable missing or invalid game ID | Returns structured descriptive error (`KeyNotFoundException`, `FileNotFoundException`) in `CommandResponse.Error`. |
| `EndSessionCommandHandler` | Exception during game kill or session teardown | Protected by `try ... finally` so station lock and lease revocation always occur. |
| `LeaseManager` | Fail-closed lease expiry leaving game running | `GameService` hooked to `SessionService.OnSessionEnded` terminates running games on lease expiration. |
| `ConnectionWorker` | `StateReportPayload.RunningGameId` hardcoded to null | Dynamically populated from `_gameService.CurrentRunningGameId`. |
| `PipeClient` (LockUI) | Background keepalive loop leaking on reconnect | Bound to a per-connection `CancellationTokenSource` and cancelled upon disconnect. |
| `InteractiveProcessLauncher` | Running in Session 0 vs interactive desktop | Queries active console session user token for `CreateProcessAsUser`, with fallback to `Process.Start`. |
| `GameService` | Game spawns sub-processes / launcher | Uses `process.Kill(entireProcessTree: true)` after graceful timeout. |
