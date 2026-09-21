# BaronDesk Agent : WebSocket Connection Architecture

This document describes the current implementation and architecture of the WebSocket communication layer in `BaronDeskAgent.ServiceCore`.

---

## Folder Structure

```text
BaronDeskAgent
│
├── BaronDesk.Shared
│   └── Contracts
│       ├── Envelope.cs
│       ├── AgentJsonContext.cs
│       ├── MessageTypes.cs
│       ├── CommandTypes.cs
│       ├── HandshakePayload.cs
│       ├── CommandAckPayload.cs
│       ├── CommandNackPayload.cs
│       ├── CommandRequest.cs
│       └── CommandResponse.cs
│
└── BaronDeskAgent.ServiceCore
    │
    ├── Configuration
    │   └── AgentOptions.cs
    │
    ├── Security
    │   ├── ReplayGuard.cs
    │   └── IdempotencyTracker.cs
    │
    ├── Communication
    │   ├── IServerConnection.cs
    │   ├── WebSocketConnection.cs
    │   └── ConnectionWorker.cs
    │
    ├── Services
    │   ├── Telemetry
    │   │   └── WebSocketTelemetryTransport.cs
    │   │
    │   └── Commands
    │       └── CommandService.cs
    │
    └── appsettings.json
```

---

## 1. WebSocket Communication Architecture

The agent communicates with the BaronDesk server over a secure, persistent WebSocket channel (`/agent-ws`):

```text
Backend Server (/agent-ws)
         │
         │ WSS (TLS + Pinned Certificate)
         ▼
 ┌───────────────────────┐
 │  WebSocketConnection  │  (IServerConnection)
 └──────────┬────────────┘
            │
            │ Inbound / Outbound Envelopes
            ▼
 ┌───────────────────────┐
 │   ConnectionWorker    │  (Hosted BackgroundService)
 └──────────┬────────────┘
            │
            ├── Handshake on connect
            ├── ReplayGuard (Seq + Ts verification)
            ├── IdempotencyTracker (Duplicate detection)
            │
            ├── Inbound Command
            │       │
            │       ▼
            │   CommandService ──► Handlers (LOCK, UNLOCK, etc.)
            │       │
            │       ▼
            │   AckAsync / NackAsync
            │
            └── Outbound Telemetry (via WebSocketTelemetryTransport)
```

The WebSocket layer is completely decoupled from command execution and local services. It handles framing, TLS validation, anti-replay, and network resilience.

---

## 2. TLS Certificate Pinning

In LAN environments where internal or self-signed certificates are used without a public CA, the agent enforces certificate pinning before establishing trust.

```text
Incoming TLS Certificate
         │
         ▼
SHA-256 Hash of Raw Cert Data
         │
         ▼
CryptographicOperations.FixedTimeEquals()
         │
         ├── Mismatch ────► Abort immediately (Send zero bytes of credential)
         │
         └── Match ───────► Complete TLS Handshake & WSS Upgrade
```

Implementation details in `WebSocketConnection`:

- The validation callback runs during the TLS handshake, before any HTTP upgrade headers or station tokens are transmitted.
- Hashes are compared in constant time (`CryptographicOperations.FixedTimeEquals`) to prevent timing side-channel attacks.
- If no pin is configured and `AllowUntrustedCertificate` is enabled, the agent permits connections for local development and testing.

---

## 3. Connection Lifecycle & Reconnection

The connection lifecycle is driven by `ConnectionWorker`:

```text
Start ConnectionWorker
         │
         ▼
 ┌───────────────────────┐
 │ ConnectAsync()        │
 └──────────┬────────────┘
            │
      Failed? ───► Exponential Backoff + Jitter ───► Retry Connect
            │
         Success
            │
            ▼
 ┌───────────────────────┐
 │ ResetSequence()       │ (Replay guard & sequence reset)
 └──────────┬────────────┘
            │
            ▼
 ┌───────────────────────┐
 │ SendHandshakeAsync()  │ (Handshake frame sent)
 └──────────┬────────────┘
            │
            ▼
 ┌───────────────────────┐
 │ RunReceiveLoopAsync() │ ◄─── Processes frames until disconnect
 └──────────┬────────────┘
            │
      Disconnected
            │
            ▼
 Clean Disconnect & Cleanup ──► Reconnect with backoff
```

Key resilience features:
- **Exponential Backoff:** Delays escalate progressively (`ReconnectBaseDelaySeconds` up to `ReconnectMaxDelaySeconds`).
- **Randomized Jitter:** Adds random fractional delays to prevent thundering-herd reconnect storms when the venue server restarts.

