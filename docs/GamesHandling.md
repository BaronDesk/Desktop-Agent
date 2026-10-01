# BaronDesk Agent : Games Handling Architecture

This document describes how `BaronDeskAgent.ServiceCore` handles games: catalog delivery from the backend, validation and local storage, launching games into the gamer's session (directly, through Steam or through the Epic Games Launcher), game process tracking, discovery of installed launcher games, and how to test all of it end to end against the real backend.

Session teardown (which stops the game) and OS power control are in `SessionCommandsAndSystem.md`.

---

## Folder Structure

```text
Desktop-Agent
│
├── src
│   ├── BaronDesk.Shared
│   │   └── Contracts
│   │       ├── CommandPayloads.cs          (LaunchGamePayload)
│   │       ├── GameCatalogContracts.cs     (GameLaunchTypes, CatalogGame, GameCatalogResponse, CatalogStatusPayload)
│   │       └── InstalledGamesPayload.cs    (installed_games)
│   │
│   └── BaronDeskAgent.ServiceCore
│       │
│       ├── Commands
│       │   └── Handlers
│       │       ├── LaunchGameCommandHandler.cs
│       │       └── CatalogUpdateCommandHandler.cs
│       │
│       ├── Connection
│       │   └── PinnedHttpClient.cs         (HTTPS client pinned like the WSS link)
│       │
│       ├── Games
│       │   ├── GameCatalogEntity.cs
│       │   ├── GameCatalogRepository.cs    (SQLite; ReplaceAllAsync for syncs)
│       │   ├── GameCatalogValidator.cs     (checks every entry from the backend)
│       │   ├── GameCatalogService.cs       (pull → validate → store → catalog_status → installed_games)
│       │   ├── GameCatalogSyncWorker.cs    (background syncs, coalesced, retried)
│       │   ├── HttpGameCatalogClient.cs    (GET /stations/me/games)
│       │   ├── GameLibraries.cs            (Steam / Epic discovery: WindowsGameLibraryLocator)
│       │   ├── GameLaunchResolver.cs       (entry → command line, or "not installed" reason)
│       │   ├── InstalledGameScanner.cs     (Steam / Epic games installed on this PC)
│       │   ├── GameProcesses.cs            (find a game's processes by name in the gamer's session)
│       │   ├── GameService.cs              (+ GameNotFoundException, GameNotInstalledException)
│       │   └── InteractiveProcessLauncher.cs
│       │
│       ├── Platform
│       │   └── InteractiveSession.cs       (console session, gamer session, user token)
│       │
│       ├── Persistence
│       │   └── DevelopmentDataSeeder.cs    (charmap test entry, Development only)
│       │
│       └── Session
│           └── StationController.cs        (stops the game on LOCK / END_SESSION / fail-closed)
│
└── tests
    └── BaronDeskAgent.ServiceCore.Tests
        └── Games                           (validator, resolver, libraries, repository, sync, scanner, GameService)
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
      │                                                 ├── catalog_status ──────────► Backend
      │                                                 └── installed_games ─────────► Backend
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
      └── LOCK / END_SESSION / lease fail-closed / SHUTDOWN
                │
                ▼
          StationController
                ├── overlay shown first (screen covered immediately)
                └── GameService.StopCurrentGameAsync()  (LOCK: only if StopGameOnLock)
```

---

## 2. Game Catalog

`LAUNCH_GAME` only ever executes entries of the local catalog, **never a path taken from a command payload**. The backend owns the catalog; SQLite is the station's copy, so games still launch while the backend is briefly unreachable.

> The backend implements `CATALOG_UPDATE`, `GET /stations/me/games` and `catalog_status` with the shapes below, and sends `CATALOG_UPDATE` by itself to every online station whose resolved catalog changed. It also stores `installed_games` per station: staff see them, matched against the catalog, as the source for "add to catalog".

### 2.1 Delivery

| Direction | Message | Shape |
|---|---|---|
| server → agent | `CATALOG_UPDATE` command | `{}`: "your catalog changed". Acked once a sync is queued. |
| agent → server | `GET /stations/me/games` | `Authorization: Bearer <station JWT>` → `{ "games": [CatalogGame, …] }` |
| agent → server | `catalog_status` | `{ "games": [{ "gameId", "installed", "reason"? }, …] }` |
| agent → server | `installed_games` | `{ "games": [{ "launchType", "target", "name", "processName"?, "inCatalog" }, …] }`, see §2.8 |

