# ADR 0003 — The external effect is keyed by a persistent operation key

- **Status:** accepted
- **Date:** 2026-09-11
- **Scope:** `FakeErp`, `ErpClient`

## Context

A retry is only safe if repeating the external call is safe. The dangerous case is not a clean
failure — it is an **unknown outcome**: the call was made, the effect may or may not have
happened, and the response never arrived.

A client timeout, a connection reset mid-response, a `200` with an unreadable body and a
process crash between the call and the database write all produce the same state: *we do not
know*. Treating "unknown" as "failed" and retrying is what double-charges a customer.

## Decision

The external system keys its effect on a **persistent operation key**, and the caller never
retries inside a single attempt.

On the external side (`FakeErp`):

- `OperationKey` = the caller's `requestId`, with a unique index on it;
- the `Idempotency-Key` header **must equal** the body's `requestId`, otherwise `400` — an
  ambiguous request is refused rather than applied under one of the two keys;
- same key + same payload hash → the **original receipt**, forever, with `replayed: true`;
- same key + different payload → `409 payload_conflict`, and the original effect is untouched;
- a concurrent apply that loses the unique-index race reads the winner's row and replays it.

On the calling side (`ErpClient`):

- exactly **one** HTTP attempt per job attempt, one timeout, no hidden retry, no Polly;
- the response is read with a hard byte cap;
- every outcome is reduced to a safe `ErpResult`, and nothing may claim "definitely did not
  happen" unless the external system said so:

| Outcome | Classification | Reasoning |
|---|---|---|
| `408`, `429`, `5xx` | transient | the server said "later" |
| client timeout | transient | the effect is **unknown** |
| connection failure | transient | unknown |
| `200` with no parsable receipt | transient | unknown, and replay is safe |
| other `4xx` | terminal | the server made a decision |

## Consequences

**Accepted:**

- A retry after a lost response returns the original receipt instead of applying twice.
- The job's stored `ExternalReceiptId` always equals the receipt in the external system's own
  table, which is what makes reconciliation trivial.
- Because the HTTP client never retries on its own, `AttemptsStarted` is the whole truth about
  how many times the other system was called.

**Costs:**

- The external system must store every operation key forever, or at least longer than the
  longest possible retry window. This lab never prunes `AppliedExports`.
- A payload conflict is a hard error that a human has to resolve; the system deliberately does
  not pick a winner.
- The "unknown → retry" rule means duplicate *calls* are normal. Everything downstream of the
  external system has to tolerate that, which is a constraint on the other side, not just ours.

## Alternatives rejected

| Alternative | Why not |
|---|---|
| Treat a timeout as failure and retry blindly | double-applies whenever the effect did commit |
| Treat a timeout as success | loses work whenever it did not |
| Retry inside `ErpClient` (Polly) | the job row would undercount real calls, so the budget would not bound them |
| Idempotency by natural key (`externalReference`) | a legitimate second export of the same reference becomes impossible |
| In-memory idempotency cache on the ERP | evaporates on restart, which is exactly when a retry arrives |
| A `GET` reconciliation query before every retry | doubles the call volume, and only works if the external system exposes one — see below |

## When the external system is not idempotent

This lab owns both sides, which is a luxury. When a real third party offers no idempotency key:

1. **Reconcile before retrying** — query the external system for the operation, and only retry
   if it is absent. This needs a queryable identifier and is racy by construction, but it
   narrows the window a lot.
2. **Accept the duplicate and compensate** — apply twice, detect it later, issue a reversal.
   Only viable where a compensating action exists.
3. **Refuse to retry and escalate** — mark the job as "outcome unknown" and require a human.
   Correct but expensive, and it turns a transient blip into an incident.

None of these is implemented here. Which one is right is a property of the other system's
contract, not of this code.

## Evidence

- `ExternalIdempotencyTests.ALostResponseDoesNotApplyTheOperationTwice`
- `ExternalIdempotencyTests.TheSameOperationKeyAlwaysReturnsTheSameReceipt`
- `ExternalIdempotencyTests.TheSameOperationKeyWithADifferentPayloadIsAConflict` — this test
  found a real defect: the replay path compared the stored row's hash **with itself**, so the
  `409` branch was unreachable and a changed payload was silently replayed as success.
- `ExternalIdempotencyTests.ConcurrentIdenticalAppliesProduceOneRowAndOneReceipt`
- `ExternalIdempotencyTests.TheIdempotencyKeyMustAgreeWithTheBody`
