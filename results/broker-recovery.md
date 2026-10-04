# Broker outage, unroutable publish, confirm timeout, crash after confirm

> A focused run recorded on 2026-09-11, during development and before the first commit, so
> the counts below are those of that date. The current, commit-pinned run of the whole suite
> is in [verification.md](verification.md).

## Environment

| | |
|---|---|
| Date | 2026-09-11 (UTC) |
| .NET SDK | 10.0.400 |
| Docker | 29.6.1 |
| OS | Windows 11 Pro, build 10.0.26200.9445 |
| RabbitMQ | `rabbitmq:4.3.5-management-alpine@sha256:b3b8b7f9…` |
| RabbitMQ.Client | 7.2.2 |
| Commit | none (development tree, before the first commit) |

## Command

```powershell
dotnet test --filter "FullyQualifiedName~OutboxTests"
```

## Result

All 8 tests in `OutboxTests` pass.

| Test | Failure it reproduces | What must hold |
|---|---|---|
| `BrokerOutageKeepsRowsRetryableUntilTheBrokerReturns` | container stopped | never `Published` during the outage; published after |
| `AnUnroutableMessageIsReturnedNotMarkedPublished` | `basic.return` | `Pending` + `broker_unroutable`, `PublishedAtUtc` null |
| `NackOutcomeKeepsTheRowPending` | `basic.nack` (through the publishing seam) | `Pending` + `broker_nack` |
| `ConfirmTimeoutAgainstAFrozenBrokerKeepsTheRowPendingThenRecovers` | broker frozen, TCP open | `Pending` + `broker_confirm_timeout`, then recovers |
| `CrashAfterConfirmRepublishesTheSameTransportMessageId` | process killed after the confirm | republish with the **same** message id; 1 receipt, 1 job |
| `TwoDispatchersClaimDisjointRows` | two claimers | 5 rows, 5 distinct claims |
| `TwoWorkersPublishEveryRowExactlyOnce` (since renamed `TwoCompetingWorkersPublishEachRowOnce`) | two workers | every row `Published`, 1 receipt, 1 job each |
| `StaleLeaseOwnerCannotMarkPublished` | lease expiry | the stale owner's update affects 0 rows |

## The application defect this produced

Writing `ConfirmTimeoutAgainstAFrozenBrokerKeepsTheRowPendingThenRecovers` exposed a real
liveness bug in `ConfirmedPublisher`.

The channel was created with `publisherConfirmationTrackingEnabled: true`, which makes the
RabbitMQ client **wait for the confirm inside `BasicPublishAsync` itself**, with no timeout. A
frozen broker — TCP open, no frames answered — therefore parked the dispatcher indefinitely:

```text
outbox(Export) = Publishing / tries=1 / error=-      (and it stayed there)
```

The same setting also surfaces a nack and a `basic.return` as one `PublishException`, collapsing
two outcomes this lab must tell apart.

The fix: the client's own tracking is off, the publisher tracks sequence numbers and returns
itself, and the publish call and the confirm wait **share one timeout budget**. The row now
reaches `Pending` + `broker_confirm_timeout` and recovers on the next attempt.

## A test-harness defect worth recording

The first version of the freeze used `kill -STOP 1` inside the container. It ran without error
and proved nothing: in the RabbitMQ alpine image the Erlang VM **is** pid 1 of the container's
pid namespace, and the kernel discards `SIGSTOP` sent to a namespace's init from inside that
namespace. Verified directly:

```text
docker exec labsigtest sh -c 'kill -STOP 1; sleep 1; cut -d" " -f3 /proc/1/stat'
S                       <- still Sleeping, not Stopped

docker pause labsigtest && docker inspect -f '{{.State.Status}}' labsigtest
paused                  <- the cgroup freezer works
```

`LabFixture` now uses the container runtime's pause.

A second harness fix in the same class: the fixture binds **explicit host ports** for RabbitMQ.
Testcontainers gives a restarted container a new random mapping, which a worker configured once
at startup could never reconnect to — the outage tests were measuring that, not the outbox.

## Expected-failure output

These tests deliberately produce alarming log lines. They are the point, not defects:

```text
warn: Publisher channel shut down (CONNECTION_FORCED - broker forced connection closure ...)
warn: No broker confirm for outbox <id> within 2s; treating the publish as unknown.
warn: Broker returned message <id> as unroutable (312 NO_ROUTE); it will not be Published.
warn: Outbox <id> publish outcome Returned (broker_unroutable); returning to Pending.
```

## Limits

- One broker node. Nothing here says anything about clustering, quorum queues or failover.
- The confirm timeout is configurable but not tuned; the right value for a real workload is not
  measured here.
- `basic.nack` is exercised through the publishing seam, because a healthy single node cannot be
  made to nack deterministically. The confirm and return paths run against the real broker.
