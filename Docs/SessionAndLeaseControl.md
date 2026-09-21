# BaronDesk Agent : Session & Lease Control Architecture

This document describes the current implementation and architecture of session lifecycle tracking, lease management, presence heartbeats, and fail-closed security in `BaronDeskAgent.ServiceCore`.

---

## Folder Structure

```text
BaronDeskAgent
│
├── BaronDesk.Shared
│   └── Contracts
│       ├── Envelope.cs
│       ├── AgentJsonContext.cs
│       ├── MessageTypes.cs
│       ├── HeartbeatPayload.cs
│       ├── HeartbeatAckPayload.cs
│       ├── StateReportPayload.cs
│       ├── CommandRequest.cs
│       └── CommandResponse.cs
│
└── BaronDeskAgent.ServiceCore
    │
    ├── Configuration
    │   └── AgentOptions.cs
    │
    ├── Communication
    │   ├── IServerConnection.cs
    │   ├── WebSocketConnection.cs
    │   └── ConnectionWorker.cs
    │
    ├── Commands
    │   └── Handlers
    │       ├── UnlockCommandHandler.cs
    │       ├── LockCommandHandler.cs
    │       └── EndSessionHandler.cs
    │
    └── Services
        │
        ├── Commands
        │   └── LockService.cs
        │
        └── Session
            ├── SessionService.cs
            ├── LeaseManager.cs
            └── HeartbeatWorker.cs
```

---

## 1. Session Lifecycle Architecture

The desktop agent operates under a strict principle: **the agent never decides authorization**. The server holds authoritative control over user balances, reservations, and billing timers. The agent manages local runtime state and enforces access controls:

```text
Backend Server
      │
      │ UNLOCK { sessionId }
      ▼
UnlockCommandHandler
      │
      ├── 1. SessionService.StartSessionAsync(sessionId)
      ├── 2. LockService.UnlockAsync()
      └── 3. LeaseManager.GrantLease(duration)
      │
      ▼
Active Paid Session Running
      │
      │ END_SESSION { reason }  (or Lease Expiration)
      ▼
EndSessionCommandHandler (or LeaseManager fail-closed)
      │
      ├── 1. SessionService.EndSessionAsync(reason)
      ├── 2. LockService.LockAsync()
      └── 3. LeaseManager.RevokeLease()
      │
      ▼
Station Locked
```

---

## 2. SessionService (Session Control Engine)

`SessionService` manages the station's session state and exposes lifecycle events:

```csharp
public sealed class SessionService
{
    public bool IsSessionActive { get; }
    public Guid? CurrentSessionId { get; }
    public DateTimeOffset? SessionStartedAt { get; }
    public TimeSpan? PlayDuration { get; }

    public event Action<Guid>? OnSessionStarted;
    public event Action<string>? OnSessionEnded;

    public Task StartSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    public Task EndSessionAsync(string reason = "normal", CancellationToken cancellationToken = default);
}
```

Key features:
- **Thread-Safe State Transitions:** Guarded by internal synchronization locks.
- **Idempotent Starts:** If an active session receives a repeat start request with the same `sessionId`, the request completes safely without restarting state.
- **Duration Tracking:** Tracks `SessionStartedAt` and computes live play duration.
- **Decoupled Notification:** Dispatches `OnSessionStarted` and `OnSessionEnded` events to notify dependent components (e.g. game launchers, UI helpers).

---

## 3. Authorization Lease & Monotonic Clock

Authorization to remain unlocked is modeled as a **server-granted lease** that is renewed on every heartbeat:

```text
Unlock Command / Heartbeat Ack
            │
            ▼
   Lease Granted (e.g. 60s)
            │
    Monotonic Clock Tick
   (TimeProvider.GetTimestamp)
            │
     Lease Runs Out
            │
     Grace Window (e.g. 10s)
            │
    FAIL-CLOSED LOCKOUT
```

### Why a Monotonic Clock?
To prevent gamers from tampering with the Windows system clock to artificially prolong playtime, lease expiration is tracked using .NET's `TimeProvider.System.GetTimestamp()`:
- System wall-clock jumps (e.g., NTP adjustments or user manual changes) **cannot** extend an active lease.
- Lease duration and elapsed time are calculated via high-resolution hardware timestamp frequencies (`TimestampFrequency`).

### Lease Management API (`LeaseManager`)

```csharp
public sealed class LeaseManager : IDisposable
{
    public bool IsLeaseValid { get; }
    public DateTimeOffset? LeaseExpiresAt { get; }
    public TimeSpan? RemainingLease { get; }

    public void GrantLease(TimeSpan duration);
    public void UpdateLease(DateTimeOffset? serverLeaseExpiresAt);
    public void RevokeLease();
}
```

---

## 4. Fail-Closed Enforcement

If the venue LAN fails, the backend crashes, or the connection is severed:

```text
Server Connection Lost
          │
          ├── Blip shorter than lease ──► Gamer continues playing seamlessly
          │                                (Telemetry buffered in SQLite outbox)
          │
          └── Lease expires + grace ───► FAIL-CLOSED TRIGGERED:
                                           ├── EndSessionAsync("lease_expired")
                                           └── LockService.LockAsync()
```

