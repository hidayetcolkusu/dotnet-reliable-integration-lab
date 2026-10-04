# Failure windows

A "failure window" here means a moment where a process can die, or a dependency can vanish,
between two effects that are not in the same transaction. Every window listed as **closed**
below has a test that produces that exact window against real SQL Server, a real RabbitMQ and
real processes — not a test whose name merely resembles it.

The named points live in
[`FaultPoints`](../../src/Integration.Shared/Runtime/IFaultHooks.cs) and are activated only
from configuration (`Lab:Faults:<hook>` = `crash` | `throw` | `delay:<ms>`, optionally prefixed
`once:`). They are never reachable from a request body, and the hooks are composed **only in
`Testing`** — in `Development` the process gets `NoOpFaultHooks`, so a configuration value left
behind in a developer's settings cannot crash or stall a normal run
([`FaultHookRegistration`](../../src/Integration.Shared/Runtime/IFaultHooks.cs),
`CompositionTests.FaultHooksAreNotComposedOutsideTesting`).

## Windows that are closed, and by which test

| # | Window | How it is produced | What must remain true | Test |
|---|---|---|---|---|
| 1 | request row inserted, outbox row not yet | `api.after-request-insert` (`crash`, child process) | neither row exists — the transaction rolls back | `ApiTests.FaultAfterRequestInsertRollsBackRequestAndOutbox`, `RecoveryTests.ACrashBetweenTheRequestAndItsOutboxRowLeavesNoAcceptedWorkBehind` |
| 2 | outbox row claimed, publish not attempted | `outbox.after-claim` (`crash`, child process) | the row stays `Publishing`; the expired lease makes it claimable again and it is republished under the **same** outbox id | `FailureWindowTests.AHardStopBetweenTheOutboxClaimAndThePublishRepublishesTheSameMessage` |
| 3 | broker confirmed, SQL not yet updated | `outbox.after-confirm-before-update` | the row stays `Publishing`, is republished with the **same** message id, and the duplicate delivery collapses to one job | `OutboxTests.CrashAfterConfirmRepublishesTheSameTransportMessageId` |
| 4 | inbox transaction committed, ACK not sent | `inbox.after-commit-before-ack` (`crash`, child process) | the broker redelivers; the existing receipt absorbs it; one receipt, one job, and the queue drains | `FailureWindowTests.AHardStopBetweenTheInboxCommitAndTheAckIsAbsorbedByTheExistingReceipt` |
| 5 | consumer holds a delivery, SQL is unreachable | a loopback TCP gate in front of SQL Server is closed, then reopened | nothing durable, therefore nothing ACKed; the consumer backs off; the same delivery is accepted when SQL returns | `FailureWindowTests.AConsumerThatCannotReachSqlNeverAcksAndRecoversWhenSqlReturns` |
| 6 | delivery ACKed, job not yet processed | `job.after-claim` (`delay`), then a bounded stop | the broker holds nothing at all; the work lives only in SQL and a restart finishes it with one external effect | `FailureWindowTests.WorkAckedButNotYetProcessedSurvivesAWorkerStopWithAnEmptyBroker` |
| 7 | job claimed, external call not started | `job.after-claim` (`crash`, child process) | the attempt is already spent; the row is `Processing` until the lease expires | `JobRetryTests.ACrashInsideAnAttemptStillConsumesThatAttempt` |
| 8 | external call answered, job row knows nothing | `job.after-http-before-update` | the outcome is unknown, so the retry replays against the external idempotency key | `ExternalIdempotencyTests.ALostResponseDoesNotApplyTheOperationTwice` |
| 9 | the external system restarts after committing | FakeErp killed hard and restarted on the same address and database | the same operation key answers with the same receipt and leaves one row — idempotency is durable, not a process cache | `FailureWindowTests.TheExternalSystemStillReplaysTheSameReceiptAfterAProcessRestart` |
| 10 | two owners: a lease expires mid-attempt and the old owner writes late | `job.after-http-before-update` (`delay`) + a 1-second lease + a second worker | the stale write changes nothing — state, receipt and attempt history stay the current owner's — and there is exactly one external effect | `FailureWindowTests.ALateResultFromAnExpiredOwnerCannotOverwriteTheCurrentOwnersOutcome` |
| 11 | terminal transaction committed, dead-letter publish not attempted | `outbox.after-claim` (`crash`) with only the dead-letter row pending | the job is `DeadLetterPending`, exactly one terminal event exists and is unpublished; a restart confirms that one event | `FailureWindowTests.AHardStopBetweenTheTerminalCommitAndTheDeadLetterPublishKeepsOneTerminalEvent` |
| 12 | dead-letter publish confirmed, job not closed | `deadletter.after-confirm-before-update` | the job stays `DeadLetterPending`; the restart closes it | `DeadLetterTests.ACrashBetweenDeadLetterConfirmAndUpdateStillClosesTheTerminalState` |
| 13 | a trace exporter is configured and unreachable | an OTLP endpoint pointed at a reserved port with nothing behind it | the work completes anyway; only the export of the trace fails. *Not* configuring an exporter would prove nothing | `FailureWindowTests.AConfiguredButUnreachableOtlpExporterDoesNotStopTheWork` |
| 14 | shutdown while an attempt is in flight | `job.after-claim` (`delay:600000`) + `StopAsync` | the stop is bounded (it does not wait out the delay), no new work is claimed, and the half-finished job is recovered through its lease | `FailureWindowTests.ShutdownIsBoundedAndLeavesInFlightWorkRecoverable` |
| 15 | the broker is simply gone | the broker container is stopped | work waits in SQL; nothing is marked done; the backlog drains on return | `OutboxTests.BrokerOutageKeepsRowsRetryableUntilTheBrokerReturns`, `DeadLetterTests.ABrokerOutageDelaysTheDeadLetterEventButNeverLosesIt` |

