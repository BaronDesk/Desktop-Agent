# BaronDesk Agent : Session Commands, Game Catalog & OS System Control Architecture

This document describes the current implementation and architecture of game catalog resolution, launching games into the gamer's session, game process tracking, clean session teardown and OS power management in `BaronDeskAgent.ServiceCore`.

---

## Folder Structure

```text
Desktop-Agent
│
├── src
│   ├── BaronDesk.Shared
│   │   └── Contracts
│   │       └── CommandPayloads.cs          (LaunchGamePayload, ShutdownPayload, EndSessionPayload)
│   │
│   └── BaronDeskAgent.ServiceCore
│       │
│       ├── Commands
│       │   └── Handlers
│       │       ├── LaunchGameCommandHandler.cs
│       │       ├── EndSessionCommandHandler.cs
│       │       └── ShutdownCommandHandler.cs
│       │
│       ├── Games
│       │   ├── GameCatalogEntity.cs
│       │   ├── GameCatalogRepository.cs
│       │   ├── GameService.cs              (+ GameNotFoundException)
│       │   └── InteractiveProcessLauncher.cs
│       │
│       ├── Platform
│       │   └── InteractiveSession.cs       (console session, user token, process session)
│       │
│       ├── Power
│       │   └── SystemPowerService.cs
│       │
│       ├── Persistence
│       │   └── DevelopmentDataSeeder.cs    (notepad test entry, Development only)
│       │
│       └── Session
│           └── StationController.cs        (stops the game on LOCK / END_SESSION / fail-closed)
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
      │         ├── gameId required (1–128 chars)          else INVALID_PAYLOAD
      │         ├── station unlocked AND session bound     else EXEC_FAILED
      │         ├── GameCatalogRepository.GetByIdAsync     unknown → EXEC_FAILED
      │         ├── InteractiveProcessLauncher.Launch      (console user's session)
      │         └── GameService tracks the process
      │
      ├── LOCK / END_SESSION / lease fail-closed
      │         │
      │         ▼
      │   StationController
      │         ├── overlay shown first (screen covered immediately)
      │         └── GameService.StopCurrentGameAsync()  (LOCK: only if StopGameOnLock)
      │
      └── SHUTDOWN { action, delaySeconds, reason }
                │
                ▼
          ShutdownCommandHandler
                ├── validate, then command_ack BEFORE executing
                ├── StationController.PrepareForShutdownAsync()   (lock, stop game, end session)
                └── SystemPowerService.Execute(action, delay, reason)
```

---

## 2. SQLite Game Catalog Architecture

