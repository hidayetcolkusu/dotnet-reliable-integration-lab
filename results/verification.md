# Acceptance runs

This file keeps every acceptance run, newest first. An older run is evidence of what was true
**then**; it is never re-presented as a current result.

| Run | Tests | What it covered |
|---|---|---|
| 2026-09-13 | 182 passed, 0 failed | after the gap remediation (G1–G8) |
| 2026-09-11 | 98 passed, 0 failed | the first full run, before the remediation |

---

# Run 2026-09-13 — after the gap remediation

This run follows an internal audit of the tree against its own design contract. The audit found
eight real gaps between what the documentation claimed and what the code enforced; each one is
listed below with the change that closed it and the test that fails without it.

The test count is not comparable with the 98 below: tests were added, not renamed.

## Environment

| | |
|---|---|
| Date | 2026-09-13 (local, Europe/Istanbul) |
| .NET SDK | 10.0.400 (pinned in `global.json`, `rollForward: disable`) |
| PowerShell | 7.x (`pwsh`) |
| Docker | 29.6.1 |
| OS | Windows 11 Pro, build 10.0.26200 |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-latest@sha256:97b4488…` |
| RabbitMQ | `rabbitmq:4.3.5-management-alpine@sha256:b3b8b7f9…` |
| Commit | none — the tree is still not committed at the time of this run |

## Commands and their real results

| Command | Result |
|---|---|
| `dotnet tool restore` | ok (`dotnet-ef` 10.0.12) |
| `dotnet restore --locked-mode` | ok — the lock files were not modified |
| `dotnet build -c Release --no-restore` | ok — 0 warnings, 0 errors |
| `dotnet test -c Release --no-build` | **182 passed, 0 failed, 0 skipped** |
| `dotnet format --verify-no-changes --no-restore` | exit code 0 |

The suite starts real SQL Server and RabbitMQ containers through Testcontainers; there is no
in-memory broker and no mocked database.

| Test class | Tests | Covers |
|---|---|---|
| `ApiTests` | 17 | acceptance, atomicity, `problem+json`, SQL unreachable, the `decimal(18,2)` bound |
| `CompositionTests` | 33 | each app's DI graph under Development validation, the loopback boundary, the Testing-only fault hooks |
| `ContractBoundaryTests` | 21 | transport-identity and trace-metadata limits, amount bounds — pure, no dependencies |
| `DeadLetterTests` | 4 | terminal state, confirmed DLQ publish, payload safety |
| `ErpAttemptBudgetTests` | 6 | one deadline for the whole HTTP attempt, against a real loopback socket |
| `ExternalIdempotencyTests` | 9 | replay, conflict, concurrency, the lost response, the external amount bound |
| `FailureWindowTests` | 9 | the nine named windows between a commit and the act that follows it |
| `InboxTests` | 14 | duplicates, quarantine reasons, ACK after commit, unstorable identities |
| `JobRetryTests` | 19 | retry classification, schedule, budget, restart, crash, idle claim |
| `OutboxTests` | 8 | confirm, return, nack, confirm timeout, leases, crash recovery |
| `RecoveryTests` | 3 | every dependency failing at once |
| `SchemaTests` | 9 | invariants against raw SQL, environment guard, no migration on startup |
| `ScriptTests` | 26 | scripts, `.env` parsing, connection-string quoting, pinned digests, demo isolation guards |
| `TracingTests` | 4 | one trace across API, outbox, inbox and the external call; degraded trace metadata |
| **Total** | **182** | |

## The eight findings, mapped to the evidence that closed them

| # | Finding | Change | Test that fails without it |
|---|---|---|---|
| G1 | "loopback only" was a default, not a boundary | [`LoopbackGuard`](../src/Integration.Shared/Runtime/LoopbackGuard.cs), called from both web composition roots | `CompositionTests.TheApiRefusesToStartOnANonLoopbackUrl`, `FakeErpRefusesToStartOnANonLoopbackUrl`, `AKestrelEndpointOverrideCannotEscapeLoopbackEither`, `APortOnlyOverrideIsRefusedBecauseItBindsEveryInterface` |
| G2 | the response body was read outside the attempt deadline | one linked deadline in `ErpClient`; `HttpClient.Timeout` set to infinite so there is no second clock | `ErpAttemptBudgetTests.HeadersThatArriveBeforeAStalledBodyDoNotEscapeTheAttemptBudget`, `ABodyDrippedOneByteAtATimeCannotOutlastTheBudgetEither` |
| G3 | identities and trace metadata could exceed their columns | `MessageValidator.IsStorableTransportMessageId`, `MessageProperties.ReadTraceContext` normalisation, new `unstorable_message_id` reason | `InboxTests.AnOversizedTransportMessageIdIsQuarantinedAndAckedInsteadOfLoopingForever`, `TracingTests.TraceMetadataTooLargeForItsColumnIsDroppedWithoutStoppingTheWork`, `ContractBoundaryTests` |
| G4 | `decimal(18,2)` was not enforced at the edges | `ExportLimits.MaxAmount`, applied in the API, the message validator and FakeErp | `ApiTests.AnAmountLargerThanTheExternalContractIsAValidationErrorNotAServerError`, `ExternalIdempotencyTests.AnAmountLargerThanTheExternalContractIsAnInvalidPayload` |
| G5 | job state and attempt result were two unguarded writes | one owner-guarded batch per transition in `JobStore`; a reclaim closes an open attempt as `Abandoned` | `FailureWindowTests.ALateResultFromAnExpiredOwnerCannotOverwriteTheCurrentOwnersOutcome` |
| G6 | the demo shared the developer's databases and broker | each run creates and removes its own database pair and its own RabbitMQ container | `ScriptTests.AScenarioRunNeverPointsItsApplicationsAtTheDevelopmentDatabases`, `AScenarioRunNeverStopsTheSharedComposeBroker`, `TheCleanupHelpersRefuseAnythingThisRunDidNotCreate` |
| G7 | several required failure windows had no test | `FailureWindowTests` (nine windows) plus the doc alignment in this file and in [failure-windows.md](../docs/architecture/failure-windows.md) | `FailureWindowTests` (all) |
| G8 | fault hooks were composed in Development too | `FaultHookRegistration` composes them only in `Testing` | `CompositionTests.FaultHooksAreNotComposedOutsideTesting`, `ANoOpHookDoesNothingEvenWhenAFaultIsConfigured` |

## The five scenarios, run for real on isolated resources

Each run created its own database pair and its own RabbitMQ container, and removed both
afterwards. Real output (trimmed to the evidence lines):

```text
==> Scenario 'broker-down' (run 60ab730b)
    databases: IntegrationLab_scn60ab730b, FakeErpLab_scn60ab730b
    broker container: lab-scn60ab730b-rabbit (amqp 57975, management 57976)
    [requestId=c0bbe850-...] API answered 202 with the broker DOWN
    [requestId=c0bbe850-...] outbox=Pending (durable, waiting for the broker)
    [requestId=c0bbe850-...] outbox=Publishing while the broker is down (never Published)
    [requestId=c0bbe850-...] outbox=Published after the broker returned
    [requestId=c0bbe850-...] final: outbox=Published, job=Completed/attempts=1, receipts=1, applied=1

