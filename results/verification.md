# Acceptance runs

This file keeps every acceptance run, newest first. An older run is evidence of what was true
**then**; it is never re-presented as a current result.

| Run | Tests | What it covered |
|---|---|---|
| 2026-10-04 | 182 passed, 0 failed (fresh clone of `c94ac50`); 186 passed, 0 failed with the launch-profile fix | the first run pinned to a commit: a fresh clone, the README setup path, `broker-down`; remote CI green on the same commit |
| 2026-09-13 | 182 passed, 0 failed | after the gap remediation (G1–G8) |
| 2026-09-11 | 98 passed, 0 failed | the first full run, before the remediation |

---

# Run 2026-10-04 — a fresh clone of `c94ac50`

The two runs below were made on working trees before the repository had a commit. This one
starts from a commit. The repository was cloned into an empty directory, so no `.env`, `bin/`,
`obj/` or `TestResults/` came along, and the documented chain and the documented setup ran there.

## Environment

| | |
|---|---|
| Date | 2026-10-04, 12:22–13:00 UTC |
| Commit | `c94ac503c7246fa70b90558ef03c1fee3f055627` (`main`, equal to `origin/main` at the time of the run) |
| Checkout | `git clone --no-local` into `%TEMP%\lrl-clean`, a directory that did not exist before |
| .NET SDK | 10.0.400 (pinned in `global.json`, `rollForward: disable`) |
| PowerShell | 7.6.6 |
| Docker | 29.6.1 |
| OS | Windows 11 Pro, build 10.0.26200 |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-latest@sha256:97b4488…` |
| RabbitMQ | `rabbitmq:4.3.5-management-alpine@sha256:b3b8b7f9…` |

## What changed since the last run

The 2026-09-13 run was made on an uncommitted tree. Since then the tree has been committed
(`8260ae8`), followed by three commits. **None of them changes application code**:

| Commit | Kind | What it fixed |
|---|---|---|
| `fc3ec80` | repository hygiene | `.gitattributes` and `.editorconfig` pin line endings, so `dotnet format --verify-no-changes` gives the same answer on Linux and on Windows |
| `cd2c902` | test infrastructure | xUnit ran test *collections* in parallel, so the pure test classes competed with the `lab` collection for CPU, and the timing-sensitive lab tests (attempt budgets, leases, lost-response retry) depended on that competition. All collections now run sequentially. |
| `c94ac50` | test infrastructure | outbox rows left behind by an earlier test (a crashed process, a confirm that never came) could be claimed by the dispatcher of a later test. Tests that drive the dispatcher now clear them first, and the dead-letter test waits until the worker has declared its binding before it publishes. |

An earlier run after these commits exists as a TRX file:
a TRX file in the local `TestResults/` (not committed), 182/182, 2026-09-14 00:36 local time.
It ran on the development working tree, **not** on a fresh clone, so it is not presented as
this run.

## The acceptance chain in the fresh clone

| Command | Exit code | Result |
|---|---|---|
| `dotnet --version` | 0 | `10.0.400` |
| `dotnet tool restore` | 0 | `dotnet-ef` 10.0.12 |
| `dotnet restore --locked-mode` | 0 | the lock files were not modified |
| `dotnet build -c Release --no-restore` | 0 | 0 warnings, 0 errors |
| `dotnet test -c Release --no-build --logger "trx;LogFileName=clean-checkout.trx" --results-directory TestResults` | 0 | **182 passed, 0 failed, 0 skipped**, 3 min 7 s |
| `dotnet format --verify-no-changes --no-restore` | 0 | no changes |

`git status --short` in the clone was empty afterwards. The test project uses VSTest
(`Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`), so the TRX flag is `--logger trx`,
the same flag the CI workflow uses.

### Two earlier attempts that did not count

Both attempts are recorded because both of them failed, and neither failure came from the commit:

1. **Attempt 1: 86 passed, 96 failed.** All 96 failures were in the `lab` collection and had
   a single cause. Docker refused to start the fixture's container with `failed to bind host
   port 0.0.0.0:50076/tcp: address already in use`, so `LabFixture.InitializeAsync` threw and
   every test in the collection failed with it. Another project's containers were starting on
   the same machine at the same moment. The failure was not reproduced on any later run.
2. **Attempt 2, in the same clone: 179 passed, 3 failed**
   (`RecoveryTests.ACrashBetweenTheRequestAndItsOutboxRowLeavesNoAcceptedWorkBehind`,
   `RecoveryTests.TheSameRequestIdCanBeRetriedAfterACrashWithoutCreatingASecondExport`,
   `FailureWindowTests.TheExternalSystemStillReplaysTheSameReceiptAfterAProcessRestart`).
   These are the tests that run the apps as **child processes** from each app's own `bin`.
   The child output showed `DllNotFoundException: Microsoft.Data.SqlClient.SNI.dll …
   (0x800700CE)`: the file name was too long. The first clone sat under a directory about
   150 characters deep, and the native SNI library under
   `src\…\bin\Release\net10.0\runtimes\win-x64\native\` went past the 260-character Windows
   path limit. The same three tests passed in the development tree, and they passed in a
   clone at a short path (`%TEMP%\lrl-clean`, the run recorded above). The README now
   documents this requirement.

## The README setup path in the fresh clone

The development containers are called `lab-sql` and `lab-rabbit` (the names are fixed in
`compose.yaml`, and the scripts use `docker exec lab-sql`), so a second checkout on the same
machine cannot run its own lab next to them. To keep this run off the development
databases and broker:

- the two development containers were stopped already, and were only **renamed** for the
  duration of the run (never started, never removed, and their volumes were not touched);
- the clone ran under its own compose project (`docker compose -p lrl-clean up -d`), so its
  volumes were `lrl-clean_*`, on its own ports (SQL 21433, AMQP 25672, management 25673);
- `init-lab.ps1` writes to the per-user user-secrets store, which every checkout of this repo
  shares. The three `secrets.json` files were backed up first and restored afterwards; their
  SHA-256 hashes matched the originals.

After the run, the compose project was removed with `down -v`, the containers got their
names back, and no `lab-scn*` container was left.

| Step | Exit code | Result |
|---|---|---|
| `cp .env.example .env` (ports and passwords edited) | — | |
| `docker compose -p lrl-clean up -d --wait` | 0 | both services healthy |
| `pwsh -File scripts/init-lab.ps1` | 0 | `IntegrationLab: 6 tables`, `FakeErpLab: 1 table`, user-secrets written |
| the three `dotnet run` commands from the README | — | **failed: see below** |

### What the fresh clone exposed

The documented `dotnet run` path did not work from a fresh clone. In the development tree, the
environment had always been supplied from outside:

| # | Symptom | Cause |
|---|---|---|
| D1 | the worker exits: `Integration.Worker refuses to start in environment 'Production'` | the worker had no `launchSettings.json`, so `dotnet run` started it as Production, and the lab's environment guard refuses Production |
| D2 | the API listens on `https://localhost:53357` and `http://localhost:53358`; the README's `http://127.0.0.1:5099` is refused | the tracked `launchSettings.json` set `applicationUrl`, which overrides `Program.DefaultLoopbackUrl` |
| D3 | FakeErp listens on `localhost:53359/53360`; the worker's `ErpBaseAddress` (`http://127.0.0.1:5199`) is refused | the same, in FakeErp's `launchSettings.json` |

