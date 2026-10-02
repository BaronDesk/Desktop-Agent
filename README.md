# BaronDesk Desktop Agent

The BaronDesk Desktop Agent runs on every gaming PC ("station") of a BaronDesk gaming centre. It keeps the PC locked until the backend grants a paid session, launches the games the venue offers, reports the PC's health, and raises an alert when hardware overheats or equipment is unplugged. It is written in C# on .NET 10, secure by default, and designed to stay light on the gamer's machine.

**Status:** the agent runs end to end against the real BaronDesk backend. The venue's admin PC hosts the backend and the dashboards; the gaming PCs connect to it over the local network, enroll with admin approval, and are then driven from the admin dashboard and unlocked by gamers with the PIN of their booking.

| | |
|---|---|
| **Platform** | Windows 10 / 11 x64, .NET 10 |
| **Projects** | Service core (the authority), WPF lock screen, shared contracts |
| **Backend link** | WebSocket over TLS (`/agent-ws`) + HTTPS for enrollment and the game catalog |

---

## Contents

1. [What It Does](#what-it-does)
2. [How It Fits in BaronDesk](#how-it-fits-in-barondesk)
3. [Running with the BaronDesk Backend](#running-with-the-barondesk-backend)
4. [Demo Walkthrough](#demo-walkthrough)
5. [Security Highlights](#security-highlights)
6. [Build and Tests](#build-and-tests)
7. [Technology](#technology)
8. [Documentation](#documentation)
9. [Solution Layout](#solution-layout)

---

## What It Does

| Capability | In short |
|---|---|
| **Paid access** | A fullscreen lock screen covers every monitor. The gamer types the PIN of their booking; the backend checks it and unlocks the station for the paid time. |
| **Session control** | The station stays unlocked only while it holds a valid lease from the backend, and locks itself when the lease runs out, the network drops, or anything goes wrong. |
| **Remote commands** | Staff lock, unlock, end a session, launch a game or shut a PC down from the admin dashboard. |
| **Games** | The venue's game catalog is synced from the backend; games start in the gamer's session through their executable, Steam or the Epic Games Launcher, and are closed when the session ends. The agent also reports which Steam / Epic games are installed on the PC. |
| **In-session notices** | A small corner box warns the gamer before the station locks (low balance, booking ending). |
| **Telemetry** | CPU, GPU, RAM and fan readings are sent to the dashboard, only when they change. |
| **Alerts** | Overheating, sustained overload, removed USB equipment (anti-theft) and lock-screen tampering raise alerts, kept offline until they can be delivered. |
| **Enrollment** | A new PC joins the venue with a one-time token and admin approval, and gets its own credential, renewed automatically. |

---

## How It Fits in BaronDesk

```text
 ┌──────────────── Admin PC (venue desk) ─────────────────┐       Gamer's phone
 │  Admin dashboard (staff)                               │      ┌──────────────┐
 │        │                                               │      │ Gamer portal │
 │        ▼                                               │◄─────│ book, PIN    │
 │  Docker: Caddy :443 (TLS) ─► NestJS backend :3000      │      └──────────────┘
 │                               Postgres · Redis         │
 └──────────────────────────────▲─────────────────────────┘
                                │  LAN · wss://cstam-server.local/agent-ws
                                │  station credential (JWT, DPAPI-protected)
 ┌──────────── Gaming PC (one per station) ───────────────┐
 │  BaronDeskAgent.ServiceCore   (the authority)          │
 │      connection · commands · session · games · sensors │
 │                     │  locked-down named pipe          │
 │  BaronDesk.LockUI             (the gamer's screen)     │
 │      lock overlay · PIN box · notices                  │
 └────────────────────────────────────────────────────────┘
```

The service core holds the connection and every decision; the LockUI only draws the overlay and forwards what the gamer types. The backend decides who may play and for how long; the agent enforces it on the PC. The full design is in [docs/Architecture.md](docs/Architecture.md), and the backend side of the protocol in `back-end/docs/STATION_AGENT.md`.

---

## Running with the BaronDesk Backend

This is the setup used for the demo: the **admin PC** runs the backend stack and the dashboards, and each **gaming PC** runs the agent and the lock screen.

### 1. Admin PC

1. Start the backend stack (from the `back-end` repository): `npm run docker:dev`. Caddy serves the API and `/agent-ws` on port **443** for the names `localhost` and `cstam-server.local`.
2. Start the dashboards (from the `front-end` repository) and open the admin dashboard.
3. Allow inbound **TCP 443** in Windows Defender Firewall, so the gaming PCs can reach Caddy.
4. Note the admin PC's LAN address (`ipconfig`), for example `192.168.1.20`.

### 2. Gaming PC: prerequisites

| Need | Why |
|---|---|
| Windows 10 / 11 x64 and the **.NET 10 SDK** (`dotnet --list-sdks`) | Build and run the agent |
| A **hosts entry** `192.168.1.20  cstam-server.local` in `C:\Windows\System32\drivers\etc\hosts` (edit as Administrator) | Caddy answers only for the names in its certificate; an IP address sends no TLS SNI and is refused |
| An **elevated** PowerShell (Run as administrator) for the agent | The station credential is stored with DPAPI in files only SYSTEM and Administrators can read; it also enables CPU temperatures and the Task Manager policy |

Check the link: `curl.exe -k https://cstam-server.local/health` should answer from the backend.

### 3. Enroll the PC

1. **Admin dashboard → New stations → Add a PC:** with the station's branch picked in the top bar, choose how long the token stays valid and copy the one-time enrollment token.
2. **Gaming PC, elevated PowerShell**, in the `Desktop-Agent` folder:

   ```powershell
   $env:Agent__ServerUrl       = "wss://cstam-server.local/agent-ws"
   $env:Agent__SerialNumber    = $env:COMPUTERNAME        # unique per station
   $env:Agent__EnrollmentToken = "<token from the dashboard>"
   dotnet run --project src/BaronDeskAgent.ServiceCore
   ```

   The agent generates its own key pair, sends a signed enrollment request and logs `Enrollment pending…`.
3. **Admin dashboard → New stations → Requests and enrolled PCs:** the PC appears as *Waiting for approval*. Approve it.
4. On its next poll (15 s) the agent receives its station credential, stores it with DPAPI, and connects: `Station enrolled`, then `Connected to wss://cstam-server.local/agent-ws` and `Handshake acknowledged`. The station shows **Online** in the dashboard and syncs its game catalog.

The token is used once. From now on, start the agent the same way **without** `Agent__EnrollmentToken`: it reconnects with its stored credential, which the backend renews automatically before it expires.

### 4. Start the lock screen

In a **normal** (non-elevated) terminal on the gaming PC, signed in as the gamer's Windows account:

```powershell
dotnet run --project src/BaronDesk.LockUI
```

The overlay covers every monitor and blocks the Windows key, Alt+Tab and Alt+F4. It stays until a gamer types a valid booking PIN or staff unlock the station from the dashboard.

> **Getting out during testing:** type a valid PIN, or unlock from the dashboard. If the agent is stopped ("Service unavailable"), **Ctrl+Alt+Shift+F12** closes the LockUI (Debug builds only, which is what `dotnet run` builds), and **Ctrl+Alt+Del → Sign out** always works.

### About the run mode

`dotnet run` starts the agent in the **Development** environment (`Properties/launchSettings.json`). On the venue LAN this is what lets it accept the certificate of Caddy's internal CA without a pin (`AllowUntrustedCertificate`: the link is still TLS-encrypted end to end. Every other protection (station credential, DPAPI, signed enrollment, anti-replay, allow-listed commands, fail-closed lease) is identical to production. Settings can also be put in `src/BaronDeskAgent.ServiceCore/appsettings.Development.json` instead of environment variables; all keys are listed in [docs/Architecture.md §6](docs/Architecture.md#6-configuration).

---

## Demo Walkthrough

With the PC enrolled, the agent and the LockUI running:

| # | Action | What you see |
|---|---|---|
| 1 | Nothing: the agent just started | The PC is **locked**. Dashboard: station *Online*, locked, with live CPU / GPU / RAM telemetry |
| 2 | Gamer portal: *Play now* on this station (wallet credited from the desk) | The booking gets a 6-digit **PIN** |
| 3 | Type a wrong PIN on the lock screen | "Incorrect PIN": the **backend** rejected it; the agent never checks PINs. 5 wrong tries block the box for 30 s |
| 4 | Type the right PIN | The overlay disappears; the dashboard shows the session running |
| 5 | Dashboard: launch a game on the station | The game opens on the gamer's desktop |
| 6 | Let the balance or the booking run low | A corner box counts down before the station locks |
| 7 | Dashboard: end the session | The game closes and the lock screen comes back |
| 8 | Unplug a wired USB mouse or keyboard while the station is idle | An **anti-theft** alert appears in the dashboard |
| 9 | Unplug the network cable during a session | The dashboard marks the station *Offline* within a minute; the PC **locks itself** when its lease runs out (fail closed), and reconnects on its own when the cable is back |
| 10 | Dashboard: shut the station down (manager / HQ) | The PC powers off |

The backend repository has a full manual test plan for this setup: `back-end/docs/STATION_PHYSICAL_TEST.md`.

---

## Security Highlights

- **The agent decides nothing:** PINs, balances and sessions are checked by the backend. The station only obeys `UNLOCK` / `LOCK`.
- **Fail closed:** the station starts locked and locks again on any expired lease, lost connection or broken component.
- **Certificate pinning:** in production the server certificate is pinned, and on a mismatch nothing, not even the credential, leaves the PC (implemented and tested for the LAN setup).
- **Protected identity:** the station credential and key pair are stored with Windows DPAPI in files only SYSTEM and Administrators can read; enrollment requests are signed with the station's own ECDSA key.
- **Anti-replay:** every inbound frame is checked for sequence and freshness against the server clock.
- **Allow-listed commands only:** seven commands, matched exactly; games run only from the synced catalog, never from a path in a command.
- **Locked-down IPC:** only the service and the signed-in console user can use the lock screen pipe.
- **Unsafe production settings refused:** outside Development the agent will not start without a pin, with `ws://`, with untrusted certificates allowed, or with a token in plain-text configuration.
- **No secrets in logs or the local database.**

The September 2026 security and quality review, with every finding and fix, is in [docs/ReviewFixes.md](docs/ReviewFixes.md).

---

## Build and Tests

```powershell
dotnet build BaronDeskAgent.slnx    # 0 warnings, 0 errors (warnings are errors)
dotnet test  BaronDeskAgent.slnx    # Passed: 193, Failed: 0
```

The tests cover the protocol guards (replay, stale frames, backoff), the command pipeline, sessions and leases, policy validation, hardware alerts, telemetry deltas, the DPAPI stores, enrollment, credential renewal, the game catalog and launchers, installed-game discovery, session notices and the peripheral contract. The list per test class is in [docs/LocalTesting.md §1](docs/LocalTesting.md#1-build-and-run-the-automated-tests).

Every feature can also be exercised **without the backend**, on a single PC, against the bundled mock server: [docs/LocalTesting.md](docs/LocalTesting.md). It is the quickest way to see the behaviours that are hard to trigger on purpose against the real stack: replayed and stale commands, malformed frames, a wrong certificate pin, production configuration checks.

---

## Technology

| Area | Choice |
|---|---|
| Language / runtime | C# on .NET 10 (`net10.0-windows`), nullable enabled, warnings as errors |
| Hosting | `Microsoft.Extensions.Hosting` (hosted background workers) |
| Lock screen | WPF, low-level keyboard hook |
| Local storage | SQLite (`Microsoft.Data.Sqlite`, WAL), versioned migrations |
| Hardware sensors | LibreHardwareMonitorLib |
| Secrets | Windows DPAPI (`System.Security.Cryptography.ProtectedData`) |
| Serialization | `System.Text.Json` source generation |
| Devices | CfgMgr32 / SetupAPI device notifications (P/Invoke) |
| Test backend | Zero-dependency Node.js mock server |

---

## Documentation

| Document | Covers |
|---|---|
| [Architecture](docs/Architecture.md) | Overview, processes, design principles, wire protocol, startup, configuration |
| [LocalTesting](docs/LocalTesting.md) | Step-by-step test of every feature against the mock server |
| [WebSocketConnection](docs/WebSocketConnection.md) | Server link, TLS pinning, reconnection, server clock, anti-replay |
| [CommandsHandling](docs/CommandsHandling.md) | Command pipeline, allow-list, idempotency, acknowledgements |
| [SessionAndLeaseControl](docs/SessionAndLeaseControl.md) | Sessions, lease, heartbeat, fail-closed locking |
| [SessionCommandsAndSystem](docs/SessionCommandsAndSystem.md) | Session teardown, shutdown |
| [GamesHandling](docs/GamesHandling.md) | Game catalog, launching, process tracking, installed-game discovery |
| [LockUIAndIPC](docs/LockUIAndIPC.md) | Lock screen, named-pipe IPC, PIN login, kiosk mode, notices |
| [HardwareTelemetry](docs/HardwareTelemetry.md) | Sensors, delta telemetry, offline outbox |
| [TelemetryAlerts](docs/TelemetryAlerts.md) | Hardware alerts, USB anti-theft, peripheral status |
| [PolicyStore](docs/PolicyStore.md) | Station policy and live updates |
| [CredentialStore](docs/CredentialStore.md) | DPAPI station credential and its renewal |
| [Enrollment](docs/Enrollment.md) | First connection of a new PC |
| [LocalStorage](docs/LocalStorage.md) | SQLite database, data directory, migrations |
| [ReviewFixes](docs/ReviewFixes.md) | Security and quality review |

---

## Solution Layout

```text
Desktop-Agent
├── BaronDeskAgent.slnx                  open this in Visual Studio / Rider
├── src
│   ├── BaronDesk.Shared                 wire + IPC contracts
│   ├── BaronDeskAgent.ServiceCore       the agent (service core)
│   └── BaronDesk.LockUI                 WPF lock overlay (user session)
├── tests
│   └── BaronDeskAgent.ServiceCore.Tests unit / integration tests
├── tools
│   └── mock-server                      mock /agent-ws backend (Node.js, no dependencies)
└── docs
```

Local data lives in `C:\ProgramData\BaronDeskAgent\Data` (`agent.sqlite`, and the DPAPI-protected `station.credential` and `station.key`).

---
