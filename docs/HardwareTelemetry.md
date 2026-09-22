# BaronDesk Agent : Hardware Telemetry & Outbox

This document describes the current implementation of hardware sampling, delta-based telemetry streaming, and the SQLite outbox that delivers alerts reliably in `BaronDeskAgent.ServiceCore`.

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

- The update visitor now also updates **sub-hardware**. The Super I/O chip that carries the fan sensors is sub-hardware of the motherboard, and it was never refreshed before.
- `IsControllerEnabled` was dropped: no controller data is reported, and opening it costs footprint.
- On some Ryzen parts `Tctl` includes a +10–20 °C offset, so package sensors are preferred.

### Failure Isolation

```text
Sensor.Open() fails (no admin rights, driver blocked by AV/HVCI)
      └── ERROR logged, telemetry + hardware alerts disabled, AGENT KEEPS RUNNING

One sample throws
      └── WARNING logged, retried on the next tick
```

Before the review, `Open()` ran outside any `try`, so a sensor failure faulted the hosted service. With the default `StopHost` behaviour that shut down the whole agent, lock enforcement included.

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

The old `TelemetryMessagePolicy`, which listed message types the agent never sent, is gone. So are the off-contract `device_event` messages.

---

## 5. SQLite Outbox

Alerts always go through the outbox. That makes them durable across outages and restarts, and delivers them **in order**. Before, a live alert could overtake older buffered ones.

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

### What Changed

| Before | Now |
|---|---|
| Stored whole envelopes with the original `seq`/`ts` | Stores the payload only; `seq`/`ts` stamped at send, original `id` kept for deduplication |
| Busy loop while offline (instant failures + SQLite writes) | Waits for `IsReady`; 5 s retry delay |
| Polled SQLite every 5 s when empty | Wakes on a signal from `EnqueueAsync` |
| Stopped for good on any unexpected exception | Logs and retries after 5 s |
| Undeserializable rows blocked the queue forever | Deleted |
| Unbounded | 1,000 rows, oldest dropped first |

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

## 7. Footprint Notes (15-point deliverable)

- Event-driven workers; no polling loops (outbox signal, `PeriodicTimer`, CM notifications).
- Adaptive cadence (3× slower while idle), delta telemetry, one reused JSON writer and receive buffer.
- Per-frame logs are at `Debug` level (the old code logged at `Information` every 5 s).
- ServiceCore: workstation, non-concurrent GC, `InvariantGlobalization`, `UseSystemResourceKeys`.
- The benchmark report (roadmap branch 10) is still to be written.

---

## Current Status

### Hardware Telemetry

- [x] CPU, RAM, GPU (per GPU) and fan monitoring through one LibreHardwareMonitor handle
- [x] Sub-hardware updated (fan readings refreshed)
- [x] Sensor failures isolated: the agent keeps running
- [x] Adaptive cadence (session vs idle), live policy updates
- [x] Delta filter with TTL-safe full snapshots
- [x] `NODE_TELEMETRY`-shaped `samples` payload, server-clock timestamps
- [x] Telemetry never buffered offline (cache-only data)

### Outbox

- [x] Alerts always persisted before sending; delivered in insertion order
- [x] Payload-only rows; fresh `seq`/`ts` at send; original id for deduplication
- [x] Event-driven worker, waits for a ready connection (no busy loop)
- [x] Poison messages removed; dead-letter after 10 failed attempts while connected
- [x] Bounded at 1,000 rows (oldest dropped first)
- [x] Worker never stops for good on an error
- [ ] Benchmark report proving idle CPU ≪ 1 % and RAM in the tens of MB (roadmap branch 10)
