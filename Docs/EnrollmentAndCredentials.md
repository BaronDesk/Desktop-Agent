# BaronDesk Agent : Station Enrollment, DPAPI Credentials & PIN Verification

This document details the implementation of station bootstrapping, machine-scoped credential storage using Windows DPAPI, WebSocket upgrade authentication, and booking PIN verification in `BaronDeskAgent.ServiceCore`.

---

## Folder Structure

```text
BaronDeskAgent
│
├── BaronDesk.Shared
│   └── Contracts
│       ├── EnrollmentRequest.cs            # One-time bootstrap registration payload
│       ├── EnrollmentResponse.cs           # Server credential response with StationJwt
│       ├── MessageTypes.cs                 # enroll_request and enroll_response constants
│       ├── AgentJsonContext.cs             # AOT/STJ source generation registrations
│       ├── Envelope.cs                     # Strict wire envelope
│       └── CommandRequest.cs / CommandResponse.cs
│
└── BaronDeskAgent.ServiceCore
    │
    ├── Security
    │   ├── IStationCredentialStore.cs      # Station credential persistence abstraction
    │   └── DpapiCredentialStore.cs         # Windows DPAPI LocalMachine credential store
    │
    ├── Services
    │   ├── Enrollment
    │   │   └── EnrollmentService.cs        # Station enrollment lifecycle manager
    │   └── Session
    │       └── SessionService.cs           # PIN storage and constant-time validation
    │
    ├── Commands
    │   └── Handlers
    │       └── UnlockCommandHandler.cs     # UNLOCK handler (direct vs PIN booking unlock)
    │
    ├── Communication
    │   ├── IServerConnection.cs            # Connection contract
    │   ├── WebSocketConnection.cs          # Bearer token upgrade injection
    │   └── ConnectionWorker.cs             # Connection & enrollment exchange worker
    │
    ├── Configuration
    │   └── AgentOptions.cs                 # BootstrapToken & StationDataPath options
    │
    └── Program.cs                          # DI registrations & startup initialization
```

---

## 1. Enrollment Lifecycle Architecture

The agent starts in an unauthenticated or authenticated state depending on whether a station credential has already been stored:

```text
                                    ┌───────────────────────┐
                                    │    Agent Startup      │
                                    └───────────┬───────────┘
                                                │
                                  DPAPI Store has station.dat?
                                                │
                                ┌───────────────┴───────────────┐
                                │ YES                           │ NO
                                ▼                               ▼
                     ┌─────────────────────┐         ┌─────────────────────┐
                     │   State: ENROLLED   │         │ State: NOT_ENROLLED │
                     └──────────┬──────────┘         └──────────┬──────────┘
                                │                               │
                    Inject Bearer JWT into               Connect to WS server
                    WSS Upgrade Request                         │
                                │                               ▼
                                ▼                    Handshake Accepted?
                    Normal Operation Loop                       │
                    (Heartbeat, Commands)                       ▼
                                                     BootstrapToken Configured?
                                                                │
                                                ┌───────────────┴───────────────┐
                                                │ YES                           │ NO
                                                ▼                               ▼
                                     Send `enroll_request`             Log warning; wait
                                                │
                                ┌───────────────┴───────────────┐
                                │                               │
                      Status: "ENROLLED"              Status: "REJECTED"
                                │                               │
                                ▼                               ▼
                     Persist JWT via DPAPI            State: REJECTED
                                │                     Halt enrollment attempts
                                ▼
                     Reconnect with JWT
```

### Enrollment States

| State | Description | Next Step |
|---|---|---|
| `NotEnrolled` | Agent has no stored JWT. Bootstrap token configured. | Sends `enroll_request` after handshake. |
| `Pending` | Server received enrollment request; awaiting admin approval. | Retries on subsequent connection cycles. |
| `Enrolled` | Server granted station JWT; token persisted to DPAPI store. | Injects Bearer token into WSS upgrade header. |
| `Rejected` | Server rejected bootstrap token (invalid or expired). | Logs critical error; halts enrollment. |

---

## 2. Machine-Scoped Credential Storage (DPAPI)

### Why DPAPI LocalMachine?

