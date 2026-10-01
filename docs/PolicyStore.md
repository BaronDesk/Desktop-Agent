# BaronDesk Agent : Policy Store & Live Configuration Architecture

This document describes the station policy: its model and defaults, strict `POLICY_UPDATE` parsing and validation, SQLite persistence, and live propagation to running services in `BaronDeskAgent.ServiceCore`.

---

## Folder Structure

```text
Desktop-Agent
│
├── src
│   ├── BaronDesk.Shared
│   │   └── Contracts
│   │       └── PolicyUpdatePayload.cs         (wire DTO only, no behaviour)
│   │
│   └── BaronDeskAgent.ServiceCore
│       │
│       ├── Commands
│       │   └── Handlers
│       │       └── PolicyUpdateCommandHandler.cs
│       │
│       └── Policy
│           ├── StationPolicy.cs               (model, defaults, Apply, Validate)
│           ├── IPolicyStore.cs                (+ PolicyValidationException)
│           ├── PolicyStore.cs
│           └── PolicyRepository.cs
│
└── tests
    └── BaronDeskAgent.ServiceCore.Tests
        ├── Policy
        │   └── StationPolicyTests.cs
        └── Commands
            └── CommandPayloadTests.cs         (strict POLICY_UPDATE parsing)
```

`StationPolicy` moved from `BaronDesk.Shared/Models` into ServiceCore: only the agent uses it. `PolicyEntity` was removed; the repository maps rows to `StationPolicy` directly.

---

## Architecture

```text
                                  BACKEND / SERVER
                                         │
                              POLICY_UPDATE { partial policy }
                                         │
                                         ▼
                               ┌───────────────────┐
                               │ CommandDispatcher │  allow-list, anti-replay, idempotency
                               └─────────┬─────────┘
                                         ▼
                        ┌─────────────────────────────────┐
                        │   PolicyUpdateCommandHandler    │
                        │  - strict parse (unknown field  │
                        │    → INVALID_PAYLOAD)           │
                        │  - no field at all → INVALID    │
                        └────────────────┬────────────────┘
                                         ▼
                             ┌───────────────────────┐
                             │  PolicyStore          │
                             │  (update gate)        │
                             │  Apply → Validate     │── invalid ──► PolicyValidationException
                             └───────┬───────────────┘                 → INVALID_PAYLOAD
                                     │
           ┌─────────────────────────┼─────────────────────────┐
           │                         │                         │
     Persist to SQLite         Swap reference            Notify subscribers
           │                   (immutable record)        (each isolated)
           ▼                         ▼                         ▼
┌─────────────────────┐    ┌───────────────────┐   ┌───────────────────────────┐
│   PolicyRepository  │    │  CurrentPolicy    │   │ HeartbeatWorker (Period)  │
│   StationPolicy row │    │  (volatile read)  │   │ HardwareMonitorService    │
│   agent.sqlite      │    └───────────────────┘   │   (Period)                │
└─────────────────────┘                            └───────────────────────────┘
                              Read on every use:
                              LeaseManager · HardwareAlertEvaluator ·
                              UsbMonitorService · StationController (StopGameOnLock)
```

---

## 1. StationPolicy Model

`StationPolicy` is an immutable `record`. Its property initializers are the **single source of defaults**: no other file (configuration, seed SQL, store) repeats them, so they cannot drift apart.

| Property | Default | Valid Range |
|---|---|---|
| `TelemetryCadenceSeconds` | `5.0 s` | `1` – `300` |
| `HeartbeatIntervalSeconds` | `15.0 s` | `1` – `120`, and ≤ half the lease |
| `DefaultLeaseDurationSeconds` | `60.0 s` | `5` – `3600` (also the lease cap) |
| `LeaseGracePeriodSeconds` | `10.0 s` | `1` – `300` |
| `CpuTempAlertThreshold` | `85.0 °C` | `30` – `120` |
| `GpuTempAlertThreshold` | `85.0 °C` | `30` – `120` |
| `CpuLoadAlertThreshold` | `95.0 %` | `10` – `100` |
| `RamLoadAlertThreshold` | `95.0 %` | `10` – `100` |
| `HardwareAlertCooldownSeconds` | `60.0 s` | `1` – `600` |
| `UsbDebounceWindowSeconds` | `5.0 s` | `0.5` – `60` |
| `EnableAntiTheftAlerts` | `true` | boolean |
| `StopGameOnLock` | `true` | boolean (new: `LOCK` stops the running game) |

```csharp
public StationPolicy Apply(PolicyUpdatePayload update, DateTimeOffset updatedAt); // non-null fields only
public IReadOnlyList<string> Validate();                                           // empty when valid
```

### Validation Rules

- Every numeric field must be **finite** and inside its range. Checks are written as `!(IsFinite(v) && v >= min && v <= max)`, so `NaN` fails. A check written as `v is < min or > max` would let `NaN` through, because every comparison with `NaN` is false.
- `HeartbeatIntervalSeconds × 2 ≤ DefaultLeaseDurationSeconds`: a heartbeat must land well inside the lease, or sessions lock between two heartbeats.
- A rejected update changes nothing (not in memory, not in SQLite).

---

## 2. Wire Protocol (`POLICY_UPDATE`)

