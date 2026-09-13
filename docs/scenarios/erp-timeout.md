# Scenario: the external system applied the change but the response was lost

## What is being demonstrated

This is the hardest case in the whole lab, and the one most systems get wrong. The external
call **succeeded** — the row is committed on the other side — and then the response never
arrived. The caller's timeout tells it nothing about the outcome.

There are only two honest options, and only one of them is safe:

- treat the attempt as **failed** and retry — which double-applies, unless the other side
  is idempotent;
- treat the attempt as **unknown** and retry — which is safe *because* the other side is
  idempotent.

This lab takes the second option and makes the external system earn it: `POST /erp/exports`
keys its effect on a persistent `OperationKey`, so the retry returns the **original receipt**
instead of applying anything again.

## Run it

```powershell
pwsh -File scripts/run-scenario.ps1 -Scenario erp-timeout
```

FakeErp is told to commit the effect and then hold the first response for 8 seconds; the
worker runs with a 2-second HTTP timeout.

## What you should see

```text
    [requestId=...] FakeErp will commit the effect and then lose the response
==> Starting the worker with a 2s HTTP timeout
    [requestId=...] retried 2 times, applied exactly once: outbox=Published, job=Completed/attempts=2, receipts=1, applied=1
```

`attempts=2` and `applied=1` together are the evidence. The first attempt is recorded as a
transient failure with the code `http_timeout`; the second one replays.

## Why "timeout" is never "failed"

Look at the classification in [`ErpClient`](../../src/Integration.Worker/Processing/ErpClient.cs):

- a client-side timeout → transient, `http_timeout`;
- a connection failure → transient, `http_connection`;
- `408`, `429`, `5xx` → transient;
- `200` with an unparsable body → transient, because the effect may well have happened;
- `4xx` other than `408`/`429` → terminal, because the other side made a decision.

Nothing in that list is allowed to say "the operation definitely did not happen" unless the
external system said so itself.

## Where to look in the code

| Step | Code |
|---|---|
| One attempt, no hidden retry, one timeout | [`ErpClient.ApplyAsync`](../../src/Integration.Worker/Processing/ErpClient.cs) |
| Persistent idempotency on the external side | [`ApplyExportHandler`](../../samples/FakeErp/ApplyExportHandler.cs) |
| The retry schedule lives in SQL | [`JobStore.ScheduleRetryAsync`](../../src/Integration.Shared/Persistence/JobStore.cs) |
| The delayed-response scenario knob | [`ScenarioRegistry`](../../samples/FakeErp/ScenarioRegistry.cs) |

## The same claim as a test

`ExternalIdempotencyTests.ALostResponseDoesNotApplyTheOperationTwice` asserts
`attempts >= 2`, `applied = 1`, and that the job's stored receipt equals the receipt in the
external system's own table. The neighbouring tests cover replay, payload conflict, the
`Idempotency-Key` contract and a concurrent apply storm.

## What this does not prove

Idempotency here is implemented by an external system this repo also owns. Real third-party
systems often do not offer it; when they do not, the safe options are a reconciliation query
before the retry, or accepting the duplicate and compensating. That trade-off is discussed in
[ADR 0003](../decisions/0003-external-idempotency.md) but not implemented.
