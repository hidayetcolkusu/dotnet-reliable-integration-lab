# Results

Real output from real runs. Every file here records the exact command, when it ran, the
versions involved, and what the run did **not** prove.

Rules these files follow:

- **Only output that was actually produced.** No illustrative numbers, no reconstructed logs.
- **Expected failures are labelled as such.** A test that proves a broker outage is survived
  produces alarming log lines on purpose; those are not defects.
- **No performance claims.** Nothing here measures throughput, latency percentiles or loss
  rates, so none are stated.
- **No remote CI claim.** `.github/workflows/ci.yml` exists; until a remote run is green, that
  is all that is claimed. Everything recorded here was run locally, on one machine.

| File | What it records |
|---|---|
| [verification.md](verification.md) | every acceptance run, newest first: restore, build, test, format, and the five scenarios |
| [broker-recovery.md](broker-recovery.md) | outage, unroutable publish, confirm timeout, crash after confirm |
| [duplicates.md](duplicates.md) | the three duplicate shapes and the quarantine reasons |
| [retry-and-dlq.md](retry-and-dlq.md) | retry budget, external idempotency, dead-lettering |
| [tracing.md](tracing.md) | one request, one trace, across processes |