==> Scenario 'duplicate-delivery' (run bf41ea64)
    [requestId=6dc26dd8-...] after the normal delivery: job=Processing/attempts=1, receipts=1, applied=0
    [requestId=6dc26dd8-...] two receipts, ONE job: job=Completed/attempts=1, receipts=2, applied=1

==> Scenario 'erp-timeout' (run 46af6437)
    [requestId=0ded3c7f-...] FakeErp will commit the effect and then lose the response
    [requestId=0ded3c7f-...] retried 2 times, applied exactly once: job=Completed/attempts=2, applied=1

==> Scenario 'poison-message' (run c67ce55a)
    [requestId=d348a146...] quarantined durably with reason 'malformed_body' (body sha256 e88adb78...)
    [requestId=d348a146...] export queue drained: acked after the quarantine committed, not requeued
    [requestId=d348a146...] dead-letter queue holds 1 message(s) carrying hashes and reason codes, not the raw body

==> Scenario 'worker-restart' (run b21d68e6)
    [requestId=d3499caa-...] before the kill: job=Processing/attempts=1, applied=0
    [requestId=d3499caa-...] the durable counter survived the kill: attempts=1
    [requestId=d3499caa-...] continued from attempt 1 to 3 and applied exactly once: job=Completed/attempts=3, applied=1
