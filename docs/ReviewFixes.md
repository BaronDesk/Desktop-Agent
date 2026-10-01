# BaronDesk Agent : Code Review Fixes (September 2026)

This document lists what the September 2026 code review found in the Desktop Agent and how each finding was fixed. The current design of each area is described in its own document (see `Architecture.md` §3).

---

## Folder Structure Changes

```text
Before                                         After
BaronDeskAgent/                                src/
├── BaronDesk.Shared/                          ├── BaronDesk.Shared/
│   ├── Contracts/  (+ internal types)         │   ├── Contracts/   wire contracts only
│   └── Models/                                │   └── Ipc/         pipe contracts
├── BaronDeskAgent.ServiceCore/                ├── BaronDeskAgent.ServiceCore/
│   ├── Communication/  Security/              │   ├── Connection/  Commands/  Session/
│   ├── Data/{Database,Entities,Repositories}  │   ├── Games/  Power/  Telemetry/{Outbox}
│   ├── Hardware/                              │   ├── AntiTheft/  Policy/  Ipc/
│   └── Services/{Commands,Games,Outbox,       │   ├── Credentials/  Persistence/  Platform/
│        Policy,Session,System,Telemetry}      │   └── Configuration/
└── BaronDesk.LockUI/  (empty MVVM folders)    └── BaronDesk.LockUI/  Ipc/  Kiosk/
mock-server.js                                 tests/BaronDeskAgent.ServiceCore.Tests/
Docs/                                          tools/mock-server/mock-server.js
                                               docs/
```

---

## 1. Critical Findings

| # | Finding | Fix | Document |
|---|---|---|---|
| C1 | Any PIN unlocked an idle station (`ValidatePin` returned `true` when no PIN was expected), and the agent validated PINs itself, against golden rule 1. | PIN validation removed. PINs are relayed to the backend (`login_request`); only `UNLOCK` unlocks. Invariant *unlocked ⇒ valid lease* enforced every 5 s. | LockUIAndIPC, SessionAndLeaseControl |
| C2 | Any local user could open (and host instances of) the named pipe and send `SubmitPin`. | ACL = service + console user, no `CreateNewInstance`; `FirstPipeInstance` squatting detection; client session/image verified; LockUI verifies the server runs in Session 0. | LockUIAndIPC |
| C3 | Production `appsettings.json` disabled TLS validation. | Pinning mandatory outside Development; `AllowUntrustedCertificate` and `ws://` rejected at startup. | WebSocketConnection |
| C4 | Clock drift (no NTP offline) made every command `STALE`, while `heartbeat_ack` (not replay-checked) kept renewing; an expired lease was renewed with a full default lease. | `ServerClock` offset; every inbound frame replay-checked; `LOCK`/`END_SESSION` exempt from freshness only; lease from server-clock values, capped, never renewed when expired. | WebSocketConnection, SessionAndLeaseControl |

---

## 2. High Findings

| # | Finding | Fix | Document |
|---|---|---|---|
| H1 | Command ids recorded before execution, so failed commands were acked on redelivery. | Only successes recorded, persisted in SQLite (`HandledCommands`). | CommandsHandling |
| H2 | Three `seq` counters on one socket; `seq` assigned outside the send lock; outbox replayed old `seq`/`ts`. | `seq`/`ts` stamped inside the send lock; outbox stores payloads and keeps the message id. | WebSocketConnection, HardwareTelemetry |
| H3 | Clean close → immediate reconnect; backoff reset on every TCP connect; one malformed frame dropped the link; unbounded frames. | Always back off; reset after 30 s stable; malformed frames ignored; 64 KiB cap; binary rejected. | WebSocketConnection |
| H4 | Outbox hot-looped offline, died on errors, poison messages blocked it, unbounded. | Waits for a ready connection and a signal; retry delay; dead-letter; 1,000-row cap; never exits. | HardwareTelemetry |
| H5 | `LOCK`/`END_SESSION` acked without the overlay being shown; no LockUI watchdog; LockUI never failed closed. | Correlated confirmations; `EXEC_FAILED` when unconfirmed; watchdog alert + relaunch; LockUI locks itself 15 s after losing the service. | LockUIAndIPC |
| H6 | `LOCK` could not cover a fullscreen game; new monitors not covered. | `StopGameOnLock` policy (default on); overlay re-sized on display changes. | GamesHandling, SessionCommandsAndSystem, LockUIAndIPC |
| H7 | Game launch fell back to `Process.Start` as SYSTEM in Session 0; double launch; writable data directory. | No Session-0 fallback; PID resolved while the handle is open; data directory locked down. | GamesHandling, LocalStorage |
| H8 | Races: USB debounce dictionary on two threads; lock/unlock vs overlay; async multicast event. | Single-reader USB loop; `LockService` serializes state + overlay; events removed. | TelemetryAlerts, SessionAndLeaseControl |
| H9 | A sensor failure stopped the whole host. | Sensor failures isolated; the agent keeps running. | HardwareTelemetry |
| H10 | Traffic could precede the handshake; `state_report` waited for a non-frozen `handshake_ack`. | `IsReady` gate; state report always sent; heartbeat right after reconnect. | WebSocketConnection, SessionAndLeaseControl |

