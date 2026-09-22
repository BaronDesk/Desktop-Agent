# Branch 7: Telemetry Alerts & Anti-Theft Debouncing

## Overview

This branch implements real-time hardware alert evaluation and anti-theft USB peripheral monitoring with debounce logic. Alerts are sent to the backend as `alert` envelope payloads via WebSocket, with outbox fallback for offline buffering.

---

## Architecture

```
┌─────────────────────────────┐    ┌─────────────────────────┐
│  HardwareSensorReader       │    │  WindowsDeviceMonitor   │
│  (LibreHardwareMonitor)     │    │  (CM_Register_Notif.)   │
└─────────────┬───────────────┘    └──────────┬──────────────┘
              │ HardwareTelemetry             │ DeviceNotification
              ▼                               ▼
┌─────────────────────────────┐    ┌─────────────────────────┐
│  HardwareAlertEvaluator     │    │  Debounce State Machine │
│  ┌───────────────────────┐  │    │  ┌───────────────────┐  │
│  │ Temperature Hysteresis│  │    │  │ PendingDisconnect │  │
│  │ Cooldown per metric   │  │    │  │ CTS + Timer       │  │
│  │ Severity mapping      │  │    │  └───────────────────┘  │
│  └───────────────────────┘  │    └──────────┬──────────────┘
└─────────────┬───────────────┘               │
              │ AlertPayload[]                │ AlertPayload
              ▼                               ▼
         ┌────────────────────────────────────────┐
         │        TelemetryService                │
         │   PublishAlertAsync(AlertPayload)       │
         │   → Envelope { type: "alert" }         │
         └─────────────────┬──────────────────────┘
                           │
                ┌──────────▼──────────┐
                │  ITelemetryTransport │
                │  (WebSocket / Log)   │
                └─────────────────────┘
```

---

## Components

### 1. AlertPayload (`BaronDesk.Shared/Contracts/AlertPayload.cs`)

Wire-format contract matching the Prisma `TelemetryAlert` model:

| Field       | Type             | Values                                                       |
|-------------|------------------|--------------------------------------------------------------|
| `category`  | `string`         | `"hardware"`, `"anti_theft"`, `"security_violation"`         |
| `type`      | `string`         | `"TEMPERATURE_WARNING"`, `"CPU_USAGE"`, `"MEMORY_USAGE"`, `"HARDWARE_FAILURE"` |
| `severity`  | `string`         | `"LOW"`, `"MEDIUM"`, `"HIGH"`, `"CRITICAL"`                  |
| `detail`    | `string`         | Human-readable description with metric values                |
| `occurredAt`| `DateTimeOffset` | UTC timestamp when the condition was detected                |

### 2. HardwareAlertEvaluator (`ServiceCore/Hardware/HardwareAlertEvaluator.cs`)

Stateful evaluator that checks `HardwareTelemetry` snapshots against configured thresholds each sampling cycle (every 5 seconds):

- **CPU Temperature** → `TEMPERATURE_WARNING` (HIGH/CRITICAL)
- **GPU Temperature** → `TEMPERATURE_WARNING` (HIGH/CRITICAL), per-GPU
- **CPU Load** → `CPU_USAGE` (HIGH at threshold, CRITICAL at 99%+)
- **RAM Usage** → `MEMORY_USAGE` (HIGH at threshold, CRITICAL at 99%+)

**Anti-storm mechanisms:**
- **Cooldown**: Each metric type has a configurable cooldown (`HardwareAlertCooldownSeconds`, default 60s). Alerts are suppressed during cooldown.
- **Hysteresis**: Temperature alerts require a 5°C drop below threshold before re-arming. This prevents oscillation around the threshold boundary.
- **Critical Factor**: Temperatures 10%+ above threshold → CRITICAL instead of HIGH.

### 3. USB Anti-Theft Debouncing (`ServiceCore/Hardware/WindowsDeviceMonitorService.cs`)

Prevents false anti-theft alerts from USB cable flaps:

```
Disconnect Event
       │
       ▼
  Start Debounce Timer (UsbDebounceWindowSeconds, default 5s)
       │
       ├── Device reconnects before timer expires
       │        → Cancel timer, log "flap resolved"
       │
       └── Timer expires, device still absent
                → Emit AlertPayload {
                    category: "anti_theft",
                    type: "HARDWARE_FAILURE",
                    severity: "CRITICAL",
                    detail: "Peripheral disconnected and not restored: ..."
                  }
```

Each pending disconnect is tracked with a `CancellationTokenSource` so reconnection cancels the timer instantly.

### 4. TelemetryService.PublishAlertAsync

New method on `TelemetryService` that wraps `AlertPayload` in a standard `Envelope` with `type: "alert"` and sends via the transport layer. Alerts are flagged by `TelemetryMessagePolicy.RequiresOutbox` as `true`, ensuring they're buffered to SQLite when the WebSocket is disconnected.

---

## Configuration (AgentOptions)

| Property                      | Default | Description                                    |
|-------------------------------|---------|------------------------------------------------|
| `CpuTempAlertThreshold`       | 85.0°C  | CPU temperature threshold for alerts           |
| `GpuTempAlertThreshold`       | 85.0°C  | GPU temperature threshold for alerts           |
| `CpuLoadAlertThreshold`       | 95.0%   | CPU load threshold for alerts                  |
| `RamLoadAlertThreshold`       | 95.0%   | RAM usage threshold for alerts                 |
| `HardwareAlertCooldownSeconds` | 60.0s   | Min interval between same-metric alerts        |
| `UsbDebounceWindowSeconds`    | 5.0s    | USB disconnect debounce window                 |
| `EnableAntiTheftAlerts`       | true    | Master toggle for USB anti-theft monitoring    |

---

## Database Schema Alignment

All alert payloads map directly to the `TelemetryAlert` Prisma model:

- `type` → `TelemetryAlertType` enum (TEMPERATURE_WARNING, CPU_USAGE, MEMORY_USAGE, HARDWARE_FAILURE)
- `severity` → `AlertSeverity` enum (LOW, MEDIUM, HIGH, CRITICAL)
- `category` → String field on `TelemetryAlert` model
- `detail` → String field on `TelemetryAlert` model
- `occurredAt` → DateTime field on `TelemetryAlert` model

---

## Edge Cases & Failure Handling

1. **Alert evaluation failures** are caught and logged without crashing the hardware monitoring loop.
2. **USB debounce timer failures** are caught silently — the worst case is a missed anti-theft alert, not a crash.
3. **Shutdown cleanup** cancels all pending debounce timers via `CancelAllPendingDisconnects()` in the `finally` block.
4. **Null/missing sensor data** is gracefully skipped (all evaluators check `.HasValue` before threshold comparison).
5. **Multiple GPUs** are independently tracked with per-GPU hysteresis keys (`gpu_temp_0`, `gpu_temp_1`, etc.).