Why a pull over HTTPS instead of the catalog inside the command: the WSS link rejects inbound frames over 64 KB, and a venue catalog can be larger. The endpoint defaults to `/stations/me/games` on the `ServerUrl` host (`wss` → `https`, so `wss://cstam-server.local/agent-ws` gives `https://cstam-server.local/stations/me/games`); `Agent:GameCatalogUrl` overrides it. The client uses the same certificate pin, no proxy and no redirects as enrollment (`PinnedHttpClient`), a 15 s timeout and a 1 MB response cap.

The request carries the same station credential as the WSS upgrade: the DPAPI store first, then `Agent:StationToken` (Development only). Without one, the sync fails with "No station credential is provisioned." and is retried.

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
- Production databases are never seeded.

### 2.7 Tamper Protection

The catalog decides which executables the service starts, so the data directory is restricted to SYSTEM and Administrators, and files planted by other users are deleted at startup (see `LocalStorage.md`). The Epic manifests folder may be writable by users, but a planted manifest can only make the agent start the admin-installed Epic launcher, as the gamer, which the gamer could do anyway.

### 2.8 Discovering installed games (`InstalledGameScanner`)

After every catalog sync, right after `catalog_status`, the agent reports the **launcher games installed on this
station**, each marked `inCatalog` or not. The dashboard can then offer "add to catalog" with the Steam app id or
Epic AppName already filled in, instead of an admin typing them.

```json
{ "type": "installed_games", "payload": { "games": [
  { "launchType": "steam", "target": "291550", "name": "Brawlhalla", "processName": null, "inCatalog": false },
  { "launchType": "epic", "target": "Fortnite", "name": "Fortnite", "processName": "FortniteLauncher.exe", "inCatalog": true }
] } }
```

| Source | Read from | Kept |
|---|---|---|
| Steam | `steamapps\appmanifest_*.acf` in every library (`libraryfolders.vdf`) | Fully installed (`StateFlags` bit 4); redistributables, runtimes and Proton skipped |
| Epic | `ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item` | Complete installs tagged `games`; `LaunchExecutable` file name suggested as `processName` |

- **Suggestions only.** Nothing is launched from this list; `LAUNCH_GAME` still runs catalog entries only.
- Plain `.exe` games cannot be found reliably, so they are not listed.
- Same machine-wide locations as the launchers themselves; manifests over 1 MB and unreadable files are skipped;
  at most 500 games, names trimmed to 200 characters.
- A scan failure is logged and never fails the sync (the catalog is already applied and `catalog_status` sent).
- `inCatalog` matches on launch type + target (case-insensitive), so a Steam entry never hides an Epic one.

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

Security properties of the launcher:

- **No Session-0 fallback.** When no user is signed in, or the token call fails, the launch fails. It never falls back to `Process.Start`, which would run the game invisibly as SYSTEM.
- **No double launch.** The PID is resolved while the process handle is still open, so a process that exits immediately is reported as a failure instead of being started a second time.
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
| `exe` without `processName` | The launched process |
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

### When the game is stopped

The agent never stops a game on its own schedule: `StationController` stops it as part of every teardown, **after** the overlay is shown.

| Trigger | Game stopped |
|---|---|
| `END_SESSION`, lease fail-closed, `SHUTDOWN` preparation | Always |
| `LOCK` | Only if `StationPolicy.StopGameOnLock` (default on): an overlay cannot cover a game in exclusive fullscreen |

The full teardown order is in `SessionCommandsAndSystem.md` §1.

---

## 5. Backend Integration & End-to-End Testing

### 5.1 What the backend checks before sending `LAUNCH_GAME`

The backend refuses a launch request itself (HTTP 409/404, nothing is sent to the agent) when:

| Code | Why |
|---|---|
| `STATION_OFFLINE` | The agent is not connected |
| `STATION_NOT_IN_SESSION` | The station's last `state_report` is locked or has no session |
| `GAME_NOT_FOUND` / `GAME_DISABLED` | Unknown or disabled `gameId` |
| `GAME_NOT_ASSIGNED` | The game is not in this station's resolved catalog |
| `GAME_STATUS_UNKNOWN` | The station has not sent a `catalog_status` for it yet |
| `GAME_NOT_INSTALLED` | The station's `catalog_status` said it cannot launch it (with the agent's reason) |

