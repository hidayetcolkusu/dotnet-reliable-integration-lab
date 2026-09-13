# dotnet-reliable-integration-lab

> **Türkçe özet.** Bu repo, bir iş kaydının bir dış sisteme ulaştırılmasının neden zor olduğunu
> ve .NET ile nasıl **ele alındığını** çalıştırılabilir biçimde gösterir. Veritabanı, mesaj
> kuyruğu ve HTTP sınırlarının her biri ayrı ayrı bozulabilir; hiçbiri diğeriyle aynı transaction
> içine alınamaz.
>
> Buradaki güvence uçtan uca "exactly-once" **değildir**; öyle bir şey bu mimaride yoktur.
> Taşıma katmanı **en az bir kez** teslim eder: kopyalar beklenen ve normal durumdur. Kopyaların
> **tek bir işe** indirgenmesini kalıcı inbox, **tek bir dış etkiye** indirgenmesini ise dış
> sistemin operation key üzerinden kalıcı idempotency'si sağlar — yani "bir kez uygulanır"
> ifadesi, karşı tarafın bu sözleşmeyi tutmasına bağlı bir sonuçtur, kendiliğinden bir garanti
> değil. Bütçesi biten iş sessizce kaybolmaz, dead-letter'a düşer. Neyin kanıtlandığı ve neyin
> kanıtlanmadığı için [Limits](#limits) ve
> [failure windows](docs/architecture/failure-windows.md) bölümlerine bakın.
>
> Outbox, kalıcı inbox, SQL tabanlı retry, dış sistem idempotency'si ve dead-letter akışı gerçek
> container'larla test edilir. Kodun mantığını adım adım takip etmek için
> [öğrenme rehberi](docs/learning/code-walkthrough.md) Türkçedir.

---

## The problem

A request arrives. It has to end up applied in another system — once. Between the two there is
a database, a message broker, an HTTP call and any number of process restarts.

The usual first implementation is a **dual write**:

```csharp
await _db.SaveChangesAsync();            // 1
await _channel.BasicPublishAsync(...);   // 2  <-- what if this fails?
```

There is no ordering of those two lines that is correct, and no distributed transaction
available to make them atomic. Every reliability mechanism in this repository exists because
of that one gap.

This lab is deliberately small — one synthetic request type — so that all of its surface area
is spent on failure handling rather than on a domain model.

## What it demonstrates

| Claim | Where it is proven |
|---|---|
| Work is accepted while the broker is down | `ApiTests.SubmitAcceptsWithBrokerDownAndWorkWaitsInSql` |
| `Published` is only written after a confirmed **and routed** publish | `OutboxTests` |
| A crash after the confirm republishes the same message id | `OutboxTests.CrashAfterConfirmRepublishesTheSameTransportMessageId` |
| A duplicate delivery never creates a second job | `InboxTests` |
| A poison message is quarantined, then ACKed — never dropped, never looped | `InboxTests`, `DeadLetterTests` |
| The retry budget survives a hard kill | `JobRetryTests`, `RecoveryTests` |
| A lost response does not apply the operation twice | `ExternalIdempotencyTests` |
| The external system replays the same receipt across a process restart | `FailureWindowTests.TheExternalSystemStillReplaysTheSameReceiptAfterAProcessRestart` |
| A late result from an expired lease owner overwrites nothing | `FailureWindowTests.ALateResultFromAnExpiredOwnerCannotOverwriteTheCurrentOwnersOutcome` |
| One external attempt has one deadline, body included | `ErpAttemptBudgetTests` |
| A value the database cannot store is quarantined, not retried forever | `InboxTests`, `TracingTests`, `ContractBoundaryTests` |
| The API and FakeErp refuse to bind anything but loopback | `CompositionTests` |
| One request is one trace, across process boundaries | `TracingTests` |

## Architecture

```text
                POST /api/exports
                        |
                        v
        +-------------------------------+
        |        Integration.Api        |   ONE SQL transaction:
        |   (no broker connection)      |   ExportRequests + OutboxMessages
        +---------------+---------------+
                        |
        +---------------v---------------------------------------+
        |                    SQL Server                          |
        |  ExportRequests   OutboxMessages   InboxReceipts       |
        |  IntegrationJobs  JobAttempts      RejectedMessages    |
        +---+-------------------------------------------+--------+
            |                                          ^
   claim + publish                              receipt + job
   (mandatory + confirm)                        (one transaction, then ACK)
            v                                          |
        +---+------------------+   RabbitMQ  +---------+----------+
        |  OutboxDispatcher    |------------>|    InboxConsumer   |
        +----------------------+             +---------+----------+
                                                       |
                                                       v
                                             +---------+----------+      +------------------+
                                             |    JobProcessor    |----->| FakeErp (external)|
                                             | claim -> HTTP -> SQL|     | Idempotency-Key   |
                                             +--------------------+      +------------------+
```

### Three different meanings of "done"

This is the distinction the whole repository is built around. Collapsing any two of these is
how systems lose work:

```text
 1. publisher confirm            2. inbox ACK                  3. external completion
 "the broker has it"             "we have durably              "the other system
                                  accepted it"                   applied it"
        |                               |                               |
        v                               v                               v
 OutboxMessages.Status          InboxReceipts row +            IntegrationJobs.Status
      = Published               IntegrationJobs row             = Completed
                                                                + ExternalReceiptId
```

An ACK is sent **after** the inbox transaction commits, and it means "durably accepted", not
"business done". A confirm means the broker took the message, not that anyone consumed it.

### The retry is in SQL, not in RabbitMQ

RabbitMQ can retry with a TTL queue plus a dead-letter exchange. This lab deliberately does
not: the topology has no `x-message-ttl`, no `x-dead-letter-exchange` and no `x-max-length`.
The attempt count, the next attempt time and the last error code are **columns**, so an
operator can answer "what is stuck and why" with a `SELECT`, and a queue purge or a broker
rebuild cannot erase a business retry policy. See
[ADR 0002](docs/decisions/0002-sql-retry.md).

## Requirements

- .NET SDK **10.0.400** exactly (pinned in `global.json` with `rollForward: disable`)
- Docker, with a working daemon — the tests start their own SQL Server and RabbitMQ
- PowerShell **7+** (`pwsh`) for the scripts — Windows PowerShell 5.1 is not supported
- Verified on Windows 11 with PowerShell 7.6.6 and Docker 29.6.1. No other platform has been tried.

## Setup

```powershell
cp .env.example .env        # then edit the passwords
dotnet tool restore
dotnet restore --locked-mode
docker compose up -d
pwsh -File scripts/init-lab.ps1
```

> **Do not put `$` in a `.env` value.** docker compose interpolates it, so the container would
> receive a different password than the file records, and the only symptom is a login failure.

The scripts read `.env` literally and quote each connection-string value, so `=`, `;` and
quotes in a password are safe. Verification queries run through `sqlcmd` inside the SQL
container rather than through a .NET driver loaded into PowerShell.

`init-lab.ps1` creates and migrates both databases and stores the connection strings as
per-project user-secrets. **Normal application startup never migrates** — that is a rule the
tests enforce (`SchemaTests.NormalStartupNeverMigrates`), so the only way a schema changes is
this script or an explicit `--initialize-db`.

Then start the three apps, each in its own terminal so their process lifetime is visible:

```powershell
dotnet run --project samples/FakeErp/FakeErp.csproj
dotnet run --project src/Integration.Worker/Integration.Worker.csproj
dotnet run --project src/Integration.Api/Integration.Api.csproj
```

Optional trace viewer (Jaeger on <http://127.0.0.1:16686>):

```powershell
docker compose --profile tracing up -d
# then start the apps with --Lab:OtlpEndpoint=http://127.0.0.1:4317
```

## Try it

```bash
curl -i -X POST http://127.0.0.1:5099/api/exports \
  -H 'Content-Type: application/json' \
  -d '{"requestId":"11111111-1111-1111-1111-111111111111",
       "externalReference":"PO-100",
       "amount":160.00,
       "currency":"TRY"}'
```

```http
HTTP/1.1 202 Accepted
Location: /api/exports/11111111-1111-1111-1111-111111111111

{"requestId":"11111111-1111-1111-1111-111111111111",
 "statusUrl":"/api/exports/11111111-1111-1111-1111-111111111111"}
```

```bash
curl http://127.0.0.1:5099/api/exports/11111111-1111-1111-1111-111111111111
```

```json
{
  "requestId": "11111111-1111-1111-1111-111111111111",
  "externalReference": "PO-100",
  "amount": 160.00,
  "currency": "TRY",
  "createdAtUtc": "2026-09-11T08:12:04.1234567+00:00",
  "payloadHash": "9b74c9897bac770ffc029102a200c5de...",
  "publish": { "status": "Published", "publishAttempts": 1, "publishedAtUtc": "...", "lastErrorCode": null },
  "job": { "status": "Completed", "attemptsStarted": 1, "nextAttemptAtUtc": null,
           "completedAtUtc": "...", "externalReceiptId": "ERP-9f2c...", "lastErrorCode": null },
  "deadLetter": null
}
```

The status is split on purpose: `publish`, `job` and `deadLetter` are **different facts**.
`publish.status = "Published"` never means the work is done, and a missing inbox record shows
as `job: null` rather than as success.

Stop RabbitMQ first (`docker compose stop rabbit`) and the `POST` still answers `202` — the
work simply waits in SQL.

## Failure scenarios

Each scenario runs end to end and prints its evidence keyed by `requestId`:

```powershell
pwsh -File scripts/run-scenario.ps1 -Scenario broker-down
pwsh -File scripts/run-scenario.ps1 -Scenario duplicate-delivery
pwsh -File scripts/run-scenario.ps1 -Scenario erp-timeout
pwsh -File scripts/run-scenario.ps1 -Scenario poison-message
pwsh -File scripts/run-scenario.ps1 -Scenario worker-restart
```

Every run is **self-contained**: it creates its own pair of databases
(`IntegrationLab_scnXXXXXXXX` / `FakeErpLab_scnXXXXXXXX`) inside the shared SQL container, its
own RabbitMQ container on free loopback ports, and its own API, worker and FakeErp processes —
then removes exactly those and nothing else.

That matters because a demo does not only *read*: it claims outbox rows and jobs, and a claim
takes every due row in whatever database it points at. Sharing your development database would
mean the demo publishing your pending messages and applying your pending work; sharing the
compose broker would mean `broker-down` stopping the broker for everything else on the machine.
Every helper that can delete checks the run-scoped name first, so a run can only ever remove
what it made (`ScriptTests.TheCleanupHelpersRefuseAnythingThisRunDidNotCreate`).

Your `lab-sql` and `lab-rabbit` containers are used as *servers* and are never stopped,
emptied or reconfigured.

| Scenario | What fails | What must still hold | Notes |
|---|---|---|---|
| [broker-down](docs/scenarios/broker-down.md) | RabbitMQ is stopped | work is accepted and published later | nothing is marked `Published` during the outage |
| [duplicate-delivery](docs/scenarios/duplicate-delivery.md) | the same event arrives twice | two receipts, **one** job, one effect | the receipt key absorbs it |
| [erp-timeout](docs/scenarios/erp-timeout.md) | the effect commits, the response is lost | retried, applied **once** | timeout is "unknown", never "failed" |
| [poison-message](docs/scenarios/poison-message.md) | an unprocessable delivery | quarantined + dead-lettered, then ACKed | never dropped, never requeued forever |
| [worker-restart](docs/scenarios/worker-restart.md) | the worker is killed mid-flight | the attempt budget continues, one effect | leases recover the row |

## Failure matrix

| Failure | Detected by | Recorded as | Recovery | Cost |
|---|---|---|---|---|
| broker unreachable | connection/publish failure | `broker_unavailable` | outbox retry backoff | latency |
| unroutable message | `basic.return` | `broker_unroutable` | stays `Pending` | none |
| broker nack | `basic.nack` | `broker_nack` | stays `Pending` | none |
| no confirm at all | publisher timeout | `broker_confirm_timeout` | republish, same message id | duplicate delivery |
| crash after confirm | lease expiry | row stays `Publishing` | republish | duplicate delivery |
| crash after inbox commit | redelivery | receipt already exists | ACK on redelivery | duplicate delivery |
| invalid message | validation | `RejectedMessages.ReasonCode` | quarantine + DLQ | none |
| external 5xx/408/429 | status code | `http_503`, … | job retry (budget of 5) | latency |
| external timeout | client timeout | `http_timeout` | job retry | duplicate external call |
| external 4xx | status code | `http_422`, … | terminal, dead-lettered | none |
| budget exhausted | attempt counter | `attempt_budget_exhausted` | terminal, dead-lettered | manual intervention |
| worker killed mid-attempt | lease expiry | attempt already counted | reclaim | one spent attempt |

Full detail: [failure windows](docs/architecture/failure-windows.md).

## Tests

The suite starts **real** SQL Server and RabbitMQ containers through Testcontainers, at digests
pinned in `config/lab-images.json`. There is no in-memory broker and no mocked database.

```powershell
dotnet test                                          # everything
dotnet test --filter "FullyQualifiedName~OutboxTests"  # one class
pwsh -File scripts/verify-clean.ps1                  # the full acceptance chain
```

The runner is **VSTest** (`Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`), so
`--filter` uses VSTest syntax (`FullyQualifiedName~`, `ClassName=`), not the
Microsoft.Testing.Platform `--filter-*` options.

`verify-clean.ps1` runs exact tool restore → locked package restore → Release build → the whole
suite → `dotnet format --verify-no-changes`, and **fails when a filter matches zero tests**: a
run that executed nothing is not a pass.

| Test class | What it covers |
|---|---|
| `ApiTests` | acceptance, atomicity, `problem+json` error contract |
| `SchemaTests` | the invariants hold against raw SQL, not only handlers |
| `OutboxTests` | confirm, return, nack, confirm-timeout, leases, crash recovery |
| `InboxTests` | duplicates, quarantine reasons, ACK-after-commit |
| `JobRetryTests` | retry classification, schedule, attempt budget, restart |
| `ExternalIdempotencyTests` | replay, conflict, concurrency, the lost response |
| `DeadLetterTests` | terminal state, confirmed DLQ publish, payload safety |
| `TracingTests` | one trace across API, outbox, inbox and the external call |
| `RecoveryTests` | all of the above failing at once |
| `ScriptTests` | the setup scripts, pinned digests, documented commands |
| `CompositionTests` | each application's DI graph under Development validation |

## Documentation

- [Architecture overview](docs/architecture/overview.md)
- [State machines](docs/architecture/state-machines.md) — every state and guarded transition
- [Failure windows](docs/architecture/failure-windows.md) — every named window, the test that
  produces it, and the windows that are still open
- [Decisions](docs/decisions) — six ADRs, each with the alternatives that were rejected
- [Öğrenme rehberi](docs/learning/code-walkthrough.md) — seven runnable sessions, in Turkish
- [Results](results) — real command output from real runs

## Limits

These are limits of the lab, not open bugs:

- **Delivery is at-least-once; only the *effect* is once.** Duplicates are normal on the wire.
  They collapse to one job because the inbox is durable, and to one external effect because the
  external system keys on the operation id — so "applied once" is a consequence of that
  contract, not an end-to-end exactly-once guarantee. A third party without it loses the
  property; see [ADR 0003](docs/decisions/0003-external-idempotency.md).
- **Unconditional losslessness is not claimed either.** Work that exhausts its budget goes to
  the dead-letter queue rather than disappearing, but it is not applied. The windows that remain
  open are listed in [failure windows](docs/architecture/failure-windows.md).
- **No high availability.** One broker node, one SQL instance.
- **No retention.** Receipts, attempts and quarantine rows grow forever, so deduplication has
  no time bound.
- **No ordering guarantee.** Retries and concurrent dispatchers reorder freely.
- **The external system's idempotency is ours to implement.** A real third party may not offer
  it; the alternatives are discussed in
  [ADR 0003](docs/decisions/0003-external-idempotency.md) but not implemented here.
- **No authentication.** Every endpoint is unauthenticated, which is why the API and FakeErp
  *refuse to start* on anything but a loopback address — `--urls`, `ASPNETCORE_URLS`,
  `ASPNETCORE_HTTP_PORTS` and `Kestrel:Endpoints` are all checked before the server binds
  ([`LoopbackGuard`](src/Integration.Shared/Runtime/LoopbackGuard.cs)). That is a boundary, not
  a substitute for authentication.
- **Fault injection exists only in `Testing`.** In `Development` the hooks are `NoOp`, so a
  stray configuration value cannot crash or stall a normal run.
- **No reprocessing of quarantined messages.**
- **CI has not been run remotely.** The workflow in `.github/workflows/ci.yml` is committed;
  no green remote run is claimed until one exists.
- **One machine, one OS.** Everything here was verified on Windows 11 with PowerShell 7.6.6 and
  Docker 29.6.1. The scripts use no Windows-only API, but no other platform has been tried.

## Licence

No licence has been chosen yet, so default copyright applies.
