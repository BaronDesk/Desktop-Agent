# BaronDesk Desktop Agent

C# / .NET 10 agent that runs on every gaming PC of a BaronDesk venue. It executes backend commands over a pinned WSS channel, enforces the paid-access lock screen, streams hardware telemetry and raises hardware / anti-theft alerts, with a minimal footprint.

- **Design:** [docs/Architecture.md](docs/Architecture.md) (overview and index of every component document)
- **Review fixes and open contract items:** [docs/ReviewFixes.md](docs/ReviewFixes.md)

---

## Solution Layout

```text
Desktop-Agent
├── BaronDeskAgent.slnx                  open this in Visual Studio / Rider
├── src
│   ├── BaronDesk.Shared                 wire + IPC contracts
│   ├── BaronDeskAgent.ServiceCore       the agent (Session 0 service core)
│   └── BaronDesk.LockUI                 WPF lock overlay (user session)
├── tests
│   └── BaronDeskAgent.ServiceCore.Tests unit / integration tests
├── tools
│   └── mock-server                      mock /agent-ws backend (Node.js, no dependencies)
└── docs
```

---

# Local Testing Guide

This guide takes you through testing everything on your own PC, from the automated tests to the lock screen, alerts, TLS pinning and the DPAPI credential. Each step says what to do and what you should see.

## Contents

