# ADR 0006 — Trace context is stored durably in SQL, not only propagated in memory

- **Status:** accepted
- **Date:** 2026-09-11
- **Scope:** `LabTelemetry`, `MessageProperties`, `OutboxMessages.TraceParent`, `IntegrationJobs.TraceParent`

## Context

One request crosses an HTTP boundary, a database, a broker and a retry loop that may run hours
later in a different process. Standard OpenTelemetry propagation covers the synchronous hops
(`Activity.Current` flows through async calls, and the ASP.NET Core and `HttpClient`
instrumentations carry it across HTTP).

It does not cover the two hops that matter most here:

- between the API's transaction and the dispatcher's publish — the two are separated by a
  **database row** and an arbitrary amount of time;
- between a first attempt and its retry — separated by a **schedule** and possibly a restart.

If the trace context only lives in memory, a trace ends at the API and a new, unrelated trace
starts in the worker. The one question worth asking during an incident — "show me everything
that happened to request X" — then has no answer.

## Decision

**The trace context is stored where the work is stored.** `OutboxMessages` and
`IntegrationJobs` each carry `TraceParent` and `TraceState` columns, written in the same
transaction as the work itself.

- The API captures `Activity.Current` and commits it with the outbox row.
- The dispatcher starts its publish span **linked to the stored context**, and writes the same
  W3C headers onto the AMQP message (`traceparent`, `tracestate`).
- The consumer reads those headers, starts the inbox span linked to them, and stores the context
  on the job row.
- Every later attempt — in any process, at any time — links to the job's stored context.

Spans are named in one place (`LabTelemetry.Spans`) and tagged with `lab.request_id`,
`lab.event_id`, `lab.outbox_id`, `lab.transport_message_id` and `lab.attempt_number`, so a
business id or a broker message id can both be turned back into a trace.

**A malformed stored value must never poison the work.** `ActivityContext.TryParse` decides;
an unparsable header is dropped and the stage starts a fresh root span. Observability is never
a precondition for work completing — which is also why the OTLP exporter is optional and its
absence changes nothing.

## Consequences

**Accepted:**

- One `requestId` is one trace, from the `POST` to the external receipt, across processes and
  restarts.
- A broker message id found in the RabbitMQ management UI can be turned back into a trace, and
  vice versa.
- Tracing being down, misconfigured or absent cannot fail a request.

**Costs:**

- Two extra columns on two tables, written in the hot path.
- A trace can span **hours** if a retry is scheduled far out. Many trace backends assume short
  traces and will show this as an unusually long, mostly-idle span tree; some drop late spans
  entirely.
- The stored context is a snapshot. If the original trace was never sampled, the linked spans
  inherit that decision and the later stages are invisible too.
- Only tracing is implemented. There are **no metrics and no structured log correlation beyond
  the ids** — no queue-depth gauge, no attempt histogram, no SLO.

## Alternatives rejected

| Alternative | Why not |
|---|---|
| Rely on `Activity.Current` only | the trace ends at the API; the worker starts an unrelated one |
| Correlate by `requestId` in logs only | works for text search, but gives no span tree, no timing and no parent/child structure |
| Store only `traceparent`, drop `tracestate` | loses vendor-specific sampling decisions on the way |
| Put the context in the message body | makes it part of the business contract, so a schema change breaks tracing |
| A custom header format | W3C is what every backend already understands |
| Fail the request when the stored context is unparsable | makes observability a precondition for correctness — exactly backwards |

## Evidence

- `TracingTests.OneRequestProducesOneTraceAcrossApiOutboxInboxAndTheExternalCall` — asserts the
  trace id stored on the outbox row equals the one on the job row, and that all four spans share it
- `TracingTests.EveryStageTagsTheRequestIdSoATraceCanBeFoundFromABusinessId`
- `TracingTests.AMalformedTraceParentDegradesToAFreshTraceInsteadOfPoisoningTheWork`

To see the traces, run the optional viewer:

```powershell
docker compose --profile tracing up -d
# start the apps with --Lab:OtlpEndpoint=http://127.0.0.1:4317
# Jaeger UI: http://127.0.0.1:16686
```
