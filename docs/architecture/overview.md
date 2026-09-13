# Architecture overview

## The problem this repository is about

One business request has to reach an external system exactly once, across a database, a
message broker, an HTTP hop and an arbitrary number of process restarts. Every one of those
boundaries can fail independently, and none of them can be made transactional with the others.

The lab is deliberately small — a single synthetic request type — so that all the surface area
is spent on the failure handling rather than on the domain.

## The three components

```text
                POST /api/exports
                        |
                        v
        +-------------------------------+
        |        Integration.Api        |   ONE SQL transaction:
        |                               |   ExportRequests + OutboxMessages
        |   no broker connection at all  |
        +---------------+---------------+
                        |
                    (SQL only)
                        |
        +---------------v---------------------------------------+
        |                    SQL Server                          |
        |  ExportRequests   OutboxMessages   InboxReceipts       |
        |  IntegrationJobs  JobAttempts      RejectedMessages    |
        +---+-------------------------------------------+--------+
            |                                          ^
   claim + publish                              receipt + job
   (confirm + mandatory)                        (one transaction)
            |                                          |
            v                                          |
        +---+------------------+            +-----------+----------+
        |  OutboxDispatcher    |  RabbitMQ  |     InboxConsumer    |
        |  (Integration.Worker)+----------->+  (Integration.Worker)|
        +----------------------+  exchange  +-----------+----------+
                                  + queue               |
                                                        v
                                            +-----------+----------+
                                            |     JobProcessor     |
                                            |  claim -> HTTP -> SQL|
                                            +-----------+----------+
                                                        |
                                              Idempotency-Key
                                                        v
                                            +----------------------+
                                            |  FakeErp (external)  |
                                            |  AppliedExports      |
                                            |  keyed by OperationKey|
                                            +----------------------+
```

The worker hosts three independent loops in one process: `OutboxDispatcher`, `InboxConsumer`
and `JobProcessor`. They share nothing but SQL, which is the point — each one can be killed
without the others losing state.

## The one diagram that matters: three different meanings of "done"

The single most common source of data loss in this kind of system is treating these three as
the same event. They are not, and the code marks them in three different places.

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

- A **confirm** says the broker took responsibility for the message. It says nothing about
  any consumer having seen it, so `Published` is a statement about the transport only.
- An **ACK** says the consumer wrote a receipt and a job row and committed them. It says
  nothing about the business work being done. This is why the ACK happens *after* the SQL
  commit and never before.
- **External completion** says the other system applied the effect and gave us a receipt id.
  Only this one is the business outcome.

Collapsing 2 into 3 — ACKing after "processing succeeded" — is what makes a consumer lose
work whenever the external call is slow, the process restarts, or the queue is redelivered.

## Why the retry lives in SQL, not in the broker

RabbitMQ can be made to retry with a TTL queue plus a dead-letter exchange: publish to a
delay queue, let the message expire, have the DLX route it back. This lab deliberately does
**not** do that, and the topology has no `x-message-ttl`, no `x-dead-letter-exchange` and no
`x-max-length`.

| | Broker TTL/DLX retry | SQL retry (this lab) |
|---|---|---|
| Where the attempt count lives | a header on a message | a column, `AttemptsStarted` |
| Visible to an operator | only by consuming the message | `SELECT` |
| Survives a queue purge | no | yes |
| Survives a broker rebuild | no | yes |
| Can be queried ("what is stuck and why") | no | yes |
| Schedule precision | one queue per delay tier | any `DATEADD` |
| Couples business retry to transport | yes | no |

The decisive argument is the last two rows. A retry policy is a *business* decision about how
many times it is acceptable to call another company's system; encoding it in transport
metadata makes it invisible exactly when someone is trying to explain, at 3am, why a payment
was attempted five times. See [ADR 0002](../decisions/0002-sql-retry.md).

The two retry budgets are also kept separate on purpose: a broker outage consumes
`OutboxMessages.PublishAttempts`, never the five external attempts in `IntegrationJobs`.

## What each table is for

| Table | Purpose | The invariant it enforces |
|---|---|---|
| `ExportRequests` | the accepted business request | `RequestId` is the primary key, so a duplicate submit cannot create a second request |
| `OutboxMessages` | messages waiting to be published | one `Export` row per request, one `DeadLetter` row per request or rejection (filtered unique indexes) |
| `InboxReceipts` | proof one delivery was accepted | `(ConsumerName, TransportMessageId)` primary key absorbs redeliveries |
| `IntegrationJobs` | the unit of work and its retry state | keyed by the event id, so N deliveries create at most one job |
| `JobAttempts` | one row per **started** attempt | an audit trail that a crash cannot rewrite |
| `RejectedMessages` | quarantined deliveries | a fingerprint makes repeat poison messages collapse |

These are enforced by the schema, not only by the handlers — `SchemaTests` inserts invalid
rows with raw SQL to prove it.

## What is deliberately not here

- **No high availability.** One broker node, one SQL instance. This lab is about application
  behaviour under failure, not about clustering.
- **No quorum queues.** Their delivery-limit would drop a message during a long SQL outage,
  which is exactly the case where the inbox table — not the broker — is supposed to carry the
  work after the ACK.
- **No saga or compensation.** There is one external effect, and it is idempotent. Multi-step
  workflows with rollback are a different problem.
- **No retention or archival.** Receipts, attempts and quarantine rows grow forever. A real
  deployment needs a policy, and that policy decides how late a duplicate may still arrive.
- **No authentication or authorization.** Every endpoint is loopback-only and unauthenticated.

## Where to go next

- [State machines](state-machines.md) — every state and transition of the three durable rows.
- [Failure windows](failure-windows.md) — every named window, the test that produces it, and the windows that are still open.
- [Decisions](../decisions) — six ADRs with the alternatives that were rejected.
- [Code walkthrough](../learning/code-walkthrough.md) — a guided, runnable tour (in Turkish).