`LAUNCH_GAME` only ever executes entries of the local catalog, **never a path taken from a command payload**.

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
```

### Repository: `GameCatalogRepository`

- `GetByIdAsync(gameId, ct)`: returns the entry or `null` (parameterized query).
- `UpsertAsync(game, ct)`: insert or update. This is the entry point for catalog delivery.

### Seeding

- **Development only:** `DevelopmentDataSeeder` adds `notepad` → `%SystemRoot%\System32\notepad.exe`, so `LAUNCH_GAME { "gameId": "notepad" }` works against the mock server.
- Production databases are no longer seeded with a test game; migration v2 removes the old `notepad` row (see `LocalStorage.md`).
- ⚠ OPEN (skill §15 item 9): how the backend delivers the real catalog (e.g. `GET /games` with the station credential) is not defined yet.

### Tamper Protection

The catalog decides which executables the service starts, so the data directory is restricted to SYSTEM and Administrators, and files planted by other users are deleted at startup (see `LocalStorage.md`).

---

## 3. Session 0 vs. Session 1 Process Launching

### The Challenge

As a Windows Service the agent runs in **Session 0**, which has no desktop. A process started there with `Process.Start` is invisible to the gamer and runs as **LocalSystem**.

### The Solution: `InteractiveProcessLauncher`

```text
Launch(exe, args, workDir)
      │
      ├── executable missing ──────────────────────► FileNotFoundException
      │
      ├── agent in a desktop session (development) ─► Process.Start
      │
      └── agent in Session 0 (service)
             │
             ├── WTSGetActiveConsoleSessionId + WTSQueryUserToken
             │      └── nobody signed in ──────────► InvalidOperationException
             │                                       (NO fallback to Process.Start)
             ├── DuplicateTokenEx (primary token, minimal rights)
             ├── CreateEnvironmentBlock (user's environment)
             ├── CreateProcessAsUserW (lpDesktop = winsta0\default, mutable command line)
             └── Process.GetProcessById(pid) while hProcess is still open
                    └── exited already ────────────► InvalidOperationException (no second launch)
```

Security fixes compared to the previous launcher:

- **No Session-0 fallback.** It used to fall back to `Process.Start` when no user was signed in or the token call failed, running the game invisibly as SYSTEM.
- **No double launch.** If the new process exited before its PID was looked up, the old code started a second copy with `Process.Start`.
- Token handles are `SafeAccessTokenHandle`s, and the requested rights are only those needed (`TOKEN_QUERY | DUPLICATE | ASSIGN_PRIMARY | ADJUST_DEFAULT | ADJUST_SESSIONID`).

The same launcher relaunches the LockUI when it dies (see `LockUIAndIPC.md`).

---

## 4. Game Process Tracking & Lifecycle

`GameService` tracks the one game the agent launched:

```csharp
public string? CurrentGameId { get; }   // while the process is alive
public Task LaunchAsync(string gameId, CancellationToken ct);
public Task StopCurrentGameAsync(CancellationToken ct);
```

- **Same game requested again** → no second launch.
- **Different game** → the current one is stopped first.
- **Natural exit** (player quits, crash) → `Exited` handler clears the state and releases the handle.
- **Stop sequence:**
  1. `CloseMainWindow()` with a 3 s wait. This only works when the agent shares the game's session (development); from Session 0 it returns `false`.
  2. `Kill(entireProcessTree: true)` with a 2 s wait.
  3. Handle disposed.
- **Service stop** only releases the handle. Stopping or updating the agent does not kill the gamer's game.

Honest limit: games started through a launcher that exits (Steam, Epic) detach from the tracked process tree and are not stopped. Handling them needs per-game knowledge in the catalog.

---

## 5. Clean Session Teardown & Fail-Closed Integration

All teardown goes through `StationController` (one gate, one code path):

```text
END_SESSION / lease fail-closed / SHUTDOWN preparation
      │
      ├── 1. LeaseManager.Revoke()
      ├── 2. LockService.LockAsync()           ◄── screen covered first
      ├── 3. GameService.StopCurrentGameAsync()  (errors logged, never skip the lock)
      └── 4. SessionService.End(reason)

LOCK
      ├── 1. LeaseManager.Revoke()
      ├── 2. LockService.LockAsync()
      └── 3. StopCurrentGameAsync() if StationPolicy.StopGameOnLock   (session stays bound)
```

The old wiring stopped the game twice: once in the `END_SESSION` handler, and again through a fire-and-forget `OnSessionEnded` handler in `Program.cs`. Both are replaced by this single path.

---

## 6. Real OS Power Execution

`SystemPowerService.Execute(PowerAction action, TimeSpan delay, string reason)` runs `%SystemRoot%\System32\shutdown.exe`:

| Action | Arguments |
|---|---|
| `Shutdown` | `/s /f /t <delay> /c <reason>` |
| `Restart` | `/r /f /t <delay> /c <reason>` |

- Arguments go through `ProcessStartInfo.ArgumentList` (quoted per argument), so a reason cannot inject extra switches. Reasons are truncated to 256 characters.
- A non-zero exit code from `shutdown.exe` is reported as a failure (logged after the early ack).
- `SHUTDOWN` payload: `action` = `"shutdown"` (default) or `"restart"`, `delaySeconds` = `0`–`600` (default `2`, so the ack leaves the network card first).

```json
{
  "type": "SHUTDOWN",
  "id": "3c8d6a1e-4b2f-4d0e-9a51-7f6e2c1b0a93",
  "ts": "2026-09-22T22:00:00.000Z",
  "seq": 120,
  "payload": { "action": "restart", "delaySeconds": 5, "reason": "Maintenance" }
}
```

---

## 7. Edge Cases Handled

| Component | Edge Case | Handling / Resolution |
|---|---|---|
| `LaunchGameCommandHandler` | Launch while locked or without a session | `EXEC_FAILED` ("must be unlocked with an active session") |
| `LaunchGameCommandHandler` | Missing / non-string / oversized `gameId` | `INVALID_PAYLOAD`; no more guessing from `id`, numbers or `ToString()` |
| `LaunchGameCommandHandler` | Unknown id / missing executable | `EXEC_FAILED` with a generic reason (no file paths sent to the server) |
| `InteractiveProcessLauncher` | No user signed in while running as a service | Refused, never started as SYSTEM |
| `InteractiveProcessLauncher` | Process exits before its PID is resolved | Reported as a failure, never launched twice |
| `GameService` | Game spawns sub-processes | Whole process tree killed after the graceful timeout |
| `StationController` | `LOCK` while a game runs in exclusive fullscreen | Game stopped (`StopGameOnLock`), since the overlay cannot cover it |
| `StationController` | Error while stopping the game | Logged; the station is already locked |
| `ShutdownCommandHandler` | Invalid `action` / `delaySeconds` | `INVALID_PAYLOAD`, nothing executed, no early ack |
| `ShutdownCommandHandler` | `shutdown.exe` refuses | Logged; the station is already locked |
| `SystemPowerService` | Reason containing quotes or switches | Passed as a single quoted argument |

---

## Current Status

- [x] SQLite game catalog; only catalog entries are executed
- [x] Development-only `notepad` seed; production catalog delivery OPEN (skill §15 item 9)
- [x] `CreateProcessAsUser` launch into the console user's session, no Session-0 fallback
- [x] No double launch; PID resolved while the process handle is open
- [x] Single tracked game with graceful close, then process-tree kill
- [x] Teardown through one `StationController` path (overlay first, then game, then session)
- [x] `LOCK` stops the game by policy (`StopGameOnLock`)
- [x] `SHUTDOWN` validated, acked early, station locked before power-off
- [x] `shutdown.exe` invoked with `ArgumentList` and exit-code checking
