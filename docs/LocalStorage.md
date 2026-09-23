# BaronDesk Agent : Local Storage & Schema Migrations Architecture

This document describes the current implementation of the agent's local data: the SQLite database, the protection of the data directory, versioned schema migrations, and startup initialization in `BaronDeskAgent.ServiceCore`.

---

## Folder Structure

```text
Desktop-Agent
│
└── src
    └── BaronDeskAgent.ServiceCore
        │
        ├── Persistence
        │   ├── AgentPaths.cs              (data directory, database, credential file)
        │   ├── SecureDataDirectory.cs     (ACL hardening, planted-file removal)
        │   ├── AgentDatabase.cs           (connection factory)
        │   ├── DatabaseInitializer.cs     (PRAGMAs + versioned migrations)
        │   └── DevelopmentDataSeeder.cs   (Development-only sample data)
        │
        ├── Commands/CommandOutcomeStore.cs        → HandledCommands
        ├── Games/GameCatalogRepository.cs         → GameCatalog
        ├── Policy/PolicyRepository.cs             → StationPolicy
        ├── Telemetry/Outbox/OutboxRepository.cs   → OutboxMessages
        └── Credentials/DpapiStationCredentialStore.cs → station.credential (not SQLite)
```

---

## 1. What Is Stored Locally

```text
%ProgramData%\BaronDeskAgent\
└── Data\
    ├── agent.sqlite          (+ -wal, -shm)
    │     ├── OutboxMessages   alerts waiting for delivery
    │     ├── GameCatalog      gameId → executable (source of LAUNCH_GAME)
    │     ├── StationPolicy    backend-tuned limits (single 'active' row)
    │     └── HandledCommands  ids of the last 256 successful commands
    │
    └── station.credential    station JWT, DPAPI-encrypted (see CredentialStore.md)
```

**Never stored:** passwords, PINs or the station credential in SQLite, and live telemetry (cache-only on the backend).

---

## 2. Data Directory Protection (`SecureDataDirectory`)

`C:\ProgramData` lets standard users **create** files and folders. Without hardening, a gamer could pre-create the agent's folder, the database, or its `-wal`/`-shm` files, then edit the game catalog the service launches programs from.

```text
AgentDatabase constructor
      │
      └── SecureDataDirectory.Prepare(root, data)
             │
             ├── not running as LocalSystem (developer, tests) ──► just create the folder
             │
             └── LocalSystem (Windows service)
                    ├── root + data folders: owner SYSTEM, protected DACL
                    │      SYSTEM         : Full Control (inherited by children)
                    │      Administrators : Full Control (inherited by children)
                    │
                    └── every file in Data\ not owned by SYSTEM or Administrators
                           → CRITICAL log ("possible tampering attempt") → deleted
```

Deleting a planted database costs only local, rebuildable state (outbox, policy, catalog cache). That is the safe side of the trade-off.

---

## 3. AgentDatabase

```csharp
public sealed class AgentDatabase
{
    public AgentDatabase(ILogger<AgentDatabase> logger);   // %ProgramData%\BaronDeskAgent\Data\agent.sqlite
    internal AgentDatabase(string databaseFile);           // tests

    public string ConnectionString { get; }
    public Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct);
}
```

- `Microsoft.Data.Sqlite` directly (no EF Core) for footprint.
- Connections are pooled by the provider. The old `Cache=Shared` was removed (discouraged together with WAL).
- `OpenConnectionAsync` disposes the connection if opening fails.
- All SQL is parameterized; timestamps use `CultureInfo.InvariantCulture` and the round-trip (`"O"`) format.

---

## 4. Versioned Schema Migrations

`DatabaseInitializer` applies **append-only** migrations tracked by `PRAGMA user_version`:

```text
InitializeAsync()
      │
      ├── PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;   (outside any transaction)
      │
      ├── version = PRAGMA user_version
      │
      └── for each migration after `version`
             ├── BEGIN
             ├── run migration SQL
             ├── PRAGMA user_version = n
             └── COMMIT            (a failure rolls the step back)
```

| Version | Content |
|---|---|
| **v1** | Original schema: `OutboxMessages`, `GameCatalog`, `StationPolicy` (`CREATE … IF NOT EXISTS`, so databases created before versioning adopt it) |
| **v2** | `StationPolicy.StopGameOnLock` column (default 1); clears `OutboxMessages` (old rows held whole envelopes with a stale `seq`/`ts` that the backend's anti-replay rejects); creates `HandledCommands`; removes the old seeded `notepad` catalog row |

Rules for future changes: **add a new migration at the end, never edit a shipped one.**

### Schema: `HandledCommands` (v2)

```sql
CREATE TABLE IF NOT EXISTS HandledCommands
(
    Seq INTEGER PRIMARY KEY AUTOINCREMENT,
    CommandId TEXT NOT NULL UNIQUE,
    HandledAt TEXT NOT NULL
);
-- pruned to the last 256 rows on every insert
```

The other tables are documented with their features: `OutboxMessages` in `HardwareTelemetry.md`, `GameCatalog` in `SessionCommandsAndSystem.md`, and `StationPolicy` in `PolicyStore.md`.

---

## 5. Startup Initialization Order

`Program.cs` prepares local state **before** any hosted worker starts:

```csharp
static async Task InitializeLocalStateAsync(IServiceProvider provider, IHostEnvironment environment)
{
    await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();   // directory hardening + migrations

    if (environment.IsDevelopment())
    {
        await provider.GetRequiredService<DevelopmentDataSeeder>().SeedAsync();  // 'notepad' test game
    }

    await provider.GetRequiredService<IPolicyStore>().InitializeAsync();          // validated policy
    await provider.GetRequiredService<CommandOutcomeStore>().LoadAsync();         // redelivery protection

    provider.GetRequiredService<StationController>();                             // lease watchdog starts
}
```

`AgentOptions` are validated at the same time (`ValidateOnStart`), so an unsafe configuration stops the agent before it touches the network.

---

## 6. Edge Cases Handled

| Case | Handling |
|---|---|
| Folder or files pre-created by a standard user | ACL reset; foreign-owned files deleted with a CRITICAL log |
| Database created before versioning | v1 is idempotent, v2 upgrades it |
| Migration fails half-way | Transaction rolled back; the version is not bumped |
| Stored policy corrupted or out of range | Rejected at load; defaults persisted |
| SQLite unusable at policy load | In-memory defaults; lock enforcement keeps working |
| `HandledCommands` write fails | In-memory deduplication still works; only restart protection is lost (warning logged) |
| Outbox grows during a long outage | Capped at 1,000 rows, oldest dropped |
| Developer runs the agent without admin rights | No ACL changes; plain folder creation |

---

## Current Status

- [x] Single SQLite database (WAL) for outbox, catalog, policy and handled command ids
- [x] Data directory restricted to SYSTEM and Administrators when running as a service
- [x] Files planted by other users detected and removed at startup
- [x] Versioned, transactional, append-only migrations (`PRAGMA user_version`)
- [x] Development-only sample data; no test entries in production databases
- [x] Invariant-culture timestamps; parameterized SQL everywhere
- [x] Local state initialized before any worker starts
- [x] Station credential kept out of SQLite (DPAPI file)
