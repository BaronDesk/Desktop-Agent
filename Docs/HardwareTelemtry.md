# BaronDesk Agent : Hardware Telemetry & Outbox

This document describes the current implementation of hardware telemetry
and the SQLite outbox in `BaronDeskAgent.ServiceCore`.

---

## Folder Structure

```text
BaronDeskAgent
│
├── BaronDesk.Shared
│   ├── Contracts
│   │   ├── Envelope.cs
│   │   ├── AgentJsonContext.cs
│   │   ├── MessageTypes.cs
│   │   ├── CommandTypes.cs
│   │   ├── CommandRequest.cs
│   │   ├── CommandResponse.cs
│   │   └── PipeMessage.cs
│   │
│   └── Models
│       ├── HardwareTelemetry.cs
│       ├── DeviceTelemtry.cs
│       ├── HardwareTelemetryPayload.cs
│       └── NodeTelemetryMetric.cs
│
└── BaronDeskAgent.ServiceCore
    │
    ├── Hardware
    │   ├── HardwareSensorReader.cs
    │   ├── HardwareTelemetryMapper.cs
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

- CPU usage, temperature, and core loads
- RAM usage, available RAM, total RAM, and usage percent
- GPU load, temperatures (core, hotspot, memory), and VRAM metrics
- Fan information and speeds when available

### Pipeline

```text
HardwareSensorReader
        │
        ▼
HardwareMonitorService
        │ every ~5 seconds
        ▼
HardwareTelemetry
        │
        ▼
HardwareTelemetryMapper
        │
        ▼
HardwareTelemetryPayload (IReadOnlyList<NodeTelemetryMetric>)
        │
        ▼
TelemetryService (Wraps in Envelope with monotonic sequence)
        │
        ▼
LoggingTelemetryTransport (ITelemetryTransport)
```

Hardware telemetry is live-only and is **not stored in the SQLite outbox**.

---

## 2. Device Monitoring

Windows device changes are monitored separately using Win32 SetupAPI device enumeration and window message events.

### Pipeline

```text
WindowsDeviceMonitorService
        │
        ▼
DeviceTelemetry
        │
        ▼
TelemetryService (Wraps in Envelope with monotonic sequence)
        │
        ▼
LoggingTelemetryTransport / Outbox
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
   ├── alert ────────────► SQLite Outbox
   ├── command_ack ──────► SQLite Outbox
   ├── command_nack ─────► SQLite Outbox
   ├── state_report ─────► SQLite Outbox
   └── device_event ─────► SQLite Outbox
```

Durable message types include:

```text
alert
command_ack
command_nack
state_report
device_event
session_started
session_ended
lease_changed
command_acknowledgement
agent_status_changed
```

Live-only message types include:

```text
telemetry
heartbeat
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
ITelemetryTransport (Envelope)
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
Send message (Envelope)
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
                         ┌───────────────────────┐
                         │ HardwareSensorReader  │
                         └──────────┬────────────┘
                                    │
                               every ~5 sec
                                    │
                                    ▼
                         ┌───────────────────────┐
                         │ HardwareMonitorService│
                         └──────────┬────────────┘
                                    │
                                    ▼
                         ┌───────────────────────┐
                         │HardwareTelemetryMapper│
                         └──────────┬────────────┘
                                    │
                         HardwareTelemetryPayload
                                    │
                                    ▼
                         ┌───────────────────────┐
                         │   TelemetryService    │
                         │ (Monotonic Sequence)  │
                         └──────────┬────────────┘
                                    │
                         ┌──────────┴──────────┐
                         │                     │
                     telemetry             device_event / alert
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

## 7. Wire Protocol Envelope

All messages sent across transports adhere to the unified envelope specification:

```csharp
public sealed record Envelope
{
    public required string Type { get; init; }
    public required Guid Id { get; init; }
    public required DateTimeOffset Ts { get; init; }
    public required long Seq { get; init; }
    public object? Payload { get; init; }
}
```

Serialized JSON format:

```json
{
  "type": "telemetry",
  "id": "c7a8b2d1-0f4b-4f91-8e56-2e8c1a2b3c4d",
  "ts": "2026-09-20T11:22:33.456Z",
  "seq": 1,
  "payload": {
    "timestamp": "2026-09-20T11:22:33.456Z",
    "metrics": [
      {
        "metric": "cpu.load_percent",
        "value": 14.2,
        "sampledAt": "2026-09-20T11:22:33.456Z"
      }
    ]
  }
}
```

---

## Current Status

### Hardware Telemetry

- [x] CPU monitoring (load, temperature, core max load)
- [x] RAM monitoring (used, available, total, percent)
- [x] GPU monitoring (load, temperature, hotspot, memory temp, VRAM)
- [x] Fan monitoring (speed RPM)
- [x] Flat metric mapping via `HardwareTelemetryMapper` (`NodeTelemetryMetric`)
- [x] Periodic sampling via `PeriodicTimer`
- [x] Central `TelemetryService` with monotonic sequence counter
- [x] Unified `Envelope` serialization
- [x] Live transport (`LoggingTelemetryTransport`)
- [x] Live metrics excluded from SQLite outbox

### Outbox

- [x] SQLite persistence via `OutboxRepository`
- [x] Message durability classification via `TelemetryMessagePolicy`
- [x] Outbox worker background service (`OutboxWorker`)
- [x] Pending batch retrieval
- [x] Unified `Envelope` deserialization and sending
- [x] Successful message deletion
- [x] Attempt counter increments on retry
- [ ] Real WebSocket transport integration
- [ ] Network failure / exponential backoff with jitter
- [ ] Bounded outbox storage policy (drop oldest telemetry on overflow, keep alerts)
