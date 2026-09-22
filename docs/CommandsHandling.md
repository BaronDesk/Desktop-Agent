# BaronDesk Agent : Command Handling Architecture

This document describes the current implementation and architecture of server command execution in `BaronDeskAgent.ServiceCore`: the allow-list, anti-replay, idempotency, payload validation, the six command handlers and the `command_ack` / `command_nack` replies.

---

## Folder Structure

```text
Desktop-Agent
│
├── src
│   ├── BaronDesk.Shared
│   │   └── Contracts
│   │       ├── CommandTypes.cs          (frozen allow-list)
│   │       ├── CommandPayloads.cs       (UNLOCK, LOCK, END_SESSION, LAUNCH_GAME, SHUTDOWN)
│   │       ├── PolicyUpdatePayload.cs   (POLICY_UPDATE)
│   │       ├── CommandAckPayload.cs
│   │       ├── CommandNackPayload.cs
│   │       └── NackCodes.cs
│   │
│   └── BaronDeskAgent.ServiceCore
│       │
│       └── Commands
│           ├── CommandDispatcher.cs
│           ├── CommandOutcomeStore.cs
│           ├── CommandPayload.cs
│           ├── CommandResult.cs         (CommandResult + CommandContext)
│           └── Handlers
│               ├── ICommandHandler.cs
│               ├── LockCommandHandler.cs
│               ├── UnlockCommandHandler.cs
│               ├── EndSessionCommandHandler.cs
│               ├── LaunchGameCommandHandler.cs
│               ├── ShutdownCommandHandler.cs
│               └── PolicyUpdateCommandHandler.cs
│
└── tests
    └── BaronDeskAgent.ServiceCore.Tests
        └── Commands
            ├── CommandDispatcherTests.cs
            └── CommandPayloadTests.cs
```

---

## 1. Command Handling Architecture

```text
Backend Server
      │
      │ { "type": "UNLOCK", "id", "ts", "seq", "payload": { … } }
      ▼
ConnectionWorker (receive loop)
      │
      ▼
CommandDispatcher.DispatchAsync(envelope)
      │
      ├── 1. Allow-list (ordinal, case-sensitive) ─── unknown ──► nack UNKNOWN_TYPE
      │
      ├── 2. ReplayGuard.Validate(seq, ts, enforceFreshness: !handler.IsRestrictive)
      │                                          ─── rejected ──► nack STALE
      │
      ├── 3. CommandOutcomeStore.WasCompleted(id) ─── yes ─────► ack again (no re-execution)
      │
      ├── 4. handler.HandleAsync(CommandContext)
      │         │
      │         ├── parse + validate payload ───── invalid ───► nack INVALID_PAYLOAD
      │         └── execute through services ───── failed ────► nack EXEC_FAILED
      │
      └── 5. success ──► CommandOutcomeStore.MarkCompleted(id) ──► command_ack
```

Nothing a command does can throw out of the receive loop: unexpected exceptions become `EXEC_FAILED`.

---

## 2. CommandDispatcher

```csharp
public sealed class CommandDispatcher
{
    public CommandDispatcher(
        IEnumerable<ICommandHandler> handlers,
        IServerConnection connection,
        ReplayGuard replayGuard,
        CommandOutcomeStore outcomes,
        ILogger<CommandDispatcher> logger);

    public Task DispatchAsync(Envelope<JsonElement> envelope, CancellationToken cancellationToken);
}
```

- Handlers are indexed in a `FrozenDictionary` with `StringComparer.Ordinal`: `"lock"` is not `"LOCK"`.
- The constructor fails fast if any of the six frozen commands has no registered handler.
- Ack/nack sends that fail (connection dropped) are logged, not thrown. The backend redelivers unacknowledged commands, and the outcome store turns a redelivered success into a plain re-ack.

---

## 3. ICommandHandler

```csharp
public interface ICommandHandler
{
    string CommandType { get; }

    // LOCK and END_SESSION: skip the timestamp-freshness check (never the sequence check)
    bool IsRestrictive => false;

    Task<CommandResult> HandleAsync(CommandContext command, CancellationToken cancellationToken);
}
```

### CommandContext

```csharp
public sealed class CommandContext
{
    public Guid CommandId { get; }                 // envelope id
    public JsonElement Payload { get; }
    public DateTimeOffset ServerTimestamp { get; } // envelope ts (server clock)
    public bool WasAcknowledgedEarly { get; }

    public Task AcknowledgeEarlyAsync();           // SHUTDOWN only
}
```