```

### The isolation check, measured rather than asserted

Before the five runs, a sentinel was placed in the **development** `IntegrationLab`: one
`Pending` outbox row and one `Pending` job — exactly the rows a non-isolated demo's dispatcher
and job processor would have claimed, published and applied. After all five runs:

```text
job=Pending/attempts=0 outbox=Pending/tries=0 publishedAt=null attemptRows=0
leftoverScenarioDatabases=0
```

`FakeErpLab.AppliedExports` still held only the five rows dated 2026-09-11, so the five runs of
2026-09-13 produced **no** external effect in the development database. No `lab-scn*` container
was left behind, and the developer's `lab-sql` and `lab-rabbit` containers were never stopped.
The sentinel rows were removed afterwards, leaving the development database as it was found.

## What this run does not prove

- **No remote CI run exists.** `.github/workflows/ci.yml` is committed; nothing more is claimed.
- **No clean-checkout run.** The chain ran in a working tree, not in a fresh clone, and there is
  still no commit.
- **One machine, one OS.** Windows 11 with PowerShell 7. The scripts use no Windows-only API,
  but no other platform has been tried.
- **No performance data.** Nothing here measures throughput, latency or loss rates.
- **The windows listed as open in [failure-windows.md](../docs/architecture/failure-windows.md)
  are still open** — in particular a SQL Server failover with a transaction in flight, and an
  external system that crashes *before* committing.

---

# Run 2026-09-11 — the first full run

*Historical record of the run that preceded the gap remediation. The 98/98 below was true of
that tree; it is not a result of the 2026-09-13 run above.*

## Environment

| | |
|---|---|
| Date | 2026-09-11 (UTC) |
| .NET SDK | 10.0.400 (pinned in `global.json`, `rollForward: disable`) |
| PowerShell | 7.6.6 |
| Docker | 29.6.1 |
| OS | Windows 11 Pro, build 10.0.26200.9445 |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-latest@sha256:97b4488…` |
| RabbitMQ | `rabbitmq:4.3.5-management-alpine@sha256:b3b8b7f9…` |
| Commit | none — the tree is not committed at the time of this run |

## Command

```powershell
pwsh -File scripts/verify-clean.ps1
```

which runs, stopping at the first failure:

```text
dotnet --version
dotnet tool restore
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --logger trx
dotnet format --verify-no-changes --no-restore
```

## Result

```text
==> dotnet --version
    SDK 10.0.400
==> dotnet tool restore
==> dotnet restore --locked-mode
==> dotnet build -c Release
==> dotnet test -c Release
    total=98 passed=98 failed=0
==> dotnet format --verify-no-changes

==> Verification summary
    SDK: 10.0.400
    tool restore: ok
    locked restore: ok
    Release build: ok
    tests: ok (total=98 passed=98)
    format: ok
```

The suite starts real SQL Server and RabbitMQ containers through Testcontainers; there is no
in-memory broker and no mocked database.

| Test class | Tests | Covers |
|---|---|---|
| `ApiTests` | 15 | acceptance, atomicity, `problem+json`, SQL unreachable |
| `SchemaTests` | 9 | invariants against raw SQL, environment guard, no migration on startup |
| `OutboxTests` | 8 | confirm, return, nack, confirm timeout, leases, crash recovery |
| `InboxTests` | 10 | duplicates, quarantine reasons, ACK after commit |
| `JobRetryTests` | 19 | retry classification, schedule, budget, restart, crash, idle claim |
| `ExternalIdempotencyTests` | 6 | replay, conflict, concurrency, the lost response |
| `DeadLetterTests` | 4 | terminal state, confirmed DLQ publish, payload safety |
| `TracingTests` | 3 | one trace across API, outbox, inbox and the external call |
| `RecoveryTests` | 3 | every dependency failing at once |
| `ScriptTests` | 17 | scripts, `.env` parsing, connection-string quoting, pinned digests, documented commands |
| `CompositionTests` | 4 | each app's DI graph under Development validation |
| **Total** | **98** | |