---

## 3. Medium Findings

| # | Finding | Fix | Document |
|---|---|---|---|
| M1 | Agent invented session/game ids from arbitrary payloads. | Typed payloads; required fields → `INVALID_PAYLOAD`. | CommandsHandling |
| M2 | New session silently replaced the old one; stale `END_SESSION` ended the new one. | Previous session ended first; mismatched `END_SESSION` is a no-op. | SessionAndLeaseControl |
| M3 | Culture-dependent parsing (fails on fr-FR), `NaN` passed validation, empty/unknown updates acked. | Strict source-generated parsing; unknown fields rejected; finite ranges; empty → `INVALID_PAYLOAD`. | PolicyStore |
| M4 | Socket leaked per failed connect; bad pin hash retried forever. | Socket disposed on failure; hash validated at startup. | WebSocketConnection |
| M5 | USB alerts for every device, gamers' own devices, locale-specific hub filter. | Wired USB HID by class; baseline at startup/idle; composite devices grouped. | TelemetryAlerts |
| M6 | Load/RAM alerts every minute during play; full telemetry every tick. | 60 s sustain + hysteresis; delta filter; adaptive cadence. | TelemetryAlerts, HardwareTelemetry |
| M7 | Info logs every 5 s. | Per-frame logs at Debug. | HardwareTelemetry |
| M8 | LockUI leaked a keep-alive loop per reconnect; stuck "Verifying PIN"; no "service unavailable". | Per-connection cancellation; login timeout; availability state. | LockUIAndIPC |
| M9 | Policy defaults duplicated 4×, `AgentOptions` values ignored; Notepad seeded in production; no schema versioning. | Single defaults source; Development-only seed; `PRAGMA user_version` migrations. | PolicyStore, LocalStorage |

### Also Fixed While Rewriting

- LibreHardwareMonitor sub-hardware (fan chips) was never updated.
- The pipe server blocked its read loop while waiting for the login verdict.
- A heartbeat now follows every reconnect immediately (found during the end-to-end run).
- Task Manager was never actually disabled: standard users can only read their own `HKCU\Software\Microsoft\Windows\CurrentVersion\Policies` key, so the LockUI's write always failed silently. The service (LocalSystem) now writes `DisableTaskMgr` into the console user's hive.
- `LOCK` / `END_SESSION` nacks now say *why* the overlay was not confirmed (`HelperNotConnected` vs `NotConfirmed`); the old text read like "already locked".

---

## 4. Additions After the Review

| Addition | Document |
|---|---|
| DPAPI station credential store (`LocalMachine`, protected file ACL) and `--set-station-token` provisioning | CredentialStore |
| Plain-text `StationToken` rejected outside Development | CredentialStore |
| 77 unit / integration tests (`tests/BaronDeskAgent.ServiceCore.Tests`) | each document's testing section |
| Mock server: TLS mode, lease terms, login relay, drift/malformed/replay scenarios, `MOCK_AUTO_RELOCK_SECONDS` | WebSocketConnection §11 |
| LockUI windowed test mode (`--windowed`, Debug builds only) | LockUIAndIPC |

---

## 5. Contract items with the backend

Checked against the backend on 2026-10-01 (backend `docs/STATION_AGENT.md`, `docs/FLOW_FIXES.md`):

- [x] `login_request` / `login_result` message shapes (skill §15 item 2)
- [x] `handshake_ack` (`{}`) / `heartbeat_ack` (`{ leaseSeconds, serverTime }`) payloads and lease terms (item 3)
- [x] Station credential transport: the station JWT in the upgrade's `Authorization` header, renewed by `station_credential` (item 1)
- [ ] The enrollment answer shape: agent side built, see `Enrollment.md` §4 (item 8, roadmap branch 4)
- [x] Game catalog delivery: `GET /stations/me/games`, `CATALOG_UPDATE`, `catalog_status` (item 9)
- [ ] Final lease duration, grace, cadences and thresholds (item 10)
- [ ] `END_SESSION` semantics (item 12) and whether `LOCK` should stop the game (`StopGameOnLock`, default on)
- [x] Alert types `DEVICE_REMOVED`, `LOCK_SCREEN_MISSING`, `IPC_TAMPERING`: the backend stores `type` as sent (free text)
- [x] `peripheral_status` and `state_report.peripherals`: stored on the machine, shown on the staff station page
- [x] `session_notice` (low balance / booking ending), see `LockUIAndIPC.md` §4b
- [x] `installed_games` (catalog suggestions), see `GamesHandling.md` §2.8
- [x] `station_credential` (token renewal), see `CredentialStore.md`

---

## Current Status

- [x] All critical, high and medium findings fixed
- [x] `dotnet build` with 0 warnings (warnings as errors), 77 tests passing
- [x] End-to-end run against the mock server (handshake, commands, drift, replay, malformed frames, reconnect)
- [ ] OPEN contract items above confirmed with the backend (left: the enrollment answer shape, lease durations and cadences, `END_SESSION` / `StopGameOnLock`)