0. [Prerequisites](#0-prerequisites)
1. [Build and run the automated tests](#1-build-and-run-the-automated-tests)
2. [Start the mock server](#2-start-the-mock-server)
3. [Start the agent](#3-start-the-agent)
4. [Test the connection and protocol security](#4-test-the-connection-and-protocol-security)
5. [Test sessions, games, lease and fail-closed](#5-test-sessions-games-lease-and-fail-closed)
6. [Test the lock screen (LockUI)](#6-test-the-lock-screen-lockui)
7. [Test telemetry, alerts and the outbox](#7-test-telemetry-alerts-and-the-outbox)
8. [Test USB anti-theft](#8-test-usb-anti-theft)
9. [Test policy updates](#9-test-policy-updates)
10. [Test TLS certificate pinning and production validation](#10-test-tls-certificate-pinning-and-production-validation)
11. [Test the station credential (DPAPI)](#11-test-the-station-credential-dpapi) and [enrollment](#11b-test-enrollment-first-connection)
12. [Troubleshooting](#12-troubleshooting)
13. [Final checklist](#13-final-checklist)

---

## 0. Prerequisites

| Tool | Version | Used for | Check |
|---|---|---|---|
| Windows 10 / 11 | x64 | everything (the agent is Windows-only) | — |
| .NET SDK | 10.0 | build, tests, run | `dotnet --list-sdks` |
| Node.js | 18+ (tested with 22) | mock backend server | `node --version` |
| OpenSSL | any 3.x (ships with **Git for Windows**) | TLS pinning test (step 10) | `openssl version` in Git Bash |
| Administrator terminal | — | CPU temperatures (optional), DPAPI test (step 11) | — |
| A wired USB mouse or keyboard | — | anti-theft test (step 8) | — |

**Recommended:** test the lock screen (step 6) in a **virtual machine** or on a spare PC. Its kiosk mode covers every monitor and blocks the Windows key and Alt+Tab. The guide explains how to get out, and step 6A shows a safe windowed test mode first.

All commands below are **PowerShell**, run from the `Desktop-Agent` folder, unless marked *Git Bash*.

---

## 1. Build and Run the Automated Tests

```powershell
dotnet build BaronDeskAgent.slnx
dotnet test BaronDeskAgent.slnx
```

**Expected:**

```text
La génération a réussi. / Build succeeded.
    0 Warning(s)
    0 Error(s)
Passed!  - Failed: 0, Passed: 189, Skipped: 0, Total: 189
```

Warnings are treated as errors, so any warning fails the build.

What the 189 tests cover:

| Test class | Covers |
|---|---|
| `ReplayGuardTests` | sequence replay, stale timestamps, server clock offset, restrictive-command exemption |
| `ReconnectBackoffTests` | exponential backoff with jitter stays within bounds |
| `AgentOptionsValidatorTests` | pin required in production, no untrusted TLS, no plain-text station/enrollment token, https enrollment and game catalog endpoints, allowed game folders |
| `CommandDispatcherTests` | allow-list, idempotency (only successes recorded), STALE, early SHUTDOWN ack |
| `CommandPayloadTests` | strict parsing: no culture-dependent numbers, unknown policy fields rejected |
| `StationControllerTests` | unlock, lease expiry fail-closed, renewals, LOCK / END_SESSION semantics |
| `LeaseManagerTests` | lease states, cap, server-clock-only duration |
| `StationPolicyTests` | defaults, bounds, `NaN`, heartbeat ≤ half lease |
| `HardwareAlertEvaluatorTests` | hysteresis, 60 s sustained load, severity |
| `TelemetryDeltaFilterTests` | only changes sent, full snapshot before the 30 s backend TTL |
| `PipeLineReaderTests` | IPC messages capped at 4 KiB |
| `DpapiStationCredentialStoreTests` | encryption, file ACL, fail-closed on corrupt blobs, never logged |
| `DpapiStationKeyStoreTests` | station key pair persists, never in plain text, unreadable key → new identity |
| `EnrollmentServiceTests` | first-contact enrollment: signed request, PENDING polling, ENROLLED / REJECTED, retries, tokens never logged |
| `GameCatalogValidatorTests` | every catalog field checked: exe paths, Steam app ids, Epic AppNames, arguments, reserved process names |
| `GameLaunchResolverTests` | exe / Steam / Epic command lines, "not installed" reasons without paths, allowed game folders (incl. `..` traversal) |
| `GameLibrariesTests` | Steam `libraryfolders.vdf`, Epic manifests, protocol-handler command parsing |
| `GameCatalogRepositoryTests` | launcher fields round trip, `ReplaceAllAsync`, migration v3 keeps old rows |
| `GameCatalogServiceTests` | sync replaces the catalog, bad entries skipped and reported, `catalog_status`, `CATALOG_UPDATE` and reconnect trigger a sync |
| `GameServiceTests` | unknown / not-installed games refused before anything is stopped |
| `InstalledGameScannerTests` | Steam / Epic manifests: installed games found, partial installs, tools and non-games skipped, `inCatalog` matching |
| `SessionNoticesTests` | `session_notice`: countdown from the server clock, stale sessions and unknown kinds ignored, message cap |
| `PeripheralStatusContractTests` | `peripheral_status` / `state_report.peripherals` wire shape, registry snapshot |

Run a single class:

```powershell
dotnet test BaronDeskAgent.slnx --filter "FullyQualifiedName~StationControllerTests"
```

---

## 2. Start the Mock Server

The mock plays the venue backend on `ws://127.0.0.1:8443/agent-ws`. **Terminal A:**

```powershell
node tools/mock-server/mock-server.js
```

**Expected:**

```text
[READY] BaronDesk Mock WebSocket Server listening on ws://127.0.0.1:8443/agent-ws
Waiting for BaronDesk Agent to connect...
```

Type a menu key and press **Enter** to send something to the agent:

| Key | Sends | Expected agent reaction |
|---|---|---|
| `1` | `LOCK` | locks. With the LockUI running: `COMMAND_ACK`. **Without it: `COMMAND_NACK EXEC_FAILED` "…the lock screen app (LockUI) is not running…"**. This is expected: the station *is* locked, but nothing is shown on screen, so the agent refuses to tell the backend it is. |
| `2` | `UNLOCK` with a new session and a 60 s lease | unlocks, `command_ack` |
| `3` | `LAUNCH_GAME` `charmap` | opens Character Map, the test game (only when unlocked) |
| `l` | `LAUNCH_GAME` `calc` | opens Calculator; `calc.exe` exits at once and the agent follows `CalculatorApp` by `processName` |
| `s` | `LAUNCH_GAME` `cs2` (Steam) | `COMMAND_NACK EXEC_FAILED` "…not installed…" unless CS2 is installed; otherwise Steam starts it |
| `c` | `CATALOG_UPDATE` | `command_ack`, then the agent downloads the catalog and sends `catalog_status` |
| `4` | `END_SESSION` for the current session | closes the running game, locks |
| `5` | valid `POLICY_UPDATE` | `command_ack` |
| `6` | `SHUTDOWN` | ⚠ **really shuts your PC down** after 2 s: don't press it on your own machine |
| `7` | `LOCK` with a reused `seq` | nack `STALE` |
| `8` | unknown command type | nack `UNKNOWN_TYPE` |
| `9` | `UNLOCK` stamped 10 minutes ago | nack `STALE` |
| `0` | `LOCK` stamped 10 minutes ago | executed anyway (locking is always safe) |
| `m` | malformed JSON frame | ignored, connection kept |
| `p` | `POLICY_UPDATE` with a typo in a field name | nack `INVALID_PAYLOAD` |
| `a` | enable USB anti-theft alerts (5 s debounce) | `command_ack` |
| `t` | temperature thresholds 30 °C | `TEMPERATURE_WARNING` alerts |
| `r` | restore temperature thresholds 85 °C | `command_ack` |
| `w` | `session_notice` LOW_BALANCE, 3 min left (press `2` first) | with the LockUI running: amber "Low balance" box bottom-right, counting down |
| `k` | `session_notice` CLEAR | the box disappears |
| `q` | quit the mock | agent reconnects with backoff |

The mock also plays the login server: **PIN `1234` is accepted**, anything else is rejected.

Optional: `$env:MOCK_AUTO_RELOCK_SECONDS = "20"` before starting the mock makes it send `LOCK` automatically 20 s after every acknowledged `UNLOCK`. This is useful for the fullscreen lock screen, which covers this terminal (step 6B).

---

## 3. Start the Agent

**Terminal B:**

```powershell
dotnet run --project src/BaronDeskAgent.ServiceCore
```

`dotnet run` uses `Properties/launchSettings.json`, which sets `DOTNET_ENVIRONMENT=Development` (mock URL `ws://…`, untrusted certificates allowed, `charmap` (Character Map) seeded as a test game until the first catalog sync).

**Expected in the agent (terminal B):**

```text
warn: … AllowUntrustedCertificate is on: server certificates are NOT validated (Development only).
info: … Loaded station policy from SQLite: StationPolicy { … }
info: … Connecting to ws://127.0.0.1:8443/agent-ws...
info: … Connected to ws://127.0.0.1:8443/agent-ws.
info: … Handshake sent (serial STATION-DEV-01, agent 1.0.0+…).
info: … State report sent: StateReportPayload { Locked = True, SessionId = , … }
info: … Handshake acknowledged; server clock offset 00:00:00.0…
```

**Expected in the mock (terminal A)**, in this order, with `Seq` 1, 2, 3…:

```text
[CONNECTED] Agent connected from 127.0.0.1
[AUTH] No station credential presented
[AGENT -> SERVER] Type: handshake    | Seq: 1
[AGENT -> SERVER] Type: state_report | Seq: 2
[AGENT -> SERVER] Type: heartbeat    | Seq: 3      ← sent immediately after connecting
[AGENT -> SERVER] Type: telemetry    | Seq: 4
```

Then a `heartbeat` every 15 s (each answered by a `heartbeat_ack`), and `telemetry` only when values change.

> **Tip:** run terminal B **as Administrator** to get CPU temperatures. Without admin rights, LibreHardwareMonitor cannot read the CPU sensors; GPU, RAM and load still work.
>
> Local data lives in `C:\ProgramData\BaronDeskAgent\Data\agent.sqlite`.

---

## 4. Test the Connection and Protocol Security

With the mock (A) and the agent (B) running, type in terminal A:

| Step | Key | Expected in the mock | Expected in the agent |
|---|---|---|---|
| 4.1 | `8` | `[COMMAND_NACK] … Code: UNKNOWN_TYPE` | `Rejected message type UNKNOWN_TEST` |
| 4.2 | `7` | `Code: STALE, Reason: Sequence 1 is not greater than …` | `Rejected LOCK …: Sequence 1 …` |
| 4.3 | `9` | `Code: STALE, Reason: Timestamp is 599.9s away from the server clock` | `Rejected UNLOCK …` |
| 4.4 | `0` | `COMMAND_ACK`, or `COMMAND_NACK EXEC_FAILED` without LockUI | `LOCK requested (drift_test)`: executed despite the old timestamp |
| 4.5 | `m` | `Sent a malformed frame.` | `Ignored a malformed frame: Invalid envelope JSON …`, **still connected** |
| 4.6 | `q`, then restart the mock | — | `Connection to the server failed` → `Reconnecting in 1.x s (attempt 1)`, `2.x s (attempt 2)`, … then reconnects on its own |

**What this proves:** only the six allowed commands run; replayed or stale commands are refused, except restrictive ones (a drifted clock can never keep a station unlocked); a bad frame doesn't kill the link; reconnects back off instead of hammering the server.

---

## 5. Test Sessions, Games, Lease and Fail-Closed

Keep the LockUI **closed** for this step, so you can keep typing in the terminals.

| Step | Action | Expected |
|---|---|---|
| 5.0 | (on connect, nothing to type) | Mock: `[CATALOG] GET /stations/me/games`, then `[CATALOG_STATUS] 2/6 game(s) launchable`: `charmap`, `calc` installed; `cs2`, `fortnite`, `missing` not launchable with a reason; `invalid` rejected. Then `[INSTALLED_GAMES]`: the Steam / Epic games installed on this PC, each `in catalog` or `not in catalog`. Needs a station credential: without one the mock answers 401, the agent retries with backoff, and the seeded `charmap` still works. |
| 5.0b | Mock: `c` (CATALOG_UPDATE) | `COMMAND_ACK`, then a second `[CATALOG]` download and `[CATALOG_STATUS]` |
| 5.1 | Mock: `3` (game while locked) | `COMMAND_NACK EXEC_FAILED`: "The station must be unlocked with an active session to launch a game." |
| 5.2 | Mock: `2` (UNLOCK) | `COMMAND_ACK`; agent: `Session … is active.` and `Station UNLOCKED` |
| 5.3 | Mock: `3` (LAUNCH_GAME charmap) | Character Map opens on your desktop, `COMMAND_ACK` |
| 5.4 | Mock: `3` again | No second Character Map; agent: `Game charmap is already running.` |
| 5.4b | Mock: `s` (LAUNCH_GAME cs2) | Without CS2 installed: `COMMAND_NACK EXEC_FAILED` "…cannot be launched on this station: The game is not installed in any Steam library…", and **Character Map keeps running** (checked before stopping the current game) |
| 5.4c | Mock: `l` (LAUNCH_GAME calc) | Character Map closes, Calculator opens (close any Calculator of your own first: session end closes every `CalculatorApp`). `calc.exe` exits at once, but the agent follows `CalculatorApp` by `processName`: `l` again does not open a second one |
| 5.5 | Mock: `4` (END_SESSION) | The running game (Calculator) closes; agent: `Station LOCKED (overlay: HelperNotConnected)` and `Session … ended (user_logout)`. The mock shows `EXEC_FAILED` "Session ended. The station is now locked, but the lock screen app (LockUI) is not running…"; with the LockUI running it is `COMMAND_ACK`. |
| 5.5b | Mock: `2`, then `1` (LOCK) | Same as 5.5: the agent locks (`Station LOCKED (overlay: HelperNotConnected)`) but answers `EXEC_FAILED` because no LockUI shows the overlay. In step 6 the same LOCK is acknowledged. |
| 5.6 | Mock: `2` (UNLOCK), wait for one heartbeat, then `q` (backend goes down) | Agent keeps the session for the lease (60 s) + grace (10 s). After about **60–75 s**: `Station is unlocked without a valid lease (Expired). Failing closed.` then `Station LOCKED` and `Session … ended (lease_expired)`. |
| 5.7 | Restart the mock | Agent reconnects; the mock shows a `state_report` with `Locked: true`, `SessionId: none` |

**What this proves:** the catalog comes from the backend and the station reports what it can launch; games only launch for an active session; a request for a missing game never kills the running one; games that start through a stub or launcher are still tracked; teardown stops the game; a network outage longer than the lease locks the station (fail closed), while a short blip would not.

---

## 6. Test the Lock Screen (LockUI)

The LockUI can run in two modes. Start with **6A**: it tests the whole lock/unlock/PIN flow while your terminals stay usable. Then do **6B** to test the real kiosk behaviour.

| Mode | Command | Covers the screen | Blocks keys | Use it for |
|---|---|---|---|---|
| **6A. Windowed test mode** (Debug builds only) | `dotnet run --project src/BaronDesk.LockUI -- --windowed` | No, normal window | No | IPC, PIN relay, LOCK/UNLOCK acks, "service unavailable", fail-closed |
| **6B. Kiosk mode** (the real one) | `dotnet run --project src/BaronDesk.LockUI` | Yes, every monitor | Yes | Keyboard blocking, fullscreen, multi-monitor |

`--windowed` is ignored in Release builds. Otherwise a gamer could start a windowed instance first, take the single-instance slot, and receive "lock" in a window they can simply move away.

### 6A. Windowed Test Mode

Start the mock (A) and the agent (B), then **terminal C:**

```powershell
dotnet run --project src/BaronDesk.LockUI -- --windowed
```

A normal window titled **"BaronDesk LockUI — TEST MODE — LOCKED"** appears, with a coloured banner showing the state. In the real mode the window would be hidden when unlocked; here it stays visible and switches to **UNLOCKED**, so you can watch both transitions.

| Step | Action | Expected |
|---|---|---|
| 6A.1 | LockUI starts | Banner "state: LOCKED"; the status line clears once the pipe is connected and the server reachable. Agent: `LockUI helper connected.` |
| 6A.2 | Type `0000`, press Enter | "Checking your PIN…" → "Incorrect PIN. Please try again." (the **backend** rejected it; the agent never checks PINs) |
| 6A.3 | Type a wrong PIN 5 times, then try once more | The 6th attempt shows "Too many attempts. Try again in 30s." and the button is disabled for 30 s |
| 6A.4 | Type `1234`, press Enter | "PIN accepted — unlocking…", banner switches to **UNLOCKED**. Mock: `[LOGIN_REQUEST] method=pin -> ACCEPTED`, `UNLOCK`, `COMMAND_ACK` |
| 6A.5 | Mock: `1` (LOCK) | Banner back to **LOCKED**; the mock gets **`COMMAND_ACK`** (compare with step 5, where it was `EXEC_FAILED` without the LockUI). Agent: `Station LOCKED (overlay: Confirmed).` |
| 6A.6 | Mock: `2` (UNLOCK from the dashboard), then `3` (Character Map), then `1` (LOCK) | UNLOCKED → Character Map opens → LOCKED **and Character Map closes** (`StopGameOnLock`: an overlay cannot cover a fullscreen game) |
| 6A.7 | Unlocked: stop the **mock** (`q`) | Status "Service unavailable — please ask the staff for help."; after ~60–75 s (lease + grace) the banner switches to **LOCKED**, and the PIN box stays disabled: **no new sessions offline**. Restart the mock to log in again. |
| 6A.8 | Unlocked: stop the **agent** (Ctrl+C in terminal B) | After **15 s** the LockUI switches to **LOCKED** by itself (without the service nothing enforces the lease). Restart the agent: it reconnects and stays locked. |
| 6A.9 | Start a second LockUI in another terminal | It exits immediately: one lock screen per session |
| 6A.10 | Close the window (✕) | Allowed in test mode only; the real lock screen cannot be closed while locked |

> **Agent warning `Task Manager policy could not be applied … must run as LocalSystem or an administrator`** is expected here. Standard users cannot write their own `Policies` registry key, so disabling Task Manager is done by the service, which runs as LocalSystem in production. To see it work, run terminal B as Administrator: Task Manager is then refused while locked and available again after unlocking.

### 6B. Kiosk Mode (Real Lock Screen)

> ⚠ **Before you start — how to get out.**
> The overlay covers every monitor and blocks the Windows key, Alt+Tab and Alt+F4, and **you cannot type in your terminals while it is shown**.
>
> - **Normal way out:** type PIN **`1234`** and press Enter (mock and agent must be running).
> - **Stuck on "Service unavailable"** (the agent stopped, so the PIN box is disabled): press **Ctrl+Alt+Shift+F12**. The LockUI closes. Debug builds only; the combination does nothing in Release.
> - **Emergency:** press **Ctrl+Alt+Del → Sign out**. Ctrl+Alt+Del cannot be blocked from user mode.
> - Once it is **unlocked** (hidden), stop it from any terminal: `taskkill /IM BaronDesk.LockUI.exe /F`.
> - If the agent ran as Administrator and Task Manager stays disabled afterwards, from an **elevated** prompt: `reg delete HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System /v DisableTaskMgr /f`

Because the terminals are covered, start the mock with **auto-relock**: it sends `LOCK` by itself N seconds after every acknowledged `UNLOCK`, so you never need to type in the mock while testing:

```powershell
# terminal A
$env:MOCK_AUTO_RELOCK_SECONDS = "20"
node tools/mock-server/mock-server.js

# terminal B
dotnet run --project src/BaronDeskAgent.ServiceCore

# terminal C
dotnet run --project src/BaronDesk.LockUI
```

| Step | Action | Expected |
|---|---|---|
| 6B.1 | LockUI starts | Fullscreen lock screen on **all** monitors |
| 6B.2 | Press the Windows key, Alt+Tab, Alt+Esc, Alt+F4, Ctrl+Esc, Ctrl+Shift+Esc | Nothing happens |
| 6B.3 | Type `1234`, press Enter | The overlay disappears; you have your desktop back |
| 6B.4 | Wait 20 s | The mock's auto-relock sends `LOCK`: the overlay comes back by itself. Mock log (read it after unlocking): `[AUTO-RELOCK] Sending LOCK in 20s...` then `COMMAND_ACK` |
| 6B.5 | While locked, plug in or unplug a second monitor | The overlay resizes and covers the new layout |
| 6B.6 | Click elsewhere / try to switch windows | The overlay re-activates and stays on top |

When done: type `1234`, then within 20 s run `taskkill /IM BaronDesk.LockUI.exe /F`, and stop the mock (`q`) and the agent (Ctrl+C). Remove the variable with `Remove-Item Env:MOCK_AUTO_RELOCK_SECONDS`.

---

## 7. Test Telemetry, Alerts and the Outbox

| Step | Action | Expected |
|---|---|---|
| 7.1 | Watch the mock for a minute | First `[TELEMETRY] Received N changed sample(s)` lists every metric (full snapshot); later frames list only what changed (`cpu.load_percent`, …) |
| 7.2 | Mock: `t` (thresholds 30 °C) | Within **15 s** (idle sampling = 3 × the 5 s cadence): `[ALERT] hardware / TEMPERATURE_WARNING / CRITICAL: <GPU/CPU> temperature at 47.0 °C exceeded the 30.0 °C threshold.`. Only **once** per sensor: hysteresis prevents repeats until it cools below 25 °C. |
| 7.3 | Mock: `r` (restore 85 °C) | `COMMAND_ACK` |
| 7.4 | **Outbox test:** wait ~20 s after `r` (the alarms re-arm on the next sample), then press `t` and immediately `q` (mock down) | The agent logs `Hardware alert: CRITICAL TEMPERATURE_WARNING …` while offline; the alert is stored in SQLite, not lost |
| 7.5 | Restart the mock, wait for the reconnect | The buffered `[ALERT]` arrives right after the handshake. Then press `r`. |

**What this proves:** telemetry is light and on-change; alerts do not storm; alerts raised offline survive and are delivered in order after reconnecting.

> Sustained CPU/RAM alerts need load ≥ 95 % for **60 s** (normal gaming spikes do not alert). Covered by `HardwareAlertEvaluatorTests`.

---

## 8. Test USB Anti-Theft

Requires a **wired** USB mouse or keyboard that was plugged in **before** the agent started. Keep the station locked, without a session.

| Step | Action | Expected |
|---|---|---|
| 8.1 | Mock: `a` (enable anti-theft, 5 s debounce) | `COMMAND_ACK` |
| 8.2 | Unplug the USB mouse, **replug within 5 s** | No alert (cable flap); agent: `<device> reconnected within the debounce window.` Mock: two `[PERIPHERAL_STATUS]` frames, `DISCONNECTED` then `connected` |
| 8.3 | Unplug it and **wait 6 s** | Mock: `[ALERT] anti_theft / DEVICE_REMOVED / CRITICAL: Peripheral removed and not reconnected: <name> (VID_…&PID_…).` |
| 8.4 | Replug it | Watched again, no alert; mock `[PERIPHERAL_STATUS]` shows it `connected` (the backend can resolve the alert) |
| 8.5 | Mock `2` (UNLOCK, a session starts), plug in a **new** USB device, then unplug it | No alert: devices brought during a session belong to the gamer. (A device plugged in while the station is idle joins the watched equipment.) |

Scope: wired USB HID devices only; Bluetooth and wireless dongles are not detected. This is a documented limit (see `docs/TelemetryAlerts.md`).

---

## 9. Test Policy Updates

| Step | Action | Expected |
|---|---|---|
| 9.1 | Mock: `5` | `COMMAND_ACK`; agent: `Station policy updated: StationPolicy { … HeartbeatIntervalSeconds = 15, TelemetryCadenceSeconds = 5 … }` |
| 9.2 | Mock: `p` (typo `heartbeatIntervalSecs`) | `COMMAND_NACK INVALID_PAYLOAD: … UnmappedJsonProperty, heartbeatIntervalSecs …`; policy unchanged |
| 9.3 | Restart the agent | `Loaded station policy from SQLite: …` shows the last accepted values (they persist) |

Out-of-range values, `NaN`, strings instead of numbers and an empty update are all rejected with `INVALID_PAYLOAD` (see `CommandPayloadTests` and `StationPolicyTests`).

---

## 10. Test TLS Certificate Pinning and Production Validation

### 10.1 Create a test certificate (*Git Bash*)

```bash
cd tools/mock-server
openssl req -x509 -newkey rsa:2048 -nodes -keyout key.pem -out cert.pem -days 30 \
  -subj "/CN=127.0.0.1" -addext "subjectAltName=IP:127.0.0.1"
```

> On Git Bash, if `-subj` gets mangled into a path, prefix the command with `MSYS_NO_PATHCONV=1`.
> `key.pem` / `cert.pem` are test files: don't commit them.

### 10.2 Start the mock with TLS (terminal A)

```powershell
$env:MOCK_TLS_CERT = "tools/mock-server/cert.pem"
$env:MOCK_TLS_KEY  = "tools/mock-server/key.pem"
node tools/mock-server/mock-server.js
```

**Expected:**

```text
[TLS] Pin this certificate: PinnedCertificateHash = "5D:3D:55:…:78:E6"
[READY] BaronDesk Mock WebSocket Server listening on wss://127.0.0.1:8443/agent-ws
```

### 10.3 Correct pin (terminal B)

```powershell
$env:Agent__ServerUrl = "wss://127.0.0.1:8443/agent-ws"
$env:Agent__AllowUntrustedCertificate = "false"
$env:Agent__PinnedCertificateHash = "<paste the fingerprint printed by the mock>"
dotnet run --project src/BaronDeskAgent.ServiceCore
```

**Expected:** `Connected to wss://127.0.0.1:8443/agent-ws.` and `Handshake acknowledged`.

### 10.4 Wrong pin

```powershell
$env:Agent__PinnedCertificateHash = "0000000000000000000000000000000000000000000000000000000000000000"
$env:Agent__StationToken = "must-never-leave-this-pc"
dotnet run --project src/BaronDeskAgent.ServiceCore
```

**Expected:**

```text
crit: … Certificate pinning mismatch (expected 0000…, got 5D3D…). Aborting the connection.
```

The mock prints **no** `[CONNECTED]` and **no** `[AUTH]` line: the check runs during the TLS handshake, so the credential is never sent.

### 10.5 Production refuses unsafe configuration

`dotnet run` forces Development through `launchSettings.json`; add `--no-launch-profile` to test production:

```powershell
Remove-Item Env:Agent__* -ErrorAction SilentlyContinue
$env:DOTNET_ENVIRONMENT = "Production"
dotnet run --no-launch-profile --project src/BaronDeskAgent.ServiceCore
```

**Expected:** the agent refuses to start:

```text
Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException:
PinnedCertificateHash is required outside Development: the LAN server certificate must be pinned.
```

Setting `Agent__AllowUntrustedCertificate=true`, `Agent__ServerUrl=ws://…` or `Agent__StationToken=…` in Production is refused the same way.

### 10.6 Clean up the environment

```powershell
Remove-Item Env:Agent__*, Env:DOTNET_ENVIRONMENT, Env:MOCK_TLS_* -ErrorAction SilentlyContinue
```

---

## 11. Test the Station Credential (DPAPI)

Use an **elevated** PowerShell (Run as administrator) in the `Desktop-Agent` folder.

| Step | Command | Expected |
|---|---|---|
| 11.1 | `dotnet run --project src/BaronDeskAgent.ServiceCore -- --set-station-token` in a **non-elevated** terminal | `Run this command from an elevated (administrator) prompt.` (exit code 1) |
| 11.2 | `dotnet run --project src/BaronDeskAgent.ServiceCore -- --set-station-token abc` | `--set-station-token takes no arguments…` (exit code 2): tokens are never passed as arguments |
| 11.3 | `"my test token" \| dotnet run --project src/BaronDeskAgent.ServiceCore -- --set-station-token` | Rejected: spaces are not allowed in a token |
| 11.4 | `"dev-station-jwt-123" \| dotnet run --project src/BaronDeskAgent.ServiceCore -- --set-station-token` | `Station credential stored in C:\ProgramData\BaronDeskAgent\Data\station.credential (DPAPI, machine scope).` |
| 11.5 | `dotnet run --project src/BaronDeskAgent.ServiceCore -- --set-station-token` (no pipe) | Prompts `Station token:` and hides what you type |
| 11.6 | `icacls C:\ProgramData\BaronDeskAgent\Data\station.credential` | Only `NT AUTHORITY\SYSTEM` and `BUILTIN\Administrators`, no inherited entries, no Users |
| 11.7 | `Get-Content C:\ProgramData\BaronDeskAgent\Data\station.credential` | Binary garbage: the token is **not** readable in plain text |
| 11.8 | Start the mock, then the agent **in the elevated terminal** | Mock: `[AUTH] Station credential presented (Bearer, 19 chars, value hidden)` |
| 11.9 | Start the agent in a **non-elevated** terminal | Agent: `The station credential file cannot be read (UnauthorizedAccessException).`; mock: `[AUTH] No station credential presented`. The file really is SYSTEM/Administrators-only. |
| 11.10 | `dotnet run --project src/BaronDeskAgent.ServiceCore -- --clear-station-token` (elevated) | `Station credential and key pair removed.` |

In production the service runs as LocalSystem, which can always read the file.

## 11B. Test Enrollment (First Connection)

The station's first contact: it sends the one-time token and its own public key, signed with its private key, and waits for admin approval. Details: [docs/Enrollment.md](docs/Enrollment.md). Start from a station **without** a credential (`--clear-station-token`, elevated).

| Step | Command | Expected |
|---|---|---|
| 11B.1 | Terminal A: `node tools/mock-server/mock-server.js` | Menu shows `[e] Approve` / `[x] Reject` |
| 11B.2 | Terminal B: `$env:DOTNET_ENVIRONMENT = "Development"; $env:Agent__EnrollmentToken = "enroll-demo-7f3c9a"; dotnet run --project src/BaronDeskAgent.ServiceCore` | Agent: `Generated a new station key pair`, `Enrollment pending…`. Mock: `[ENROLLMENT] … token=(present, hidden) signature=VALID`, `PENDING` |
| 11B.3 | Press `e` in terminal A | Next poll: mock `Issuing the station token`, then `[AUTH] Station credential presented`. Agent: `Station enrolled` |
| 11B.4 | Restart the agent | No enrollment request: the stored credential is used directly |
| 11B.5 | Delete `station.credential` + `station.key`, restart the mock and agent, press `x` | Agent: `Enrollment rejected by the server (declined by admin)`, connects without a credential |

Hands-free: `$env:MOCK_AUTO_APPROVE_ENROLLMENT_SECONDS = "3"` approves every new station after 3 s. In production the one-time token is provisioned with `"<token>" | BaronDeskAgent.ServiceCore.exe --set-enrollment-token` (elevated); `Agent__EnrollmentToken` is refused outside Development.

---

## 12. Troubleshooting

| Problem | Fix |
|---|---|
| Mock: `EADDRINUSE :8443` | A previous mock is still running: `Get-NetTCPConnection -LocalPort 8443 -State Listen \| ForEach-Object { Stop-Process -Id $_.OwningProcess }` |
| Agent keeps logging `Reconnecting in …` | Mock not running, or a `wss://`/`ws://` mismatch: check `$env:Agent__ServerUrl` and clean it up (step 10.6) |
| `LOCK` / `END_SESSION` answered `EXEC_FAILED` "…lock screen app (LockUI) is not running or not connected…" | Expected without the LockUI: the station is locked, but nothing covers the screen, so the agent reports it honestly. Start the LockUI (step 6) and the same command is acknowledged. |
| Production run still says "Development" | Add `--no-launch-profile` (step 10.5) |
| No CPU temperature in telemetry | Run the agent as Administrator (sensor driver) |
| `Hardware sensors are unavailable; telemetry and hardware alerts are disabled.` | Sensor driver blocked (no admin rights, antivirus, HVCI). The rest of the agent keeps working. |
| Alerts from `t` don't appear | Wait up to 15 s (idle sampling); the alarm fires only once until the temperature drops below threshold − 5 °C |
| USB alert never fires | Anti-theft may be off in your stored policy: press `a` first. The device must be wired USB HID and present before the agent started, or plugged in while idle. |
| Stuck on the lock screen | PIN `1234` (mock + agent running). If it says "Service unavailable" the agent stopped: **Ctrl+Alt+Shift+F12** closes the LockUI (Debug builds), or **Ctrl+Alt+Del → Sign out** |
| Agent warns `Task Manager policy could not be applied …` | Expected when the agent runs as a standard user: only LocalSystem / Administrators can write that policy. Run the agent as Administrator to test it. |
| Task Manager disabled after testing (agent ran as Administrator and was stopped while locked) | From an **elevated** prompt: `reg delete HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System /v DisableTaskMgr /f` |
| `--windowed` shows the fullscreen lock screen anyway | You are running a Release build (the flag is ignored there on purpose). Use `dotnet run` (Debug) |
| Can't type in the mock while the kiosk lock screen is shown | Use `MOCK_AUTO_RELOCK_SECONDS` (step 6B), or the windowed mode (step 6A) |
| Start from a clean local state | Stop the agent, then delete `C:\ProgramData\BaronDeskAgent\Data\agent.sqlite*`; it is re-created (policy defaults, `charmap` seed) on the next start |
| Inspect the local database | Open `agent.sqlite` with *DB Browser for SQLite* (tables `OutboxMessages`, `GameCatalog`, `StationPolicy`, `HandledCommands`) |

---

## 13. Final Checklist

- [ ] `dotnet build`: 0 warnings, 0 errors; `dotnet test`: 189 passed
- [ ] Agent connects: `handshake`, `state_report`, `heartbeat` in `seq` order
- [ ] `UNKNOWN_TYPE`, replayed `seq`, stale `UNLOCK` rejected; stale `LOCK` executed; malformed frame ignored
- [ ] Reconnect with growing, jittered delays after the mock stops
- [ ] UNLOCK → Character Map launch → END_SESSION closes it
- [ ] Mock down while unlocked → station locks after lease + grace
- [ ] LockUI windowed mode (6A): wrong PIN rejected by the backend, `1234` unlocks, 5 failures → 30 s lockout, LOCK acknowledged
- [ ] LockUI kiosk mode (6B): fullscreen on every monitor, escape keys blocked, auto-relock brings the overlay back
- [ ] LOCK shows the overlay and closes the running game
- [ ] Mock down → "Service unavailable", no login possible
- [ ] Agent killed → LockUI locks itself after 15 s; second LockUI instance exits
- [ ] Telemetry sends only changes; `t` raises one temperature alert per sensor
- [ ] Alert raised offline is delivered after reconnecting
- [ ] USB unplug > 5 s raises an `anti_theft` alert; a quick replug does not
- [ ] Invalid `POLICY_UPDATE` → `INVALID_PAYLOAD`; valid one persists across restarts
- [ ] Correct pin connects; wrong pin aborts with no credential sent; production refuses to start without a pin
- [ ] DPAPI: token stored encrypted, file ACL SYSTEM/Administrators only, `[AUTH]` shown by the mock, removed with `--clear-station-token`