## Setup verified from the documented path

```powershell
docker compose up -d
pwsh -File scripts/init-lab.ps1
```

```text
==> Creating and migrating IntegrationLab
      IntegrationLab database created and migrated.
==> Creating and migrating FakeErpLab
      FakeErpLab database created and migrated.
==> Verifying the schema exists
    IntegrationLab: 6 tables
    FakeErpLab: 1 table
==> Storing connection strings in user-secrets
```

## The eight goals, mapped to evidence

| # | Goal | Test | Document |
|---|---|---|---|
| 1 | the event is written to the outbox in the same SQL transaction as the request | `ApiTests.FaultAfterRequestInsertRollsBackRequestAndOutbox`, `RecoveryTests.ACrashBetweenTheRequestAndItsOutboxRowLeavesNoAcceptedWorkBehind` | [ADR 0001](../docs/decisions/0001-outbox-inbox.md) |
| 2 | a broker outage does not lose the record; it is published on return | `ApiTests.SubmitAcceptsWithBrokerDownAndWorkWaitsInSql`, `OutboxTests.BrokerOutageKeepsRowsRetryableUntilTheBrokerReturns` | [broker-down](../docs/scenarios/broker-down.md) |
| 3 | a lost confirm or a crash before the update can redeliver the same message | `OutboxTests.ConfirmTimeoutAgainstAFrozenBrokerKeepsTheRowPendingThenRecovers`, `OutboxTests.CrashAfterConfirmRepublishesTheSameTransportMessageId` | [failure windows](../docs/architecture/failure-windows.md) |
| 4 | ACK follows the durable inbox; a redelivery creates no second job | `InboxTests` (all) | [duplicate-delivery](../docs/scenarios/duplicate-delivery.md) |
| 5 | a transient external failure is retried on a budget a restart cannot reset | `JobRetryTests` (all) | [ADR 0002](../docs/decisions/0002-sql-retry.md), [worker-restart](../docs/scenarios/worker-restart.md) |
| 6 | the same operation key produces no second external effect | `ExternalIdempotencyTests` (all) | [ADR 0003](../docs/decisions/0003-external-idempotency.md), [erp-timeout](../docs/scenarios/erp-timeout.md) |
| 7 | a terminal failure or an exhausted budget produces an inspectable dead letter | `DeadLetterTests` (all) | [ADR 0005](../docs/decisions/0005-dead-letter.md), [poison-message](../docs/scenarios/poison-message.md) |
| 8 | one request is traceable across API, outbox, broker, inbox, HTTP attempt and result | `TracingTests` (all) | [ADR 0006](../docs/decisions/0006-tracing.md) |

## Defects these tests found in the application

Recorded because they are the point of writing the tests, not footnotes:

| # | Defect | Where it hid |
|---|---|---|
| 1 | `BasicPublishAsync` waited for the confirm with **no timeout**, and collapsed nack and `basic.return` into one exception | only a frozen (not stopped) broker reveals it |
| 2 | FakeErp compared the stored payload hash **with itself**, so `409 payload_conflict` was unreachable | needs a repeat with a changed payload |
| 3 | array configuration **appends** to a non-empty default, so a configured retry schedule was silently ignored | the run still passed, just on the wrong schedule |
| 4 | `/health/live` ran **all** health checks, so a database outage reported the process as dead | only visible with SQL actually unreachable |
| 5 | the valueless `--initialize-db` flag **swallowed the next argument**, so `--environment=Development` was lost | only on the documented setup path |
| 6 | the context factories were singletons consuming scoped options | DI validation is on only in Development |
| 7 | `MapHealthChecks` in FakeErp had no `AddHealthChecks()` | FakeErp was never started by a test before |
| 8 | `JobStore.TryClaimAsync` committed while its DataReader was still open | throws only when NOTHING is due, and the tests always had work waiting |

