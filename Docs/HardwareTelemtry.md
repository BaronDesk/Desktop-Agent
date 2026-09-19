# BaronDesk Agent — Hardware Telemetry & Outbox

This document describes the current implementation of hardware telemetry
and the SQLite outbox in `BaronDeskAgent.ServiceCore`.

---

## Folder Structure

```text
BaronDeskAgent
│
├── BaronDesk.Shared
│   └── Models
│       ├── HardwareTelemetry.cs
│       ├── DeviceTelemetry.cs
│       └── TelemetryEnvelope.cs
│
└── BaronDeskAgent.ServiceCore
    │
    ├── Hardware
    │   ├── HardwareSensorReader.cs
    │   ├── HardwareMonitorService.cs
    │   ├── WindowsDeviceEnumerator.cs
    │   └── WindowsDeviceMonitorService.cs
    │
    ├── Services
    │   ├── TelemetryService.cs
    │   ├── ITelemetryTransport.cs
    │   ├── LoggingTelemetryTransport.cs
    │   ├── TelemetryMessagePolicy.cs
    │   │
    │   └── Outbox
    │       └── OutboxWorker.cs
    │
    └── Data
        ├── Database
        │   ├── AgentDatabase.cs
        │   ├── DatabaseInitializer.cs
        │   └── DatabasePaths.cs
        │
        ├── Entities
        │   └── OutboxMessageEntity.cs
        │
        └── Repositories
            └── OutboxRepository.cs
```

---

## 1. Hardware Telemetry

Hardware monitoring collects:

- CPU usage
- RAM usage
- GPU usage
- GPU memory
- GPU temperatures
- Fan information when available

### Pipeline

```text
HardwareSensorReader
        │
        ▼
HardwareMonitorService
        │
        │ every ~5 seconds
        ▼
HardwareTelemetry
        │
        ▼
TelemetryService
        │
        ▼
LoggingTelemetryTransport
```

Hardware telemetry is live-only and is **not stored in the SQLite outbox**.

---

## 2. Device Monitoring

Windows device changes are monitored separately.

### Pipeline

```text
WindowsDeviceMonitorService
        │
        ▼
DeviceTelemetry
        │
        ▼
TelemetryService
```

Device events such as device connected and device disconnected are treated
as important events.

---

## 3. Message Classification

`TelemetryMessagePolicy.cs` determines whether a message requires durable
storage.

```text
Message
   │
   ▼
TelemetryMessagePolicy
   │
   ├── telemetry ────────► Live only
   │
   ├── heartbeat ────────► Live only
   │
   └── device_event ─────► SQLite Outbox
```

Current durable message types include:

```text
device_event
session_started
session_ended
lease_changed
command_acknowledgement
agent_status_changed
```

---

## 4. SQLite Outbox

Important messages are stored in SQLite before being sent.

### Pipeline

```text
Important Event
      │
      ▼
TelemetryService
      │
      ▼
TelemetryMessagePolicy
      │
      ▼
OutboxRepository
      │
      ▼
SQLite
      │
      ▼
OutboxWorker
      │
      ▼
Telemetry Transport
```

The outbox stores:

```text
Id
Type
Payload
CreatedAt
Attempts
```

---

## 5. Successful Delivery

When `OutboxWorker` successfully sends a message:

```text
SQLite Outbox
      │
      ▼
Send message
      │
      ▼
Success
      │
      ▼
Delete message from SQLite
```

Successfully delivered messages are removed from the outbox.

---

## 6. Current Overall Pipeline

```text
                         ┌─────────────────────┐
                         │ HardwareSensorReader │
                         └──────────┬──────────┘
                                    │
                              every ~5 sec
                                    │
                                    ▼
                         ┌─────────────────────┐
                         │ HardwareMonitor     │
                         │ Service             │
                         └──────────┬──────────┘
                                    │
                                    ▼
                         ┌─────────────────────┐
                         │ TelemetryService    │
                         └──────────┬──────────┘
                                    │
                         ┌──────────┴──────────┐
                         │                     │
                   telemetry             device_event
                         │                     │
                         ▼                     ▼
                   Live Transport        SQLite Outbox
                                               │
                                               ▼
                                         OutboxWorker
                                               │
                                               ▼
                                         Live Transport
```

---

## Current Status

### Hardware Telemetry

- [x] CPU monitoring
- [x] RAM monitoring
- [x] GPU monitoring
- [x] Fan monitoring
- [x] Periodic sampling
- [x] Central `TelemetryService`
- [x] Live transport
- [x] Not stored in SQLite

### Outbox

- [x] SQLite persistence
- [x] Important message classification
- [x] Device events stored
- [x] Outbox worker
- [x] Pending message retrieval
- [x] Successful message deletion
- [x] Attempt counter
- [ ] Real WebSocket transport
- [ ] Network failure/retry testing
- [ ] Retry backoff
