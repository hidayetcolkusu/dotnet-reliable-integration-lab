> **Historical record.** This is the acceptance run of commit `686d6cc` as it was recorded on
> 2026-10-04. That commit, and the two documentation commits that followed it (`b37263e`,
> `3c966ce`), were later re-recorded with their commit-message trailers removed; the trees are
> byte-identical, so the result below applies unchanged to `fa79883`, `c436c05` and `aa0062e`
> respectively. The current state is in [verification.md](../verification.md).

# Run 2026-10-04 (2) — a fresh clone of `686d6cc`

`686d6cc` is the commit that closes the 2026-10-04 review: the quarantine race fix, the
deterministic delivery-race tests, the launch-profile fix and the wording fixes, all previously
uncommitted on top of `c94ac50`. This run is of that commit and nothing else.

## Environment

| | |
|---|---|
| Date | 2026-10-04, 13:31:48–13:35:24 UTC |
| Commit | `686d6ccc6b02f8816fc385766d0cd3941aa3715d` (`main`, local; not yet pushed at the time of the run) |
| Checkout | `git clone --no-local` into `%TEMP%\lrl-clean-686d6cc`, a directory that did not exist before; `git status --short --ignored` was empty before the run |
| .NET SDK | 10.0.400 |
| PowerShell | 7.6.6 |
| Docker | 29.6.1 |
| OS | Windows 11 Pro, build 10.0.26200 |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-latest@sha256:97b448857967be55e005424a660056fe6d51814435804dc07e8f79f028bab5fb` |
| RabbitMQ | `rabbitmq:4.3.5-management-alpine@sha256:b3b8b7f95f5382a19f9ea33540e604f30aad081d37ad9aba72255135765373a1` |

Both images come from `compose.yaml` through `LabImages` and are started by Testcontainers
(`MsSqlBuilder`, `RabbitMqBuilder`); no test substitutes an in-memory store or broker.

## The acceptance chain in the fresh clone

`pwsh -File scripts/verify-clean.ps1`, exit code **0**:

| Step | Result |
|---|---|
| `dotnet tool restore` | `dotnet-ef` 10.0.12 |
| `dotnet restore --locked-mode` | ok |
| `dotnet build -c Release --no-restore` | 0 warnings, 0 errors |
| `dotnet test -c Release` | **189 passed, 0 failed, 0 skipped** (TRX: `executed="189" passed="189" failed="0"`), 3 min 12 s |
| `dotnet format --verify-no-changes` | no changes |

`git status --short` in the clone was empty afterwards.

## What is new in the count

187 → 189: two regression tests for the inbox's concurrent-delivery races. Each one commits a
competing delivery's job and receipt from a second connection inside a `SaveChangesInterceptor`,
right before the acceptor's own insert, so the unique-key conflict happens on every run:

| Test | Asserts |
|---|---|
| `InboxTests.LosingAReceiptRaceToTheSameDeliveryIsAcceptedWithoutASecondJob` | the loser returns `Accepted` (ACKable); 1 job, 1 receipt |
| `InboxTests.LosingAJobKeyRaceToARepublishIsNotAckedUntilItsRedeliveryAddsAReceipt` | the loser returns `Failed` (not ACKed); its redelivery returns `Accepted`; 1 job, 2 receipts |

Both were checked against deliberately broken production code on the development tree, and
the second test failed each time: (1) the job-key loser returning `Accepted` (an ACK with
nothing durable behind it); (2) the race handler no longer clearing the failed inserts from
the change tracker. The production file was restored byte-for-byte after each check.

The same chain on the development tree before the commit: 189 passed, exit code 0.

## Not run in this clone

- **The interactive README path** (`init-lab.ps1`, the three `dotnet run` commands,
  `broker-down`). `init-lab.ps1` writes to the per-user user-secrets store that every checkout
  shares, and the scenarios use the development `lab-sql` container; neither was touched for
  this run. The launch profiles that path depends on are covered inside the suite by
  `ScriptTests.DotnetRunStartsEachAppInDevelopmentOnItsDocumentedUrl` and
  `ScriptTests.TheWorkerCallsFakeErpWhereFakeErpListensByDefault`. The last time the path itself
  ran end to end is the run below.

## Remote CI

**Green on `b37263e`** (this commit plus the commit that recorded this run, which changes only
this file). Read on 2026-10-04 with `gh run view` and `gh run view --log`, as the repository owner.

| | |
|---|---|
| Run | [37207068481](https://github.com/hidayetcolkusu/dotnet-reliable-integration-lab/actions/runs/37207068481), workflow `ci`, event `push` |
| Commit | `b37263e` (`main`) |
| Time | 2026-10-04, about 13:50–13:54 UTC |
| Conclusion | **success**: `scripts` success, `build-and-test` success (3 min 41 s) |
| Runner | `ubuntu-latest` |
| Build | 0 warnings |
| Tests | **Total tests: 189, Passed: 189**, 2.42 min; the job pre-pulled the same SQL Server and RabbitMQ digests listed above |
| Format | `dotnet format --verify-no-changes` passed |

The run's annotations are platform notices only (Node.js 20 actions forced onto Node.js 24;
`ubuntu-latest` moving to Ubuntu 26), not failures.