The BaronDesk Agent runs as a **Windows Service** in Session 0 (`NT AUTHORITY\SYSTEM`), while Lock UI and user processes may run in active user sessions. 
- Windows Data Protection API (DPAPI) with `DataProtectionScope.LocalMachine` encrypts data using a key derived from the local computer hardware and operating system.
- Any process running on the same computer can decrypt the credential, allowing the Windows Service to reliably read the JWT across reboots without requiring hardcoded encryption keys.
- **Additional Entropy:** A static entropy salt (`BaronDesk.Station.Credential.v1`) is combined during encryption and decryption to prevent third-party local processes from trivially interpreting the blob.

```text
Plaintext Station JWT (UTF-8)
             │
             ▼
ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine)
             │
             ▼
Atomic File Write (Write to station.dat.tmp → File.Move with overwrite)
             │
             ▼
%ProgramData%\BaronDesk\station.dat
```

### File Location & Permissions

- **Default Path:** `%ProgramData%\BaronDesk\station.dat`
- **Configurable:** via `AgentOptions.StationDataPath` in `appsettings.json`.
- **Atomic Writes:** Written to a temporary file (`.tmp`) first, then moved atomically using `File.Move(..., overwrite: true)` to avoid partial or corrupted files in case of sudden power loss.

---

## 3. Wire Protocol & Contracts

### `enroll_request` (Agent → Server)

Sent once after initial `handshake_ack` when `State == NotEnrolled` and a `BootstrapToken` is present in configuration:

```json
{
  "type": "enroll_request",
  "id": "c7a6e118-2e38-4e89-9a74-d4b3df36c589",
  "ts": "2026-09-20T22:00:00.0000000Z",
  "seq": 2,
  "payload": {
    "bootstrapToken": "BOOTSTRAP-ABC-123-XYZ",
    "machineName": "DESKTOP-STATION01",
    "serialNumber": "STATION-01",
    "osVersion": "Microsoft Windows NT 10.0.26100.0",
    "agentVersion": "1.0.0"
  }
}
```

### `enroll_response` (Server → Agent)

Returned by the server to confirm enrollment or communicate status:

```json
{
  "type": "enroll_response",
  "id": "84c8fb23-74b8-4e67-bb89-50983cf20935",
  "ts": "2026-09-20T22:00:00.2000000Z",
  "seq": 102,
  "payload": {
    "stationId": "e3065d6e-df8d-4be9-8802-ecdeaaef8e94",
    "stationJwt": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "status": "ENROLLED",
    "message": "Station successfully enrolled."
  }
}
```

### WebSocket Upgrade Authentication

Once the station JWT is saved in DPAPI, `WebSocketConnection.ConfigureHeadersAndOptions` sets the standard HTTP `Authorization` header during the RFC 6455 upgrade request:

```http
GET /agent-ws HTTP/1.1
Host: 127.0.0.1:8443
Upgrade: websocket
Connection: Upgrade
Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==
Sec-WebSocket-Version: 13
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

**Token Resolution Hierarchy:**
1. **DPAPI Credential Store:** `EnrollmentService.StationJwt` (Active machine credential).
2. **Configuration Fallback:** `AgentOptions.StationToken` (Manual development override in `appsettings.json`).
3. **No Header:** Used during initial bootstrap connection to exchange the one-time token.

---

## 4. Booking PIN Verification Workflow

When users book a gaming station in advance, the station must not unlock immediately upon receiving the server's command. The station must verify that the customer at the physical desk is the person who made the booking:

```text
Backend Server                         Agent (ServiceCore)                   Lock UI (Station Monitor)
     │                                         │                                         │
     │─── UNLOCK { sessionId, pin: "482917" } ─▶│                                         │
     │                                         │                                         │
     │                                   StartSessionAsync(sessionId, pin)               │
     │                                   (RequiresPin = true)                            │
     │                                   (Screen remains LOCKED)                         │
     │                                         │                                         │
     │                                         │─── IPC: Display PIN Form ──────────────▶│
     │                                         │                                         │ Displays numeric
     │                                         │                                         │ keypad on lock screen
     │                                         │                                         │
     │                                         │◀── IPC: SubmitPin("482917") ────────────│ User types PIN
     │                                         │                                         │
     │                                   ValidatePin("482917")                           │
     │                                   FixedTimeEquals(expected, entered)              │
     │                                         │                                         │
     │                                   [Valid]                                         │
     │                                   LockService.UnlockAsync() ─────────────────────▶│ Unlocks desktop
     │                                   LeaseManager.UpdateLease()                      │ Session begins
