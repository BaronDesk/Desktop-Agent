# BaronDesk Agent : Security and Quality Review

In September 2026 the Desktop Agent went through a full code review focused on security, reliability and footprint. This document lists every finding, by severity, with the fix that was applied and the document that describes the resulting design. The current design of each area is described in its own document (see `Architecture.md` §3).

---

## 1. Critical Findings

| # | Finding | Fix | Document |
|---|---|---|---|
| C1 | Any PIN unlocked an idle station (`ValidatePin` returned `true` when no PIN was expected), and the agent validated PINs itself, against design principle 1 ("the agent decides nothing"). | PIN validation removed from the agent. PINs are relayed to the backend (`login_request`); only `UNLOCK` unlocks. The invariant *unlocked ⇒ valid lease* is enforced every 5 s. | LockUIAndIPC, SessionAndLeaseControl |
| C2 | Any local user could open (and host instances of) the named pipe and send `SubmitPin`. | ACL = service + console user, no `CreateNewInstance`; `FirstPipeInstance` squatting detection; client session and image verified; the LockUI verifies that the server runs in Session 0. | LockUIAndIPC |
| C3 | The production `appsettings.json` disabled TLS validation. | Certificate pinning mandatory outside Development; `AllowUntrustedCertificate` and `ws://` rejected at startup. | WebSocketConnection |
| C4 | Clock drift (no NTP offline) made every command `STALE`, while `heartbeat_ack` (not replay-checked) kept renewing the lease; an expired lease was renewed with a full default lease. | `ServerClock` offset; every inbound frame replay-checked; `LOCK`/`END_SESSION` exempt from freshness only; lease computed from server-clock values, capped, never renewed once expired. | WebSocketConnection, SessionAndLeaseControl |

---

## 2. High Findings

| # | Finding | Fix | Document |
|---|---|---|---|
| H1 | Command ids were recorded before execution, so failed commands were acked on redelivery. | Only successes are recorded, persisted in SQLite (`HandledCommands`). | CommandsHandling |
| H2 | Three `seq` counters on one socket; `seq` assigned outside the send lock; the outbox replayed old `seq`/`ts`. | `seq`/`ts` stamped inside the send lock; the outbox stores payloads and keeps the message id. | WebSocketConnection, HardwareTelemetry |
| H3 | A clean close caused an immediate reconnect; backoff reset on every TCP connect; one malformed frame dropped the link; frames were unbounded. | Always back off; reset after 30 s of stable connection; malformed frames ignored; 64 KiB cap; binary frames rejected. | WebSocketConnection |
| H4 | The outbox hot-looped offline, died on errors, could be blocked by a poison message, and was unbounded. | Waits for a ready connection and a signal; retry delay; dead-letter; 1,000-row cap; never exits. | HardwareTelemetry |
| H5 | `LOCK`/`END_SESSION` were acked without the overlay being shown; no LockUI watchdog; the LockUI never failed closed. | Correlated confirmations; `EXEC_FAILED` when unconfirmed; watchdog alert and relaunch; the LockUI locks itself 15 s after losing the service. | LockUIAndIPC |
| H6 | `LOCK` could not cover a fullscreen game; new monitors were not covered. | `StopGameOnLock` policy (default on); the overlay is resized on display changes. | GamesHandling, SessionCommandsAndSystem, LockUIAndIPC |
| H7 | Game launch fell back to `Process.Start` as SYSTEM in Session 0; double launch; writable data directory. | No Session-0 fallback; PID resolved while the handle is open; data directory locked down. | GamesHandling, LocalStorage |
| H8 | Races: the USB debounce dictionary on two threads; lock/unlock against the overlay; an async multicast event. | Single-reader USB loop; `LockService` serializes state and overlay; events removed. | TelemetryAlerts, SessionAndLeaseControl |
| H9 | A sensor failure stopped the whole host. | Sensor failures isolated; the agent keeps running. | HardwareTelemetry |
| H10 | Traffic could precede the handshake; `state_report` waited for an optional `handshake_ack`. | `IsReady` gate; state report always sent; heartbeat right after reconnect. | WebSocketConnection, SessionAndLeaseControl |

