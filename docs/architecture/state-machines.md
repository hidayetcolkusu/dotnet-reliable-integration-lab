# State machines

Every durable state in this lab lives in a SQL column, and every transition is a single
guarded `UPDATE`. There is no in-memory state machine, which is why a `SELECT` is always a
complete answer to "where is this request?".

## OutboxMessages

```text
                      claim (UPDLOCK, READPAST)
                      PublishAttempts += 1
                      LeaseToken = @token
        +---------+   LeaseUntilUtc = now + lease   +------------+
        | Pending |-------------------------------->| Publishing |
        +----+----+                                 +-----+------+
             ^                                            |
             |                                            | publish with
             |  ScheduleRetryAsync                        | mandatory + confirm
             |  (Returned | Nacked | TimedOut | Failed)   |
             |  NextAttemptAtUtc = now + backoff          |
             |  LastErrorCode = broker_*                  |
             +--------------------------------------------+
                                                          |
                                        Confirmed AND not returned
                                        AND LeaseToken = @token
                                                          v
                                                   +-------------+
                                                   |  Published  |
                                                   +-------------+
                                                     (terminal)
```

Facts that the diagram encodes:

- **`Publishing` is not "in flight on the wire", it is "claimed by an owner".** The lease is
  what makes a row claimable again after the owner dies; the row is never stuck.
- **A returned message never reaches `Published`.** `mandatory: true` means an unroutable
  message comes back, and a `basic.return` is remembered per message id even if a `basic.ack`
  follows it on the same channel.
- **Every transition out of `Publishing` carries `AND LeaseToken = @token`.** A previous owner
  that wakes up late affects zero rows.
- `PublishAttempts` is incremented **on claim**, not on success, so a crash between claim and
  confirm is visible as an attempt.

There is no `Failed` state. A publish that cannot succeed stays `Pending` with a
`LastErrorCode`, because a broker outage is not a property of the message.

## IntegrationJobs

```text
                     (created by the inbox transaction)
                              +---------+
                              | Pending |
                              +----+----+
                                   |
                claim: AttemptsStarted += 1, JobAttempts row,
                       LeaseToken, LeaseUntilUtc            +----------------+
                                   |                        | RetryScheduled |
                                   v                        +--------+-------+
                            +------------+   transient failure        |
                            | Processing |--------------------------->+
                            +-----+------+   NextAttemptAtUtc =        |
                             |    |          now + backoff            |
                             |    |                                   |
                             |    |  lease expiry  <------------------+
                             |    |  (claim again)      due again
                             |    |
            success          |    |    terminal: permanent failure
            + ReceiptId      |    |    OR budget exhausted
                             v    v
                    +-----------+  +--------------------+
                    | Completed |  | DeadLetterPending  |
                    +-----------+  +----------+---------+
                     (terminal)               |
                                              | DLQ publish CONFIRMED
                                              v
                                     +----------------+
                                     |  DeadLettered  |
                                     +----------------+
                                        (terminal)
```

Facts that the diagram encodes:

- **`AttemptsStarted` counts started attempts, not completed ones.** It is incremented in the
  same transaction as the claim. A process that dies mid-call has still spent an attempt, which
  is what stops a crash loop from calling the external system forever.
- **`DeadLetterPending` and `DeadLettered` are two states on purpose.** The job becomes
  `DeadLetterPending` in the same transaction that writes the dead-letter outbox row; it only
  becomes `DeadLettered` once that message was confirmed by the broker. Claiming
  "dead-lettered" before the confirm would be a claim about a message that may not exist.
- **A claim with an exhausted budget goes straight to terminal.** It is reclaimed only to write
  the terminal state, and never opens attempt six.
- `Processing` with an expired `LeaseUntilUtc` is claimable. That single `OR` clause in the
  claim SQL is the entire crash-recovery mechanism for jobs.

## JobAttempts

One row per started attempt, so the history survives anything the job row does later.

```text
Started ---> Succeeded
        |--> TransientFailure  (+ SafeErrorCode: http_503, http_timeout, http_connection, ...)
        |--> PermanentFailure  (+ SafeErrorCode: http_422, ...)
        \--> (left as Started if the process died before finishing it)
```

An attempt row left at `Started` is itself the evidence of a crash inside that attempt.

## RejectedMessages

Not a state machine so much as an append-only quarantine, with one nullable timestamp:

```text
(row inserted with a fingerprint)  --->  DeadLetterPublishedAtUtc set
                                          once the DLQ event was confirmed
```

The fingerprint is `SHA256(transportId | bodySha256 | reasonCode)`, so a redelivered poison
message finds its own row and produces no second quarantine and no second dead-letter event.

## The transitions that are deliberately impossible

| Attempted transition | Why it is refused |
|---|---|
| `Publishing` → `Published` by a stale owner | the `LeaseToken` guard fails; zero rows affected |
| `Publishing` → `Published` after a `basic.return` | the return is remembered per message id and wins over the ack |
| `Processing` → `Completed` by a stale owner | the `LeaseToken` guard fails |
| `DeadLetterPending` → `DeadLettered` without a confirm | only the dispatcher writes it, after `MarkPublishedAsync` succeeded |
| a sixth attempt after `MaxJobAttempts` | the claim reports `BudgetExhausted` before any HTTP call |
| a second job for the same event id | the job's primary key is the event id |
| a second `Export` outbox row for one request | filtered unique index on `(SourceRequestId)` where `Kind = 'Export'` |

Each row in that table has a test in `SchemaTests`, `OutboxTests` or `JobRetryTests`.
