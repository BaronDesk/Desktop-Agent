# BaronDesk Agent : Command Handling Architecture

This document describes the current implementation and architecture of command handling in `BaronDeskAgent.ServiceCore`.

## 1. Command Handling Architecture

The command flow is:

```text
Backend
   │
   │ CommandRequest (via Envelope)
   ▼
WebSocket Layer
   │
   ▼
CommandService
   │
   ▼
ICommandHandler
   │
   ▼
Local Service
   │
   ▼
CommandResponse
```

The WebSocket layer is separate from the command system.

`CommandService` does not need to know how a command was received. It receives a `CommandRequest`, finds the appropriate handler, executes it, and returns a `CommandResponse`.

---

## 2. CommandService

`CommandService` is the central command router.

Its responsibilities are:

1. Receive a `CommandRequest`.
2. Find the handler associated with `command.Type`.
3. Execute the handler.
4. Return the resulting `CommandResponse`.
5. Handle unknown command types.
6. Handle unexpected command execution errors.

The service receives all registered `ICommandHandler` implementations through dependency injection.

Conceptually:

```text
CommandRequest
      │
      ▼
CommandService
      │
      ├── Handler exists?
      │      │
      │      ├── No → Failed CommandResponse
      │      │
      │      └── Yes
      │             │
      │             ▼
      │       Handler.HandleAsync()
      │             │
      │             ▼
      │       CommandResponse
```

The handler lookup is based on the command type (case-insensitive).

---

## 3. ICommandHandler

The command handler abstraction is:

```csharp
using BaronDesk.Shared.Contracts;

namespace BaronDeskAgent.ServiceCore.Commands.Handlers;

public interface ICommandHandler
{
    string CommandType { get; }

    Task<CommandResponse> HandleAsync(
        CommandRequest command,
        CancellationToken cancellationToken = default);
}
```

Every command handler implements this interface.

The `CommandType` property identifies which command the handler is responsible for.

For example:

```text
"LOCK"          → LockCommandHandler
"UNLOCK"        → UnlockCommandHandler
"END_SESSION"   → EndSessionCommandHandler
"LAUNCH_GAME"   → LaunchGameCommandHandler
"SHUTDOWN"      → ShutdownCommandHandler
"POLICY_UPDATE" → PolicyUpdateCommandHandler
```

This avoids having one large command `switch` statement.

---

## 4. Current Command Handlers

The current command handlers correspond to the 6 frozen wire commands:

| Command | Handler | Service |
|---|---|---|
| `LOCK` | `LockCommandHandler` | `LockService` |
| `UNLOCK` | `UnlockCommandHandler` | `LockService` |
| `END_SESSION` | `EndSessionCommandHandler` | `SessionService` |
| `LAUNCH_GAME` | `LaunchGameCommandHandler` | `GameService` |
| `SHUTDOWN` | `ShutdownCommandHandler` | `SystemPowerService` |
| `POLICY_UPDATE` | `PolicyUpdateCommandHandler` | Policy Store |

The handlers are intentionally thin.

Their main responsibilities are:

- Validate command input where necessary.
- Call the appropriate local service.
- Return a `CommandResponse`.
- Log command execution.

The actual local operation belongs to the corresponding service.

---

## 5. Folder Structure

The current command-related structure is:

```text
BaronDeskAgent.ServiceCore
│
├── Commands
│   ├── CommandService.cs
│   │
│   └── Handlers
│       ├── ICommandHandler.cs
│       ├── LockCommandHandler.cs
│       ├── UnlockCommandHandler.cs
│       ├── EndSessionHandler.cs
│       ├── LaunchGameCommandHandler.cs
│       ├── ShutdownCommandHandler.cs
│       └── PolicyUpdateCommandHandler.cs
│
└── Services
    │
    ├── Commands
    │   └── LockService.cs
    │
    ├── Session
    │   └── SessionService.cs
    │
    ├── Games
    │   └── GameService.cs
    │
    └── System
        └── SystemPowerService.cs
```

The `Services/Commands` folder is used for services directly related to command functionality.

---

## 6. Lock and Unlock

The lock commands use the same local service:

```text
LOCK
  │
  ▼
LockCommandHandler
  │
  ▼
LockService.LockAsync()
```

```text
UNLOCK
  │
  ▼
UnlockCommandHandler
  │
  ▼
LockService.UnlockAsync()
```

`LockService` currently maintains the internal lock state:

```csharp
public bool IsLocked
```

and exposes:

```csharp
LockAsync()
UnlockAsync()
```

The current implementation manages the internal lock state.

The LockUI and Named Pipe integration are handled separately via IPC.

---

## 7. End Session

The session functionality is located in:

```text
Services
└── Session
    └── SessionService.cs
```

The service maintains:

```csharp
public bool IsSessionActive
```

and exposes:

```csharp
StartSessionAsync()
EndSessionAsync()
```

The current command flow is:

```text
END_SESSION
     │
     ▼
EndSessionCommandHandler
     │
     ▼
SessionService.EndSessionAsync()
```

The current implementation changes the local session state.

Stopping active agent-launched games and enforcing station lockout are coordinated during `END_SESSION`.

---

## 8. Game Commands

Game functionality is located in:

```text
Services
└── Games
    └── GameService.cs
```

The service currently exposes:

```csharp
LaunchGameAsync(string gameId)
StopGameAsync(string gameId)
```

The command flow is:

```text
LAUNCH_GAME
     │
     ▼
LaunchGameCommandHandler
     │
     ▼
GameService.LaunchGameAsync()
```

At the current stage, `GameService` logs the request.

The actual game catalog resolution and process launching will be added in upcoming feature iterations.

The handler validates that a game ID is provided in the request payload.

---

## 9. System Power Commands

System power functionality is located in:

```text
Services
└── System
    └── SystemPowerService.cs
```

The service exposes:

```csharp
ShutdownAsync()
RestartAsync()
```

The command flow is:

```text
SHUTDOWN
    │
    ▼
ShutdownCommandHandler
    │
    ▼
SystemPowerService.ShutdownAsync()
```

At the current stage, this method logs the request.

Actual Windows shutdown/restart behavior will be executed via Win32 process execution in upcoming system feature work.

---

## 10. Policy Update Commands

Policy update functionality handles remote configuration changes from the server:

The command flow is:

```text
POLICY_UPDATE
      │
      ▼
PolicyUpdateCommandHandler
      │
      ▼
Policy Store (SQLite persistence)
```

`PolicyUpdateCommandHandler` handles `CommandTypes.PolicyUpdate`, validating the incoming configuration and returning an acknowledgement.

---

## 11. Dependency Injection

The command handlers are registered through dependency injection:

```csharp
builder.Services.AddSingleton<ICommandHandler, LockCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, UnlockCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, EndSessionCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, LaunchGameCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, ShutdownCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, PolicyUpdateCommandHandler>();

builder.Services.AddSingleton<CommandService>();
```

The supporting services are registered as:

```csharp
builder.Services.AddSingleton<LockService>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<GameService>();
builder.Services.AddSingleton<SystemPowerService>();
```

`CommandService` receives:

```csharp
IEnumerable<ICommandHandler>
```

and builds the command lookup dictionary automatically using case-insensitive ordinal matching.

Adding another command follows the same pattern:

```text
New Command Type
       │
       ├── New Handler
       │
       ├── Local Service if required
       │
       └── DI Registration
```

---

## 12. Shared Command Contracts

Command contracts are located in:

```text
BaronDesk.Shared
└── Contracts
    ├── Envelope.cs
    ├── AgentJsonContext.cs
    ├── MessageTypes.cs
    ├── CommandTypes.cs
    ├── CommandRequest.cs
    ├── CommandResponse.cs
    └── PipeMessage.cs
```

The ServiceCore uses the existing shared contracts.

Command types remain centralized in the Shared project:

```csharp
public static class CommandTypes
{
    public const string Lock = "LOCK";
    public const string Unlock = "UNLOCK";
    public const string Shutdown = "SHUTDOWN";
    public const string LaunchGame = "LAUNCH_GAME";
    public const string EndSession = "END_SESSION";
    public const string PolicyUpdate = "POLICY_UPDATE";
}
```

The command system works with:

```csharp
CommandRequest
CommandResponse
CommandTypes
```

from `BaronDesk.Shared.Contracts`.

---

## 13. Command Request

A command request conceptually contains:

```text
Id
Type
Payload
```

For example:

```json
{
  "id": "c7a8b2d1-0f4b-4f91-8e56-2e8c1a2b3c4d",
  "type": "LOCK",
  "payload": null
}
```

A game command contains a game identifier:

```json
{
  "id": "c7a8b2d1-0f4b-4f91-8e56-2e8c1a2b3c4d",
  "type": "LAUNCH_GAME",
  "payload": "game-id"
}
```

