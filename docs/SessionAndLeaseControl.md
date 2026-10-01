# BaronDesk Agent : Session & Lease Control Architecture

This document describes session lifecycle tracking, lease management, presence heartbeats and fail-closed enforcement in `BaronDeskAgent.ServiceCore`.

---

## Folder Structure

```text
Desktop-Agent
│
├── src
│   ├── BaronDesk.Shared
│   │   └── Contracts
│   │       ├── HeartbeatPayload.cs
│   │       ├── ServerControlPayloads.cs   (HeartbeatAckPayload)
│   │       ├── StateReportPayload.cs
│   │       └── CommandPayloads.cs         (UnlockPayload, LockPayload, EndSessionPayload)
│   │
│   └── BaronDeskAgent.ServiceCore
│       │
│       ├── Connection
│       │   ├── ConnectionWorker.cs        (heartbeat_ack → RenewLease, state_report)
│       │   └── ServerClock.cs
│       │
│       ├── Commands
│       │   └── Handlers
│       │       ├── UnlockCommandHandler.cs
│       │       ├── LockCommandHandler.cs
│       │       └── EndSessionCommandHandler.cs
│       │
│       └── Session
│           ├── StationController.cs       (single owner of every transition)
│           ├── LockService.cs
│           ├── ILockScreen.cs             (implemented by Ipc/PipeServer)
│           ├── SessionService.cs
│           ├── LeaseManager.cs
│           ├── HeartbeatWorker.cs
│           └── LoginRelay.cs              (see LockUIAndIPC.md)
│
└── tests
    └── BaronDeskAgent.ServiceCore.Tests
        └── Session
            ├── StationControllerTests.cs
            └── LeaseManagerTests.cs
```

---

## 1. Session Lifecycle Architecture

The agent never decides authorization. The backend owns balances, reservations and billing timers; the agent binds the session id it is given, holds the lease, and locks when told to or when the lease runs out.

```text
Backend Server
      │
      │ UNLOCK { sessionId, leaseSeconds? }
      ▼
StationController.StartSessionAsync
      │
      ├── 1. Different session active? → stop its game, end it
      ├── 2. SessionService.Start(sessionId)
      ├── 3. LeaseManager.Grant(lease)        ◄── granted BEFORE unlocking
      └── 4. LockService.UnlockAsync()        ──► HideLock to the LockUI
      │
      ▼
Active Paid Session Running  ◄── heartbeat_ack renews the lease
      │
      ├── LOCK { reason }            (e.g. billing run-out)
      │     └── revoke lease, show overlay, stop game (policy), session stays bound
      │
      ├── END_SESSION { sessionId? }
      │     └── show overlay, stop game, end session, revoke lease
      │
      └── Lease expired past grace (offline)
            └── FAIL-CLOSED: same as END_SESSION ("lease_expired")
      │
      ▼
Station Locked
```

Every transition runs under **one gate** inside `StationController`, so commands, lease expiry and shutdown cannot interleave.

---

## 2. StationController (Session Control Engine)

```csharp
public sealed class StationController : IDisposable
{
    public StationSnapshot GetSnapshot();   // Locked, SessionId, RunningGameId, LeaseExpiresAt

    public Task StartSessionAsync(Guid sessionId, TimeSpan? leaseDuration, CancellationToken ct);  // UNLOCK
    public Task<OverlayResult> LockAsync(CancellationToken ct);                                    // LOCK
    public Task<EndSessionResult> EndSessionAsync(Guid? expectedSessionId, string reason, CancellationToken ct);
    public void RenewLease(TimeSpan? duration);                                                    // heartbeat_ack
    public Task PrepareForShutdownAsync();                                                         // SHUTDOWN
}
```

### The Enforced Invariant: unlocked ⇒ valid lease

```text
Every 5 s (ITimer from TimeProvider)
      │
      ├── Station locked? ──────────────────────────► nothing to do
      │
      ├── Lease Valid or InGrace? ──────────────────► nothing to do
      │
      └── Unlocked without a usable lease ──► FailClosedAsync()
                                               ├── re-check under the gate
                                               ├── revoke lease
                                               ├── show the overlay
                                               ├── stop the game
                                               └── end the session ("lease_expired")
```

The check covers more than offline expiry. **Any** path that could leave the station unlocked without a lease is closed within 5 seconds, whatever caused it: the invariant is checked, not assumed.

### SessionService

`SessionService` only remembers the bound session id (thread-safe). It has no events and no PIN:

```csharp
public Guid? CurrentSessionId { get; }
public bool IsSessionActive { get; }
public void Start(Guid sessionId);
public Guid? End(string reason);   // returns the ended id
```

---

## 3. Authorization Lease & Monotonic Clock

