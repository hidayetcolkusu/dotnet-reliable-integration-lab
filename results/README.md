# Results

Real output from real runs. Every file here records the exact command, when it ran, the
versions involved, and what the run did **not** prove.

Rules these files follow:

- **Only output that was actually produced.** No illustrative numbers, no reconstructed logs.
- **Expected failures are labelled as such.** A test that proves a broker outage is survived
  produces alarming log lines on purpose; those are not defects.
- **No performance claims.** Nothing here measures throughput, latency percentiles or loss
  rates, so none are stated.
- **CI claims are tied to a run.** Each CI claim names the run and the commit it tested.
  The current one is in [verification.md](verification.md); earlier ones, including the red
  runs before CI first went green, are in [history/](history/README.md).

| File | What it records |
|---|---|
| [verification.md](verification.md) | the current verified state: commit, test count, format, CI run, platforms, limits |
| [history/](history/README.md) | earlier acceptance runs, as recorded on their dates, and the defects each one exposed |
| [broker-recovery.md](broker-recovery.md) | outage, unroutable publish, confirm timeout, crash after confirm |
| [duplicates.md](duplicates.md) | the three duplicate shapes and the quarantine reasons |
| [retry-and-dlq.md](retry-and-dlq.md) | retry budget, external idempotency, dead-lettering |
| [tracing.md](tracing.md) | one request, one trace, across processes |