A policy update contains the policy dictionary or payload:

```json
{
  "id": "c7a8b2d1-0f4b-4f91-8e56-2e8c1a2b3c4d",
  "type": "POLICY_UPDATE",
  "payload": {
    "heartbeatIntervalSeconds": 15,
    "telemetryCadenceSeconds": 5
  }
}
```

---

## 14. Command Response

Handlers return a `CommandResponse`.

Successful command:

```json
{
  "commandId": "c7a8b2d1-0f4b-4f91-8e56-2e8c1a2b3c4d",
  "success": true,
  "error": null,
  "respondedAt": "2026-09-20T11:22:33.456Z"
}
```

Failed command:

```json
{
  "commandId": "c7a8b2d1-0f4b-4f91-8e56-2e8c1a2b3c4d",
  "success": false,
  "error": "Game ID is required.",
  "respondedAt": "2026-09-20T11:22:33.456Z"
}
```

The response contains the original command ID and timestamp (`DateTimeOffset`) so that the backend can correlate the result with the request.

---

## 15. WebSocket Integration

The command system is intentionally independent from the WebSocket implementation.

The eventual flow is:

```text
Backend
   │
   │ WSS (Envelope)
   ▼
WebSocket Layer
   │
   │ Extract CommandRequest
   ▼
CommandService
   │
   ▼
CommandHandler
   │
   ▼
Local Service
   │
   ▼
CommandResponse
   │
   ▼
WebSocket Layer (Envelope with command_ack / command_nack)
   │
   │ WSS
   ▼
Backend
```

The WebSocket layer is responsible for transport and envelope unwrapping.

The command system is responsible for routing and executing commands.

---

## 16. Testing the Command System

The command system can be tested locally by calling `CommandService.HandleAsync`.

A command request is created:

```csharp
var command = new CommandRequest
{
    Id = Guid.NewGuid(),
    Type = CommandTypes.Lock
};
```

and executed:

```csharp
var response =
    await _commandService.HandleAsync(
        command,
        cancellationToken);
```

The resulting flow is:

```text
Caller (ConnectionWorker / Test)
   │
   ▼
CommandService
   │
   ▼
CommandHandler
   │
   ▼
Local Service
   │
   ▼
CommandResponse
```

The 6 commands:

```text
LOCK
UNLOCK
END_SESSION
LAUNCH_GAME
SHUTDOWN
POLICY_UPDATE
```

can all be executed and routed through `CommandService`.

---

## 17. Command Idempotency

Commands may be delivered more than once because of network retries.

The command system accounts for idempotency:

```text
LOCK + LOCK
    → remains locked

UNLOCK + UNLOCK
    → remains unlocked

END_SESSION + END_SESSION
    → safely terminates any active session
```

`LockService` handles repeated lock/unlock calls safely.

---

## 18. Authorization and Lease

The agent does not make independent authorization decisions:

```text
Backend
   │
   │ Authorization / Lease
   ▼
Command
   │
   ▼
Agent
   │
   │ Execute authorized operation
   ▼
Local machine
```

The backend remains authoritative for session timing, lease renewals, and permissions.

The agent validates local parameters (such as required game IDs and current local states) while executing authorized commands.

---

## 19. Final Command Architecture

```text
                         BACKEND
                            │
                            │ WSS (Envelope)
                            ▼
                    ┌───────────────┐
                    │ WebSocket     │
                    │ Layer         │
                    └───────┬───────┘
                            │
                      CommandRequest
                            │
                            ▼
                    ┌───────────────┐
                    │ CommandService│
                    │    Router     │
                    └───────┬───────┘
                            │
       ┌──────────────┬─────┴────────┬──────────────┬──────────────┐
       ▼              ▼              ▼              ▼              ▼
  Lock Handlers Session Handler Game Handler  Shutdown Handler Policy Handler
       │              │              │              │              │
       ▼              ▼              ▼              ▼              ▼
  LockService   SessionService  GameService  SystemPowerService  PolicyStore
       │              │              │              │              │
       └──────────────┴──────┬───────┴──────────────┴──────────────┘
                             │
                             ▼
                      CommandResponse
                             │
                             ▼
                      WebSocket Layer (Envelope)
                             │
                             │ WSS (command_ack / command_nack)
                             ▼
                          BACKEND
```

The main principle is:

```text
Transport
    ↓
Routing
    ↓
Handler
    ↓
Local Service
    ↓
Result
```

Each layer remains independent and focused on its own responsibility.
