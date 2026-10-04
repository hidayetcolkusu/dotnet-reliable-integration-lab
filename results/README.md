# Results

Real output from real runs. Every file here records the exact command, when it ran, the
versions involved, and what the run did **not** prove.

Rules these files follow:

- **Only output that was actually produced.** No illustrative numbers, no reconstructed logs.
- **Expected failures are labelled as such.** A test that proves a broker outage is survived
  produces alarming log lines on purpose; those are not defects.
- **No performance claims.** Nothing here measures throughput, latency percentiles or loss
  rates, so none are stated.
- **CI claims are tied to a run.** The only CI claim is
  [run 34765658412](https://github.com/hidayetcolkusu/dotnet-reliable-integration-lab/actions/runs/34765658412):
  green on `c94ac50`, 182/182 on `ubuntu-24.04`. It is recorded, along with the red runs
  before it, in [verification.md](verification.md). Every other result here was run locally,
  on one machine.

| File | What it records |
|---|---|
| [verification.md](verification.md) | every acceptance run, newest first: restore, build, test, format, and the five scenarios |
| [broker-recovery.md](broker-recovery.md) | outage, unroutable publish, confirm timeout, crash after confirm |
| [duplicates.md](duplicates.md) | the three duplicate shapes and the quarantine reasons |
| [retry-and-dlq.md](retry-and-dlq.md) | retry budget, external idempotency, dead-lettering |
| [tracing.md](tracing.md) | one request, one trace, across processes |
