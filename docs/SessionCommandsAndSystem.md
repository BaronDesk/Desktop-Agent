# BaronDesk Agent : Session Commands, Game Catalog & OS System Control Architecture

This document describes the current implementation and architecture of game catalog delivery, launching games into the gamer's session (directly, through Steam or through the Epic Games Launcher), game process tracking, clean session teardown and OS power management in `BaronDeskAgent.ServiceCore`.

---

## Folder Structure

```text
Desktop-Agent
│
├── src
│   ├── BaronDesk.Shared
│   │   └── Contracts
│   │       ├── CommandPayloads.cs          (LaunchGamePayload, ShutdownPayload, EndSessionPayload)
│   │       └── GameCatalogContracts.cs     (GameLaunchTypes, CatalogGame, GameCatalogResponse, CatalogStatusPayload)
│   │
│   └── BaronDeskAgent.ServiceCore
│       │
│       ├── Commands
│       │   └── Handlers
│       │       ├── LaunchGameCommandHandler.cs
│       │       ├── CatalogUpdateCommandHandler.cs
│       │       ├── EndSessionCommandHandler.cs
│       │       └── ShutdownCommandHandler.cs
│       │
│       ├── Connection
│       │   └── PinnedHttpClient.cs         (HTTPS client pinned like the WSS link)
│       │
│       ├── Games
│       │   ├── GameCatalogEntity.cs
│       │   ├── GameCatalogRepository.cs    (SQLite; ReplaceAllAsync for syncs)
│       │   ├── GameCatalogValidator.cs     (checks every entry from the backend)
│       │   ├── GameCatalogService.cs       (pull → validate → store → catalog_status)
│       │   ├── GameCatalogSyncWorker.cs    (background syncs, coalesced, retried)
│       │   ├── HttpGameCatalogClient.cs    (GET /stations/me/games)
│       │   ├── GameLibraries.cs            (Steam / Epic discovery: WindowsGameLibraryLocator)
│       │   ├── GameLaunchResolver.cs       (entry → command line, or "not installed" reason)
│       │   ├── GameProcesses.cs            (find a game's processes by name in the gamer's session)
│       │   ├── GameService.cs              (+ GameNotFoundException, GameNotInstalledException)
│       │   └── InteractiveProcessLauncher.cs
│       │
│       ├── Platform
│       │   └── InteractiveSession.cs       (console session, gamer session, user token)
│       │
│       ├── Power
│       │   └── SystemPowerService.cs
│       │
│       ├── Persistence
│       │   └── DevelopmentDataSeeder.cs    (charmap test entry, Development only)
│       │
│       └── Session
│           └── StationController.cs        (stops the game on LOCK / END_SESSION / fail-closed)
│
└── tests
    └── BaronDeskAgent.ServiceCore.Tests
        └── Games                           (validator, resolver, libraries, repository, sync, GameService)
```

---

## 1. High-Level Architecture Flow

