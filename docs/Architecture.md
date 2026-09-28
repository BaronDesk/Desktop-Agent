# BaronDesk Agent : Overall Architecture

This document describes the overall architecture of the BaronDesk Desktop Agent: its processes, solution layout, component responsibilities, wire protocol, startup sequence and configuration. Each area has its own detailed document, listed in section 3.

---

## Folder Structure

```text
Desktop-Agent
│
├── BaronDeskAgent.slnx
├── Directory.Build.props                (nullable, warnings as errors, version)
├── README.md
│
├── src
│   ├── BaronDesk.Shared                 wire + IPC contracts, no behaviour
│   │   ├── Contracts                    envelope, message/command types, payloads, AgentJsonContext
│   │   └── Ipc                          PipeConfig, PipeMessage, PipeJsonContext, PipeLineReader
│   │
│   ├── BaronDeskAgent.ServiceCore       the agent (Session 0 service core)
│   │   ├── Configuration                AgentOptions, AgentOptionsValidator
│   │   ├── Connection                   WebSocketConnection, ConnectionWorker, ServerClock, ReplayGuard
│   │   ├── Credentials                  DPAPI station credential store + provisioning CLI
│   │   ├── Commands                     CommandDispatcher, CommandOutcomeStore, Handlers/
│   │   ├── Session                      StationController, LockService, SessionService,
│   │   │                                LeaseManager, HeartbeatWorker, LoginRelay
│   │   ├── Games                        GameService, InteractiveProcessLauncher, catalog sync,
│   │   │                                Steam/Epic discovery, launch resolver
│   │   ├── Power                        SystemPowerService
│   │   ├── Telemetry                    sensors, mapper, delta filter, alerts, publisher, Outbox/
│   │   ├── AntiTheft                    UsbMonitorService, UsbDeviceEnumerator
│   │   ├── Policy                       StationPolicy, PolicyStore, PolicyRepository
│   │   ├── Ipc                          PipeServer, PipeClientVerifier
│   │   ├── Persistence                  AgentDatabase, migrations, secure data directory
│   │   ├── Platform                     Windows session helpers, Task Manager policy
│   │   └── Program.cs                   composition root
│   │
│   └── BaronDesk.LockUI                 WPF lock overlay (user session)
│       ├── Ipc                          PipeClient
│       └── Kiosk                        KeyboardHook
│
├── tests
│   └── BaronDeskAgent.ServiceCore.Tests (77 unit / integration tests)
│
├── tools
│   └── mock-server                      zero-dependency Node.js mock of /agent-ws
│
└── docs
```

---

## 1. System Overview

```text
┌──────────────────────────────────────────────────────────────┐
│                  Venue Backend (Fastify)                     │
│                       /agent-ws (WSS)                        │
└──────────────────────────────┬───────────────────────────────┘
                               │ pinned TLS + station JWT (DPAPI)
                               │ envelope { type, id, ts, seq, payload }
┌──────────────────────────────┴───────────────────────────────┐
│             Session 0 — BaronDeskAgent.ServiceCore           │
│                                                              │
│  ConnectionWorker ──► CommandDispatcher ──► Handlers         │
│         │                                     │              │
│         │                                     ▼              │
│         │          StationController (lock · session · lease · game)
│         │                                     │              │
│  HeartbeatWorker · OutboxWorker · HardwareMonitorService     │
│  UsbMonitorService · LoginRelay · PolicyStore · SQLite       │
│                                     │                        │
│                               PipeServer (ILockScreen)       │
└─────────────────────────────────────┬────────────────────────┘
                                      │ named pipe (console user only)
┌─────────────────────────────────────┴────────────────────────┐
│            Session 1+ — BaronDesk.LockUI (WPF)               │
│  fullscreen overlay · keyboard hook · PIN box → relayed      │
└──────────────────────────────────────────────────────────────┘
```

### Why Two Processes?

A Windows Service runs in Session 0 with no desktop, so it cannot show the lock screen. The service holds the connection, the state and the authority. The LockUI only draws the overlay and forwards what the gamer types.

---

## 2. Golden Rules

1. **The agent decides nothing.** It never verifies a credential, judges a balance or invents a session. It relays, and obeys `UNLOCK` / `LOCK`.
2. **Fail closed.** Unknown, stale, offline or broken states end with the station locked. The code enforces the invariant *unlocked ⇒ valid lease*.
3. **Only allow-listed commands**, matched case-sensitively: the six frozen ones (`UNLOCK`, `LOCK`, `SHUTDOWN`, `LAUNCH_GAME`, `END_SESSION`, `POLICY_UPDATE`) plus the proposed `CATALOG_UPDATE`. Games run only from the local catalog, never from a path in a command.
4. **No secrets in logs or SQLite.** Credentials typed on the lock screen are relayed and forgotten; the station credential lives in DPAPI.
5. **Event-driven, feather-light.** No polling loops; workers sleep until a timer tick, a frame or a signal.
6. **The backend clock is authoritative.** Timing uses the estimated server clock or the monotonic clock, never the station's wall clock.