The agent checks the same things again on its side: the backend's view can be a few seconds old.

### 5.2 How a station gets unlocked

`LAUNCH_GAME` needs an unlocked station with a session, so a test starts with a gamer logging in:

- The PIN comes with the gamer's booking (shown in their app). The LockUI relays it as `login_request`; the backend checks it and answers `login_result`.
- Only after an accepted login does the backend send `UNLOCK { sessionId, leaseSeconds, serverTime }`. No PIN ever travels in `UNLOCK`.
- A staff Unlock from the dashboard resumes the station's own session with the same payload. With no session the backend refuses it (`409 NO_SESSION_TO_UNLOCK`) and sends nothing, so the agent never sees an `UNLOCK` without a `sessionId`.

### 5.3 Test run with the real backend

The run uses the BaronDesk web apps: the **admin app** for staff and the **gamer portal**.

**1. Connect the station.** Enroll the gaming PC as described in `Enrollment.md` (*New stations* page → enrollment token → approve). On approval the agent logs `Station enrolled`, connects, and immediately syncs its catalog: `Game catalog synced: N games, N launchable here, N rejected.` followed by `Installed launcher games: …`.

**2. Add and offer a game** (admin app, *Games* page, branch admin or HQ):

| Field | Value |
|---|---|
| Name | `Character Map` |
| Game id | `charmap` |
| Launch with | `exe` |
| Executable path | `C:\Windows\System32\charmap.exe` |
| Process name | `charmap` |

Offer it at the station's branch (or at the station only). The backend sends `CATALOG_UPDATE` by itself, and the agent logs a new `Game catalog synced` line with `charmap` launchable. The *Games* page then shows it as installed on that station.

**3. Start a session** (gamer portal): the gamer picks *Play now* on the station and gets a PIN, then types it on the station's lock screen. The agent relays it (`login_request`), the backend accepts it (`login_result`), and the agent receives `UNLOCK`: the overlay disappears.

