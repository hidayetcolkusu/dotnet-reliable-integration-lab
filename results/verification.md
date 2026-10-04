# Verification

The current verified state of the repository. Earlier acceptance runs, with the defects each
one exposed, are kept in [history/](history/README.md).

## Current state

| | |
|---|---|
| Verified commit | `0b02c5d7e4336bbb91248826453ff4c88693c2e7` (`main`) |
| Tests | **189 passed, 0 failed, 0 skipped**, locally and in CI |
| Build | Release, 0 warnings, 0 errors |
| Format | `dotnet format --verify-no-changes`: no changes |
| CI | **green**: [run 37212031299](https://github.com/hidayetcolkusu/dotnet-reliable-integration-lab/actions/runs/37212031299), `ubuntu-latest` (image `ubuntu-24.04`), 2026-10-04 15:10–15:14 UTC; `build-and-test` and `scripts` both succeeded |
| Local | a fresh `git clone --no-local` into an empty directory; Windows 11 Pro (build 10.0.26200), PowerShell 7.6.6, Docker 29.6.1; 2026-10-04 15:06–15:10 UTC |
| .NET SDK | 10.0.400, pinned in `global.json` with `rollForward: disable` |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-latest@sha256:97b448857967be55e005424a660056fe6d51814435804dc07e8f79f028bab5fb` |
| RabbitMQ | `rabbitmq:4.3.5-management-alpine@sha256:b3b8b7f95f5382a19f9ea33540e604f30aad081d37ad9aba72255135765373a1` |

Later commits on `main` change only this record. CI runs on every push to `main`; the badge in
the [README](../README.md) shows the latest result.

## Command

```powershell
pwsh -File scripts/verify-clean.ps1
```

| Step | Result |
|---|---|
| `dotnet tool restore` | `dotnet-ef` 10.0.12 |
| `dotnet restore --locked-mode` | ok, lock files unchanged |
| `dotnet build -c Release --no-restore` | 0 warnings, 0 errors |
| `dotnet test -c Release --no-build` | **189 passed, 0 failed, 0 skipped** (TRX `executed="189" passed="189" failed="0"`), 3 min 34 s |
| `dotnet format --verify-no-changes --no-restore` | no changes |

`git status --short --ignored` was empty before the run and `git status --short` was empty
after it. The CI workflow runs the same steps (`.github/workflows/ci.yml`) and additionally
parses every PowerShell script.

## What the suite runs against

Every test that needs a database or a broker gets **real** SQL Server and RabbitMQ containers,
started by Testcontainers from the digests above (pinned in `config/lab-images.json`). There
is no in-memory broker and no mocked database. Crash and restart tests run the applications as
child processes and kill them.

| Test class | Tests | Covers |
|---|---|---|
| `ApiTests` | 17 | acceptance, atomicity of request + outbox row, `problem+json`, SQL unreachable, the `decimal(18,2)` bound |
| `CompositionTests` | 33 | each app's DI graph under Development validation, the loopback boundary, Testing-only fault hooks |
| `ContractBoundaryTests` | 21 | transport-identity, trace-metadata and amount limits (no dependencies) |
| `DeadLetterTests` | 4 | terminal state, confirmed DLQ publish, no raw payload in the dead letter |
| `ErpAttemptBudgetTests` | 6 | one deadline for the whole HTTP attempt, body included, on a real loopback socket |
| `ExternalIdempotencyTests` | 9 | replay, payload conflict, concurrency, the lost response, the external amount bound |
| `FailureWindowTests` | 9 | the named windows between a commit and the act that follows it |
| `InboxTests` | 17 | duplicates, quarantine reasons, ACK after commit, deterministic concurrent-delivery races |
| `JobRetryTests` | 19 | retry classification, schedule, attempt budget, restart, crash, idle claim |
| `OutboxTests` | 8 | confirm, `basic.return`, nack, confirm timeout, leases, crash after confirm |
| `RecoveryTests` | 3 | every dependency failing at once |
| `SchemaTests` | 9 | invariants against raw SQL, environment guard, no migration on startup |
| `ScriptTests` | 30 | scripts, `.env` parsing, pinned digests, demo isolation, documented `dotnet run` URLs |
| `TracingTests` | 4 | one trace across API, outbox, inbox and the external call; degraded trace metadata |
| **Total** | **189** | |

## Limits of this verification

- **Two platforms, one run each.** Windows 11 locally and Ubuntu 24.04 in CI. macOS and other
  Linux distributions have not been tried.
- **The scenario scripts are not part of this run.** `run-scenario.ps1` drives a developer's
  local Docker and `.env`, so CI only parses it. `broker-down` last ran from a fresh clone on
  2026-10-04, and all five scenarios last ran on isolated resources on 2026-09-13; see
  [history/](history/README.md).
- **No performance data.** Nothing here measures throughput, latency or loss rates.
- **Timing-sensitive tests exist.** Lease expiry, attempt budgets and lost-response retries
  depend on wall-clock deadlines. They run with generous tolerances and test collections run
  sequentially, but a heavily loaded machine can still slow them.
- **Open windows stay open.** The windows listed as open in
  [failure-windows.md](../docs/architecture/failure-windows.md) (for example a SQL Server
  failover with a transaction in flight) are not closed by any test.
