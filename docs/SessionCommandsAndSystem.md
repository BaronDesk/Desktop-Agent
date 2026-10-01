# BaronDesk Agent : Session Teardown & OS System Control Architecture

This document describes clean session teardown (`END_SESSION`, lease fail-closed, `LOCK`) and OS power management (`SHUTDOWN`) in `BaronDeskAgent.ServiceCore`.

Games (catalog delivery, launching, Session 0 launching, process tracking, installed game discovery) are in `GamesHandling.md`.

---

## Folder Structure

```text
Desktop-Agent
│
└── src
    ├── BaronDesk.Shared
    │   └── Contracts
    │       └── CommandPayloads.cs          (ShutdownPayload, EndSessionPayload)
    │
    └── BaronDeskAgent.ServiceCore
        │
        ├── Commands
        │   └── Handlers
        │       ├── EndSessionCommandHandler.cs
        │       └── ShutdownCommandHandler.cs
        │
        ├── Games
        │   └── GameService.cs              (StopCurrentGameAsync, see GamesHandling.md)
        │
        ├── Power
        │   └── SystemPowerService.cs
        │
        └── Session
            └── StationController.cs        (stops the game on LOCK / END_SESSION / fail-closed)
```

---

## 1. Clean Session Teardown & Fail-Closed Integration

All teardown goes through `StationController` (one gate, one code path):

```text
END_SESSION / lease fail-closed / SHUTDOWN preparation
      │
      ├── 1. LeaseManager.Revoke()
      ├── 2. LockService.LockAsync()           ◄── screen covered first
      ├── 3. GameService.StopCurrentGameAsync()  (errors logged, never skip the lock)
      └── 4. SessionService.End(reason)

LOCK
      ├── 1. LeaseManager.Revoke()
      ├── 2. LockService.LockAsync()
      └── 3. StopCurrentGameAsync() if StationPolicy.StopGameOnLock   (session stays bound)
```

Every way a session ends goes through this single path, so the game is stopped exactly once and in a known order.

How the game itself is found and closed (graceful close, then process-tree kill; launchers never killed) is in `GamesHandling.md` §4.

---

## 2. Real OS Power Execution

```text
SHUTDOWN { action, delaySeconds, reason }
      │
      ▼
ShutdownCommandHandler
      ├── validate, then command_ack BEFORE executing
      ├── StationController.PrepareForShutdownAsync()   (lock, stop game, end session)
      └── SystemPowerService.Execute(action, delay, reason)
```

`SystemPowerService.Execute(PowerAction action, TimeSpan delay, string reason)` runs `%SystemRoot%\System32\shutdown.exe`:

| Action | Arguments |
|---|---|
| `Shutdown` | `/s /f /t <delay> /c <reason>` |
| `Restart` | `/r /f /t <delay> /c <reason>` |

- Arguments go through `ProcessStartInfo.ArgumentList` (quoted per argument), so a reason cannot inject extra switches. Reasons are truncated to 256 characters.
- A non-zero exit code from `shutdown.exe` is reported as a failure (logged after the early ack).
- `SHUTDOWN` payload: `action` = `"shutdown"` (default) or `"restart"`, `delaySeconds` = `0`–`600` (default `2`, so the ack leaves the network card first).

```json
{
  "type": "SHUTDOWN",
  "id": "3c8d6a1e-4b2f-4d0e-9a51-7f6e2c1b0a93",
  "ts": "2026-09-22T22:00:00.000Z",
  "seq": 120,
  "payload": { "action": "restart", "delaySeconds": 5, "reason": "Maintenance" }
}
```

---

## 3. Edge Cases Handled

| Component | Edge Case | Handling / Resolution |
|---|---|---|
| `StationController` | `LOCK` while a game runs in exclusive fullscreen | Game stopped (`StopGameOnLock`), since the overlay cannot cover it |
| `StationController` | Error while stopping the game | Logged; the station is already locked |
| `ShutdownCommandHandler` | Invalid `action` / `delaySeconds` | `INVALID_PAYLOAD`, nothing executed, no early ack |
| `ShutdownCommandHandler` | `shutdown.exe` refuses | Logged; the station is already locked |
| `SystemPowerService` | Reason containing quotes or switches | Passed as a single quoted argument |

---

## Implementation Summary

- Teardown through one `StationController` path (overlay first, then game, then session)
- `LOCK` stops the game by policy (`StopGameOnLock`)
- `SHUTDOWN` validated, acked early, station locked before power-off
- `shutdown.exe` invoked with `ArgumentList` and exit-code checking
