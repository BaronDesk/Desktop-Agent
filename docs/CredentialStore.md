# BaronDesk Agent : Station Credential Store (DPAPI) Architecture

This document describes the current implementation of the station credential storage in `BaronDeskAgent.ServiceCore`: DPAPI encryption at machine scope, file protection, provisioning from the command line, and how the connection uses the credential.

---

## Folder Structure

```text
Desktop-Agent
│
├── src
│   └── BaronDeskAgent.ServiceCore
│       │
│       ├── Credentials
│       │   ├── IStationCredentialStore.cs
│       │   ├── DpapiStationCredentialStore.cs
│       │   ├── ProtectedFile.cs              (DPAPI + protected ACL, shared with enrollment secrets)
│       │   ├── DpapiTokenStore.cs            (base class)
│       │   └── CredentialCommandLine.cs      (--set/--clear-station-token, --set/--clear-enrollment-token)
│       │
│       ├── Connection
│       │   └── WebSocketConnection.cs        (Authorization: Bearer on the upgrade request)
│       │
│       ├── Configuration
│       │   └── AgentOptionsValidator.cs      (StationToken in config = Development only)
│       │
│       ├── Persistence
│       │   └── AgentPaths.cs                 (CredentialFile)
│       │
│       └── Program.cs                        (provisioning commands run before the host)
│
└── tests
    └── BaronDeskAgent.ServiceCore.Tests
        ├── Credentials
        │   └── DpapiStationCredentialStoreTests.cs
        └── Configuration
            └── AgentOptionsValidatorTests.cs
```

---

## 1. Station Credential Architecture

Two identities exist (ADR-003): the **station** (this machine) and the **user** (the gamer). The station authenticates the connection once with a long-lived **station JWT**; commands are then trusted on that channel.

```text
Provisioning (elevated prompt)            Enrollment (first connection, Enrollment.md)
        │                                          │
        │ --set-station-token (manual fallback)    │ signed one-time token → admin approval
        │                                          │ → backend issues the credential
        └──────────────────┬───────────────────────┘
                           ▼
          IStationCredentialStore.SaveToken(token)
                           │
                           ▼
       ProtectedData.Protect(token, entropy, LocalMachine)
                           │
                           ▼
   %ProgramData%\BaronDeskAgent\Data\station.credential
   (protected DACL: SYSTEM + Administrators only)
                           │
                           │ every connect
                           ▼
          IStationCredentialStore.TryGetToken()
                           │
                           ▼
   ClientWebSocket upgrade request: "Authorization: Bearer <station JWT>"
   (only after the pinned certificate matched — see WebSocketConnection.md §2)
```

---

## 2. IStationCredentialStore

```csharp
public interface IStationCredentialStore
{
    string? TryGetToken();          // null when missing or unreadable (fail closed)
    void SaveToken(string token);   // ArgumentException for invalid tokens
    bool DeleteToken();
}
```

`DpapiStationCredentialStore` is the implementation registered in `Program.cs`:

```csharp
services.AddSingleton<IStationCredentialStore, DpapiStationCredentialStore>();
```

---

## 3. DPAPI at Machine Scope

```csharp
ProtectedData.Protect(tokenBytes, Entropy, DataProtectionScope.LocalMachine);
ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
```

| Choice | Reason |
|---|---|
| `DataProtectionScope.LocalMachine` | The service must decrypt at boot, with nobody signed in |
| Entropy `"BaronDesk.StationCredential.v1"` | Not a secret: binds the blob to this purpose, so other machine-scope DPAPI data cannot stand in for it |
| Plaintext buffers zeroed | `CryptographicOperations.ZeroMemory` after protect/unprotect |
| Package | `System.Security.Cryptography.ProtectedData` |

### Why the File ACL Matters

Machine scope means **any process on the PC** could decrypt the blob if it could read the file. The file permissions are the real protection:

```text
station.credential
   ├── DACL protected (no inherited entries)
   ├── SYSTEM          : Full Control
   ├── Administrators  : Full Control
   └── Owner           : SYSTEM (written by the service) or Administrators (elevated provisioning)
```

- The file is created **with** this ACL, as a temporary file in the same folder that is then moved into place. Readers never see a partial file, and the credential is never readable by anyone else, even for a moment.
- The owner is set to SYSTEM or Administrators, so the service's startup check, which deletes files not owned by trusted principals, keeps it (see `LocalStorage.md`).
- The data directory itself is restricted to SYSTEM and Administrators when the service runs as LocalSystem.

---

## 4. Token Validation

A token must be **1–8192 printable ASCII characters without spaces** (`!` to `~`). JWTs and opaque tokens fit this format, and anything else could corrupt the `Authorization` header:

| Input | Result |
|---|---|
| `eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOi…` | stored |
| empty | rejected |
| contains a space | rejected |
| contains `\r\n` (header injection) | rejected |
| non-ASCII (`tokén`) | rejected |
| longer than 8192 characters | rejected |