Two boundaries next to these windows are enforced before anything durable is written, because a
value the database cannot store would otherwise fail an INSERT *inside* the acceptance
transaction — which means no ACK, which means the same message forever:

- a transport MessageId that does not fit `varchar(128)`, or that the non-Unicode column cannot
  represent, is quarantined with `unstorable_message_id`
  (`InboxTests.AnOversizedTransportMessageIdIsQuarantinedAndAckedInsteadOfLoopingForever`);
- trace metadata past its column, or a `tracestate` with no parent, is dropped rather than
  stored (`TracingTests.TraceMetadataTooLargeForItsColumnIsDroppedWithoutStoppingTheWork`).

Two deliveries can also be accepted *at the same time*: both read "no receipt, no job" and race
on a unique key. The database decides the winner; the loser resolves the conflict instead of
failing blindly. Each test commits the winner from a second connection just before the
loser's insert, so the race is forced on every run rather than hoped for:

- same transport MessageId, same body: the loser is a replay and is ACKed, with no second job
  or receipt (`InboxTests.LosingAReceiptRaceToTheSameDeliveryIsAcceptedWithoutASecondJob`);
- a republish (new MessageId, same EventId) losing on the job key has no receipt of its own, so
  it is **not** ACKed; its redelivery adds a receipt to the winner's job
  (`InboxTests.LosingAJobKeyRaceToARepublishIsNotAckedUntilItsRedeliveryAddsAReceipt`);
- the same poison message quarantined twice at once yields one quarantine row and one
  dead-letter event (`InboxTests.LosingAQuarantineRaceIsTreatedAsAlreadyQuarantined`).

## What each window costs

None of these windows is eliminated — they cannot be, without a distributed transaction. What
the design does is choose, for each one, a failure mode that is *recoverable* rather than
*silent*:

| Window | Chosen failure mode | Price paid |
|---|---|---|
| 2, 3, 4 | duplicate delivery | the inbox must deduplicate |
| 5 | latency, and a redelivery | the broker holds the message while SQL is away |
| 7 | a spent attempt with no external call | the job may reach its budget one attempt "early" |
| 8, 9, 10 | duplicate external call | the external system must be idempotent |
| 11, 12 | duplicate dead-letter message | DLQ consumers must tolerate repeats |
| 13 | a missing trace | the incident is harder to read, the work still happens |
| 15 | latency | the backlog grows while the dependency is down |

