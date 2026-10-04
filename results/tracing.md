# One request, one trace

> A focused run recorded on 2026-09-11, during development and before the first commit, so
> the counts below are those of that date. The current, commit-pinned run of the whole suite
> is in [verification.md](verification.md).

## Environment

| | |
|---|---|
| Date | 2026-09-11 (UTC) |
| .NET SDK | 10.0.400 |
| OpenTelemetry | ASP.NET Core + HttpClient instrumentation, optional OTLP exporter |
| Trace viewer | `jaegertracing/all-in-one:1.76.0@sha256:ab6f1a1f…` (optional compose profile) |
| Commit | none (development tree, before the first commit) |

## Command

```powershell
dotnet test --filter "FullyQualifiedName~TracingTests"
```

## Result

All 3 tests pass.

| Test | What it asserts |
|---|---|
| `OneRequestProducesOneTraceAcrossApiOutboxInboxAndTheExternalCall` | the trace id stored on the outbox row equals the one on the job row, and all four spans share it |
| `EveryStageTagsTheRequestIdSoATraceCanBeFoundFromABusinessId` | the publish and inbox spans carry the same `lab.transport_message_id`, and the inbox span carries `lab.event_id` |
| `AMalformedTraceParentDegradesToAFreshTraceInsteadOfPoisoningTheWork` | the work completes, `IntegrationJobs.TraceParent` is null, the inbox span is a fresh root |

## How this is verified without a collector

An in-process `ActivityListener` subscribed to the `IntegrationLab` source records every span
the run produces. The listener is what makes spans *observable to the assertions*; it is not
what makes the context *propagate* — that happens through two SQL columns and two AMQP headers,
which is exactly the claim under test.

The four spans that must share one trace id:

```text
lab.api.receive          (Integration.Api)
lab.outbox.publish       (Integration.Worker, linked to OutboxMessages.TraceParent)
lab.inbox.persist        (Integration.Worker, linked to the traceparent header)
lab.export.http_attempt  (Integration.Worker, linked to IntegrationJobs.TraceParent)
```

## Why the context is stored, not just propagated

`Activity.Current` covers the synchronous hops. It does not survive the two that matter here:

- API transaction → dispatcher publish, separated by a **database row** and arbitrary time;
- attempt → retry, separated by a **schedule** and possibly a process restart.

So `OutboxMessages` and `IntegrationJobs` each carry `TraceParent`/`TraceState`, written in the
same transaction as the work. A retry running an hour later in a different process still belongs
to the original trace.

## Seeing it

```powershell
docker compose --profile tracing up -d
# start the apps with --Lab:OtlpEndpoint=http://127.0.0.1:4317
# Jaeger UI: http://127.0.0.1:16686
```

No screenshot is included here, because none was taken.

## Limits

- **Tracing only.** No metrics, no SLOs, no queue-depth gauge, no attempt histogram.
- A trace can span **hours** when a retry is scheduled far out. Many trace backends assume short
  traces; some drop late spans entirely.
- The stored context is a snapshot of a sampling decision. If the original trace was not
  sampled, the later stages are invisible too.
- The OTLP exporter is optional by design: tracing being down must never fail a request. That
  also means a misconfigured endpoint fails silently.