Two defects in the **test harness** are recorded in
[broker-recovery.md](broker-recovery.md) and [duplicates.md](duplicates.md); one in the scripts
(an error message that echoed a connection string, password included) was found while running
`init-lab.ps1` for real.

## The scenario scripts, run for real

Against `docker compose up -d` + `pwsh -File scripts/init-lab.ps1` on the same machine, all
five scenarios completed as expected. Real output:

```text
==> Scenario 'broker-down'
    [requestId=707fd5f7-...] API answered 202 with the broker DOWN
    [requestId=707fd5f7-...] outbox=Pending (durable, waiting for the broker)
    [requestId=707fd5f7-...] outbox=Publishing while the broker is down (never Published)
    [requestId=707fd5f7-...] outbox=Published after the broker returned
    [requestId=707fd5f7-...] final: outbox=Published, job=Completed/attempts=1, receipts=1, applied=1

==> Scenario 'duplicate-delivery'
    [requestId=8567d524-...] after the normal delivery: receipts=1, applied=0
    [requestId=8567d524-...] two receipts, ONE job: receipts=2, applied=1
    [requestId=8567d524-...] final: outbox=Published, job=Completed/attempts=1, receipts=2, applied=1

==> Scenario 'erp-timeout'
    [requestId=fddba8a8-...] FakeErp will commit the effect and then lose the response
    [requestId=fddba8a8-...] retried 2 times, applied exactly once: job=Completed/attempts=2, applied=1

==> Scenario 'poison-message'
    [requestId=1ef1838e...] quarantined durably with reason 'malformed_body' (body sha256 690fb3e2...)
    [requestId=1ef1838e...] export queue drained: acked after the quarantine committed, not requeued
    [requestId=1ef1838e...] dead-letter queue holds 1 message(s) carrying hashes and reason codes, not the raw body

==> Scenario 'worker-restart'
    [requestId=b9b1d7d3-...] before the kill: job=Processing/attempts=1, applied=0
    [requestId=b9b1d7d3-...] the durable counter survived the kill: attempts=1
    [requestId=b9b1d7d3-...] continued from attempt 1 to 3 and applied exactly once: job=Completed/attempts=3, applied=1
```

Note `outbox=Publishing` in the broker-down run: the row is claimed by an owner whose publish
cannot complete. That is the lease's job, and it is exactly what "never Published" means here.

### What running them for real exposed

Three defects that no test had reached, because every one of them needs a worker running
against an idle queue or the documented `dotnet run` path rather than a test host:

| Defect | Symptom |
|---|---|
| `JobStore.TryClaimAsync` committed its transaction while the DataReader was still open | with nothing due, the claim threw on EVERY poll — an idle worker sat in an error-and-backoff loop. Tests never saw it because they always had work waiting. |
| the valueless `--initialize-db` flag swallowed the next argument | `--environment=Development` was lost, and the lab's own environment guard refused to start the setup command |
| the EF context factories were singletons consuming scoped options | DI validation runs only in Development, so `dotnet run` failed where `dotnet test` passed |

And three in the scripts themselves: an error message that echoed a connection string
(password included), a `[string]`-typed parameter silently coercing a REST response so every
queue depth read as 0, and `$` in a `.env` value being interpolated by docker compose so the
container received a different password than the file records.

## What this run does not prove

- **No remote CI run exists.** `.github/workflows/ci.yml` is committed; nothing more is claimed.
- **The scenarios were run against a lab that had already run others.** They share one
  development database on purpose (they never delete rows), so the counts above are for their
  own fresh requestIds, not for an empty database.
- **One machine, one OS.** Windows 11 with PowerShell 7.6.6. The scripts use no Windows-only
  API, but no other platform has been tried.
- **No performance data.** Nothing here measures throughput, latency or loss rates.
- **No clean-checkout run.** The acceptance chain ran in a working tree, not in a fresh clone.