```

### Constant-Time PIN Comparison

To prevent timing attacks that might determine PIN prefixes byte-by-byte, `SessionService.ValidatePin` uses `CryptographicOperations.FixedTimeEquals`:

```csharp
var expectedBytes = Encoding.UTF8.GetBytes(_expectedPin);
var enteredBytes = Encoding.UTF8.GetBytes(enteredPin);

bool matches = CryptographicOperations.FixedTimeEquals(expectedBytes, enteredBytes);
if (matches)
{
    _expectedPin = null; // Single-use consumption
}
```

### Handling Direct vs Booking Unlocks in `UnlockCommandHandler`

```csharp
if (string.IsNullOrWhiteSpace(pin))
{
    // Direct unlock (admin clicked Unlock from the dashboard)
    await _lockService.UnlockAsync(cancellationToken);
}
else
{
    // Booking unlock: Station remains locked until user enters PIN at the station
    _logger.LogInformation("Workstation remains locked. PIN entry required by user.");
}
```

---

## 5. Configuration Reference

```json
{
  "Agent": {
    "ServerUrl": "ws://127.0.0.1:8443/agent-ws",
    "SerialNumber": "STATION-01",
    "BootstrapToken": "BOOTSTRAP-ABC-123-XYZ",
    "StationDataPath": "",
    "StationToken": ""
  }
}
```

| Key | Type | Description |
|---|---|---|
| `BootstrapToken` | `string` | One-time token provided by the administrator during station setup. |
| `StationDataPath` | `string` | Optional path override for DPAPI `station.dat`. Defaults to `%ProgramData%\BaronDesk\station.dat`. |
| `StationToken` | `string` | Optional fallback JWT in configuration for testing without DPAPI persistence. |

---

## 6. Testing with `mock-server.js`

The zero-dependency mock server includes full support for enrollment exchange and booking PINs:

### Testing the Enrollment Flow:
1. Start mock server:
   ```bash
   node mock-server.js
   ```
2. Set a bootstrap token in `appsettings.Development.json`:
   ```json
   "BootstrapToken": "DEV-BOOTSTRAP-TOKEN-12345"
   ```
3. Run the agent:
   ```bash
   dotnet run --project BaronDeskAgent/BaronDeskAgent.ServiceCore
   ```
4. Observe the console output:
   - Initial connection connects without JWT.
   - Handshake accepted.
   - Agent sends `enroll_request` with bootstrap token.
   - Mock server issues `enroll_response` with mock JWT.
   - Agent saves JWT to DPAPI store and reconnects.
   - Reconnected upgrade contains `[AUTH] Upgrade request contains Authorization: Bearer mock.jwt...`.

### Testing UNLOCK Options in Mock Server:
- Press `[2]` for direct admin unlock (immediately unlocks).
- Press `[9]` for booking unlock with PIN (starts session, but station remains locked pending PIN entry).
- Press `[0]` to simulate enrollment rejection (`REJECTED` status).

---

## 7. Status Checklist

- [x] Wire contracts: `EnrollmentRequest`, `EnrollmentResponse`, `MessageTypes`, `AgentJsonContext`
- [x] Machine-scoped credential storage: `IStationCredentialStore`, `DpapiCredentialStore` (LocalMachine scope + atomic write)
- [x] Lifecycle management: `EnrollmentService` (`NotEnrolled`, `Pending`, `Enrolled`, `Rejected`)
- [x] WebSocket upgrade: `Authorization: Bearer <jwt>` injection in `WebSocketConnection`
- [x] Connection worker: Handshake, enrollment trigger, `enroll_response` processing, and automatic reconnect
- [x] Booking PIN verification: `SessionService.ValidatePin` with constant-time equality and single-use consumption
- [x] Command handler: `UnlockCommandHandler` distinguishes direct unlock from PIN booking unlock
- [x] Configuration & DI: `Program.cs` registration and startup initialization
- [x] Mock server: Enrollment response generation, authorization header logging, and booking PIN unlock option
- [x] Documentation: Architecture, DPAPI details, wire formats, sequence diagrams, and testing guide