---

## 4. Handshake Protocol

Immediately upon opening the connection, the agent transmits a `handshake` message identifying itself:

```text
Agent                                   Server
  │                                       │
  │─── handshake (Envelope) ─────────────►│
  │    { serialNumber, agentVersion,      │
  │      osVersion, machineName }         │
  │                                       │
  │◄── handshake_ack (Envelope) ──────────│
```

Payload contract (`HandshakePayload.cs`):

```csharp
public sealed record HandshakePayload
{
    public string SerialNumber { get; init; } = string.Empty;
    public string AgentVersion { get; init; } = "1.0.0";
    public string OsVersion { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
}
```

Wire representation:

```json
{
  "type": "handshake",
  "id": "e2a3c7b1-9f2d-4c8e-a123-456789abcdef",
  "ts": "2026-09-20T15:30:00.000Z",
  "seq": 1,
  "payload": {
    "serialNumber": "STATION-01",
    "agentVersion": "1.0.0",
    "osVersion": "Microsoft Windows 11 Pro",
    "machineName": "DESKTOP-GAMING01"
  }
}
```

---

## 5. App-Level Anti-Replay Guard

On top of TLS, the agent enforces application-level anti-replay via `ReplayGuard`:

```text
Inbound Frame (seq, ts)
         │
         ├── Clock drift (|now - ts| > MaxTimestampDrift)?
         │      │
         │      └── Yes ──► Reject ("STALE")
         │
         └── Sequence (seq <= lastAcceptedSeq)?
                │
                ├── Yes ──► Reject ("STALE")
                │
                └── No  ──► Accept & Update lastAcceptedSeq
```

Rules:
1. **Monotonic Sequence:** Inbound sequence numbers must strictly increase. Any frame with `seq <= _lastInboundSeq` is rejected.
2. **Per-Connection Scope:** Sequence tracking resets to zero on every new connection session.
3. **Timestamp Freshness:** Timestamps must fall within the allowable drift window (`MaxTimestampDriftSeconds`, default 60 seconds).

---

## 6. Command Idempotency

Network retries or message redeliveries can cause duplicate commands to arrive at the agent.

`IdempotencyTracker` maintains a thread-safe, bounded cache of recently processed command IDs:

```text
Inbound Command Id
         │
         ├── Already in cache?
         │      │
         │      └── Yes ──► Send command_ack without re-executing
         │
         └── Not in cache ──► Register Id & Proceed to execution
```

This ensures that redelivered commands (such as duplicate `LOCK` or `UNLOCK` requests) are acknowledged without repeating side effects.

---

## 7. Command Dispatching & Prioritized Shutdown

Commands are processed strictly from an allow-list of the 6 frozen commands:

```text
Inbound Envelope
       │
       ├── Allowed command? (LOCK, UNLOCK, SHUTDOWN, LAUNCH_GAME, END_SESSION, POLICY_UPDATE)
       │      │
       │      └── No  ──► command_nack (code: "UNKNOWN_TYPE")
       │
       ├── ReplayGuard valid?
       │      │
       │      └── No  ──► command_nack (code: "STALE")
       │
       ├── Idempotent duplicate?
       │      │
       │      └── Yes ──► command_ack (early return)
       │
       └── Execute Command:
              │
              ├── SHUTDOWN:
              │      │
              │      ├── 1. Send command_ack FIRST
              │      └── 2. Trigger SystemPowerService.ShutdownAsync()
              │
              └── ALL OTHER COMMANDS:
                     │
                     ├── 1. CommandService.HandleAsync()
                     │
                     ├── Success ──► command_ack
                     └── Failed  ──► command_nack (code: "EXEC_FAILED")
```

### Prioritized SHUTDOWN Ack

Because an operating system shutdown terminates all processes and network sockets immediately, `SHUTDOWN` requires special ordering:
- The `command_ack` frame is transmitted over the WebSocket **prior** to initiating the power command.
- This guarantees that the acknowledgement arrives at the backend before the station powers down.

---

## 8. Command Acknowledgements

### Successful Command (`command_ack`)

Contract: `CommandAckPayload.cs`

```json
{
  "type": "command_ack",
  "id": "11111111-2222-3333-4444-555555555555",
  "ts": "2026-09-20T15:30:02.100Z",
  "seq": 2,
  "payload": {
    "commandId": "e2a3c7b1-9f2d-4c8e-a123-456789abcdef"
  }
}
```

### Failed Command (`command_nack`)

Contract: `CommandNackPayload.cs`

