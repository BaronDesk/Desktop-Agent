# Branch 8: Policy Store & Dynamic Live Configuration Updates

## Overview

Branch 8 implements local SQLite persistence for workstation operational policies and provides real-time, dynamic reconfiguration via the `POLICY_UPDATE` WebSocket command. Running services (heartbeat worker, hardware telemetry sampler, threshold alert evaluator, session lease manager, and USB anti-theft watcher) adapt their operating intervals and thresholds immediately without requiring a service restart.

---

## Architecture

```
                                  BACKEND / SERVER
                                         │
                             WSS: Envelope<CommandRequest>
                             Type: "POLICY_UPDATE"
                                         │
                                         ▼
                               ┌───────────────────┐
                               │  CommandService   │
                               └─────────┬─────────┘
                                         │
                                         ▼
                        ┌─────────────────────────────────┐
                        │   PolicyUpdateCommandHandler    │
                        │  - Parse partial/full payload   │
                        │  - Validate operational bounds  │
                        └────────────────┬────────────────┘
                                         │
                                         ▼
                             ┌───────────────────────┐
                             │      IPolicyStore     │
                             │      (PolicyStore)    │
                             └───────┬───────────────┘
                                     │
           ┌─────────────────────────┼─────────────────────────┐
           │                         │                         │
     Persist to SQLite         Swap Reference          Fire OnPolicyUpdated
           │                         │                         │
           ▼                         ▼                         ▼
┌─────────────────────┐    ┌───────────────────┐   ┌───────────────────────────┐
│   PolicyRepository  │    │  _currentPolicy   │   │ Dynamic Service Consumers:│
│   (StationPolicy)   │    │  (thread-safe)    │   │ - HeartbeatWorker         │
│   agent.sqlite      │    └───────────────────┘   │ - HardwareMonitorService  │
└─────────────────────┘                            │ - HardwareAlertEvaluator  │
                                                   │ - LeaseManager            │
                                                   │ - WinDeviceMonitorService │
                                                   └───────────────────────────┘
```

---

## SQLite Database Schema

Persisted in the shared SQLite database (`agent.sqlite` with `WAL` journal mode and `NORMAL` synchronous pragma):

```sql
CREATE TABLE IF NOT EXISTS StationPolicy
(
    Id TEXT PRIMARY KEY,
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
    UpdatedAt TEXT NOT NULL
);
```

- Primary Key: `Id = 'active'` (single row active pattern for workstation configuration).
- Auto-seeded with default values if table is empty on startup.
- Upsert semantics: `ON CONFLICT(Id) DO UPDATE SET ...`

---

## Wire Protocol (`POLICY_UPDATE`)

### Command Request Envelope

```json
{
  "type": "POLICY_UPDATE",
  "id": "11111111-2222-3333-4444-555555555555",
  "ts": "2026-09-22T18:00:00.000Z",
  "seq": 105,
  "payload": {
    "telemetryCadenceSeconds": 3.0,
    "heartbeatIntervalSeconds": 10.0,
    "cpuTempAlertThreshold": 80.0,
    "enableAntiTheftAlerts": true
  }
}
```

### Flexible Wire Property Mapping

`PolicyUpdatePayload` supports partial updates (only non-null fields modify existing policy) and accepts both camelCase and snake_case aliases:

