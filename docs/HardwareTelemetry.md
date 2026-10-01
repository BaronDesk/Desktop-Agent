# BaronDesk Agent : Hardware Telemetry & Outbox

This document describes hardware sampling, delta-based telemetry streaming, and the SQLite outbox that delivers alerts reliably in `BaronDeskAgent.ServiceCore`.

Alert evaluation and USB anti-theft are described in `TelemetryAlerts.md`.

---

## Folder Structure

```text
Desktop-Agent
│
├── src
│   ├── BaronDesk.Shared
│   │   └── Contracts
│   │       ├── TelemetryPayload.cs       (samples[])
│   │       ├── TelemetrySample.cs        (metric, value, sampledAt)
│   │       └── AlertPayload.cs
│   │
│   └── BaronDeskAgent.ServiceCore
│       │
│       └── Telemetry
│           ├── HardwareSensorReader.cs   (LibreHardwareMonitor)
│           ├── HardwareTelemetry.cs      (internal snapshot model)
│           ├── HardwareTelemetryMapper.cs
│           ├── TelemetryDeltaFilter.cs
│           ├── HardwareAlertEvaluator.cs (see TelemetryAlerts.md)
│           ├── HardwareMonitorService.cs
│           ├── TelemetryPublisher.cs
│           └── Outbox
│               ├── OutboxMessageEntity.cs
│               ├── OutboxRepository.cs
│               ├── OutboxQueue.cs
│               └── OutboxWorker.cs
│
└── tests
    └── BaronDeskAgent.ServiceCore.Tests
        └── Telemetry
            ├── TelemetryDeltaFilterTests.cs
            └── HardwareAlertEvaluatorTests.cs
```

---

## 1. Hardware Telemetry

The backend keeps only the **latest** reading per metric in Redis (30 s TTL) and writes no database row per reading. The agent therefore streams **lightly and on change**, at an adaptive cadence.

### Pipeline

```text
HardwareMonitorService (PeriodicTimer)
        │
        │  cadence = policy TelemetryCadenceSeconds          during a session
        │            policy TelemetryCadenceSeconds × 3      while idle (no session)
        ▼
HardwareSensorReader.Read(ServerClock.UtcNow)
        │   one LibreHardwareMonitor Computer, opened once
        │   CPU, GPU, memory, motherboard (fan headers)
        ▼
HardwareTelemetry (snapshot)
        │
        ├──────────────────────────────► HardwareAlertEvaluator ──► alerts ──► outbox
        │
        ▼
HardwareTelemetryMapper  →  TelemetrySample[]
        │
        ▼
TelemetryDeltaFilter  →  only meaningful changes (+ full snapshot before the 30 s TTL)
        │
        ▼
TelemetryPublisher.PublishTelemetryAsync
        │
        ├── connection ready  ──► WebSocket "telemetry"
        └── offline           ──► dropped (cache-only data), delta filter reset
```

### Sensor Reading

| Hardware | Readings |
|---|---|
| CPU | package temperature (prioritized sensor names), total load, max core load |
| RAM | used, available, total (GB), usage % |
| GPU (NVIDIA / AMD / Intel, each) | core, hot-spot and memory-junction temperature, load, VRAM used/free/total |
| Fans | speed (RPM) from the motherboard's Super I/O chip |

Notes:

- The update visitor also updates **sub-hardware**: the Super I/O chip that carries the fan sensors is sub-hardware of the motherboard, so without it fan readings would never refresh.
- `IsControllerEnabled` stays off: no controller data is reported, and opening it costs footprint.
- On some Ryzen parts `Tctl` includes a +10–20 °C offset, so package sensors are preferred.

### Failure Isolation

```text
Sensor.Open() fails (no admin rights, driver blocked by AV/HVCI)
      └── ERROR logged, telemetry + hardware alerts disabled, AGENT KEEPS RUNNING

One sample throws
      └── WARNING logged, retried on the next tick
```

This isolation matters: an exception escaping a hosted service stops the whole .NET host by default (`StopHost`), which would take lock enforcement down with it. A sensor problem must never unlock or stop a station.

---

## 2. Delta Filter

`TelemetryDeltaFilter` sends a sample only when it moved by at least:

| Metric suffix | Threshold |
|---|---|
| `_c` (temperature) | 1.0 °C |
| `_percent` | 2.0 points |
| `_rpm` | 50 RPM |
| `_gb` | 0.1 GB |
| `_mb` | 64 MB |

A **full snapshot** goes out when the next tick would come too late for the backend cache:

```text
elapsed since last full snapshot + current interval ≥ 30 s TTL − 5 s margin  →  send everything
```

After a disconnect the filter is reset, so the first sample after reconnecting is a full snapshot.

---

## 3. Metric Naming

`HardwareTelemetryMapper` flattens a snapshot into `NODE_TELEMETRY`-shaped samples (`metric`, `value`, `sampledAt`). Non-finite values are skipped; values are rounded to 4 decimals.

| Metric | Example |
|---|---|
| `cpu.load_percent`, `cpu.temperature_c`, `cpu.core_max_load_percent` | `24.36` |
| `memory.used_gb`, `memory.available_gb`, `memory.total_gb`, `memory.usage_percent` | `17.09` |
| `gpu.{i}.load_percent`, `gpu.{i}.temperature_c`, `gpu.{i}.hotspot_temperature_c`, `gpu.{i}.memory_temperature_c` | `48` |
| `gpu.{i}.memory_used_mb`, `gpu.{i}.memory_free_mb`, `gpu.{i}.memory_total_mb` | `1267` |
| `fan.{i}.speed_rpm` | `1180` |

---

## 4. Message Classification