```json
{
  "type": "command_nack",
  "id": "11111111-2222-3333-4444-555555555555",
  "ts": "2026-09-20T15:30:02.100Z",
  "seq": 3,
  "payload": {
    "commandId": "e2a3c7b1-9f2d-4c8e-a123-456789abcdef",
    "code": "UNKNOWN_TYPE",
    "reason": "Type 'REBOOT' is not in the allowed command list."
  }
}
```

Standard error codes:
- `UNKNOWN_TYPE`: Command type not in the allow-list.
- `STALE`: Failed anti-replay or expired timestamp.
- `INVALID_PAYLOAD`: Required payload fields missing or malformed.
- `EXEC_FAILED`: Local service error during execution.

---

## 9. Telemetry & Outbox Transport Integration

`WebSocketTelemetryTransport` implements `ITelemetryTransport`, connecting the SQLite outbox directly to the active WebSocket:

```text
OutboxWorker (BackgroundService)
       │
       ▼
De-queue Envelope from SQLite
       │
       ▼
ITelemetryTransport.SendAsync()
       │
       ▼
WebSocketTelemetryTransport
       │
       ├── IsConnected?
       │      │
       │      ├── Yes ──► IServerConnection.SendAsync()
       │      │              │
       │      │              ▼
       │      │           Delete from SQLite outbox
       │      │
       │      └── No  ──► Throws InvalidOperationException
       │                     │
       │                     ▼
       │                  Retained in SQLite for next retry
```

If the WebSocket connection drops:
1. Calls to `SendAsync` throw an exception.
2. `OutboxWorker` catches the failure, increments retry attempts, and leaves the message in SQLite.
3. Once the connection is re-established, `OutboxWorker` drains the queue and sends all pending messages in chronological order.

---

## 10. Configuration

Configured under the `"Agent"` section in `appsettings.json`:

```json
{
  "Agent": {
    "ServerUrl": "wss://127.0.0.1:8443/agent-ws",
    "PinnedCertificateHash": "",
    "AllowUntrustedCertificate": true,
    "SerialNumber": "STATION-01",
    "ReconnectBaseDelaySeconds": 2.0,
    "ReconnectMaxDelaySeconds": 30.0,
    "KeepAliveIntervalSeconds": 30.0,
    "MaxTimestampDriftSeconds": 60.0
  }
}
```

Configuration mapping (`AgentOptions.cs`):
- `ServerUrl`: WSS endpoint target.
- `PinnedCertificateHash`: Hex-encoded SHA-256 fingerprint of the server certificate.
- `AllowUntrustedCertificate`: Development switch when certificate pinning is disabled.
- `SerialNumber`: Station identifier sent in `handshake` (falls back to `Environment.MachineName`).
- `ReconnectBaseDelaySeconds` / `ReconnectMaxDelaySeconds`: Reconnect timing bounds.
- `MaxTimestampDriftSeconds`: Anti-replay clock drift tolerance.

---

## 11. Dependency Injection Registration

All communication services are registered in `Program.cs`:

```csharp
// Configuration
builder.Services.Configure<AgentOptions>(
    builder.Configuration.GetSection(AgentOptions.SectionName));

// Security & Replay
builder.Services.AddSingleton<ReplayGuard>();
builder.Services.AddSingleton<IdempotencyTracker>();

// Transport & Connection
builder.Services.AddSingleton<IServerConnection, WebSocketConnection>();
builder.Services.AddSingleton<ITelemetryTransport, WebSocketTelemetryTransport>();

// Hosted Communication Worker
builder.Services.AddHostedService<ConnectionWorker>();
```

---

## Current Status

- [x] Pinned certificate WSS client (`WebSocketConnection`)
- [x] Constant-time SHA-256 certificate fingerprint verification
- [x] Memory-efficient frame receiving via `ArrayPool<byte>`
- [x] Thread-safe outbound transmission serialization (`SemaphoreSlim`)
- [x] Hosted connection lifecycle worker (`ConnectionWorker`)
- [x] Exponential backoff and randomized jitter on reconnect
- [x] Handshake payload and automatic initial handshake emission
- [x] Command allow-list enforcement (6 frozen commands)
- [x] Monotonic anti-replay guard (`ReplayGuard`)
- [x] Idempotency tracker for command redeliveries (`IdempotencyTracker`)
- [x] Prioritized SHUTDOWN acknowledgement before system power down
- [x] Structured `command_ack` and `command_nack` generation
- [x] Outbox integration via `WebSocketTelemetryTransport`
- [x] Source-generated JSON serialization context for all wire contracts
- [x] Configuration options in `appsettings.json`