Rules:
1. **Short Blips (Resilience):** If a disconnect lasts less than the lease duration (default: 60s), the station remains unlocked. When connectivity resumes, the heartbeat renews the lease without interrupting play.
2. **Lease Run-Dry (Fail-Closed):** If the lease expires and the grace window (default: 10s) elapses without server renewal, `LeaseManager` terminates the session and locks the station.
3. **Boot Default:** On startup, `LockService` initializes in the **LOCKED** state (`_isLocked = 1`) ensuring stations remain locked until explicitly authorized.

---

## 5. Node Tracking & Presence Heartbeats

`HeartbeatWorker` is a dedicated `BackgroundService` that periodically reports station presence:

```text
HeartbeatWorker (PeriodicTimer ~15s)
          │
          ├── IsConnected?
          │      │
          │      └── Yes ──► Send Envelope<HeartbeatPayload>
          │                     │
          │                     ▼
          │             Backend (/agent-ws)
          │                     │
          │                     ▼
          │             Reply Envelope<HeartbeatAckPayload>
          │                     │
          ▼                     ▼
ConnectionWorker ◄──────────────┘
          │
          ▼
LeaseManager.UpdateLease(leaseExpiresAt)
```

Heartbeat payload contract (`HeartbeatPayload.cs`):

```json
{
  "type": "heartbeat",
  "id": "e2a3c7b1-9f2d-4c8e-a123-456789abcdef",
  "ts": "2026-09-20T20:30:15.000Z",
  "seq": 12,
  "payload": {
    "locked": false,
    "sessionId": "b47c9d21-3a5f-4d92-9e8a-1c2d3e4f5a6b"
  }
}
```

Server heartbeat acknowledgement (`HeartbeatAckPayload.cs`):

```json
{
  "type": "heartbeat_ack",
  "id": "99999999-8888-7777-6666-555555555555",
  "ts": "2026-09-20T20:30:15.050Z",
  "seq": 105,
  "payload": {
    "leaseExpiresAt": "2026-09-20T20:31:15.000Z",
    "serverTime": "2026-09-20T20:30:15.000Z"
  }
}
```

---

## 6. State Report on Reconnection

Upon completing the TLS handshake and receiving `handshake_ack`, the agent immediately transmits a `state_report` frame:

```text
Agent                                       Server
  │                                           │
  │─── handshake ────────────────────────────►│
  │◄── handshake_ack ─────────────────────────│
  │                                           │
  │─── state_report ─────────────────────────►│
  │    { locked, sessionId,                   │
  │      runningGameId, leaseExpiresAt }      │
```

State report contract (`StateReportPayload.cs`):

```json
{
  "type": "state_report",
  "id": "33333333-4444-5555-6666-777777777777",
  "ts": "2026-09-20T20:30:00.100Z",
  "seq": 2,
  "payload": {
    "locked": true,
    "sessionId": null,
    "runningGameId": null,
    "leaseExpiresAt": null
  }
}
```

Why `state_report` is essential:
- If the agent restarts after a crash or power cut, the backend reconciles billing timers from the agent's reported state and persisted start times.
- Eliminates out-of-sync ghost sessions after network partitions.

---

## 7. Configuration Options

Configured under `"Agent"` in `appsettings.json`:

```json
{
  "Agent": {
    "ServerUrl": "ws://127.0.0.1:8443/agent-ws",
    "HeartbeatIntervalSeconds": 15.0,
    "DefaultLeaseDurationSeconds": 60.0,
    "LeaseGracePeriodSeconds": 10.0
  }
}
```

- `HeartbeatIntervalSeconds`: Cadence for sending presence heartbeats (default: 15s).
- `DefaultLeaseDurationSeconds`: Fallback lease validity window if the server does not specify an explicit timestamp (default: 60s).
- `LeaseGracePeriodSeconds`: Extra tolerance window before locking the station after lease expiry (default: 10s).

---

## 8. Dependency Injection Registration

Wired in `Program.cs`:

```csharp
// System Monotonic Time Provider
builder.Services.AddSingleton(TimeProvider.System);

// Session & Lease Management
builder.Services.AddSingleton<LockService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<LeaseManager>();

// Hosted Workers
builder.Services.AddHostedService<ConnectionWorker>();
builder.Services.AddHostedService<HeartbeatWorker>();
```

---

## Current Status

- [x] Session lifecycle state machine (`SessionService`)
- [x] Session ID, start time, and play duration tracking
- [x] Session transition events (`OnSessionStarted`, `OnSessionEnded`)
- [x] Monotonic clock lease management (`LeaseManager`) using `TimeProvider.System`
- [x] Automatic fail-closed lockout upon lease expiration + grace period
- [x] Fail-closed boot default (`LockService` starts locked)
- [x] Periodic presence heartbeat worker (`HeartbeatWorker`)
- [x] Heartbeat acknowledgement parsing and lease renewal
- [x] Automatic `state_report` frame emission upon handshake completion
- [x] `UNLOCK` command integration (session start + initial lease grant)
- [x] `END_SESSION` command integration (session teardown + lease revocation + lock)
- [x] Source-generated JSON contracts for `HeartbeatPayload`, `HeartbeatAckPayload`, and `StateReportPayload`
