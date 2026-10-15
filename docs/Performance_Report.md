# Desktop Agent — Network Resilience Handover (A2–A6)

## 1. Purpose

This document summarizes the Desktop Agent work completed so far, the local tests performed, and the remaining backend responsibilities.

The main objective is to make the agent resilient to network interruptions and process restarts while preserving the station's lock state and active-session information.

**Important:** The agent-side persistence and recovery behavior has been tested against the local mock server. Full integration with the real backend is still required.

---

## 2. Current Status

| Area | Status | Notes |
|---|---|---|
| A2 — Agent restart recovery | Implemented and locally tested | Restores the previous session and keeps the station locked after restart |
| A3 — Network-loss persistence | Implemented and locally tested | Persists and reports `ContactLostAt` after an unexpected connection loss |
| SQLite migration v5 | Applied locally | Adds `ContactLostAt` and `RestartRecovered` to `SessionState` |
| Reconnection and state reporting | Tested against mock server | Agent reconnects and sends the persisted state |
| Benchmarking | Completed | No additional CPU/RAM benchmark planned |
| A5 — Logging review | Remaining | Review logs for useful diagnostics without excessive noise |
| A4 — Development-only simulation | Remaining | Complete the planned development simulation |
| A6 — Documentation | Remaining | Finalize implementation and test documentation |
| Real backend integration | Pending | Backend teammate must implement and validate reconciliation behavior |

---

## 3. Agent-Side Implementation

### 3.1 A2 — Recovery After Agent Restart

The agent persists its session state in SQLite.

The `SessionState` record contains:

- `SessionId`
- `LockReason`
- `LockedAt`
- `ContactLostAt`
- `RestartRecovered`

After an agent process restart, the agent restores the previous session state, including the session ID, and keeps the station locked.

The recovered state is reported with:

- `LockReason = "agent_restart"`
- `RestartRecovered = true`

The goal is to prevent a process restart from accidentally leaving a station unlocked or losing track of its active session.

### 3.2 A3 — Network-Loss Persistence

When an established connection ends unexpectedly, the agent records `ContactLostAt`, unless the application is shutting down normally.

After reconnecting, the agent includes the persisted state in its `state_report`, including:

- `LockReason`
- `LockedAt`
- `ContactLostAt`
- `RestartRecovered`

The agent also uses a monotonic time anchor between server-time synchronizations.

This provides the backend with information about the agent's local recovery state. It does **not** replace backend-side session validation or authoritative disconnect tracking.

### 3.3 Database Migration

Migration v5 adds these columns to `SessionState`:

```sql
ContactLostAt TEXT NULL,
RestartRecovered INTEGER NOT NULL DEFAULT 0
```

Migration v4 should remain unchanged.

The local SQLite database is stored at:

```text
%ProgramData%\BaronDeskAgent\Data\agent.sqlite
```

### 3.4 Relevant Project Components

- `ConnectionWorker`: connection handling, unexpected disconnect detection, reconnection, and state reporting.
- `SessionState`: persisted station/session recovery state.
- `ServerClock`: server-time synchronization and monotonic time anchoring.
- `tools/mock-server/mock-server.js`: local WebSocket mock server used for development tests.

Mock WebSocket endpoint:

```text
ws://127.0.0.1:8443/agent-ws
```

---

## 4. Local Tests Performed

### Test 1 — Initial Connection

The agent connected to the mock server, completed the handshake, sent a `state_report`, and continued sending heartbeats.

Some development-environment limitations were observed:

- The station had not been provisioned with credentials.
- Game catalog synchronization failed because valid station credentials were unavailable.
- The LockUI helper was not running, so the agent reported `HelperNotConnected`.

These are environment/setup limitations and should not be interpreted as failures of the persistence tests.

### Test 2 — Network Interruption and Reconnection

The mock server was stopped and restarted.

After reconnecting, the agent reported a persisted `ContactLostAt` value:

```text
ContactLostAt: 2026-10-12T14:07:51.7309437+00:00
RestartRecovered: false
```

No session was active in this test.

**Result:** The agent persisted and reported the contact-loss timestamp across the network interruption.

### Test 3 — Agent Restart During an Active Session

A session was created through mock-server menu option 2, and the agent received an `UNLOCK` acknowledgement.

Session ID:

```text
e2aae819-e3cf-4cf0-9790-27fe47399d90
```

The agent process was then restarted.

The next `state_report` contained:

```text
Locked: true
SessionId: e2aae819-e3cf-4cf0-9790-27fe47399d90
LockReason: agent_restart
LockedAt: 2026-10-12T14:12:17.5011679+00:00
ContactLostAt: none
RestartRecovered: true
```

Heartbeats continued after recovery.

**Result:** The agent restored the session ID and remained locked after restart. The mock-server test confirmed the expected agent-side recovery behavior.

**Limitation:** This test does not prove that the real backend correctly reconciles the recovered session or preserves billing and authorization invariants.

---

## 5. Backend Work Required

The following tasks are for the backend teammate.

### 5.1 Process `state_report` as a Reconciliation Message

The backend should validate incoming state reports from authenticated, enrolled stations.

It must not blindly trust the reported state or treat a state report as an instruction to unlock a station.