```text
UNLOCK / heartbeat_ack
          │
          ▼
 Lease duration from SERVER-CLOCK values only
   leaseSeconds                    (preferred)
   leaseExpiresAt − serverTime     (serverTime, else the envelope ts)
   none given → policy default
          │
          ├── ≤ 0 (already expired) ──► NOT renewed
          │
          ▼
 capped at StationPolicy.DefaultLeaseDurationSeconds
          │
          ▼
 expiry = TimeProvider.GetTimestamp() + duration   (monotonic)
          │
     Lease runs out ──► grace window (LeaseGracePeriodSeconds) ──► FAIL-CLOSED
```

### Why These Rules?

- **Monotonic clock:** changing the Windows clock cannot extend a lease.
- **Server-clock arithmetic:** converting `leaseExpiresAt` with the *local* clock would be wrong. A station 5 minutes behind the server would get a 6-minute lease; a station ahead of it would get a fresh full lease from an already-expired one.
- **Cap:** a bogus or malicious value cannot hand out hours of free play.

### LeaseManager API

```csharp
public sealed class LeaseManager
{
    public static TimeSpan? ResolveDuration(double? leaseSeconds, DateTimeOffset? leaseExpiresAt, DateTimeOffset serverNow);

    public void Grant(TimeSpan? requested);   // null → policy default; capped
    public void Revoke();
    public LeaseStatus GetStatus();           // None, Valid, InGrace, Expired
    public DateTimeOffset? ExpiresAt { get; } // on the estimated server clock (state_report)
}
```

---

## 4. Fail-Closed Enforcement

```text
Server Connection Lost
          │
          ├── Blip shorter than the lease ──► Gamer keeps playing
          │                                    (alerts buffered in the SQLite outbox)
          │                                    Reconnect → heartbeat sent immediately → lease renewed
          │
          └── Lease expired + grace ───────► FAIL-CLOSED (overlay, stop game, end session)

Other fail-closed rules
  • Boot: LockService starts LOCKED; the LockUI starts locked.
  • LOCK and END_SESSION run even with a drifted timestamp (restrictive commands).
  • LockUI loses the service for 15 s while unlocked → locks itself (see LockUIAndIPC.md).
  • No backend at login → "Service unavailable": no new sessions offline.
```

### LockService

`LockService` owns the lock flag and serializes it with the overlay commands, so a concurrent lock and unlock can never leave the overlay hidden while the station is locked:

```csharp
public bool IsLocked { get; }                               // starts true
public Task<OverlayResult> LockAsync(CancellationToken ct);   // flag set BEFORE any await
public Task<OverlayResult> UnlockAsync(CancellationToken ct); // Confirmed | HelperNotConnected | NotConfirmed
```

`LockAsync` always re-sends `ShowLock`, which also heals a LockUI that restarted.

---

## 5. Command Semantics

| Command | Behaviour | Ack |
|---|---|---|
| `UNLOCK { sessionId, leaseSeconds? }` | Missing `sessionId` → `INVALID_PAYLOAD`. A different active session is ended first (its game stopped). Lease granted, then overlay hidden. | After the overlay is hidden |
| `LOCK { reason? }` | Lease revoked, overlay shown, game stopped when `StopGameOnLock` (default on: an overlay cannot cover a game in exclusive fullscreen). Session stays bound for a later `UNLOCK` after a top-up. | Only when the overlay is confirmed, else `EXEC_FAILED` |
| `END_SESSION { sessionId?, reason? }` | `sessionId` of a *different* session → acked as a no-op (late redelivery). Otherwise overlay shown, game stopped, session ended, lease revoked. | Only when the overlay is confirmed, else `EXEC_FAILED` |

---

## 6. Node Tracking & Presence Heartbeats

`HeartbeatWorker` reports presence on a `PeriodicTimer` driven by the policy (`HeartbeatIntervalSeconds`, default 15 s):

```text
HeartbeatWorker
      │
      ├── timer tick & IsReady ──────────┐
      ├── ReadyChanged(true) ────────────┤  ◄── immediate heartbeat after every reconnect
      │                                  ▼
      │                     heartbeat { locked, sessionId }
      │                                  │
      │                                  ▼
      │                     Backend (/agent-ws)
      │                                  │
      │                                  ▼
      │                     heartbeat_ack { leaseSeconds, serverTime }
      ▼                                  │
ConnectionWorker ◄───────────────────────┘
      │  ReplayGuard (seq + ts), ServerClock.Synchronize(serverTime)
      ▼
StationController.RenewLease(duration)   (ignored when no session is bound)
```

- A policy update changes `timer.Period` immediately; waiting out an old, longer interval could outlast a shorter new lease.
- `StationPolicy.Validate()` requires `HeartbeatIntervalSeconds ≤ DefaultLeaseDurationSeconds / 2`.

Heartbeat contract (`HeartbeatPayload.cs`):

```json
{
  "type": "heartbeat",
  "id": "514f4a0a-bbcc-402d-80c0-5d9010469ce0",
  "ts": "2026-09-22T21:44:10.004Z",
  "seq": 3,
  "payload": {
    "locked": false,
    "sessionId": "0ba15f74-3c86-4c83-b13f-6262b3a68575"
  }
}
```

