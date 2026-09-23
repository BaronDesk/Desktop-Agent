# BaronDesk Agent : WebSocket Connection Architecture

This document describes the current implementation and architecture of the WebSocket communication layer in `BaronDeskAgent.ServiceCore`: the pinned WSS link, the connection lifecycle, the server clock, app-level anti-replay and frame handling.

---

## Folder Structure

```text
Desktop-Agent
│
├── src
│   ├── BaronDesk.Shared
│   │   └── Contracts
│   │       ├── Envelope.cs
│   │       ├── AgentJsonContext.cs
│   │       ├── MessageTypes.cs
│   │       ├── CommandTypes.cs
│   │       ├── HandshakePayload.cs
│   │       ├── StateReportPayload.cs
│   │       └── ServerControlPayloads.cs      (handshake_ack, heartbeat_ack, login_result)
│   │
│   └── BaronDeskAgent.ServiceCore
│       │
│       ├── Configuration
│       │   ├── AgentOptions.cs
│       │   └── AgentOptionsValidator.cs
│       │
│       ├── Credentials
│       │   └── IStationCredentialStore.cs    (station JWT, see CredentialStore.md)
│       │
│       ├── Connection
│       │   ├── IServerConnection.cs
│       │   ├── WebSocketConnection.cs
│       │   ├── ConnectionWorker.cs
│       │   ├── ServerClock.cs
│       │   ├── ReplayGuard.cs
│       │   └── ReconnectBackoff.cs
│       │
│       └── appsettings.json
│
└── tests
    └── BaronDeskAgent.ServiceCore.Tests
        ├── Connection
        │   ├── ReplayGuardTests.cs
        │   └── ReconnectBackoffTests.cs
        └── Configuration
            └── AgentOptionsValidatorTests.cs
```

---

## 1. WebSocket Communication Architecture

The agent talks to the venue backend over one persistent, pinned WSS channel (`/agent-ws`):

```text
Backend Server (/agent-ws)
         │
         │ WSS (TLS + pinned certificate + station JWT on the upgrade request)
         ▼
 ┌───────────────────────┐
 │  WebSocketConnection  │  (IServerConnection)
 │  - seq/ts stamping    │  ◄── one send lock for every sender
 │  - bounded receive    │
 └──────────┬────────────┘
            │
            ▼
 ┌───────────────────────┐
 │   ConnectionWorker    │  (Hosted BackgroundService)
 └──────────┬────────────┘
            │
            ├── handshake + state_report on every (re)connect, then MarkReady()
            │
            ├── Control frames ─── ReplayGuard ─┬─ handshake_ack ──► ServerClock.Synchronize
            │                                    ├─ heartbeat_ack ──► StationController.RenewLease
            │                                    └─ login_result  ──► LoginRelay.Complete
            │
            └── Commands ──► CommandDispatcher (allow-list, anti-replay, idempotency, ack/nack)

Other senders (all gated on IsReady):
  HeartbeatWorker · TelemetryPublisher · OutboxWorker · LoginRelay
```

The WebSocket layer only handles framing, TLS, timing and resilience. Command execution lives in `CommandDispatcher` (see `CommandsHandling.md`).

---

## 2. TLS Certificate Pinning

The venue server uses an internal/self-signed certificate (no public CA for `192.168.x.x`), so the agent pins it:

```text
Incoming TLS Certificate
         │
         ▼
SHA-256 of the raw certificate
         │
         ▼
CryptographicOperations.FixedTimeEquals(actual, pinned)
         │
         ├── Mismatch ────► Abort (log CRITICAL). No HTTP upgrade, no station JWT sent.
         │
         └── Match ───────► TLS completes → HTTP upgrade with "Authorization: Bearer <station JWT>"
```

- The callback runs during the TLS handshake, before the upgrade request exists: on mismatch zero bytes of credential leave the machine.
- `PinnedCertificateHash` is parsed once at construction (hex, `:` and spaces allowed, exactly 32 bytes).
- **Outside Development, pinning is mandatory.** `AgentOptionsValidator` fails startup when the pin is missing or malformed, when `AllowUntrustedCertificate` is `true`, or when `ServerUrl` is not `wss://`.
- `AllowUntrustedCertificate` and `ws://` exist only for the local mock server in Development.

