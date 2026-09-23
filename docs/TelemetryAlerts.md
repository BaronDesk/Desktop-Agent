# BaronDesk Agent : Telemetry Alerts & Anti-Theft Architecture

This document describes the current implementation of hardware threshold alerts, USB anti-theft monitoring, and security-violation alerts in `BaronDeskAgent.ServiceCore`. All alerts are delivered through the SQLite outbox (see `HardwareTelemetry.md` §5).

---

## Folder Structure

```text
Desktop-Agent
│
├── src
│   ├── BaronDesk.Shared
│   │   └── Contracts
│   │       ├── AlertPayload.cs
│   │       └── AlertCodes.cs           (AlertCategories, AlertSeverities, AlertTypes)
│   │
│   └── BaronDeskAgent.ServiceCore
│       │
│       ├── Telemetry
│       │   ├── HardwareAlertEvaluator.cs
│       │   ├── HardwareMonitorService.cs
│       │   └── TelemetryPublisher.cs
│       │
│       ├── AntiTheft
│       │   ├── UsbDeviceEnumerator.cs  (SetupAPI, HID class)
│       │   └── UsbMonitorService.cs    (CM_Register_Notification, debounce)
│       │
│       └── Ipc
│           └── PipeServer.cs           (security_violation alerts)
│
└── tests
    └── BaronDeskAgent.ServiceCore.Tests
        └── Telemetry
            └── HardwareAlertEvaluatorTests.cs
```

---

## Architecture

```text
┌─────────────────────────────┐   ┌──────────────────────────────┐   ┌─────────────────────────┐
│  HardwareMonitorService     │   │  UsbMonitorService           │   │  PipeServer             │
│  (sensor snapshot per tick) │   │  (CM_Register_Notification)  │   │  (lock screen watchdog) │
└─────────────┬───────────────┘   └──────────────┬───────────────┘   └───────────┬─────────────┘
              │ HardwareTelemetry                 │ DeviceArrived / DeviceRemoved │
              ▼                                   ▼                              │
┌─────────────────────────────┐   ┌──────────────────────────────┐              │
│  HardwareAlertEvaluator     │   │  Single reader loop          │              │
│  ┌───────────────────────┐  │   │  ┌────────────────────────┐  │              │
│  │ ThresholdAlarm/metric │  │   │  │ baseline, present,     │  │              │
│  │ hysteresis, sustain,  │  │   │  │ pending removals       │  │              │
│  │ cooldown (monotonic)  │  │   │  │ (timers post events)   │  │              │
│  └───────────────────────┘  │   │  └────────────────────────┘  │              │
└─────────────┬───────────────┘   └──────────────┬───────────────┘              │
              │ category: hardware               │ category: anti_theft         │ category: security_violation
              ▼                                   ▼                              ▼
         ┌──────────────────────────────────────────────────────────────────────────────┐
         │  TelemetryPublisher.PublishAlertAsync  →  OutboxQueue  →  OutboxWorker  →  WSS │
         └──────────────────────────────────────────────────────────────────────────────┘
```

---

## 1. AlertPayload (`BaronDesk.Shared/Contracts/AlertPayload.cs`)

Wire contract aligned with the Prisma `TelemetryAlert` model plus the V4 `category`:

| Field | Type | Values |
|---|---|---|
| `category` | `string` | `AlertCategories`: `"hardware"`, `"anti_theft"`, `"security_violation"` |
| `type` | `string` | `AlertTypes`: `"TEMPERATURE_WARNING"`, `"CPU_USAGE"`, `"MEMORY_USAGE"`, `"HARDWARE_FAILURE"` |
| `severity` | `string` | `AlertSeverities`: `"LOW"`, `"MEDIUM"`, `"HIGH"`, `"CRITICAL"` |
| `detail` | `string` | Human-readable description |
| `occurredAt` | `DateTimeOffset` | When the condition happened, on the estimated server clock (required) |

The string values live in shared constants (`AlertCodes.cs`) instead of magic strings scattered across services.

---

## 2. HardwareAlertEvaluator

Evaluates every sensor snapshot against the **current policy** thresholds. Each metric has its own `ThresholdAlarm`:

```text
value < threshold − hysteresis ──► re-arm
value < threshold              ──► reset "above since", stay as is
value ≥ threshold
   ├── not armed                        ──► silent (already reported)
   ├── above for less than "sustain"    ──► silent
   ├── inside cooldown                  ──► silent
   └── otherwise                        ──► FIRE, disarm
```

| Metric key | Type | Hysteresis | Sustain | Severity |
|---|---|---|---|---|
| `cpu_temp` | `TEMPERATURE_WARNING` | 5 °C | none | HIGH; CRITICAL at ≥ threshold × 1.1 |
| `gpu_temp_{i}` (per GPU) | `TEMPERATURE_WARNING` | 5 °C | none | HIGH; CRITICAL at ≥ threshold × 1.1 |
| `cpu_load` | `CPU_USAGE` | 5 points | **60 s** | HIGH; CRITICAL at ≥ 99 % |
| `ram_usage` | `MEMORY_USAGE` | 5 points | **60 s** | HIGH; CRITICAL at ≥ 99 % |

Why the changes:

- **Sustain window for load and RAM:** a game at 100 % CPU is normal. The old evaluator raised (and the backend persisted) a `CPU_USAGE` alert every 60 s during ordinary play.
- **Hysteresis for load and RAM too:** before, only temperatures had it.
- **Monotonic timing:** cooldown and sustain use `TimeProvider.GetTimestamp()`. The old wall-clock cooldown could be suppressed by a clock change.

---

## 3. USB Anti-Theft Monitoring (`AntiTheft/`)

Best-effort detection of a venue peripheral being unplugged and taken.

### Scope (honest limits — state them in the security document)

- **Wired USB HID only** (keyboards, mice, HID interfaces of headsets), selected by device class `HIDClass` (`{745a17a0-74d3-11d0-b6fe-00a0c90f57da}`), independent of the Windows language. The old filter matched the display names "USB Root Hub" / "Hub USB racine", which only work in English and French.
- Wireless dongles, Bluetooth, hubs/KVMs and suspend/resume can hide or fake a theft.
- VID/PID identifies the **model**, not the unit.

### Which Devices Are Watched

```text
Service start ──► every present wired HID device joins the BASELINE
Device arrives
   ├── station idle (locked, no session) ──► joins the baseline (staff installing equipment)
   └── during a session                  ──► the gamer's own device: its removal is ignored
```

Before, any USB device (flash drives, webcams, …) plugged in at any time joined the watch list. A gamer removing their own USB stick raised a CRITICAL anti-theft alert.

### Composite Devices

Interfaces of one physical device (`…&MI_00`, `…&MI_01`) share a **device key**, the parent instance id resolved with `CM_Get_Parent`. A removal only counts once **all** interfaces of the device are gone.

### Debounce State Machine

```text
DeviceRemoved (baseline device, last interface gone, anti-theft enabled)
       │
       ▼
 PendingRemoval { device, generation, removedAt } ── timer: UsbDebounceWindowSeconds
       │
       ├── DeviceArrived with the same key before the timer
       │        → pending entry removed ("reconnected within the debounce window")
       │
       └── timer posts DebounceElapsed(key, generation) into the event channel
                → still pending with the same generation?
                     → alert {
                         category: "anti_theft",
                         type: "HARDWARE_FAILURE",
                         severity: "CRITICAL",
                         detail: "Peripheral removed and not reconnected: <name> (VID_…&PID_…)",
                         occurredAt: <removal time>
                       }
```

### Thread Safety

All anti-theft state (present devices, baseline, pending removals) is touched **only by the single reader loop**:

- The native CfgMgr32 callback only filters and posts `DeviceArrived` / `DeviceRemoved` into a channel.
- Debounce timers post `DebounceElapsed` with a generation number instead of mutating state. A stale timer (the device came back, or a newer removal replaced it) simply no longer matches.

The old implementation mutated a plain `Dictionary` from both the reader loop and the timer threads, and disposed `CancellationTokenSource`s that timers were still linking to.

### Native Registration