The same check runs when reading: a decrypted value that fails it is treated as absent.

---

## 5. Failure Handling (Fail Closed)

| Situation | `TryGetToken()` | Log |
|---|---|---|
| No file | `null` | none |
| File unreadable (ACL, I/O) | `null` | ERROR (exception type only) |
| Corrupted blob | `null` | ERROR "cannot be decrypted on this machine; re-provision it" |
| Blob copied from another PC (cloned disk image) | `null` | same: machine-scope DPAPI only decrypts on the machine that encrypted it |
| Different entropy (other application's data) | `null` | same |
| Decrypted value malformed | `null` | ERROR "malformed; re-provision it" |

Without a credential (and no enrollment token to enroll with, see `Enrollment.md`) the agent still attempts the connection (with no `Authorization` header) and logs a warning. The backend rejects the station until it is provisioned or enrolled, and the reconnect backoff keeps a rejected station from hammering the server.

**The token is never logged**, never written to SQLite, and never part of an exception message. `DpapiStationCredentialStoreTests.The_token_never_appears_in_logs` guards this.

---

## 6. Provisioning from the Command Line

Normally the credential comes from enrollment (`--set-enrollment-token`, see `Enrollment.md`). As a manual fallback it can be provisioned from an **elevated** prompt, before the service starts:

```powershell
# piped (installers, scripts)
"<station JWT>" | BaronDeskAgent.ServiceCore.exe --set-station-token

# interactive: typed without echo
BaronDeskAgent.ServiceCore.exe --set-station-token
Station token: ************

# remove
BaronDeskAgent.ServiceCore.exe --clear-station-token   # also removes the station key pair (station.key)
```

| Rule | Reason |
|---|---|
| The token is never accepted as an argument | Arguments end up in shell history and are visible to other processes |
| Elevation required (Administrator or SYSTEM) | Only they can write the protected file |
| Commands run **before** the host is built | Provisioning never starts the agent |

| Exit code | Meaning |
|---|---|
| `0` | Stored / removed |
| `1` | Not elevated |
| `2` | Invalid token, or unexpected extra arguments |

---

## 7. Configuration Fallback (Development Only)

`WebSocketConnection` resolves the token on every connect:

```csharp
var token = _credentials.TryGetToken() ?? _options.StationToken;
```

- **Development:** `Agent:StationToken` may be used for the mock server.
- **Everywhere else:** `AgentOptionsValidator` fails startup if `Agent:StationToken` is set: "StationToken must not be stored in configuration outside Development (plain text)".
- Reading on every connect means a rotated credential takes effect at the next reconnect, without a restart.

---

## 8. Honest Limits

- An **administrator or SYSTEM** attacker on the station can extract the credential. Machine-scope DPAPI cannot prevent this.
- Mitigation is limiting the blast radius: one credential per station, a revocable, rotatable (ideally short-lived) credential issued by the backend, and least privilege for the service.
- A stolen station credential exposes **one station's channel**. State this in the security document.

---

## 9. Testing

`DpapiStationCredentialStoreTests` (temporary directory, real DPAPI):

| Test | Guarantees |
|---|---|
| `No_file_means_no_credential` | Missing file → `null` |
| `Round_trips_and_rotates` | Save / read / overwrite |
| `Delete_removes_the_credential` | Clear works and is idempotent |
| `The_token_is_never_on_disk_in_plain_text` | Neither UTF-8 nor UTF-16 plaintext on disk; no temp files left |
| `Corrupt_or_foreign_blobs_fail_closed` | Garbage and other-entropy DPAPI data rejected |
| `Rejects_tokens_that_are_not_valid_header_values` | Empty, space, CR/LF, non-ASCII |
| `Rejects_oversized_tokens` | > 8192 characters |
| `The_file_is_not_readable_by_users_or_everyone` | Protected DACL; no Users / Everyone / Authenticated Users |
| `The_token_never_appears_in_logs` | No secret in any log message |

`AgentOptionsValidatorTests.A_plain_text_station_token_is_development_only` covers the configuration rule.

---

## Current Status

- [x] `IStationCredentialStore` abstraction
- [x] DPAPI `LocalMachine` encryption with purpose entropy; plaintext buffers zeroed
- [x] Protected file ACL (SYSTEM + Administrators), atomic write with the final ACL
- [x] Token format validation (header-safe) on save and on read
- [x] Fail closed on missing, corrupt, foreign or cloned credentials
- [x] Token read on every connect (rotation without restart)
- [x] `--set-station-token` (stdin or hidden input) / `--clear-station-token`, elevation required
- [x] Plain-text `StationToken` rejected outside Development
- [x] Unit tests, including "never on disk in plain text" and "never in logs"
- [x] Enrollment flow: one-time token → admin approval → credential issued (agent side, see `Enrollment.md`; response shape OPEN with backend C, skill §15 item 8)
- [ ] Credential transport confirmed with backend member C: upgrade header vs handshake (OPEN, skill §15 item 1)
