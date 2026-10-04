# Duplicate delivery and quarantine

> Recorded on 2026-09-11, before the repository had any commit (the first one is `8260ae8`,
> 2026-09-13). The newest run pinned to a commit, from a fresh clone, is at the top of
> [verification.md](verification.md).

## Environment

| | |
|---|---|
| Date | 2026-09-11 (UTC) |
| .NET SDK | 10.0.400 (pinned, `rollForward: disable`) |
| Docker | 29.6.1 |
| OS | Windows 11 Pro, build 10.0.26200.9445 |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-latest@sha256:97b4488…` |
| RabbitMQ | `rabbitmq:4.3.5-management-alpine@sha256:b3b8b7f9…` |
| Commit | none yet — the tree is not committed at the time of this run |

## Command

```powershell
dotnet test --filter "FullyQualifiedName~InboxTests"
```

## Result

All 10 tests in `InboxTests` pass. The class covers the three duplicate shapes and every
rejection reason that is reachable through the real delivery path.

| Test | What it asserts |
|---|---|
| `ValidDeliveryBecomesOneReceiptAndOneJob` | one receipt, one job, queue drains |
| `SameTransportMessageIdTwiceCreatesNoSecondJob` | receipts stay at 1, jobs stay at 1 |
| `DifferentTransportIdSameEventIdAddsAReceiptButNoSecondJob` | receipts 2, **jobs 1** |
| `SameEventIdWithDifferentPayloadIsQuarantinedAndLeavesTheJobUntouched` | `source_hash_mismatch`, original job untouched |
| `MissingMessageIdIsQuarantinedAndAcked` | `missing_message_id`, no job |
| `MalformedBodyIsQuarantinedNotRequeuedForever` | `malformed_body`, queue drains |
| `UnknownTypeIsQuarantined` | `unknown_type` |
| `UnsupportedSchemaVersionIsQuarantined` | `unsupported_schema` |
| `EventForAnUnknownSourceRequestIsQuarantined` | `unknown_source_request` |
| `RepeatedPoisonDeliveryProducesOneQuarantineRowAndOneDeadLetterEvent` | 1 quarantine row, 1 DLQ event |

## What the numbers mean

The receipt count **growing** is correct: `InboxReceipts` is the audit trail of deliveries, and
at-least-once delivery produces more than one. The count that must not grow is `IntegrationJobs`.

```text
two deliveries of one event  ->  receipts = 2, jobs = 1, applied = 1
```

## A finding recorded honestly

`identity_payload_mismatch` exists in the code but is **unreachable through the normal path**.
The source check — "does `ExportRequests` know this event id, and does its payload hash match?"
— runs before the receipt and job identity checks, so any payload that differs from the
accepted request is caught earlier as `source_hash_mismatch`. The later branch is
defence-in-depth behind it.

The test is named and commented for what actually happens
(`SameEventIdWithDifferentPayloadIsQuarantinedAndLeavesTheJobUntouched` asserts
`source_hash_mismatch`), rather than asserting a reason code the system does not produce.

## Two test-harness defects this class exposed

Both were in the tests, not in the application, and both are worth recording because they
produced convincing-looking passes and failures for the wrong reasons:

1. **Publishing before the binding exists.** These tests publish crafted messages straight to
   the exchange with `mandatory: false`. The applications declare the topology, so publishing
   before the worker connected meant the broker **silently discarded** the message and the
   quarantine table stayed empty. The helper now waits for the binding before publishing.
2. **A leaked worker host.** One test held a `TestWorkerHost` in a bare local and disposed it
   only on the happy path. When an assertion failed, the worker kept running for the rest of the
   suite — publishing other tests' outbox rows and claiming their jobs with its own
   configuration. Ten tests failed in places unrelated to the cause. Every host is now held in
   `await using`, and `TestHosts.cs` documents why.

## Limits

- Deduplication has **no time bound**: receipts are never pruned, so "how late may a duplicate
  arrive" is answered by disk space rather than by policy.
- Nothing here reprocesses a quarantined message after a fix.
- The reachable-reason list is specific to this lab's source check; a system that accepts events
  from producers it does not own would need different rules.