### Station Credential

The upgrade request carries the station JWT from `IStationCredentialStore` (DPAPI, see `CredentialStore.md`), read on **every** connect so a rotated credential is used without a restart. `Agent:StationToken` in configuration is a Development-only fallback.

---

## 3. Connection Lifecycle & Reconnection

```text
Start ConnectionWorker
         │
         ▼
 ReplayGuard.Reset()                    (inbound seq scope = one connection)
         │
         ▼
 ConnectAsync() ── failed ──────────────────────────────┐
         │                                                │
      Connected (outbound seq reset to 0)                 │
         │                                                │
         ▼                                                │
 SendHandshakeAsync()                                     │
 SendStateReportAsync()     ◄── always, not gated on an   │
         │                      OPEN handshake_ack        │
         ▼                                                │
 MarkReady()  ──► ReadyChanged(true)                       │
         │        (heartbeat, outbox flush, LockUI status) │
         ▼                                                │
 RunReceiveLoopAsync()  ◄── until Close / error           │
         │                                                │
         ▼                                                │
 DisconnectAsync()  ──► ReadyChanged(false)               │
         │                                                │
         ▼                                                ▼
 ALWAYS back off (also after a clean close) ─────► retry
```

### Backoff Rules

- Exponential with **equal jitter**: `capped = min(base × 2^(attempt−1), max)`, delay = `capped/2 + random × capped/2`.
- The attempt counter resets only after a connection stayed up for **30 seconds**. A server that accepts TCP and closes immediately (station not enrolled, credential revoked) is no longer hammered in a tight loop.
- Defaults: `ReconnectBaseDelaySeconds = 2`, `ReconnectMaxDelaySeconds = 30`.

### The Ready Gate

`IServerConnection.IsReady` becomes `true` only after the handshake **and** the state report are on the wire. Heartbeats, telemetry, outbox messages and login requests check it, so nothing reaches the backend before the handshake. `WaitUntilReadyAsync()` lets the outbox sleep until the link is usable.

---

## 4. Outbound Frames: One Sequence, In Order

Every outbound frame goes through `SendAsync<T>`, which builds the envelope itself:

```csharp
Task<Guid> SendAsync<T>(
    string type,
    T payload,
    JsonTypeInfo<T> payloadTypeInfo,
    CancellationToken cancellationToken,
    Guid? messageId = null);
```

Inside the send lock it:

1. increments the per-connection `seq`,
2. stamps `ts` from the **estimated server clock**,
3. writes the envelope with a reused `Utf8JsonWriter` and buffer (no per-frame allocations beyond the payload),
4. sends one text frame.

Because `seq` is assigned under the same lock that serializes sends, frames always reach the wire in `seq` order, whatever component sends them. `messageId` lets the outbox resend a message with its original `id`, so the backend can deduplicate retries.

---

## 5. Handshake Protocol

### Agent → Server (`handshake`)

```json
{
  "type": "handshake",
  "id": "1f2269bd-cc29-4560-8312-8dea06a18a3c",
  "ts": "2026-09-22T21:41:52.120Z",
  "seq": 1,
  "payload": {
    "serialNumber": "STATION-01",
    "agentVersion": "1.0.0+470efa4",
    "osVersion": "Microsoft Windows NT 10.0.22631.0",
    "machineName": "STATION-01"
  }
}
```

- `serialNumber` defaults to the machine name when `Agent:SerialNumber` is empty.
- `agentVersion` is the assembly informational version.
- The credential is never in the payload; it travels on the upgrade request (OPEN, skill §15 item 1).

### Server → Agent (`handshake_ack`) ⚠ OPEN