| Property | Aliases | Default | Valid Range |
|---|---|---|---|
| `TelemetryCadenceSeconds` | `telemetryCadenceSeconds`, `telemetry_cadence_seconds`, `cadenceSeconds` | `5.0s` | `1.0s` – `300.0s` |
| `HeartbeatIntervalSeconds` | `heartbeatIntervalSeconds`, `heartbeat_interval_seconds`, `heartbeatSeconds` | `15.0s` | `1.0s` – `120.0s` |
| `DefaultLeaseDurationSeconds` | `defaultLeaseDurationSeconds`, `default_lease_duration_seconds`, `leaseDurationSeconds` | `60.0s` | `5.0s` – `3600.0s` |
| `LeaseGracePeriodSeconds` | `leaseGracePeriodSeconds`, `lease_grace_period_seconds`, `gracePeriodSeconds` | `10.0s` | `1.0s` – `300.0s` |
| `CpuTempAlertThreshold` | `cpuTempAlertThreshold`, `cpu_temp_alert_threshold`, `cpuTempThreshold` | `85.0°C` | `30.0°C` – `120.0°C` |
| `GpuTempAlertThreshold` | `gpuTempAlertThreshold`, `gpu_temp_alert_threshold`, `gpuTempThreshold` | `85.0°C` | `30.0°C` – `120.0°C` |
| `CpuLoadAlertThreshold` | `cpuLoadAlertThreshold`, `cpu_load_alert_threshold`, `cpuLoadThreshold` | `95.0%` | `10.0%` – `100.0%` |
| `RamLoadAlertThreshold` | `ramLoadAlertThreshold`, `ram_load_alert_threshold`, `ramLoadThreshold` | `95.0%` | `10.0%` – `100.0%` |
| `HardwareAlertCooldownSeconds` | `hardwareAlertCooldownSeconds`, `hardware_alert_cooldown_seconds`, `alertCooldownSeconds` | `60.0s` | `1.0s` – `600.0s` |
| `UsbDebounceWindowSeconds` | `usbDebounceWindowSeconds`, `usb_debounce_window_seconds`, `debounceSeconds` | `5.0s` | `0.5s` – `60.0s` |
| `EnableAntiTheftAlerts` | `enableAntiTheftAlerts`, `enable_anti_theft_alerts`, `enableAntiTheft` | `true` | `boolean` |

### Invariant Validation Rules

- `HeartbeatIntervalSeconds < DefaultLeaseDurationSeconds`: Strictly enforced. Prevents the heartbeat cadence from exceeding the lease window, which would trigger false fail-closed lockouts.
- If any validation rule fails, `PolicyUpdateCommandHandler` returns `CommandResponse { Success = false, Error = "..." }`, which sends a `command_nack` to the server and keeps the previous policy unchanged.

---

## Dynamic Live Propagation

Running services adapt live when `IPolicyStore.OnPolicyUpdated` fires or by querying `_policyStore.CurrentPolicy`:

1. **`HeartbeatWorker`**:
   - Updates `timer.Period = TimeSpan.FromSeconds(policy.HeartbeatIntervalSeconds)` dynamically.
   - Adjusts presence heartbeat rate on the fly.
2. **`HardwareMonitorService`**:
   - Updates `timer.Period = TimeSpan.FromSeconds(policy.TelemetryCadenceSeconds)` dynamically.
   - Changes sensor sampling rate without interrupting the sensor session.
3. **`HardwareAlertEvaluator`**:
   - Reads active thresholds directly from `_policyStore.CurrentPolicy` on each evaluation cycle.
   - New temperature and load limits take effect immediately.
4. **`LeaseManager`**:
   - Reads `DefaultLeaseDurationSeconds` and `LeaseGracePeriodSeconds` dynamically.
   - Subsequent lease extensions and fail-closed grace windows use the updated duration.
5. **`WindowsDeviceMonitorService`**:
   - Reads `UsbDebounceWindowSeconds` and `EnableAntiTheftAlerts` dynamically.
   - If anti-theft is disabled live, disconnect events are ignored without triggering alerts.

---

## Startup & Cold Restart Resilience

1. `DatabaseInitializer.InitializeAsync()`:
   - Ensures `StationPolicy` table exists.
   - Seeds default policy row if the table was empty.
2. `PolicyStore.InitializeAsync()`:
   - Reads the active policy row from SQLite.
   - If the station was previously reconfigured via `POLICY_UPDATE`, tuned values are reloaded from disk on cold agent restart.
