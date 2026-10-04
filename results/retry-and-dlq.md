# Retry budget, external idempotency and dead-lettering

> A focused run recorded on 2026-09-11, during development and before the first commit, so
> the counts below are those of that date. The current, commit-pinned run of the whole suite
> is in [verification.md](verification.md).

## Environment

| | |
|---|---|
| Date | 2026-09-11 (UTC) |
| .NET SDK | 10.0.400 |
| Docker | 29.6.1 |
| OS | Windows 11 Pro, build 10.0.26200.9445 |
| Commit | none (development tree, before the first commit) |

## Command

```powershell
dotnet test --filter "FullyQualifiedName~JobRetryTests|FullyQualifiedName~ExternalIdempotencyTests|FullyQualifiedName~DeadLetterTests"
```

## Result

All pass: `JobRetryTests` 18 (8 theory cases + 4 pure + 6 durable), `ExternalIdempotencyTests`
6, `DeadLetterTests` 4.

### Retry budget

| Test | Evidence |
|---|---|
| `TransientFailuresAreRetriedUntilTheExternalSystemAccepts` | `attempts=3`, outcomes `TransientFailure, TransientFailure, Succeeded`, `applied=1` |
| `PermanentFailureIsNeverRetried` | `attempts=1`, outcome `PermanentFailure`, `http_422`, `applied=0` |
| `TheAttemptBudgetIsAHardCeiling` | `attempts=5`, `attempt_budget_exhausted`, still 5 after a further 3 s |
| `RetryStateSurvivesAWorkerRestartInsteadOfStartingOver` | 1 attempt spent before a hard stop, continues to 3, `applied=1` |
| `ACrashInsideAnAttemptStillConsumesThatAttempt` | child process killed at `job.after-claim`: `attempts=1`, `Processing`, `applied=0`; lease recovers it |
| `AClaimWithAnAlreadySpentBudgetNeverOpensAnotherExternalCall` | reclaimed only to write the terminal state; `applied=0` |

### External idempotency

| Test | Evidence |
|---|---|
| `ALostResponseDoesNotApplyTheOperationTwice` | first attempt `TransientFailure/http_timeout`, `attempts>=2`, **`applied=1`**, job receipt == ERP receipt |
| `TheSameOperationKeyAlwaysReturnsTheSameReceipt` | second call `replayed: true`, same receipt, 1 row |
| `TheSameOperationKeyWithADifferentPayloadIsAConflict` | `409 payload_conflict`, original row untouched |
| `TheIdempotencyKeyMustAgreeWithTheBody` | `400 idempotency_mismatch`, `applied=0` |
| `ConcurrentIdenticalAppliesProduceOneRowAndOneReceipt` | 8 parallel applies → 1 distinct receipt, 1 row |
| `AnUnresponsiveExternalSystemNeverProducesAnEffect` | 3 attempts, all `http_timeout`, `applied=0` |

### Dead-lettering

| Test | Evidence |
|---|---|
| `ATerminalJobPublishesExactlyOneDeadLetterEventAndOnlyThenIsDeadLettered` | `DeadLettered` only after the DLQ publish is `Published`; envelope carries `reason`, `errorCode`, `attemptsStarted` |
| `ACrashBetweenDeadLetterConfirmAndUpdateStillClosesTheTerminalState` | after the crash the job is `DeadLetterPending`; the restart closes it |
| `AQuarantinedMessageProducesOneDeadLetterEventWithoutItsRawBody` | a marker embedded in the poison body does **not** appear in the DLQ message |
| `ABrokerOutageDelaysTheDeadLetterEventButNeverLosesIt` | not `Published` during the outage; delivered after |

## Two application defects these tests found

**1. The external system's conflict branch was dead code.** `ApplyExportHandler.ReplayAsync`
compared the stored row's payload hash against a hash it recomputed **from that same stored
row** — always equal. A repeat with a different payload was therefore replayed as success, and
`409 payload_conflict` was unreachable. The comparison is now against the incoming request's
hash.

**2. A configured retry schedule was silently ignored.** .NET configuration binding **appends**
array elements to whatever the property already holds. With a default of `[2, 5, 15, 30]`,
setting `Lab:JobRetryDelaysSeconds:0=0` produced `[2, 5, 15, 30, 0]`, so the first wait stayed
at 2 seconds and nothing said so. Measured directly from `JobAttempts`:

```text
attemptTimings=[#1 +0,0s ; #2 +3,6s ; #3 +11,2s ; #4 +26,7s]   <- 2 s, 5 s, 15 s: the defaults
```

The defaults now live in the accessor and the property starts empty, so configuration is
authoritative. `JobRetryTests.AConfiguredScheduleReplacesTheDefaultInsteadOfExtendingIt` guards
it. This would have misled an operator in production, not only a test.

## Expected-failure output

```text
warn: ERP attempt for <id> returned 503 (transient).
warn: Job <id> transient failure (http_503); retry scheduled in 0s.
warn: Job <id> exhausted its attempt budget (5); going terminal.
warn: Job <id> moved to DeadLetterPending (attempt_budget_exhausted); dead-letter publish is pending.
warn: Quarantining delivery <id>: reason malformed_body, body sha256 <hash>, 61 bytes.
```

## Limits

- The attempt budget (5) and the schedule are lab values, not tuned for any workload.
- `AppliedExports` is never pruned, so the external idempotency window is unbounded.
- Nothing replays a dead-lettered job or a quarantined message after a fix.
- The external system is one this repository owns. A third party that offers no idempotency key
  needs a different approach — see [ADR 0003](../docs/decisions/0003-external-idempotency.md).