---

## 3. Medium Findings

| # | Finding | Fix | Document |
|---|---|---|---|
| M1 | The agent invented session/game ids from arbitrary payloads. | Typed payloads; missing required fields → `INVALID_PAYLOAD`. | CommandsHandling |
| M2 | A new session silently replaced the old one; a stale `END_SESSION` ended the new one. | The previous session is ended first; a mismatched `END_SESSION` is a no-op. | SessionAndLeaseControl |
| M3 | Culture-dependent parsing (failed on fr-FR), `NaN` passed validation, empty or unknown updates were acked. | Strict source-generated parsing; unknown fields rejected; finite ranges; empty update → `INVALID_PAYLOAD`. | PolicyStore |
| M4 | A socket leaked per failed connect; a bad pin hash was retried forever. | Socket disposed on failure; hash validated at startup. | WebSocketConnection |
| M5 | USB alerts for every device, including gamers' own; a locale-specific hub filter. | Wired USB HID selected by device class; baseline at startup and while idle; composite devices grouped. | TelemetryAlerts |
| M6 | Load/RAM alerts every minute during play; full telemetry on every tick. | 60 s sustain window + hysteresis; delta filter; adaptive cadence. | TelemetryAlerts, HardwareTelemetry |
| M7 | Information-level logs every 5 s. | Per-frame logs at Debug level. | HardwareTelemetry |
| M8 | The LockUI leaked a keep-alive loop per reconnect; could stay stuck on "Verifying PIN"; had no "service unavailable" state. | Per-connection cancellation; login timeout; availability state. | LockUIAndIPC |
| M9 | Policy defaults duplicated four times, `AgentOptions` values ignored; Notepad seeded in production; no schema versioning. | Single source of defaults; Development-only seed; `PRAGMA user_version` migrations. | PolicyStore, LocalStorage |

### Also Fixed During the Rewrite

- LibreHardwareMonitor sub-hardware (fan chips) was never updated.
- The pipe server blocked its read loop while waiting for the login verdict.
- A heartbeat now follows every reconnect immediately (found during the end-to-end run).
- Task Manager was never actually disabled: standard users can only read their own `HKCU\Software\Microsoft\Windows\CurrentVersion\Policies` key, so the LockUI's write always failed silently. The service (LocalSystem) now writes `DisableTaskMgr` into the console user's hive.
- `LOCK` / `END_SESSION` nacks now say *why* the overlay was not confirmed (`HelperNotConnected` vs `NotConfirmed`).

---

## 4. Added After the Review

| Addition | Document |
|---|---|
| DPAPI station credential store (`LocalMachine`, protected file ACL) and `--set-station-token` provisioning | CredentialStore |
| Plain-text `StationToken` rejected outside Development | CredentialStore |
| Signed first-contact enrollment with a per-station ECDSA key pair | Enrollment |
| Automatic renewal of the station credential (`station_credential`) | CredentialStore |
| Launcher-aware game catalog (executables, Steam, Epic) and installed-game discovery | GamesHandling |
| In-session notices on the station (low balance, booking ending) | LockUIAndIPC |
| Peripheral connection status and specific anti-theft / security alert types | TelemetryAlerts |
| Mock server: TLS mode, lease terms, login relay, enrollment, drift/malformed/replay scenarios, `MOCK_AUTO_RELOCK_SECONDS` | WebSocketConnection §11 |
| LockUI windowed test mode (`--windowed`, Debug builds only) | LockUIAndIPC |

---

## 5. Outcome

- All critical, high and medium findings are fixed.
- `dotnet build` succeeds with 0 warnings (warnings are errors); the 193 unit and integration tests pass.
- The agent was run end to end against the mock server (handshake, commands, drift, replay, malformed frames, reconnect). Test scenarios on a real PC against the BaronDesk backend are described in the backend repository (`docs/STATION_PHYSICAL_TEST.md`).
