# ADR 0005 — Dead-lettering goes through the application outbox, and the envelope carries no raw payload

- **Status:** accepted
- **Date:** 2026-09-11
- **Scope:** `DeadLetterWriter`, `OutboxDispatcher`, `InboxAcceptor`, `Topology`

## Context

Two different things end up "dead" in this system, and they are not the same:

- a **job** that exhausted its attempt budget or hit a terminal external failure;
- a **delivery** that could never be processed at all (a poison message).

Both need to leave the normal flow, both need to be visible afterwards, and neither may be
silently dropped.

The broker offers a mechanism: declare `x-dead-letter-exchange` on the queue and reject with
`requeue: false`. It is a fine mechanism, but it answers a transport question ("where does a
rejected message go?") rather than a business question ("which work is dead, why, and after how
many attempts?"), and the answer is not in the database.

## Decision

**Dead-lettering is an application event written to the same outbox.** There is no
`x-dead-letter-exchange` and no `x-message-ttl` anywhere in the topology.

- A terminal job moves to `DeadLetterPending` **and** its `DeadLetter` outbox row is written in
  **one transaction**. There is no window in which a job is dead without its terminal event
  being on its way.
- A quarantined delivery writes the `RejectedMessages` row **and** its `DeadLetter` outbox row
  in one transaction, then ACKs.
- The job becomes `DeadLettered` — and the rejection's `DeadLetterPublishedAtUtc` is set — only
  after the dispatcher **confirmed** that publish. "Dead-lettered" is a claim about a message
  that exists.
- Filtered unique indexes enforce exactly one `DeadLetter` row per source request and per
  rejection.

**The envelope carries metadata, never the payload that caused the problem:**

```json
{
  "requestId": "...", "eventId": "...",
  "reason": "permanent_failure", "errorCode": "http_422",
  "attemptsStarted": 1, "failedAtUtc": "...",
  "payloadHash": "sha256...", "bodyLength": 412,
  "data": { "requestId": "...", "externalReference": "PO-100", "amount": 160.00, "currency": "TRY" }
}
```

For a **job** failure, `data` is the synthetic business summary the system itself accepted and
validated. For a **quarantined delivery**, `data` is `null` and only the hash and length
travel — because that body is untrusted input of unknown content and size.

Exception text never appears. Everything that leaves the process goes through `SafeErrors`,
which maps failures to a short, fixed vocabulary.

## Consequences

**Accepted:**

- Republish and ACK reliability stay in **one** mechanism (the outbox) instead of two.
- "This job is dead" and "a terminal event exists" cannot disagree.
- A poison message is recorded durably with a reason code, so an operator can count and group
  failures with SQL instead of by consuming a queue.
- Copying an untrusted body into a database row, a log line and a broker message is avoided, so
  a hostile or oversized payload has one fewer place to do damage.

**Costs:**

- A DLQ consumer that wants the original bytes **cannot have them.** Diagnosing a poison message
  means reproducing it from the hash and the producer's own logs. This is a deliberate trade of
  convenience for blast radius, and it is the most debatable decision in this repo.
- A crash between the DLQ confirm and the state update republishes the dead-letter message, so
  DLQ consumers must tolerate duplicates.
- The terminal path costs an extra publish and an extra row compared to a broker-side DLX.

## Alternatives rejected

| Alternative | Why not |
|---|---|
| `x-dead-letter-exchange` + `BasicReject(requeue: false)` | the *reason* and the attempt history stay out of the database |
| `BasicNack(requeue: true)` | the poison message loops forever and the queue never drains |
| `BasicReject(requeue: false)` with no DLX | the message is dropped and the evidence is gone |
| Mark the job `DeadLettered` and publish afterwards | claims a message exists before the broker confirmed it |
| Put the raw body in the DLQ message | multiplies the places untrusted input must be handled safely |
| Log the exception text into the envelope | leaks connection strings, paths and internals to whoever reads the DLQ |

## Evidence

- `DeadLetterTests.ATerminalJobPublishesExactlyOneDeadLetterEventAndOnlyThenIsDeadLettered`
- `DeadLetterTests.ACrashBetweenDeadLetterConfirmAndUpdateStillClosesTheTerminalState`
- `DeadLetterTests.AQuarantinedMessageProducesOneDeadLetterEventWithoutItsRawBody` — embeds a
  marker in the poison body and asserts it does **not** reach the dead-letter queue
- `DeadLetterTests.ABrokerOutageDelaysTheDeadLetterEventButNeverLosesIt`
- `InboxTests.RepeatedPoisonDeliveryProducesOneQuarantineRowAndOneDeadLetterEvent`
