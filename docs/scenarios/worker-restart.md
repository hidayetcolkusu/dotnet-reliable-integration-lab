# Scenario: the worker is killed mid-flight

## What is being demonstrated

A retry counter that lives in memory is a lie the moment the process dies: the replacement
starts from zero, and a crash loop can call the external system without limit. The same is
true of a `Task.Delay`-held retry schedule — it evaporates.

So in this lab **the retry state is a SQL row**. The attempt counter, the next attempt time,
the last error code and the lease all live in `IntegrationJobs`, which is why a hard kill
changes nothing about a job's budget or schedule.

## Run it

```powershell
pwsh -File scripts/run-scenario.ps1 -Scenario worker-restart
```

The first worker spends one attempt against a 503, then parks the job for 30 seconds. It is
killed with no graceful shutdown. A replacement worker with a short retry wait takes over.

## What you should see

```text
    [requestId=...] accepted; the external system will answer 503 twice
==> Starting the first worker
    [requestId=...] before the kill: outbox=Published, job=RetryScheduled/attempts=1, receipts=1, applied=0
==> Killing the worker hard (no graceful shutdown)
    [requestId=...] the durable counter survived the kill: attempts=1
==> Starting a replacement worker
    [requestId=...] continued from attempt 1 to 3 and applied exactly once: ... job=Completed/attempts=3 ... applied=1
```

`attempts=1` before and `attempts=3` after is the evidence that the budget continued rather
than restarting.

## What the lease is for

A worker that dies *while holding* a job leaves the row in `Processing` with a `LeaseToken`
nobody will ever release. The lease's expiry is what makes that row claimable again:

```sql
OR (Status = 'Processing' AND LeaseUntilUtc < SYSUTCDATETIME())
```

Two rules keep that safe:

1. **A crash inside an attempt still consumes that attempt.** The counter is incremented in
   the same transaction as the claim, so a process that dies mid-call cannot get a free retry.
   Five crashes therefore exhaust the budget exactly like five failures would.
2. **A late result cannot overwrite a new owner's state.** Every state update is guarded by
   `AND LeaseToken = @token`, so the previous owner's write affects zero rows and is logged as
   a lost lease instead of corrupting the row.

A claim whose budget is already spent is reclaimed only to write the terminal state — it never
opens a sixth external call. That is
`JobRetryTests.AClaimWithAnAlreadySpentBudgetNeverOpensAnotherExternalCall`.

## Where to look in the code

| Step | Code |
|---|---|
| Atomic claim, counter and lease in one transaction | [`JobStore.TryClaimAsync`](../../src/Integration.Shared/Persistence/JobStore.cs) |
| Token-guarded result updates | `CompleteAsync` / `ScheduleRetryAsync` / `MoveToDeadLetterPendingAsync` in the same file |
| Budget check before any HTTP call | [`JobProcessor.ExecuteAsync`](../../src/Integration.Worker/Processing/JobProcessor.cs) |
| The crash window itself | `FaultPoints.JobAfterClaim` in [`IFaultHooks`](../../src/Integration.Shared/Runtime/IFaultHooks.cs) |

## The same claim as tests

- `JobRetryTests.RetryStateSurvivesAWorkerRestartInsteadOfStartingOver`
- `JobRetryTests.ACrashInsideAnAttemptStillConsumesThatAttempt` (a real child process, killed
  by an injected fault at the claim boundary)
- `OutboxTests.StaleLeaseOwnerCannotMarkPublished` for the publish side of the same rule
- `RecoveryTests.EveryDependencyFailingAtOnceStillAppliesEachRequestExactlyOnce`, which
  combines a broker outage, a hard kill and external failures in one run

## What this does not prove

The lease duration is a trade-off this lab does not tune: too short and a slow-but-alive
worker loses its claim, too long and a crashed worker's job waits. Nothing here measures the
right value for a real workload, and there is no leader election or fencing token beyond the
per-row lease.