Validate at least:

- Station identity and authentication.
- Whether the reported `SessionId` belongs to that station.
- Whether the session exists and is still eligible to resume.
- Whether the reported state conflicts with authoritative backend state.
- Whether the message is duplicated or stale.

### 5.2 Handle `ContactLostAt`

The backend should store and interpret `ContactLostAt` where appropriate.

Important distinctions:

- `ContactLostAt` is an agent-reported timestamp.
- The backend's own connection/disconnect observations are separate evidence.
- The agent timestamp must not automatically override backend records.

Validate timestamp format and plausibility. Account for clock skew and delayed or repeated reports. Keep the handling idempotent so that reconnecting repeatedly does not create inconsistent records.

The backend should define how it records the first observed disconnect, subsequent reconnects, and any additional recovery events.

### 5.3 Handle `RestartRecovered` and `LockReason`

When the backend receives:

```text
RestartRecovered: true
LockReason: agent_restart
```

it should interpret this as a report that the agent recovered persisted local state after restarting.

It should then reconcile that report against authoritative backend data.

For example:

- If the reported session is still active and belongs to the station, apply the approved recovery policy.
- If the session has ended or been cancelled, do not resume it merely because the agent reports it locally.
- If the session belongs to another station, reject the mismatch and investigate it.
- If a staff, billing, administrative, or security lock is in effect, do not override it because the agent restarted.

The final unlock decision must follow backend authorization and session policy.

### 5.4 Preserve Session and Billing Correctness

Recovery and repeated `state_report` messages must not:

- Create duplicate sessions.
- Start duplicate billing periods.
- Resume an ended session.
- Charge a user twice for the same interval.
- Override an administrative or security lock.
- Associate a session with the wrong station.

Session recovery and billing reconciliation should be handled as separate, explicitly defined responsibilities where necessary.

### 5.5 Define Acknowledgement and Durability Semantics

A successful network send does not prove that the backend has durably persisted the report.

The backend and agent should agree on:

- What acknowledgement means.
- Whether the acknowledgement is sent only after durable processing.
- How repeated reports are deduplicated.
- Whether reports require event IDs or sequence numbers.
- When persisted recovery markers can safely be cleared.
- What happens when the connection drops before the acknowledgement arrives.

Do not clear recovery information merely because a message was sent. If an acknowledgement or durable outbox protocol is introduced, ensure retries remain safe and idempotent.

### 5.6 Real Backend Integration Tests

The following cases should be tested against the actual backend:

1. Normal station connection and state report.
2. Network interruption followed by reconnection.
3. Agent restart during an active session.
4. Duplicate state reports.
5. A report arriving after the backend session has ended.
6. A report for a session belonging to another station.
7. A staff or billing lock that must remain in force.
8. Invalid, missing, or malformed session identifiers and timestamps.
9. Missing or invalid station credentials.
10. Backend restart while an agent is disconnected.
11. Reconnection after an extended offline period.
12. Billing correctness across disconnects and restarts.
13. Acknowledgement loss followed by a retry.
14. Conflicting local and backend session states.

For every test, verify both the backend's persisted state and the actual station lock behavior.

---

## 6. Remaining Agent-Side Work

### A5 — Logging Review

Review the agent's logs to ensure they clearly distinguish:

- Initial connection and successful reconnection.
- Unexpected connection loss.
- State persistence.
- Agent restart recovery.
- Reconciliation reports and acknowledgements.
- Invalid or rejected backend responses.

Avoid logging secrets, station credentials, tokens, or other sensitive values. Keep routine heartbeat logs from overwhelming the diagnostic logs.

### A4 — Development-Only Simulation

Complete the planned simulation using the local development environment.

Keep simulation behavior isolated from production code paths and clearly mark any test-only commands or configurations.

### A6 — Documentation

Document the recovery state fields, expected transitions, test procedure, and backend reconciliation contract. Update the documentation after the real backend integration tests are complete.

---

## 7. Development Environment and Safety Notes

- The project is a .NET 10 Windows agent.
- The local mock server is used for development tests.
- Development configuration currently permits untrusted certificates through `AllowUntrustedCertificate=true`. This must remain development-only; production certificate validation must not be disabled.
- The station is not fully enrolled in the local test environment, so valid credentials were unavailable.
- The LockUI helper was not running during the tests.
- A SQLite read-only error occurred when the agent ran without sufficient permissions. Running the process elevated allowed the migration and startup to proceed.
- Do not delete the SQLite database or its `-wal` / `-shm` files as a workaround.
- Do not grant `Everyone` full control over the agent's data directory. Configure the required directory and file permissions for the intended service account instead.
- The development machine was under high memory pressure during testing, so avoid interpreting its resource usage as a definitive production benchmark.

---

## 8. Handover Summary

**Completed and tested locally:** agent-side network-loss persistence, restart recovery, SQLite migration v5, reconnection, and recovery state reporting against the mock server.

**Still required:** logging review, development-only simulation, final documentation, and—most importantly—real backend reconciliation and integration tests.

The backend must remain authoritative for station identity, session validity, authorization, lock policy, and billing. Agent-reported recovery fields are evidence to reconcile, not permission to resume or unlock a session automatically.