```json
{
  "type": "handshake_ack",
  "id": "1ee3ab63-c304-49c6-9e0a-05a80cf91e47",
  "ts": "2026-09-22T21:41:52.219Z",
  "seq": 101,
  "payload": { "serverTime": "2026-09-22T21:41:52.219Z" }
}
```

The ack is sequence-checked (freshness cannot be judged yet) and sets the server clock offset from `serverTime`, or from `ts` when `serverTime` is absent.

---

## 6. Server Clock

The backend clock is authoritative, and venue PCs run without internet, so Windows cannot sync time over NTP and station clocks drift. `ServerClock` keeps the offset between the two:

```text
handshake_ack / heartbeat_ack (validated)
         │
         ▼
 offset = serverTime − local UtcNow
         │
         ▼
 ServerClock.UtcNow = local UtcNow + offset
         │
         ├── ReplayGuard freshness checks
         ├── outbound envelope "ts"
         ├── alert "occurredAt", telemetry "sampledAt"
         └── state_report "leaseExpiresAt"
```

Before the first synchronization it equals the local clock. The heartbeat ack re-synchronizes it after passing the replay check, which absorbs slow drift over long connections.

---

## 7. App-Level Anti-Replay Guard

`ReplayGuard` validates **every** inbound frame: commands and control frames alike. Before the review, `heartbeat_ack` skipped it, so a replayed ack could keep extending the lease.

```csharp
public ReplayCheck Validate(long seq, DateTimeOffset ts, bool enforceFreshness = true);
```

| Rule | Check | Failure |
|---|---|---|
| Freshness | `|ServerClock.UtcNow − ts| ≤ MaxTimestampDriftSeconds` (default 60 s) | `STALE` nack (commands) / frame dropped (control) |
| Monotonic sequence | `seq > lastAcceptedSeq` for this connection | `STALE` nack / frame dropped |

- Freshness is checked first, so a stale frame does not consume a sequence number.
- `LOCK` and `END_SESSION` pass `enforceFreshness: false`: running a late one is always safe, and clock drift must never keep a station unlocked. Their `seq` is still checked.
- `handshake_ack` also skips freshness because it establishes the offset.

---

## 8. Inbound Frame Handling

`ReceiveAsync()` reads into one pre-allocated 64 KiB buffer and returns a `ReceiveResult`:

| Status | When | ConnectionWorker reaction |
|---|---|---|
| `Message` | Valid envelope with `type` and `id` | Process it |
| `Malformed` | Invalid JSON, missing `type`/`id`, binary frame, or larger than 64 KiB (drained) | Log a warning, **keep the connection** |
| `Closed` | Close frame or socket not open | Leave the receive loop, back off, reconnect |

One malformed frame no longer tears the connection down, and an oversized frame cannot exhaust memory.

### Frame Routing

| Envelope `type` | Handled by |
|---|---|
| `handshake_ack` | `ServerClock.Synchronize` |
| `heartbeat_ack` | `ServerClock.Synchronize` + `StationController.RenewLease` (see `SessionAndLeaseControl.md`) |
| `login_result` | `LoginRelay.Complete` (see `LockUIAndIPC.md`) |
| anything else | `CommandDispatcher` (allow-list → `UNKNOWN_TYPE` nack for unknown types) |

---

## 9. Configuration

Configured under `"Agent"` in `appsettings.json` (validated at startup by `AgentOptionsValidator`):

```json
{
  "Agent": {
    "ServerUrl": "wss://192.168.1.100:8443/agent-ws",
    "PinnedCertificateHash": "AB:CD:EF:…:89",
    "AllowUntrustedCertificate": false,
    "SerialNumber": "",
    "ReconnectBaseDelaySeconds": 2.0,
    "ReconnectMaxDelaySeconds": 30.0,
    "KeepAliveIntervalSeconds": 30.0,
    "MaxTimestampDriftSeconds": 60.0
  }
}
```