### Command Request Envelope

```json
{
  "type": "POLICY_UPDATE",
  "id": "dcd9497d-5399-4c0f-9775-9a6ee46f03fd",
  "ts": "2026-09-22T21:42:10.000Z",
  "seq": 107,
  "payload": {
    "telemetryCadenceSeconds": 3.0,
    "heartbeatIntervalSeconds": 10.0,
    "cpuTempAlertThreshold": 80.0,
    "stopGameOnLock": true
  }
}
```

### Strict Parsing

`PolicyUpdatePayload` is a plain DTO deserialized by the source-generated `AgentJsonContext`:

| Rule | Example | Result |
|---|---|---|
| camelCase names (case-insensitive) | `"heartbeatIntervalSeconds": 10` | applied |
| Unknown field | `"heartbeatIntervalSecs": 10` | `INVALID_PAYLOAD` (a typo is never silently ignored) |
| Number sent as a string | `"telemetryCadenceSeconds": "2.5"` | `INVALID_PAYLOAD` |
| No policy field at all | `{}` | `INVALID_PAYLOAD` |
| Out of range / `NaN` | `"cpuLoadAlertThreshold": 150` | `INVALID_PAYLOAD` with every violated rule |
| SQLite write fails | — | `EXEC_FAILED`, policy unchanged |

Two design choices make parsing predictable. `JsonUnmappedMemberHandling.Disallow` gives each field exactly one name, so a misspelled or aliased field is refused rather than guessed. Strict number handling never reads numbers from strings, so the result does not depend on the PC's language settings (on a French Windows, `"2.5"` would otherwise fail to parse and the field would be silently dropped).

---

## 3. Dynamic Live Propagation

| Consumer | How it applies the update |
|---|---|
| `HeartbeatWorker` | `PolicyUpdated` → `timer.Period` changed immediately |
| `HardwareMonitorService` | `PolicyUpdated` → `timer.Period` (cadence × 3 while idle) |
| `HardwareAlertEvaluator` | Reads thresholds and cooldown on every evaluation |
| `LeaseManager` | Reads lease duration (cap/default) and grace on every grant / status check |
| `UsbMonitorService` | Reads debounce window and anti-theft toggle on every removal |
| `StationController` | Reads `StopGameOnLock` on every `LOCK` |

`PolicyStore` invokes each `PolicyUpdated` subscriber in its own `try/catch`, so one failing consumer cannot stop the others from receiving the update.

---

## 4. SQLite Persistence

```sql
CREATE TABLE IF NOT EXISTS StationPolicy
(
    Id TEXT PRIMARY KEY,                         -- always 'active'
    TelemetryCadenceSeconds REAL NOT NULL,
    HeartbeatIntervalSeconds REAL NOT NULL,
    DefaultLeaseDurationSeconds REAL NOT NULL,
    LeaseGracePeriodSeconds REAL NOT NULL,
    CpuTempAlertThreshold REAL NOT NULL,
    GpuTempAlertThreshold REAL NOT NULL,
    CpuLoadAlertThreshold REAL NOT NULL,
    RamLoadAlertThreshold REAL NOT NULL,
    HardwareAlertCooldownSeconds REAL NOT NULL,
    UsbDebounceWindowSeconds REAL NOT NULL,
    EnableAntiTheftAlerts INTEGER NOT NULL,
    UpdatedAt TEXT NOT NULL,
    StopGameOnLock INTEGER NOT NULL DEFAULT 1    -- added by migration v2
);
```

- Upsert: `ON CONFLICT(Id) DO UPDATE SET …`
- Timestamps written and parsed with `CultureInfo.InvariantCulture` and round-trip format.
- Read-modify-write is serialized by a `SemaphoreSlim`, so two updates cannot overwrite each other.

---

## 5. Startup & Cold Restart Resilience

```text
Program.InitializeLocalStateAsync (before any worker starts)
      │
      ├── DatabaseInitializer.InitializeAsync()     (migrations; no policy seeding anymore)
      │
      └── PolicyStore.InitializeAsync()
             │
             ├── row found AND valid ──► loaded (tuned values survive restarts)
             ├── row found but INVALID ─► ERROR logged, defaults used and persisted
             ├── no row ────────────────► defaults persisted
             └── SQLite unusable ───────► ERROR logged, in-memory defaults
                                          (lock enforcement never depends on SQLite)
```

A policy loaded from disk is **validated** too, so a corrupted or tampered row cannot, for example, set a one-hour lease.

---

## Implementation Summary

- Immutable `StationPolicy` record as the single source of defaults
- Finite-range validation (`NaN`/`Infinity` rejected) and heartbeat ≤ half-lease invariant
- Strict `POLICY_UPDATE` parsing: canonical camelCase, unknown fields and string numbers rejected
- Empty update → `INVALID_PAYLOAD`; persistence failure → `EXEC_FAILED`
- Serialized updates; subscribers isolated from each other's failures
- Live propagation to heartbeat, telemetry, alerts, lease, anti-theft and lock behaviour
- Stored policy validated on startup; safe fallbacks when SQLite fails
- New `StopGameOnLock` policy (migration v2)
- Unit tests for defaults, bounds, `NaN`, invariant and partial `Apply`