**4. Launch and stop the game** (admin app, the station's page):

| # | Action | Expected |
|---|---|---|
| 1 | *Launch game* → `charmap` | Agent: `Executing LAUNCH_GAME …`, `Launched Character Map (charmap) via exe.`; Character Map opens on the gaming PC |
| 2 | *Launch game* → `charmap` again | No second window (`Game charmap is already running.`) |
| 3 | *Lock* | Overlay back (with the LockUI running), Character Map closes (`StopGameOnLock`) |
| 4 | *Unlock* | The gamer's own session resumes |
| 5 | *End session* | Session ended and billed; any game closed; the station locks |

For fault injection, the backend accepts a development-only `simulate` option on the command request (`exec_failed`, `invalid_payload`). It sends a launch that the agent is certain to reject, bypassing the backend checks of §5.1.

### 5.4 Agent log lines

| Log line | Meaning |
|---|---|
| `Game catalog synced: N games, N launchable here, N rejected.` | Sync succeeded, `catalog_status` sent |
| `Installed launcher games: N found, N not in the catalog.` | `installed_games` sent |
| `Skipped catalog entry <id>: <error>` | One entry failed validation (§2.2) |
| `Game catalog sync failed; retrying in <delay>.` | Download failed; the exception says why (see below) |
| `Executing LAUNCH_GAME <commandId>.` | The command passed the dispatcher (allow-list, replay guard) |
| `Started <path> (Pid=…)` / `Launched <name> (<id>) via <type>.` | The game process started in the gamer's session |
| `LAUNCH_GAME <commandId> failed (EXEC_FAILED): <reason>` | Refused: locked, unknown id, not installed… |
| `Game <id> has no processName in the catalog…` | Launcher game that cannot be tracked or closed |
| `Stopping game <id> (Pid=…)` / `Game <id> exited.` | Teardown, or the player quit |

### 5.5 Troubleshooting

| Symptom | Cause / Fix |
|---|---|
| **No game log lines at all** | The agent never connected, so no sync ran. Look for the WSS connection error first (usually no or invalid station token) |
| `Game catalog sync failed` … `No station credential is provisioned.` | The station is not enrolled yet: enroll it (`Enrollment.md`) |
| `Game catalog sync failed` … `returned HTTP 401.` | The station credential was issued by another backend or for another serial: clear it (`--clear-station-token`) and enroll again |
| `Game catalog synced: 0 games` | No game is offered at this station or its branch (*Games* page) |
| Dashboard: `GAME_STATUS_UNKNOWN` | The agent has not synced since the game was offered; it syncs on every reconnect and `CATALOG_UPDATE` |
| Dashboard: `GAME_NOT_INSTALLED` | The agent's reason says why (file missing, Steam app not installed, outside `AllowedGameDirectories`) |
| Dashboard: `STATION_NOT_IN_SESSION` | A gamer must log in with a PIN first (§5.2) |
| Dashboard Unlock → `NO_SESSION_TO_UNLOCK` | Nobody is playing: Unlock only resumes a gamer's session (§5.2) |
| `LAUNCH_GAME` → `EXEC_FAILED` "…could not be started in the player's session." | Agent running as a service with nobody signed in on the console: games are never started as SYSTEM (§3) |

---

## 6. Edge Cases Handled

| Component | Edge Case | Handling / Resolution |
|---|---|---|
| `LaunchGameCommandHandler` | Launch while locked or without a session | `EXEC_FAILED` ("must be unlocked with an active session") |
| `LaunchGameCommandHandler` | Missing / non-string / oversized `gameId` | `INVALID_PAYLOAD`; the id is never guessed from other fields |
| `LaunchGameCommandHandler` | Unknown id / not installed / missing executable | `EXEC_FAILED` with a generic reason (no file paths sent to the server) |
| `GameService` | Requested game not installed while another runs | Refused before anything is stopped |
| `GameService` | Launcher game (Steam, Epic) | Launcher process never killed; the game is closed by `processName` |
| `GameService` | Bootstrapper exe that exits after starting the game | Game followed by `processName` |
| `GameService` | Game starts after a long launcher update | Still tracked and closed at session end |
| `GameService` | Game spawns sub-processes | Whole process tree killed after the graceful timeout |
| `GameCatalogValidator` | Relative path, quote in path, `..`-style app ids, control characters | Entry skipped and reported; rest of the catalog applied |
| `GameCatalogValidator` | `processName` = `explorer`, `steam`, `svchost`, the agent… | Entry refused: closing it would break Windows or the launcher |
| `GameLaunchResolver` | `exe` outside `AllowedGameDirectories` (incl. `..` traversal, prefix siblings) | Not launchable |
| `GameCatalogSyncWorker` | Backend down, 401, timeout | Retried with backoff; the stored catalog keeps working |
| `GameCatalogSyncWorker` | Catalog over the 64 KB WebSocket limit | Pulled over HTTPS (1 MB cap) |
| `GameCatalogSyncWorker` | Slow catalog download | Runs off the receive loop; `LOCK` and heartbeats are never delayed |
| `InstalledGameScanner` | Unreadable or oversized manifest, scan error | Skipped / logged; the sync itself still succeeds |
| `WindowsGameLibraryLocator` | Per-user protocol handler pointing at another program | Ignored: HKLM only |
| `InteractiveProcessLauncher` | No user signed in while running as a service | Refused, never started as SYSTEM |
| `InteractiveProcessLauncher` | Process exits before its PID is resolved | Reported as a failure, never launched twice |
| `StationController` | `LOCK` while a game runs in exclusive fullscreen | Game stopped (`StopGameOnLock`), since the overlay cannot cover it |

---

## Implementation Summary

- SQLite game catalog; only catalog entries are executed
- Catalog pulled from the backend (`GET /stations/me/games`) on connect and on `CATALOG_UPDATE`
- Per-entry validation; bad entries skipped and reported, never blocking the rest
- `catalog_status`: which games this station can launch, with generic reasons
- `exe`, `steam` and `epic` launch types; launchers discovered from machine-wide locations only
- Optional `AllowedGameDirectories` hardening
- `processName` tracking for launcher games and bootstrappers; launchers never killed
- Development-only `charmap` seed, replaced by the first sync
- `CreateProcessAsUser` launch into the console user's session, no Session-0 fallback
- No double launch; PID resolved while the process handle is open
- Single tracked game with graceful close, then process-tree kill
- Game stopped on every teardown, after the overlay; on `LOCK` by policy (`StopGameOnLock`)
- Discovery of installed Steam / Epic games (`installed_games`), used by staff as the "add to catalog" source