| Option | Rule |
|---|---|
| `ServerUrl` | Absolute URI; `wss://` (`ws://` only in Development) |
| `PinnedCertificateHash` | Required outside Development; 32 bytes of hex |
| `AllowUntrustedCertificate` | Rejected outside Development |
| `StationToken` | Development-only fallback; rejected elsewhere (use DPAPI) |
| `ReconnectBaseDelaySeconds` | `(0, 300]` |
| `ReconnectMaxDelaySeconds` | `≥ base`, `≤ 3600` |
| `KeepAliveIntervalSeconds` | `(0, 600]` |
| `MaxTimestampDriftSeconds` | `(0, 3600]` |

`appsettings.Development.json` points at the mock server: `ws://127.0.0.1:8443/agent-ws` with `AllowUntrustedCertificate: true`.

---

## 10. Dependency Injection Registration

Wired in `Program.cs`:

```csharp
services.AddOptions<AgentOptions>()
        .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
        .ValidateOnStart();
services.AddSingleton<IValidateOptions<AgentOptions>, AgentOptionsValidator>();
services.AddSingleton(TimeProvider.System);

services.AddSingleton<IStationCredentialStore, DpapiStationCredentialStore>();
services.AddSingleton<ServerClock>();
services.AddSingleton<ReplayGuard>();
services.AddSingleton<IServerConnection, WebSocketConnection>();

services.AddHostedService<ConnectionWorker>();
```

---

## 11. Testing with the Mock Server

`tools/mock-server/mock-server.js` (Node.js, no dependencies) plays the backend:

```powershell
node tools/mock-server/mock-server.js
$env:DOTNET_ENVIRONMENT = "Development"; dotnet run --project src/BaronDeskAgent.ServiceCore
```

| Menu | Scenario | Expected agent behaviour |
|---|---|---|
| `7` | `LOCK` with a reused `seq` | `command_nack` `STALE` |
| `8` | Unknown command type | `command_nack` `UNKNOWN_TYPE` |
| `9` | `UNLOCK` stamped 10 minutes ago | `command_nack` `STALE` |
| `0` | `LOCK` stamped 10 minutes ago | Executed (restrictive command) |
| `m` | Malformed frame | Ignored, connection kept |
| `p` | `POLICY_UPDATE` with an unknown field | `command_nack` `INVALID_PAYLOAD` |
| `a` / `t` / `r` | Enable anti-theft / 30 °C thresholds / restore 85 °C | `command_ack`; `t` produces `TEMPERATURE_WARNING` alerts |
| `q` | Server quits | Reconnect with growing, jittered delays |

The mock also prints `[AUTH]` for every connection (credential presented or not, value hidden). The complete step-by-step local test procedure is in the repository `README.md` ("Local Testing Guide").

For `wss://` and pinning: `MOCK_TLS_CERT=cert.pem MOCK_TLS_KEY=key.pem node tools/mock-server/mock-server.js`, then copy the printed fingerprint into `PinnedCertificateHash`.

---

## Current Status

- [x] Pinned certificate WSS client (`WebSocketConnection`), constant-time SHA-256 comparison
- [x] Pinning mandatory outside Development; unsafe options rejected at startup (`AgentOptionsValidator`)
- [x] Station JWT from the DPAPI credential store on every connect
- [x] `seq` and `ts` stamped inside the send lock (strict wire order for every sender)
- [x] Reused JSON writer and receive buffer (no per-frame buffers)
- [x] Hosted connection lifecycle worker (`ConnectionWorker`)
- [x] Backoff with equal jitter after every disconnect; reset only after a stable connection
- [x] Handshake and `state_report` on every (re)connect; `IsReady` gate for all other traffic
- [x] Server clock offset from `handshake_ack` / `heartbeat_ack` (`ServerClock`)
- [x] Anti-replay on every inbound frame (`ReplayGuard`); restrictive commands exempt from freshness only
- [x] Malformed, binary and oversized (64 KiB) frames ignored without dropping the connection
- [x] Failed connects dispose their socket (no leak per retry)
- [x] Unit tests: replay guard, backoff bounds, options validation
- [ ] `handshake_ack` / `heartbeat_ack` / `login_result` shapes confirmed with backend member C (OPEN, skill §15 item 3)