| Message | Path | Buffered offline? |
|---|---|---|
| `telemetry` | `TelemetryPublisher` → WebSocket | **No**: cache-only on the backend |
| `alert` | `TelemetryPublisher` → **SQLite outbox** → `OutboxWorker` → WebSocket | **Yes**, always (even when online) |
| `heartbeat`, `handshake`, `state_report` | Sent directly by their workers when the connection is ready | No |
| `command_ack` / `command_nack` | `CommandDispatcher` | No: the backend redelivers unacked commands |
| `login_request` | `LoginRelay` | No: no new sessions offline |

The agent sends only message types the backend defines; there are no extra or legacy message types.

---

## 5. SQLite Outbox

Alerts always go through the outbox. That makes them durable across outages and restarts, and delivers them **in order**: a live alert never overtakes older buffered ones.

### Pipeline

```text
TelemetryPublisher.PublishAlertAsync(alert)
        │
        ▼
OutboxQueue.EnqueueAsync(type, payloadJson)
        │
        ├── INSERT (new id, payload JSON only — no seq/ts)
        ├── trim to 1,000 rows (oldest dropped, warning logged)      ◄── one transaction
        └── signal the worker (bounded channel, collapses to 1)
        │
        ▼
OutboxWorker (sleeps until there is work AND a ready connection)
        │
        ├── WaitUntilReadyAsync()
        ├── GetOldestAsync(20)   (insertion order, rowid)
        │     └── empty → WaitForWorkAsync()
        │
        └── for each message, in order
              ├── corrupt JSON             → deleted (logged)
              ├── SendAsync(type, payload, messageId: original id)
              │     └── success           → deleted
              └── failure
                    ├── connection lost   → not counted, wait for reconnect
                    ├── 10th failure      → dead-lettered (deleted, ERROR logged)
                    └── otherwise         → Attempts + 1, retry in 5 s (order kept)
```

### Design Choices

| Choice | Why |
|---|---|
| Stores the payload only; `seq`/`ts` stamped at send, original `id` kept | A stored `seq`/`ts` would be stale on resend and refused by the backend's anti-replay; the `id` lets the backend deduplicate |
| Waits for `IsReady`, 5 s retry delay | No busy loop of failing sends and SQLite writes while offline |
| Wakes on a signal from `EnqueueAsync` | No polling of SQLite when the queue is empty |
| Logs and retries after an unexpected exception | The worker never stops for good |
| Undeserializable rows are deleted | A single bad row cannot block the queue |
| At most 1,000 rows, oldest dropped first | Bounded disk use during a long outage |

### Schema: `OutboxMessages`

```sql
CREATE TABLE IF NOT EXISTS OutboxMessages
(
    Id TEXT PRIMARY KEY,
    Type TEXT NOT NULL,
    Payload TEXT NOT NULL,       -- payload JSON only
    CreatedAt TEXT NOT NULL,     -- server clock, ISO 8601
    Attempts INTEGER NOT NULL DEFAULT 0
);
```

---

## 6. Wire Protocol Envelope

### Telemetry (changes only)

```json
{
  "type": "telemetry",
  "id": "4ef7042d-d904-4327-94b7-8fbdad52c3a7",
  "ts": "2026-09-22T21:42:15.004Z",
  "seq": 7,
  "payload": {
    "samples": [
      { "metric": "cpu.load_percent", "value": 20.35, "sampledAt": "2026-09-22T21:42:15.001Z" },
      { "metric": "cpu.core_max_load_percent", "value": 55.44, "sampledAt": "2026-09-22T21:42:15.001Z" },
      { "metric": "gpu.0.load_percent", "value": 0, "sampledAt": "2026-09-22T21:42:15.001Z" }
    ]
  }
}
```

### Alert (via the outbox)

```json
{
  "type": "alert",
  "id": "c1d2e3f4-a5b6-4c7d-8e9f-0a1b2c3d4e5f",
  "ts": "2026-09-22T21:50:02.410Z",
  "seq": 58,
  "payload": {
    "category": "hardware",
    "type": "TEMPERATURE_WARNING",
    "severity": "HIGH",
    "detail": "CPU temperature at 88.0 °C exceeded the 85.0 °C threshold.",
    "occurredAt": "2026-09-22T21:50:01.998Z"
  }
}
```

`ts` and `sampledAt` / `occurredAt` use the estimated server clock (see `WebSocketConnection.md` §6).

---

## 7. Footprint

- Event-driven workers; no polling loops (outbox signal, `PeriodicTimer`, CM notifications).
- Adaptive cadence (3× slower while idle), delta telemetry, one reused JSON writer and receive buffer.
- Per-frame logs are at `Debug` level, so the default `Information` log stays quiet during normal operation.
- ServiceCore: workstation, non-concurrent GC, `InvariantGlobalization`, `UseSystemResourceKeys`.

---

## Implementation Summary

### Hardware Telemetry

- CPU, RAM, GPU (per GPU) and fan monitoring through one LibreHardwareMonitor handle
- Sub-hardware updated (fan readings refreshed)
- Sensor failures isolated: the agent keeps running
- Adaptive cadence (session vs idle), live policy updates
- Delta filter with TTL-safe full snapshots
- `NODE_TELEMETRY`-shaped `samples` payload, server-clock timestamps
- Telemetry never buffered offline (cache-only data)

### Outbox

- Alerts always persisted before sending; delivered in insertion order
- Payload-only rows; fresh `seq`/`ts` at send; original id for deduplication
- Event-driven worker, waits for a ready connection (no busy loop)
- Poison messages removed; dead-letter after 10 failed attempts while connected
- Bounded at 1,000 rows (oldest dropped first)
- Worker never stops for good on an error
