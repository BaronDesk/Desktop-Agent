# BaronDesk Agent — Command Handling Architecture

This document describes the current implementation and architecture of command handling in `BaronDeskAgent.ServiceCore`.

## 1. Command Handling Architecture

The command flow is:

```text
Backend
   │
   │ CommandRequest
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

The handler lookup is based on the command type.

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
"lock"        → LockCommandHandler
"unlock"      → UnlockCommandHandler
"end_session" → EndSessionCommandHandler
```

This avoids having one large command `switch` statement.

---

## 4. Current Command Handlers

The current command handlers are:

| Command | Handler | Service |
|---|---|---|
| `LOCK` | `LockCommandHandler` | `LockService` |
| `UNLOCK` | `UnlockCommandHandler` | `LockService` |
| `END_SESSION` | `EndSessionCommandHandler` | `SessionService` |
| `LAUNCH_GAME` | `LaunchGameCommandHandler` | `GameService` |
| `STOP_GAME` | `StopGameCommandHandler` | `GameService` |
| `SHUTDOWN` | `ShutdownCommandHandler` | `SystemPowerService` |
| `RESTART` | `RestartCommandHandler` | `SystemPowerService` |

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
│       ├── EndSessionCommandHandler.cs
│       ├── LaunchGameCommandHandler.cs
│       ├── StopGameCommandHandler.cs
│       ├── ShutdownCommandHandler.cs
│       └── RestartCommandHandler.cs
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

The current implementation only manages the internal lock state.

The LockUI and Named Pipe integration are handled separately and are not part of this command implementation.

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

Further session coordination can be added later when the complete session and lease behavior is implemented.

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

The command flows are:

```text
LAUNCH_GAME
     │
     ▼
LaunchGameCommandHandler
     │
     ▼
GameService.LaunchGameAsync()
```

and:

```text
STOP_GAME
     │
     ▼
StopGameCommandHandler
     │
     ▼
GameService.StopGameAsync()
```

At the current stage, these methods only log the request.

The actual game process implementation will be added later.

The handlers already validate that a game ID is provided.

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

The command flows are:

```text
SHUTDOWN
    │
    ▼
ShutdownCommandHandler
    │
    ▼
SystemPowerService.ShutdownAsync()
```

and:

```text
RESTART
    │
    ▼
RestartCommandHandler
    │
    ▼
SystemPowerService.RestartAsync()
```

At the current stage, these methods only log the request.

Actual Windows shutdown/restart behavior will be implemented later.

This allows the command routing to be tested without accidentally shutting down or restarting the development machine.

---

## 10. Dependency Injection

The command handlers are registered through dependency injection:

```csharp
builder.Services.AddSingleton<ICommandHandler, LockCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, UnlockCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, EndSessionCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, LaunchGameCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, StopGameCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, ShutdownCommandHandler>();
builder.Services.AddSingleton<ICommandHandler, RestartCommandHandler>();

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

and builds the command lookup dictionary automatically.

Adding another command therefore follows the same pattern:

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

## 11. Shared Command Contracts

Command contracts are located in:

```text
BaronDesk.Shared
└── Contracts
    ├── AgentMessage.cs
    ├── MessageTypes.cs
    ├── CommandTypes.cs
    ├── CommandRequest.cs
    ├── CommandResponse.cs
    └── PipeMessage.cs
```

The ServiceCore uses the existing shared contracts.

Command types should remain centralized in the Shared project rather than being duplicated inside ServiceCore.

The command system therefore works with:

```csharp
CommandRequest
CommandResponse
CommandTypes
```

from `BaronDesk.Shared.Contracts`.

---

## 12. Command Request

A command request conceptually contains:

```text
Id
Type
Payload
```

For example:

```json
{
  "id": "command-guid",
  "type": "lock",
  "payload": null
}
```

A game command can contain a game identifier:

```json
{
  "id": "command-guid",
  "type": "launch_game",
  "payload": "game-id"
}
```

The exact payload format must follow the existing Shared/backend contract.

For structured payloads, a typed payload model should eventually be preferred over relying on:

```csharp
command.Payload?.ToString()
```

---

## 13. Command Response

Handlers return a `CommandResponse`.

Successful command:

```json
{
  "commandId": "command-guid",
  "success": true,
  "error": null
}
```

Failed command:

```json
{
  "commandId": "command-guid",
  "success": false,
  "error": "Game ID is required."
}
```

The response contains the original command ID so that the backend can correlate the result with the request.

---

## 14. WebSocket Integration

The command system is intentionally independent from the WebSocket implementation.

The eventual flow is:

```text
Backend
   │
   │ WSS
   ▼
WebSocket Layer
   │
   │ Deserialize
   ▼
CommandRequest
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
   │
   ▼
WebSocket Layer
   │
   │ WSS
   ▼
Backend
```

The WebSocket layer is responsible for communication.

The command system is responsible for routing and executing commands.

This separation also allows the command system to be tested without a live backend connection.

---

## 15. Testing the Command System

The command system can currently be tested locally through the existing `Worker`.

A test command can be created:

```csharp
var command = new CommandRequest
{
    Id = Guid.NewGuid(),
    Type = CommandTypes.Lock
};
```

and executed with:

```csharp
var response =
    await _commandService.HandleAsync(
        command,
        stoppingToken);
```

The resulting flow is:

```text
Worker
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

Safe commands for testing currently include:

```text
LOCK
UNLOCK
END_SESSION
LAUNCH_GAME
STOP_GAME
```

`SHUTDOWN` and `RESTART` currently only log their requests, so their routing can also be tested without performing the actual system operation.

---

## 16. Command Idempotency

Commands may eventually be delivered more than once because of network retries.

The command system therefore needs to account for idempotency.

For example:

```text
LOCK + LOCK
    → should remain locked

UNLOCK + UNLOCK
    → should remain unlocked

END_SESSION + END_SESSION
    → should safely handle the second request
```

`LockService` already handles repeated lock/unlock calls safely.

A command ID can also be used later for duplicate command detection when required.

---

## 17. Authorization and Lease

The agent should not make its own authorization decisions.

The intended responsibility is:

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

The backend remains responsible for authorization and lease decisions.

The agent can still perform local validation, such as:

```text
Missing game ID
Invalid command payload
Invalid local state
No tracked game to stop
```

but it should not replace the backend's authorization logic.

---

## 18. Future Command Work

The current command architecture provides the base.

Remaining work includes:

1. Connect `CommandService` to the real WebSocket command receiver.
2. Send command responses/acknowledgements back to the backend.
3. Integrate the LockUI/Named Pipe implementation.
4. Implement the real game launching mechanism.
5. Track the launched game process.
6. Implement controlled game stopping.
7. Implement Windows shutdown/restart.
8. Integrate session lifecycle with lease management.
9. Add command idempotency where required.
10. Add final authorization/state validation.
11. Add end-to-end command tests.

---

## 19. Final Command Architecture

```text
                         BACKEND
                            │
                            │ WSS
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
          ┌─────────────────┼──────────────────┐
          │                 │                  │
          ▼                 ▼                  ▼
    Lock Handlers     Session Handler     Game Handlers
          │                 │             ┌────┴────┐
          ▼                 ▼             ▼         ▼
     LockService      SessionService   Launch     Stop
                                           │         │
                                           └────┬────┘
                                                ▼
                                           GameService

                            │
                            ▼
                    System Power Handlers
                            │
                            ▼
                    SystemPowerService
                       │            │
                       ▼            ▼
                    Shutdown      Restart

                            │
                            ▼
                     CommandResponse
                            │
                            ▼
                      WebSocket Layer
                            │
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