The fix is the smallest one that removes the second source of truth. The two web profiles no
longer set `applicationUrl` (or `launchBrowser`), and the worker gets a profile that sets
`DOTNET_ENVIRONMENT=Development`. Two tests in `ScriptTests` pin this:
`DotnetRunStartsEachAppInDevelopmentOnItsDocumentedUrl` (one case per app) and
`TheWorkerCallsFakeErpWhereFakeErpListensByDefault`. With the committed launch profiles
restored, three of the four cases fail with exactly D1–D3; with the fix, all four pass.

With the fix copied into the clone, the same three commands, started one after another:

```text
FakeErp:  Now listening on: http://127.0.0.1:5199   Hosting environment: Development
Worker:   integration-worker-host-ready            Hosting environment: Development
API:      Now listening on: http://127.0.0.1:5099   Hosting environment: Development

POST /api/exports -> 202 Location=/api/exports/7a107b99-5b4c-4682-ae73-b71fbd55b67f
requestId=7a107b99-... publish=Published/attempts=1 job=Completed/attempts=1
                       receipt=ERP-3930aa3decf04602ab64c5cb627bbe55 deadLetter=null
```

### `broker-down`, from the fresh clone

`pwsh -File scripts/run-scenario.ps1 -Scenario broker-down`, exit code 0:

```text
==> Scenario 'broker-down' (run 39806229, topology prefix 'scn39806229-')
    databases: IntegrationLab_scn39806229, FakeErpLab_scn39806229
    broker container: lab-scn39806229-rabbit (amqp 64686, management 64687)
    [requestId=a28ca3eb-...] API answered 202 with the broker DOWN
    [requestId=a28ca3eb-...] outbox=Pending (durable, waiting for the broker)
    [requestId=a28ca3eb-...] outbox=Publishing while the broker is down (never Published)
    [requestId=a28ca3eb-...] outbox=Published after the broker returned
    [requestId=a28ca3eb-...] final: outbox=Published, job=Completed/attempts=1, receipts=1, applied=1
==> Scenario 'broker-down' completed as expected.
==> Removing this run's RabbitMQ container
==> Removing this run's databases
```

The scenario passes its own processes their URLs on the command line, so D1–D3 do not
affect it.

## The suite with the fix

On the development tree at `c94ac50` with the launch-profile fix and the new tests on top:

| Command | Exit code | Result |
|---|---|---|
| `dotnet restore --locked-mode` | 0 | |
| `dotnet build -c Release --no-restore` | 0 | 0 warnings, 0 errors |
| `dotnet test -c Release --no-build` | 0 | **186 passed, 0 failed, 0 skipped** (`ScriptTests` 26 → 30), 3 min 42 s |
| `dotnet format --verify-no-changes --no-restore` | 0 | no changes |

## Remote CI

**Green on `c94ac50`.** This was read on 2026-10-04 with `gh` (2.102.0), as the repository owner:
`gh run view`, the run's jobs endpoint and `gh run view --log`.

| | |
|---|---|
| Run | [34765658412](https://github.com/hidayetcolkusu/dotnet-reliable-integration-lab/actions/runs/34765658412), workflow `ci`, attempt 1, event `push` |
| Commit | `c94ac503c7246fa70b90558ef03c1fee3f055627` (`main`), the commit cloned above |
| Time | 2026-09-13 15:27:32 → 15:32:10 UTC |
| Conclusion | **success**: `build-and-test` success, `scripts` success |
| Runner | `ubuntu-latest`, image `ubuntu-24.04`, version 20260907.300.1 |
| Versions | .NET SDK 10.0.400, Docker 28.0.4 |
| Build | 0 warnings, 0 errors |
| Tests | **182 total, 182 passed**, 2.78 min, real SQL Server and RabbitMQ from the pinned digests (both pre-pulled in the run) |
| Format | `dotnet format --verify-no-changes` passed |

The test count comes from the run's log. The TRX artifact (`test-results`, ID 10320650996)
was uploaded, but it is no longer listed: the workflow keeps it for 14 days.

The three runs before it on `main` were **red**, and they are part of the record:

| Run | Commit | Conclusion |
|---|---|---|
| 34755437613 | `8260ae8` | failure |
| 34756462241 | `fc3ec80` | failure |
| 34764078057 | `cd2c902` | failure: `DeadLetterTests.AQuarantinedMessageProducesOneDeadLetterEventWithoutItsRawBody` and `RecoveryTests.EveryDependencyFailingAtOnceStillAppliesEachRequestExactlyOnce` timed out waiting on outbox rows that stayed `Pending` |

That last failure is the one `c94ac50` fixed: earlier tests left outbox rows behind, and they
got in the way of the dispatcher. The fix is in the test infrastructure.

The launch-profile fix and the new `ScriptTests` cases are **not** covered by any CI run yet.

## What this run does not prove

- **CI covers `c94ac50` only.** No CI run exists for anything after it.
- **The launch-profile fix is not part of `c94ac50`.** The 182/182 fresh-clone run is of
  `c94ac50` exactly. The 186/186 run and the working README path include the fix as
  uncommitted changes on top of it.
- **Only one scenario ran from the fresh clone** (`broker-down`). The other four last ran on
  2026-09-13 (below).
- **One machine, one OS.** Windows 11 with PowerShell 7.6.6.
- **No performance data.**
- **The windows listed as open in [failure-windows.md](../docs/architecture/failure-windows.md)
  are still open.**

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
