# ADR 0001 — Transactional outbox and durable inbox instead of a dual write

- **Status:** accepted
- **Date:** 2026-09-11
- **Scope:** `Integration.Api`, `Integration.Worker`

## Context

The API must accept a request and cause a message to be published. The obvious implementation
is two statements:

```csharp
await _db.SaveChangesAsync();               // 1
await _channel.BasicPublishAsync(...);      // 2
```

This is a **dual write**, and it has no correct failure behaviour:

- if 2 fails, the request exists and no one will ever act on it;
- if 2 succeeds and the process dies before the response, the caller retries and may create a
  second message;
- if the order is reversed, a published message can reference a request that was rolled back.

There is no ordering of two independent systems that fixes this, and a distributed transaction
across SQL Server and RabbitMQ is not available (and would not be desirable: it makes the
broker's availability a precondition for accepting work).

On the consuming side the mirror problem exists. If a consumer ACKs and *then* writes to the
database, a crash in between loses the work permanently — the broker considers it delivered.

## Decision

**Publish side.** The API writes the business row and an `OutboxMessages` row in **one SQL
transaction** and never opens a broker connection. A separate loop (`OutboxDispatcher`) claims
outbox rows, publishes them with `mandatory: true` and publisher confirms, and only then marks
them `Published`.

**Consume side.** `InboxAcceptor` turns a delivery into either (`InboxReceipts` row +
`IntegrationJobs` row) or (`RejectedMessages` row + dead-letter outbox row) in **one
transaction**, and the consumer ACKs only after that transaction commits.

The transport identity of a message is the outbox row id, so a republish after a lost confirm
carries the same `MessageId` and the receipt primary key absorbs it.

## Consequences

**Accepted:**

- Acceptance no longer depends on the broker. `POST /api/exports` answers `202` with RabbitMQ
  stopped, and the work waits in SQL.
- Every message that exists in the broker has a row that says so, and every row that should
  become a message will, eventually.
- Duplicates become the normal failure mode, and they are handled in one place.

**Costs:**

- Publishing is now **asynchronous and polled**. There is a latency floor of one poll interval
  (250 ms by default), and the API cannot tell a caller "this was published".
- A second moving part exists: the dispatcher. If nobody runs it, nothing is published, and the
  symptom is a growing `Pending` backlog rather than an error.
- `InboxReceipts` grows without bound. Deduplication is only as good as the retention policy,
  and this lab has none.
- Message ordering is not preserved. The dispatcher claims the oldest due row, but retries and
  concurrent dispatchers reorder freely. Nothing in this lab requires ordering; a system that
  did would need a different design.

## Alternatives rejected

| Alternative | Why not |
|---|---|
| Publish inside the SQL transaction | the broker call would still be outside the commit; it just moves the window |
| `TransactionScope` / MSDTC | RabbitMQ is not a participant; couples availability |
| Publish, then write to SQL | worse: a message can exist for a request that never committed |
| Change data capture / debezium-style tailing | a much larger operational surface, and the message shape then follows the table shape |
| Broker-side deduplication plugin | moves an application invariant into broker configuration, invisible in the database |
| Idempotent consumer only, no inbox table | works only if every downstream effect is idempotent; here the effect is an external HTTP call whose outcome must be recorded |

## Evidence

- `ApiTests.SubmitAcceptsWithBrokerDownAndWorkWaitsInSql`
- `ApiTests.FaultAfterRequestInsertRollsBackRequestAndOutbox`
- `OutboxTests.CrashAfterConfirmRepublishesTheSameTransportMessageId`
- `InboxTests` (all three duplicate shapes)
- `SchemaTests` — the filtered unique indexes that make "one outbox row per request" a schema
  rule rather than a handler rule