Server acknowledgement (`HeartbeatAckPayload`), as the backend sends it:

```json
{
  "type": "heartbeat_ack",
  "id": "0da495ad-a809-4407-aefa-7ccdf76c38ae",
  "ts": "2026-09-22T21:44:10.020Z",
  "seq": 102,
  "payload": {
    "leaseSeconds": 60,
    "serverTime": "2026-09-22T21:44:10.020Z"
  }
}
```

`leaseExpiresAt` may be sent instead of `leaseSeconds`. It is then paired with `serverTime`, never with the local clock.

---

## 7. State Report on Reconnection

Right after the handshake, on every (re)connect, and without waiting for `handshake_ack`:

```text
Agent                                       Server
  │                                           │
  │─── handshake ────────────────────────────►│
  │─── state_report ─────────────────────────►│
  │    { locked, sessionId, runningGameId,    │
  │      leaseExpiresAt, peripherals }        │
  │─── heartbeat ────────────────────────────►│
  │◄── handshake_ack {} ──────────────────────│
```

State report contract (`StateReportPayload.cs`):

```json
{
  "type": "state_report",
  "id": "10b8b541-9afe-410b-bb0b-0e8082c3b31f",
  "ts": "2026-09-22T21:41:52.130Z",
  "seq": 2,
  "payload": {
    "locked": true,
    "sessionId": null,
    "runningGameId": null,
    "leaseExpiresAt": null,
    "peripherals": []
  }
}
```

`peripherals` lists the watched USB devices and whether each is connected (see `TelemetryAlerts.md`).

After a crash or power cut the agent restarts locked with no session, and the backend reconciles billing from its own persisted `start_time`.

---

## 8. Configuration

Lease and heartbeat timing are **backend-tuned policy**, changed with `POLICY_UPDATE` and persisted (see `PolicyStore.md`). They are not part of `appsettings.json`.

| Policy field | Default | Bounds |
|---|---|---|
| `HeartbeatIntervalSeconds` | 15 s | 1 – 120 s, and ≤ half the lease |
| `DefaultLeaseDurationSeconds` | 60 s | 5 – 3600 s (also the lease cap) |
| `LeaseGracePeriodSeconds` | 10 s | 1 – 300 s |
| `StopGameOnLock` | `true` | boolean |

---

## 9. Dependency Injection Registration

```csharp
services.AddSingleton(TimeProvider.System);

services.AddSingleton<PipeServer>();
services.AddSingleton<ILockScreen>(provider => provider.GetRequiredService<PipeServer>());
services.AddSingleton<LockService>();
services.AddSingleton<SessionService>();
services.AddSingleton<LeaseManager>();
services.AddSingleton<StationController>();
services.AddSingleton<LoginRelay>();

services.AddHostedService<ConnectionWorker>();
services.AddHostedService<HeartbeatWorker>();
```

`StationController` is resolved during startup initialization, so the lease watchdog runs from the first second.

---

## 10. Testing

`StationControllerTests` (fake time, fake lock screen, real SQLite) and `LeaseManagerTests`:

| Test | Guarantees |
|---|---|
| `Starts_locked` | Fail-closed boot state |
| `Unlock_binds_the_session_and_hides_the_overlay` | UNLOCK flow and lease |
| `Fails_closed_when_the_lease_runs_out_offline` | Stays unlocked in grace, locks after it |
| `Heartbeat_renewals_keep_the_station_unlocked` | Renewals work |
| `A_renewal_reporting_an_expired_lease_is_not_honoured` | Expired terms never renew |
| `Lock_revokes_the_lease_but_keeps_the_session_bound` | LOCK semantics |
| `Lock_reports_why_the_overlay_is_not_confirmed_but_still_locks` | Locked either way; `HelperNotConnected` / `NotConfirmed` reported for an honest `EXEC_FAILED` |
| `End_session_for_another_session_is_ignored` | Stale END_SESSION is a no-op |
| `A_new_unlock_replaces_the_previous_session` | Session replacement |
| `Walks_through_valid_grace_and_expired`, `Caps_any_lease_at_the_policy_duration`, `Duration_uses_server_clock_values_only` | Lease maths |

---

## Implementation Summary

- `StationController` owns every lock/session/lease/game transition under one gate
- Invariant *unlocked ⇒ valid lease* enforced every 5 s
- Monotonic lease; duration from server-clock values only; capped; never renewed when expired
- Fail-closed boot, lease expiry, and LockUI-side fail-close
- `UNLOCK` requires a session id and replaces a different active session cleanly
- `LOCK` stops the game by policy; `END_SESSION` ignores stale session ids
- Lock state and overlay serialized (`LockService`), overlay confirmation reported honestly
- Heartbeats on a policy-driven timer, plus immediately after every reconnect
- `state_report` on every (re)connect
- Lease terms taken from `heartbeat_ack` (`leaseSeconds`, `serverTime`)
- Unit tests with a fake `TimeProvider`