---

## 3. Component Documents

| Area | Document | Main types |
|---|---|---|
| Server link, TLS pinning, anti-replay, server clock | [WebSocketConnection.md](WebSocketConnection.md) | `WebSocketConnection`, `ConnectionWorker`, `ReplayGuard`, `ServerClock` |
| Command pipeline, idempotency, ack/nack | [CommandsHandling.md](CommandsHandling.md) | `CommandDispatcher`, `CommandOutcomeStore`, `ICommandHandler` |
| Session, lease, fail-closed, heartbeat | [SessionAndLeaseControl.md](SessionAndLeaseControl.md) | `StationController`, `LeaseManager`, `LockService`, `HeartbeatWorker` |
| Game catalog, launching, process tracking | [GamesHandling.md](GamesHandling.md) | `GameCatalogService`, `GameService`, `InteractiveProcessLauncher` |
| Session teardown, power | [SessionCommandsAndSystem.md](SessionCommandsAndSystem.md) | `StationController`, `SystemPowerService` |
| Lock screen, IPC, PIN relay, kiosk | [LockUIAndIPC.md](LockUIAndIPC.md) | `PipeServer`, `LoginRelay`, `PipeClient`, `KeyboardHook` |
| Sensors, delta telemetry, outbox | [HardwareTelemetry.md](HardwareTelemetry.md) | `HardwareMonitorService`, `TelemetryDeltaFilter`, `OutboxWorker` |
| Hardware alerts, USB anti-theft | [TelemetryAlerts.md](TelemetryAlerts.md) | `HardwareAlertEvaluator`, `UsbMonitorService` |
| Station policy, `POLICY_UPDATE` | [PolicyStore.md](PolicyStore.md) | `StationPolicy`, `PolicyStore` |
| Station credential (DPAPI) | [CredentialStore.md](CredentialStore.md) | `DpapiStationCredentialStore`, `CredentialCommandLine` |
| First connection: enrollment, station key pair | [Enrollment.md](Enrollment.md) | `EnrollmentService`, `HttpEnrollmentClient`, `DpapiStationKeyStore` |
| SQLite, data directory, migrations | [LocalStorage.md](LocalStorage.md) | `AgentDatabase`, `SecureDataDirectory`, `DatabaseInitializer` |
| Code review findings and fixes | [ReviewFixes.md](ReviewFixes.md) | — |

---

## 4. Wire Protocol Summary

Envelope (frozen): `{ "type", "id", "ts", "seq", "payload" }`. Outbound `seq`/`ts` are stamped by the connection under its send lock; every inbound frame passes the replay guard.

| Direction | Type | Payload | Document |
|---|---|---|---|
| agent → server | `handshake` | `serialNumber, agentVersion, osVersion, machineName` | WebSocketConnection |
| agent → server | `state_report` | `locked, sessionId, runningGameId, leaseExpiresAt` | SessionAndLeaseControl |
| agent → server | `heartbeat` | `locked, sessionId` | SessionAndLeaseControl |
| agent → server | `telemetry` | `samples: [{ metric, value, sampledAt }]` | HardwareTelemetry |
| agent → server | `alert` | `category, type, severity, detail, occurredAt` | TelemetryAlerts |
| agent → server | `command_ack` / `command_nack` | `commandId` / `commandId, code, reason` | CommandsHandling |
| agent → server | `login_request` ⚠ OPEN | `method, credential` | LockUIAndIPC |
| agent → server | `catalog_status` ⚠ OPEN | `games: [{ gameId, installed, reason? }]` | GamesHandling |
| server → agent | `handshake_ack` ⚠ OPEN | `serverTime?` | WebSocketConnection |
| server → agent | `heartbeat_ack` ⚠ OPEN | `leaseSeconds?, leaseExpiresAt?, serverTime?` | SessionAndLeaseControl |
| server → agent | `login_result` ⚠ OPEN | `requestId, accepted, reason?` | LockUIAndIPC |
| server → agent | `UNLOCK` | `sessionId, leaseSeconds?, leaseExpiresAt?, serverTime?` | SessionAndLeaseControl |
| server → agent | `LOCK` | `reason?` | SessionAndLeaseControl |
| server → agent | `END_SESSION` | `sessionId?, reason?` | SessionAndLeaseControl |
| server → agent | `LAUNCH_GAME` | `gameId` | GamesHandling |
| server → agent | `SHUTDOWN` | `action?, delaySeconds?, reason?` | SessionCommandsAndSystem |
| server → agent | `POLICY_UPDATE` | partial `StationPolicy` | PolicyStore |
| server → agent | `CATALOG_UPDATE` ⚠ OPEN | `{}` (the agent then pulls the catalog) | GamesHandling |
| agent → server (REST, before the WSS link) | `POST /enrollment/request` | `oneTimeToken, mac, ip` + ⚠ OPEN `serialNumber, machineName, agentVersion, agentPublicKey, signedAt, signature` | Enrollment |
| agent → server (REST, station JWT) | `GET /stations/me/games` ⚠ OPEN | → `{ games: [{ gameId, name, launchType, target, arguments?, workingDirectory?, processName? }] }` | GamesHandling |