### CommandResult

```csharp
public readonly record struct CommandResult(bool Succeeded, string? Code, string? Reason)
{
    public static CommandResult Success();
    public static CommandResult Invalid(string reason); // INVALID_PAYLOAD
    public static CommandResult Failed(string reason);  // EXEC_FAILED
}
```

Handlers **return** expected failures instead of throwing them.

---

## 4. Current Command Handlers

| Command | Handler | Restrictive | Delegates to |
|---|---|---|---|
| `LOCK` | `LockCommandHandler` | yes | `StationController.LockAsync` |
| `UNLOCK` | `UnlockCommandHandler` | no | `StationController.StartSessionAsync` |
| `END_SESSION` | `EndSessionCommandHandler` | yes | `StationController.EndSessionAsync` |
| `LAUNCH_GAME` | `LaunchGameCommandHandler` | no | `GameService.LaunchAsync` |
| `SHUTDOWN` | `ShutdownCommandHandler` | no | `StationController.PrepareForShutdownAsync` + `SystemPowerService` |
| `POLICY_UPDATE` | `PolicyUpdateCommandHandler` | no | `IPolicyStore.UpdatePolicyAsync` |

Session, lease and lock semantics are in `SessionAndLeaseControl.md`. Games and power are in `SessionCommandsAndSystem.md`, and policy in `PolicyStore.md`.

---

## 5. Payload Parsing & Validation

Payloads are typed records in `BaronDesk.Shared/Contracts/CommandPayloads.cs`, deserialized with the source-generated `AgentJsonContext`:

- camelCase names, case-insensitive reading;
- **strict number handling**: numbers are never parsed from strings, so parsing does not depend on the PC's language (`"2.5"` used to fail on fr-FR machines and was silently dropped);
- one canonical name per field (the old snake_case and short aliases are gone).

`CommandPayload` helpers:

```csharp
// All fields optional: missing/null payload → empty instance; non-object → error
CommandPayload.TryParse(payload, AgentJsonContext.Default.UnlockPayload, out var value, out var error);

// Payload must be present (types with required members)
CommandPayload.TryParseRequired(payload, AgentJsonContext.Default.LoginResultPayload, out var value, out var error);
```

### Required Fields & Bounds

| Command | Field | Rule | Failure |
|---|---|---|---|
| `UNLOCK` | `sessionId` | Required, non-empty GUID (never invented) | `INVALID_PAYLOAD` |
| `UNLOCK` | `leaseSeconds` / `leaseExpiresAt` | Optional; an already-expired lease is refused | `INVALID_PAYLOAD` |
| `LAUNCH_GAME` | `gameId` | Required, 1–128 characters, catalog id only | `INVALID_PAYLOAD` |
| `SHUTDOWN` | `action` | `"shutdown"` (default) or `"restart"` | `INVALID_PAYLOAD` |
| `SHUTDOWN` | `delaySeconds` | Integer `0`–`600`, default `2` | `INVALID_PAYLOAD` |
| `POLICY_UPDATE` | any | At least one known field; unknown fields rejected | `INVALID_PAYLOAD` |
| `LOCK` / `END_SESSION` | any | Malformed payload is logged and **ignored**: locking is always safe | — |

---

## 6. Command Idempotency

`CommandOutcomeStore` keeps the ids of the last **256 successfully executed** commands, in memory and in the SQLite table `HandledCommands`, so it survives an agent restart:

```text
Command id arrives
       │
       ├── In outcome store? ── yes ──► command_ack again (not executed twice)
       │
       └── no ──► execute
                    │
                    ├── success ──► record id ──► command_ack
                    └── failure ──► NOT recorded ──► command_nack
                                    (a redelivery with the same id is executed again)
```

Only successes are recorded. Before the review, the id was recorded **before** execution, so a command that failed and was redelivered got acknowledged without ever running.

---

## 7. Prioritized SHUTDOWN Acknowledgement

A shutdown kills the network before a normal ack could leave, so `ShutdownCommandHandler` acknowledges early:

```text
SHUTDOWN received
      │
      ├── validate payload (invalid → INVALID_PAYLOAD nack, nothing executed)
      ├── command.AcknowledgeEarlyAsync()   ──► outcome recorded + command_ack sent
      ├── StationController.PrepareForShutdownAsync()   (lock, stop game, end session)
      └── SystemPowerService.Execute(action, delay, reason)
             └── failure after the ack → logged (the dispatcher sends nothing else)
```

---

## 8. Command Acknowledgements

### Successful Command (`command_ack`)

```json
{
  "type": "command_ack",
  "id": "74338a04-427f-43a2-9e7a-c47ae3c38460",
  "ts": "2026-09-22T21:42:04.310Z",
  "seq": 3,
  "payload": {
    "commandId": "5b95dee5-1758-47bc-96a2-2b31a9d779b5"
  }
}
```

### Failed Command (`command_nack`)

```json
{
  "type": "command_nack",
  "id": "48778e24-c9c5-4787-9997-82dc0862607a",
  "ts": "2026-09-22T21:42:10.020Z",
  "seq": 6,
  "payload": {
    "commandId": "571ef633-4460-4972-8a01-11c626b0227b",
    "code": "STALE",
    "reason": "Timestamp is 599.9s away from the server clock (max 60s)."
  }
}
```

| Code | Meaning |
|---|---|
| `UNKNOWN_TYPE` | Not one of the six allowed commands |
| `STALE` | Replayed `seq`, or `ts` outside the drift window |
| `INVALID_PAYLOAD` | Missing/malformed fields |
| `EXEC_FAILED` | Valid command that could not be carried out (e.g. lock screen did not confirm, game not installed) |

Reasons never contain file paths or exception messages.

---

## 9. Dependency Injection

```csharp
services.AddSingleton<CommandOutcomeStore>();
services.AddSingleton<ICommandHandler, LockCommandHandler>();
services.AddSingleton<ICommandHandler, UnlockCommandHandler>();
services.AddSingleton<ICommandHandler, EndSessionCommandHandler>();
services.AddSingleton<ICommandHandler, LaunchGameCommandHandler>();
services.AddSingleton<ICommandHandler, ShutdownCommandHandler>();
services.AddSingleton<ICommandHandler, PolicyUpdateCommandHandler>();
services.AddSingleton<CommandDispatcher>();
```

`CommandOutcomeStore.LoadAsync()` runs at startup, before the workers, so redeliveries after a restart are recognized.

---

## 10. Testing the Command System

`CommandDispatcherTests` (real SQLite outcome store, fake connection, scripted handlers):

| Test | Guarantees |
|---|---|
| `Unknown_types_are_rejected` | `UNKNOWN_TYPE` nack |
| `Command_types_are_matched_case_sensitively` | `"lock"` is rejected, handler not run |
| `A_successful_command_is_acked_once_and_never_executed_twice` | Redelivered success → re-ack only |
| `A_failed_command_is_executed_again_when_redelivered_and_never_falsely_acked` | Failed commands are retried, never falsely acked |
| `Stale_commands_are_rejected` | `STALE` for drifted non-restrictive commands |
| `Restrictive_commands_are_executed_even_with_a_drifted_timestamp` | `LOCK`, `END_SESSION` run despite drift |
| `Replayed_sequence_numbers_are_rejected_even_for_restrictive_commands` | `seq` check always applies |
| `Early_acknowledgement_is_the_only_reply` | `SHUTDOWN` sends exactly one ack |
| `A_throwing_handler_is_nacked_as_exec_failed` | Exceptions never escape |

`CommandPayloadTests` covers strict number parsing, unknown policy fields, and missing/non-object payloads. The mock server (`tools/mock-server`) exercises every command end to end (see `WebSocketConnection.md` §11).

---

## Current Status

- [x] Case-sensitive allow-list of the six frozen commands
- [x] Anti-replay on every command; `LOCK`/`END_SESSION` exempt from freshness only
- [x] Idempotency: only successes recorded, persisted in SQLite (`HandledCommands`, last 256)
- [x] Typed, source-generated payloads with strict number parsing and required-field validation
- [x] The agent never invents a session id or game id
- [x] `SHUTDOWN` acknowledged before execution; station locked before power-off
- [x] `LOCK` / `END_SESSION` acked only when the overlay is confirmed (`EXEC_FAILED` otherwise)
- [x] Structured `command_ack` / `command_nack` without internal details in reasons
- [x] Unit tests for the dispatcher and payload parsing
