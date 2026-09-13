# ADR 0004 — Per-row leases with token-guarded updates

- **Status:** accepted
- **Date:** 2026-09-11
- **Scope:** `OutboxStore`, `JobStore`

## Context

Two problems appear the moment more than one worker exists, or one worker is allowed to crash:

1. **Double claiming.** Two dispatchers must not publish the same outbox row, and two
   processors must not call the external system for the same job at the same time.
2. **Stranded rows.** A worker that dies while holding a row leaves it mid-flight. Something
   has to make it claimable again, without a human.

A third problem follows from the fix for the second: once a stranded row is reclaimed, the
**original owner may still be alive** — paused by GC, blocked on a socket, or simply slow — and
may eventually try to write its result. That late write must not overwrite the new owner's
state.

## Decision

Every claimable row carries `LeaseToken` (a GUID the claimer generates) and `LeaseUntilUtc`.

**Claiming** is one statement, in its own short transaction:

```sql
;WITH candidate AS (
    SELECT TOP (1) * FROM IntegrationJobs WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE (Status = 'Pending'        AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= SYSUTCDATETIME()))
       OR (Status = 'RetryScheduled' AND NextAttemptAtUtc <= SYSUTCDATETIME())
       OR (Status = 'Processing'     AND LeaseUntilUtc < SYSUTCDATETIME())   -- the recovery path
    ORDER BY NextAttemptAtUtc, CreatedAtUtc, RequestId
)
UPDATE candidate SET Status = 'Processing', LeaseToken = @token,
                     LeaseUntilUtc = DATEADD(second, @leaseSeconds, SYSUTCDATETIME())
OUTPUT inserted.*;
```

- `UPDLOCK` takes the update lock at read time, so two claimers cannot select the same row;
- `READPAST` makes a second claimer **skip** locked rows instead of blocking;
- the third `OR` clause is the entire crash-recovery mechanism;
- `OUTPUT` returns the claimed row in the same round trip.

**Every result update** carries the token:

```sql
UPDATE IntegrationJobs SET Status = 'Completed', ...
WHERE RequestId = @id AND LeaseToken = @token AND Status = 'Processing';
```

A stale owner's update affects zero rows. The code checks the affected-row count and logs
"lost its lease" rather than assuming success.

**The clock is the server's.** Every comparison uses `SYSUTCDATETIME()`, so leases and
schedules are never compared across two machines' clocks.

## Consequences

**Accepted:**

- No distributed lock manager, no leader election, no extra infrastructure. The database that
  already holds the state also arbitrates ownership.
- A crashed worker costs at most one lease duration of latency.
- Two workers can run side by side and provably claim disjoint rows.

**Costs:**

- **The lease duration is a real trade-off with no universally right value.** Too short and a
  slow-but-alive worker loses a row it is still working on (producing a duplicate external
  call); too long and a crashed worker's job waits. This lab defaults to 30 seconds and shortens
  it in tests; nothing here measures the right value for a real workload.
- A lease is **not a fencing token in the external system.** The guard protects our database
  rows; it cannot stop a stale owner's HTTP call from reaching the ERP. That is why the external
  idempotency key ([ADR 0003](0003-external-idempotency.md)) is load-bearing, not a nicety.
- Leases are not renewed mid-work. A long external call must finish inside the lease or risk
  being reclaimed.
- `ORDER BY` makes the claim roughly FIFO, which means a permanently failing old row is
  retried before newer work each time it becomes due.

## Alternatives rejected

| Alternative | Why not |
|---|---|
| `sp_getapplock` | a session-scoped lock; a crashed session releases it, but the row state is still mid-flight and there is no record of who held it |
| Optimistic concurrency (`rowversion`) alone | detects a conflict but gives no way to reclaim a row whose owner is gone |
| Redis/etcd distributed lock | new infrastructure and a new failure mode, for state the database already owns |
| `SELECT ... FOR UPDATE SKIP LOCKED` held for the whole attempt | holds a transaction open across an HTTP call — exactly what must not happen |
| A `WorkerId` column instead of a token | a restarted worker reuses its identity and would pass its own guard |
| No recovery at all (manual reset) | turns every crash into an operational incident |

## Evidence

- `OutboxTests.TwoDispatchersClaimDisjointRows`
- `OutboxTests.TwoWorkersPublishEveryRowExactlyOnce`
- `OutboxTests.StaleLeaseOwnerCannotMarkPublished`
- `OutboxTests.CrashAfterConfirmRepublishesTheSameTransportMessageId`
- `JobRetryTests.ACrashInsideAnAttemptStillConsumesThatAttempt`
- `RecoveryTests.EveryDependencyFailingAtOnceStillAppliesEachRequestExactlyOnce`