The price is always paid on the *duplicate* side, never on the *loss* side. That is the single
design rule the whole repository follows: **when the outcome is unknown, assume it may have
happened and make repeating it safe.**

## Two invariants the job loop keeps across these windows

- **State and attempt result move together.** A job's transition (`Completed`,
  `RetryScheduled`, `DeadLetterPending`) and the `JobAttempts` row for that attempt are one
  owner-guarded statement inside one transaction, so a process killed between them leaves
  neither. A `Completed` job can therefore never be found carrying an attempt still marked
  `Started` ([`JobStore`](../../src/Integration.Shared/Persistence/JobStore.cs)).
- **A stale owner writes nothing.** Every transition carries `AND LeaseToken = @token`, and the
  attempt write only happens when that guard matched — so a late result from an expired owner
  cannot rewrite either half. An attempt whose owner never returned is closed as `Abandoned`
  when the next owner claims, rather than left open forever.

## The confirm-timeout window in particular

There is a fourth outcome besides confirmed, nacked and returned: **no answer at all**. It is
the one most easily forgotten, because a healthy broker always answers.

`OutboxTests.ConfirmTimeoutAgainstAFrozenBrokerKeepsTheRowPendingThenRecovers` produces it for
real by freezing the broker container with the cgroup freezer (`docker pause`), so the TCP
connection stays open and no confirm ever arrives. The publisher's own timeout then classifies
the publish as `broker_confirm_timeout` — unknown, therefore retryable.

Two implementation details this test forced:

- The publish call and the confirm wait **share one timeout budget**. An unbounded wait
  anywhere in that path parks the dispatcher forever, which is a liveness bug that only a
  frozen — not a stopped — broker reveals.
- The client library's own confirm tracking is deliberately **off**
  (`publisherConfirmationTrackingEnabled: false`). With it on, `BasicPublishAsync` waits for
  the confirm itself with no timeout, and it collapses a nack and a `basic.return` into one
  exception — two outcomes this lab must tell apart.

Signalling the broker from *inside* its container does not work, incidentally: the Erlang VM is
pid 1 of the container's pid namespace, and the kernel discards `SIGSTOP` sent to a namespace's
init from within that namespace. The broker keeps answering and the test silently proves
nothing. This is why `LabFixture` uses the container runtime's pause instead.

## The same problem on the HTTP side

`HttpClient.Timeout` has an analogous blind spot. Under
`HttpCompletionOption.ResponseHeadersRead` its timer stops once the response *headers* arrive,
so an external system that flushes `200 OK` and then stalls its body holds the worker's single
job loop open for as long as it likes — past the configured timeout and past the retry
schedule, with nothing in the job row to explain it.

`ErpClient` therefore owns one deadline for the **whole** attempt — send, headers, body stream
and parse — and `HttpClient.Timeout` is set to infinite so there is no second, weaker clock.
`ErpAttemptBudgetTests` proves it against a real loopback socket that flushes headers and then
either stalls or drips the body one byte at a time.

## Windows this lab does not close

- **A crash between the ERP's own commit and its response** is simulated, not survived by the
  ERP: FakeErp commits first and then delays. A real external system might crash *before*
  committing, and the caller cannot tell those two cases apart. Only a reconciliation query can.
- **A SQL Server failover mid-transaction** is not tested. Window 5 cuts connectivity between
  *transactions*; the code treats connectivity errors as retryable
  (`SafeErrors.IsSqlUnavailable`), but no test kills SQL Server with a transaction open.
- **Clock skew between processes** is bounded but not eliminated: leases and schedules use
  `SYSUTCDATETIME()` on the server, so all comparisons happen against one clock. A test that
  reads `NextAttemptAtUtc` from a client clock tolerates skew by retrying the claim.
- **Broker data loss** (a lost disk, a lost quorum) is out of scope: this is a single-node
  broker with no mirroring, and the lab says so rather than testing around it.