- `CM_Register_Notification` with `CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES`. The callback delegate is kept in a field (never garbage collected).
- The instance id is read at offset 8 of `CM_NOTIFY_EVENT_DATA`.
- `CM_Unregister_Notification` blocks until in-flight callbacks return, so the old manual callback counters and wait handles were removed.
- Device lookup on arrival uses `SetupDiOpenDeviceInfo` (one device) instead of enumerating every device on the machine for each event.

---

## 4. Security-Violation Alerts

Raised by `PipeServer` (see `LockUIAndIPC.md`), rate-limited to one per reason every 5 minutes:

| Reason | Detail |
|---|---|
| `helper_missing` | Lock screen helper not running for 30 s while the station should be locked |
| `pipe_squatted` | Another process owns the pipe name (`FirstPipeInstance` failed) |
| `pipe_client_rejected` | A process outside the console session, or not the configured LockUI, tried to use the pipe |

```json
{
  "category": "security_violation",
  "type": "HARDWARE_FAILURE",
  "severity": "HIGH",
  "detail": "The lock screen helper is not running while the station is locked.",
  "occurredAt": "2026-09-22T22:31:40.120Z"
}
```

---

## 5. Configuration (Station Policy)

Thresholds are backend-tuned **policy** (changed with `POLICY_UPDATE`, see `PolicyStore.md`), not `appsettings.json`:

| Property | Default | Bounds |
|---|---|---|
| `CpuTempAlertThreshold` | 85 °C | 30 – 120 |
| `GpuTempAlertThreshold` | 85 °C | 30 – 120 |
| `CpuLoadAlertThreshold` | 95 % | 10 – 100 |
| `RamLoadAlertThreshold` | 95 % | 10 – 100 |
| `HardwareAlertCooldownSeconds` | 60 s | 1 – 600 |
| `UsbDebounceWindowSeconds` | 5 s | 0.5 – 60 |
| `EnableAntiTheftAlerts` | `true` | boolean |

---

## 6. Database Schema Alignment

| Payload field | Prisma `TelemetryAlert` |
|---|---|
| `type` | `TelemetryAlertType` enum |
| `severity` | `AlertSeverity` enum |
| `category` | Not in `schema.prisma` yet (Database V4 drift) — sent anyway |
| `detail`, `occurredAt` | Not in `schema.prisma` yet |

The schema has no anti-theft or security-violation alert types, so those alerts use `HARDWARE_FAILURE` and are distinguished by `category`.

---

## 7. Edge Cases & Failure Handling

| Case | Handling |
|---|---|
| Alert evaluation throws | Logged; the sampling loop continues |
| Sensor value missing | Metric skipped |
| Several GPUs | One alarm per GPU (`gpu_temp_0`, `gpu_temp_1`, …) |
| Temperature oscillating around the threshold | 5 °C hysteresis |
| Sustained 100 % load during play | One alert after 60 s, then silent until it drops 5 points below the threshold |
| USB cable flap / suspend-resume | Reconnect within the debounce window cancels the alert |
| Composite keyboard (several HID interfaces) | One device key; alert only when all interfaces are gone |
| Gamer's own device removed | Ignored (not in the baseline) |
| Anti-theft disabled live | New removals are ignored |
| CM registration fails | Logged; anti-theft unavailable, agent keeps running |
| Offline when an alert fires | Persisted in the outbox, delivered after reconnect |

---

## Current Status

- [x] Shared alert constants (`AlertCategories`, `AlertSeverities`, `AlertTypes`)
- [x] Per-metric `ThresholdAlarm` with hysteresis, sustain window and monotonic cooldown
- [x] Load/RAM alerts only when sustained for 60 s
- [x] Wired USB HID detection by device class (language independent)
- [x] Baseline at startup and while idle; gamers' own devices ignored
- [x] Composite devices tracked per physical device
- [x] Debounce with generation-checked timer events; all state on one reader loop
- [x] Security-violation alerts from the lock screen watchdog, rate-limited
- [x] All alerts delivered through the SQLite outbox
- [x] Unit tests for hysteresis, sustain and severity
- [ ] `category`, `detail`, `occurredAt` and anti-theft types added to `schema.prisma` (backend, V4)
