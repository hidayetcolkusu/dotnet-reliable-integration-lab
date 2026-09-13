# Scenario: the broker is down

## What is being demonstrated

Accepting work must not depend on the message broker being reachable. The API writes the
business row and its outbox row in **one SQL transaction** and never opens an AMQP
connection, so RabbitMQ being gone changes the *latency* of the work, not whether it was
accepted.

The failure this rules out is the classic dual write: `INSERT` into the database, then
`basic.publish` to the broker. Whenever the second step fails, the two systems disagree, and
nothing in the process knows which of them is right.

## Run it

```powershell
pwsh -File scripts/run-scenario.ps1 -Scenario broker-down
```

The run first creates the resources it owns — its own database pair and its own RabbitMQ
container on free loopback ports — and it is **that** broker it stops, not the shared `rabbit`
compose service. Stopping the compose broker would take it away from every other process on
your machine, which is the opposite of a contained demonstration.

It then submits a request, shows the durable state, starts the worker *while the broker is
still down*, starts the broker again and waits. A `finally` block removes the run's container
and its two databases whatever happened — and only those: every helper that can delete checks
the run-scoped name first.

## What you should see

```text
==> Scenario 'broker-down' (run 60ab730b, topology prefix 'scn60ab730b-')
    databases: IntegrationLab_scn60ab730b, FakeErpLab_scn60ab730b
    broker container: lab-scn60ab730b-rabbit (amqp 57975, management 57976)
==> Creating this run's own databases
==> Starting this run's own RabbitMQ container
==> Stopping this run's RabbitMQ container
    [requestId=...] API answered 202 with the broker DOWN
    [requestId=...] outbox=Pending (durable, waiting for the broker)
==> Starting the worker while the broker is still down
    [requestId=...] outbox=Publishing while the broker is down (never Published)
==> Starting this run's RabbitMQ container again
    [requestId=...] outbox=Published after the broker returned
    [requestId=...] final: outbox=Published, job=Completed/attempts=1, receipts=1, applied=1
==> Removing this run's RabbitMQ container
==> Removing this run's databases
```

`outbox=Publishing` during the outage is not a slip: the dispatcher claimed the row and its
publish cannot complete. The lease is what makes that recoverable — and `Publishing` is still
not `Published`, which is the only line that matters.

The `Pending` → `Published` transition is the whole point: no message was lost, and no row
was optimistically marked as sent.

## Where to look in the code

| Step | Code |
|---|---|
| One transaction for request + outbox row | [`SubmitExportHandler.HandleAsync`](../../src/Integration.Api/Exports/SubmitExportHandler.cs) |
| The API never touches the broker | [`Integration.Api/Program.cs`](../../src/Integration.Api/Program.cs) — no `RabbitConnection` registration |
| Claim, publish, then move state | [`OutboxDispatcher`](../../src/Integration.Worker/Publishing/OutboxDispatcher.cs) |
| Bounded reconnect backoff | [`RabbitConnection`](../../src/Integration.Shared/Messaging/RabbitConnection.cs) |

## The same claim as a test

`OutboxTests.BrokerOutageKeepsRowsRetryableUntilTheBrokerReturns` stops and starts a real
broker container and asserts the row is never `Published` during the outage. `ApiTests`
covers the acceptance half: a `202` while the broker is unreachable.

## What this does not prove

A single broker node says nothing about high availability. This scenario shows that the
*application* survives a broker outage, not that the broker itself is highly available.