```text
Backend Server
      │
      ├── CATALOG_UPDATE {}   (also implicit on every (re)connect)
      │         │
      │         ▼
      │   CatalogUpdateCommandHandler ── ack ──► GameCatalogSyncWorker.RequestSync()
      │                                                 │  (off the receive loop)
      │                                                 ▼
      │                                          GameCatalogService.SyncAsync
      │                                                 ├── GET /stations/me/games   (station JWT, pinned TLS)
      │                                                 ├── GameCatalogValidator      (bad entries skipped)
      │                                                 ├── GameCatalogRepository.ReplaceAllAsync
      │                                                 └── catalog_status ──────────► Backend
      │
      ├── LAUNCH_GAME { gameId }
      │         │
      │         ▼
      │   LaunchGameCommandHandler
      │         │
      │         ├── gameId required (1–128 chars)          else INVALID_PAYLOAD
      │         ├── station unlocked AND session bound     else EXEC_FAILED
      │         ├── GameCatalogRepository.GetByIdAsync     unknown → EXEC_FAILED
      │         ├── GameLaunchResolver.Resolve             not installed → EXEC_FAILED (running game untouched)
      │         ├── InteractiveProcessLauncher.Launch      (console user's session)
      │         └── GameService tracks the process tree and/or processName
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

## 2. Game Catalog

`LAUNCH_GAME` only ever executes entries of the local catalog, **never a path taken from a command payload**. The backend owns the catalog; SQLite is the station's copy, so games still launch while the backend is briefly unreachable.

> ⚠ OPEN (skill §15 item 9): `CATALOG_UPDATE`, `GET /stations/me/games` and `catalog_status` are agent-side proposals. Backend member C must confirm or change them.

### 2.1 Delivery

| Direction | Message | Shape |
|---|---|---|
| server → agent | `CATALOG_UPDATE` command | `{}`: "your catalog changed". Acked once a sync is queued. |
| agent → server | `GET /stations/me/games` | `Authorization: Bearer <station JWT>` → `{ "games": [CatalogGame, …] }` |
| agent → server | `catalog_status` | `{ "games": [{ "gameId", "installed", "reason"? }, …] }` |

Why a pull over HTTPS instead of the catalog inside the command: the WSS link rejects inbound frames over 64 KB, and a venue catalog can be larger. The endpoint defaults to `/stations/me/games` on the `ServerUrl` host (`wss` → `https`); `Agent:GameCatalogUrl` overrides it. The client uses the same certificate pin, no proxy and no redirects as enrollment (`PinnedHttpClient`), a 15 s timeout and a 1 MB response cap.

When syncs happen (`GameCatalogSyncWorker`):

- on every (re)connect (the connection's `ReadyChanged(true)`), so the backend does not have to push after reconnects;
- on every `CATALOG_UPDATE`.

Requests that arrive while one is pending are coalesced. A failed download is retried after 5 s, 15 s, 30 s, 1 min, 2 min, then every 5 min. A response the agent cannot use at all (no `games` list, more than 2000 games) is logged and not retried until the next request. Syncs never run on the receive loop, so a slow download cannot delay `LOCK` or heartbeat acks.

### 2.2 Catalog entry (`CatalogGame`)

The backend sends each game already resolved for this machine (`Game` + `MachineGame` override applied):

```json
{
  "games": [
    { "gameId": "3f0c…", "name": "Valorant", "launchType": "exe", "target": "D:\\Games\\Riot\\VALORANT.exe", "arguments": "-fullscreen", "processName": "VALORANT-Win64-Shipping.exe" },
    { "gameId": "9a1d…", "name": "Counter-Strike 2", "launchType": "steam", "target": "730", "arguments": "-novid", "processName": "cs2.exe" },
    { "gameId": "c47e…", "name": "Fortnite", "launchType": "epic", "target": "Fortnite", "processName": "FortniteClient-Win64-Shipping.exe" }
  ]
}
```

| Field | Rule (`GameCatalogValidator`) |
|---|---|
| `gameId` | Required, 1–128 characters, no control characters |
| `name` | Optional (defaults to `gameId`), ≤ 200 characters |
| `launchType` | `exe` (default), `steam` or `epic`, case-insensitive |
| `target` | `exe`: fully qualified path ending in `.exe`, no quotes (UNC shares allowed). `steam`: app id, digits only. `epic`: Epic `AppName`, `[A-Za-z0-9._-]` |
| `arguments` | Optional, ≤ 1024 characters, no control characters. Ignored for `epic` |
| `workingDirectory` | `exe` only, fully qualified. Defaults to the executable's folder |
| `processName` | Optional file name (`cs2.exe` or `cs2`), not a path. System, launcher and agent processes (`explorer`, `svchost`, `steam`, `epicgameslauncher`, `BaronDesk.LockUI`, …) are refused |

An invalid entry is skipped and reported (`installed: false`, `reason: "Invalid catalog entry: …"`); it never blocks the rest of the catalog. Duplicate ids keep the first entry. Entries missing from a sync are removed from the station.

### 2.3 Launching by type (`GameLaunchResolver`)

| `launchType` | Installed when | Started as |
|---|---|---|
| `exe` | The file exists (and is inside `AllowedGameDirectories`, when configured) | `"<target>" <arguments>` |
| `steam` | Steam is installed and `steamapps\appmanifest_<appId>.acf` exists in one of its libraries | `"<steam.exe>" -applaunch <appId> <arguments>` |
| `epic` | The Epic Games Launcher is installed and one of its manifests lists the `AppName` with an existing install folder | `"<EpicGamesLauncher.exe>" "com.epicgames.launcher://apps/<AppName>?action=launch&silent=true"` |

`WindowsGameLibraryLocator` finds the launchers from **machine-wide locations only**, so the executables the agent starts for launcher games are ones an administrator installed:

- **Steam:** `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath` (fallback `%ProgramFiles(x86)%\Steam`); libraries from `steamapps\libraryfolders.vdf` plus the install folder itself.
- **Epic:** the `com.epicgames.launcher` protocol handler under `HKLM\SOFTWARE\Classes` (never the per-user HKCR view; fallback `%ProgramFiles(x86)%\Epic Games\Launcher\Portal\Binaries\Win64|Win32`); installed games from `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item` (`AppName`, `InstallLocation`).

Every "not installed" reason is generic and never contains a local path (it is sent to the backend).

**Optional hardening:** `Agent:AllowedGameDirectories` (e.g. `["D:\\Games"]`) limits `exe` entries to those folders. Paths are normalized first, so `D:\Games\..\Windows\x.exe` and `D:\Games2\x.exe` are both outside `D:\Games`. Empty (the default) allows any folder. This limits the damage of a compromised admin account: it can no longer make every PC run an arbitrary program.

### 2.4 Schema: `GameCatalog` (v3)

```sql
CREATE TABLE GameCatalog
(
    GameId TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Target TEXT NOT NULL,                  -- renamed from ExecutablePath in v3
    LaunchArguments TEXT,
    WorkingDirectory TEXT,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    LaunchType TEXT NOT NULL DEFAULT 'exe', -- v3
    ProcessName TEXT                        -- v3
);
```

Existing rows become `exe` entries (see `LocalStorage.md`).

### 2.5 Repository: `GameCatalogRepository`

- `GetByIdAsync(gameId, ct)` / `GetAllAsync(ct)`: parameterized reads.
- `UpsertAsync(game, ct)`: insert or update one entry (development seed).
- `ReplaceAllAsync(games, ct)`: one transaction; removes entries not in the list, upserts the rest, keeps each entry's original `CreatedAt`.

### 2.6 Seeding

- **Development only:** `DevelopmentDataSeeder` adds `charmap` → `%SystemRoot%\System32\charmap.exe` (Character Map), so `LAUNCH_GAME { "gameId": "charmap" }` works before any sync. The first sync replaces it with the server's catalog. Not Notepad: on Windows 11 `notepad.exe` is a stub that exits, and the Notepad app restores the developer's own documents, which a test session end must never close.
- Production databases are never seeded; migration v2 removed the old `notepad` row.

### 2.7 Tamper Protection

The catalog decides which executables the service starts, so the data directory is restricted to SYSTEM and Administrators, and files planted by other users are deleted at startup (see `LocalStorage.md`). The Epic manifests folder may be writable by users, but a planted manifest can only make the agent start the admin-installed Epic launcher, as the gamer, which the gamer could do anyway.

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

Steam and the Epic launcher are started the same way, so they run as the signed-in gamer, never as SYSTEM.

Security fixes compared to the previous launcher:

- **No Session-0 fallback.** It used to fall back to `Process.Start` when no user was signed in or the token call failed, running the game invisibly as SYSTEM.
- **No double launch.** If the new process exited before its PID was looked up, the old code started a second copy with `Process.Start`.
- Token handles are `SafeAccessTokenHandle`s, and the requested rights are only those needed (`TOKEN_QUERY | DUPLICATE | ASSIGN_PRIMARY | ADJUST_DEFAULT | ADJUST_SESSIONID`).

The same launcher relaunches the LockUI when it dies (see `LockUIAndIPC.md`).

---

## 4. Game Process Tracking & Lifecycle

`GameService` tracks the one game the agent launched:

```csharp
public string? CurrentGameId { get; }   // while running, or still starting (grace period)
public Task LaunchAsync(string gameId, CancellationToken ct);
public Task StopCurrentGameAsync(CancellationToken ct);
```

How a game is seen:

| Entry | Tracked by |
|---|---|
| `exe` without `processName` | The launched process (as before) |
| `exe` with `processName` | The launched process, then processes named `processName` (bootstrappers that start the real game and exit) |
| `steam` / `epic` with `processName` | Processes named `processName` in the gamer's session. The `steam.exe` / `EpicGamesLauncher.exe` process the agent starts is **never tracked or killed** |
| `steam` / `epic` without `processName` | Not trackable: reported as running for the grace period only, and not closed at session end (warning logged) |

`processName` matches are limited to the gamer's session (`InteractiveSession.GetGamerSessionId()`): nothing in Session 0 or another session is touched.

- **Grace period (2 min):** a launched game counts as running until its process appears (launcher start-up, small updates), so a repeated `LAUNCH_GAME` does not start it twice. Once seen and then gone, the game is reported as exited.
- **Same game requested again** → no second launch.
- **Different game** → the current one is stopped first.
- **Game not installed** → `GameNotInstalledException`, checked **before** stopping the current game, so a bad request never kills the running one.
- **Natural exit** (player quits, crash) → the `Exited` handler clears an `exe` game without `processName`. Games with a `processName` stay tracked until stopped or replaced, so one that starts late (after a launcher update) is still closed at session end.
- **Stop sequence** (tracked process first, then every `processName` match):
  1. `CloseMainWindow()` with a 3 s wait. This only works when the agent shares the game's session (development); from Session 0 it returns `false`.
  2. `Kill(entireProcessTree: true)` with a 2 s wait.
  3. Handle disposed.
- **Service stop** only releases the handle. Stopping or updating the agent does not kill the gamer's game.

`CurrentGameId` feeds `state_report.runningGameId` and the heartbeat.

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
| `LaunchGameCommandHandler` | Unknown id / not installed / missing executable | `EXEC_FAILED` with a generic reason (no file paths sent to the server) |
| `GameService` | Requested game not installed while another runs | Refused before anything is stopped |
| `GameService` | Launcher game (Steam, Epic) | Launcher process never killed; the game is closed by `processName` |
| `GameService` | Bootstrapper exe that exits after starting the game | Game followed by `processName` |
| `GameService` | Game starts after a long launcher update | Still tracked and closed at session end |
| `GameCatalogValidator` | Relative path, quote in path, `..`-style app ids, control characters | Entry skipped and reported; rest of the catalog applied |
| `GameCatalogValidator` | `processName` = `explorer`, `steam`, `svchost`, the agent… | Entry refused: closing it would break Windows or the launcher |
| `GameLaunchResolver` | `exe` outside `AllowedGameDirectories` (incl. `..` traversal, prefix siblings) | Not launchable |
| `GameCatalogSyncWorker` | Backend down, 401, timeout | Retried with backoff; the stored catalog keeps working |
| `GameCatalogSyncWorker` | Catalog over the 64 KB WebSocket limit | Pulled over HTTPS (1 MB cap) |
| `GameCatalogSyncWorker` | Slow catalog download | Runs off the receive loop; `LOCK` and heartbeats are never delayed |
| `WindowsGameLibraryLocator` | Per-user protocol handler pointing at another program | Ignored: HKLM only |
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
- [x] Catalog pulled from the backend (`GET /stations/me/games`) on connect and on `CATALOG_UPDATE`; contract OPEN (skill §15 item 9)
- [x] Per-entry validation; bad entries skipped and reported, never blocking the rest
- [x] `catalog_status`: which games this station can launch, with generic reasons
- [x] `exe`, `steam` and `epic` launch types; launchers discovered from machine-wide locations only
- [x] Optional `AllowedGameDirectories` hardening
- [x] `processName` tracking for launcher games and bootstrappers; launchers never killed
- [x] Development-only `charmap` seed, replaced by the first sync
- [x] `CreateProcessAsUser` launch into the console user's session, no Session-0 fallback
- [x] No double launch; PID resolved while the process handle is open
- [x] Single tracked game with graceful close, then process-tree kill
- [x] Teardown through one `StationController` path (overlay first, then game, then session)
- [x] `LOCK` stops the game by policy (`StopGameOnLock`)
- [x] `SHUTDOWN` validated, acked early, station locked before power-off
- [x] `shutdown.exe` invoked with `ArgumentList` and exit-code checking
- [ ] Automatic discovery of installed games to suggest catalog entries (later)