⚠ OPEN items are proposals awaiting confirmation from backend member C (skill document §15).

---

## 5. Startup Sequence

```text
BaronDeskAgent.ServiceCore.exe
      │
      ├── --set/--clear-enrollment-token, --set/--clear-station-token ?  ──► provisioning only, exit
      │
      ├── Build host, validate AgentOptions (unsafe TLS / plain-text token → stop)
      │
      ├── InitializeLocalStateAsync
      │     ├── secure data directory + SQLite migrations
      │     ├── Development only: seed 'charmap'
      │     ├── load + validate station policy
      │     ├── load handled command ids
      │     └── start StationController (lease watchdog)
      │
      └── Run hosted workers
            ├── PipeServer            (lock screen IPC + watchdog)
            ├── ConnectionWorker      (first run: enroll → connect → handshake → state_report → receive)
            ├── HeartbeatWorker
            ├── OutboxWorker
            ├── HardwareMonitorService
            └── UsbMonitorService
```

The station is **locked from the first instant** (`LockService` and the LockUI both start locked).

---

## 6. Configuration

Static settings live in the `Agent` section of `appsettings.json`, validated at startup:

| Key | Notes |
|---|---|
| `ServerUrl` | `wss://…/agent-ws`; `ws://` allowed only in Development |
| `PinnedCertificateHash` | SHA-256 of the server certificate; **required** outside Development |
| `AllowUntrustedCertificate` | Development only |
| `SerialNumber` | Defaults to the machine name |
| `StationToken` | Development-only fallback; production uses DPAPI |
| `EnrollmentUrl` | Defaults to `https://<ServerUrl host>/enrollment/request`; `http://` only in Development |
| `EnrollmentToken` | Development-only fallback for the one-time token; production uses `--set-enrollment-token` (DPAPI) |
| `EnrollmentPollSeconds` | How often a `PENDING` enrollment asks again (default 15) |
| `LockUiExecutablePath` | Enables LockUI image verification and relaunch |
| `GameCatalogUrl` | Defaults to `https://<ServerUrl host>/stations/me/games`; `http://` only in Development |
| `AllowedGameDirectories` | Optional: `exe` games only launch from inside these folders (empty = any) |
| `ReconnectBaseDelaySeconds`, `ReconnectMaxDelaySeconds`, `KeepAliveIntervalSeconds`, `MaxTimestampDriftSeconds` | Connection tuning |

Everything the backend tunes at runtime (cadences, lease, thresholds, debounce, `StopGameOnLock`) is **station policy**, changed with `POLICY_UPDATE` and persisted (see `PolicyStore.md`).

---

## 7. Build, Test & Run

```powershell
dotnet build BaronDeskAgent.slnx          # 0 warnings (warnings are errors)
dotnet test BaronDeskAgent.slnx           # 166 tests

node tools/mock-server/mock-server.js
$env:DOTNET_ENVIRONMENT = "Development"; dotnet run --project src/BaronDeskAgent.ServiceCore
dotnet run --project src/BaronDesk.LockUI # covers the screen; the mock accepts PIN 1234
```

Production provisioning (elevated prompt):

```powershell
"<one-time token>" | BaronDeskAgent.ServiceCore.exe --set-enrollment-token   # enrolls on first start (Enrollment.md)
"<station JWT>" | BaronDeskAgent.ServiceCore.exe --set-station-token         # manual fallback
```

---

## Current Status

- [x] Branch 1 — Frozen wire contracts, source-generated JSON
- [x] Branch 2 — Pinned WSS link, handshake, anti-replay, dispatching
- [x] Branch 3 — Session, lease, heartbeat, `state_report`, fail-closed
- [x] Branch 5 — Lock UI overlay, locked-down named-pipe IPC, PIN relay
- [x] Branch 6 — Game catalog, session-aware launching, power commands
- [x] Game catalog delivery (agent side): HTTPS pull, `catalog_status`, Steam/Epic launching, `processName` tracking (contract OPEN with backend C, skill §15 item 9)
- [x] Branch 7 — Hardware alerts, USB anti-theft
- [x] Branch 8 — Policy store with live updates
- [x] Code review fixes (critical, high, medium) — see `ReviewFixes.md`
- [x] Branch 4 (part) — DPAPI credential store and provisioning command
- [x] Branch 4 — Enrollment flow: station key pair, signed first-contact request, approval polling (agent side; response shape OPEN with backend C)
- [ ] Branch 9 — Windows Service hosting, recovery, LockUI launch at sign-in
- [ ] Branch 10 — Benchmark report (`docs/benchmark/`)
