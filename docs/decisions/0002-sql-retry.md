# ADR 0002 — Retry state lives in SQL, not in the broker and not in memory

- **Status:** accepted
- **Date:** 2026-09-11
- **Scope:** `IntegrationJobs`, `JobAttempts`, `JobProcessor`, `RetryPolicy`

## Context

A job that fails transiently must be retried a bounded number of times on a bounded schedule.
There are three common places to keep that state.

**In memory.** `Task.Delay` plus a counter in a field. It disappears when the process dies, so
the replacement starts from zero — a crash loop can call the external system without limit, and
nothing records that it did.

**In the broker.** RabbitMQ's classic delayed-retry pattern: publish to a queue with
`x-message-ttl` and `x-dead-letter-exchange`, let the message expire, have the DLX route it
back, and keep the attempt count in a header. It survives restarts, but the retry state is now
transport metadata: invisible to a `SELECT`, erased by a queue purge, gone if the broker is
rebuilt, and coupled to queue topology (one queue per delay tier).

**In the database.** A column for the count, a column for the next attempt time, a column for
the last error.

## Decision

The retry state is SQL. `IntegrationJobs` holds `AttemptsStarted`, `NextAttemptAtUtc`,
`LastErrorCode`, `LeaseToken` and `LeaseUntilUtc`; `JobAttempts` holds one row per **started**
attempt. The broker topology has **no** `x-message-ttl`, `x-dead-letter-exchange`,
`x-max-length` or `x-expires`.

Three rules make this work:

1. **`AttemptsStarted` is incremented in the same transaction as the claim**, before the
   external call. A process that dies mid-call has still spent an attempt.
2. **Every state update is guarded by `AND LeaseToken = @token`.** A previous owner that wakes
   up late affects zero rows.
3. **`RetryPolicy` is pure** — no clocks, no I/O — so the classification (transient vs terminal)
   and the schedule (including `Retry-After` parsing and its cap) are exactly testable.

The publish budget is kept **separate** from the job budget: a broker outage consumes
`OutboxMessages.PublishAttempts` and never touches the five external attempts.

## Consequences

**Accepted:**

- An operator can answer "what is stuck, for how long, and why" with a query.
- The policy survives a queue purge, a broker rebuild and any number of restarts.
- A crash cannot buy a free retry, which is what bounds the blast radius of a crash loop.
- The schedule is arbitrary (`DATEADD`), not limited to pre-created delay tiers.

**Costs:**

- The worker **polls**. There is a latency floor of one poll interval, and the database takes
  the load of that polling (mitigated by `READPAST` + `UPDLOCK` and a covering index, not
  eliminated).
- The budget is counted in *started* attempts, so a crash at exactly the wrong moment can make
  a job reach its ceiling one real attempt "early". That is the deliberate trade: an
  undercount would be unbounded retries.
- Two budgets is more to explain than one.
- Nothing here retries *across* the claim boundary faster than the poll loop; a burst of
  failures drains at poll speed.

## Alternatives rejected

| Alternative | Why not |
|---|---|
| TTL + DLX delay queues | retry state invisible in the database, erased by a purge, coupled to topology |
| `rabbitmq_delayed_message_exchange` plugin | same visibility problem, plus a plugin dependency |
| Polly in the worker | in-memory; a restart resets the budget, and the count is not durable |
| `HttpClient` resilience handler retries | worse: the retries are invisible to the job row entirely, so `AttemptsStarted` would understate reality |
| Hangfire / Quartz | a second scheduler with its own storage and failure modes, for state that already has a natural home |
| Incrementing the counter *after* the call | a crash mid-call would be free, so a crash loop is unbounded |

## Evidence

- `JobRetryTests.TheAttemptBudgetIsAHardCeiling`
- `JobRetryTests.ACrashInsideAnAttemptStillConsumesThatAttempt`
- `JobRetryTests.RetryStateSurvivesAWorkerRestartInsteadOfStartingOver`
- `JobRetryTests.AClaimWithAnAlreadySpentBudgetNeverOpensAnotherExternalCall`
- `JobRetryTests.RetryAfterIsRespectedButNeverBeyondTheCap` (pure)
- `JobRetryTests.AConfiguredScheduleReplacesTheDefaultInsteadOfExtendingIt` — a regression guard
  for a real defect found while writing these tests: array configuration **appends** to a
  non-empty default, so a configured schedule was silently ignored.
