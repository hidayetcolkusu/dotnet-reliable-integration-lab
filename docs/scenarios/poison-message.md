# Scenario: a poison message

## What is being demonstrated

A message the consumer can never process must leave the queue, and it must not disappear.
Those two requirements pull in opposite directions, and the usual shortcuts fail one of them:

- `BasicReject(requeue: false)` with no dead-letter exchange **drops** it — the evidence is gone;
- `BasicNack(requeue: true)` **loops** it forever — the queue never drains, and the consumer
  spends its life on a message it will never accept;
- a broker `x-dead-letter-exchange` moves it, but the *application* then has no durable record
  of why, and the decision is invisible in the database.

This lab does something narrower: the delivery is written to a **quarantine table** together
with a dead-letter outbox row in one transaction, and only then ACKed. "ACK" here means
"durably accepted", never "successfully processed".

## Run it

```powershell
pwsh -File scripts/run-scenario.ps1 -Scenario poison-message
```

## What you should see

```text
    [requestId=<marker>] published a structurally invalid delivery
    [requestId=<marker>] quarantined durably with reason 'malformed_body' (body sha256 e3b0c442...)
    [requestId=<marker>] export queue drained: acked after the quarantine committed, not requeued
    [requestId=<marker>] dead-letter queue holds 1 message(s) carrying hashes and reason codes, not the raw body
```

The drained export queue and the non-empty dead-letter queue are the two halves of the claim.

## What is in the quarantine row, and what is not

| Stored | Not stored |
|---|---|
| a reason code (`malformed_body`, `unknown_type`, …) | the raw message body |
| the body's SHA-256 and its length | exception messages or stack traces |
| the transport message id, truncated | anything read from the payload's fields |

The reason is that a poison body is *untrusted input of unknown size and content*. Copying it
into a database row, a log line and a broker message multiplies the places a hostile or
oversized payload has to be handled safely. A hash is enough to correlate repeats, and a
length is enough to explain the size.

`DeadLetterTests.AQuarantinedMessageProducesOneDeadLetterEventWithoutItsRawBody` asserts this
directly: it embeds a marker in the poison body and then asserts the marker does **not** appear
in the dead-letter message.

## Repeats collapse

The quarantine row is keyed by a fingerprint of `(transport id, body hash, reason)`. A
redelivered poison message therefore produces neither a second quarantine row nor a second
dead-letter event — see
`InboxTests.RepeatedPoisonDeliveryProducesOneQuarantineRowAndOneDeadLetterEvent`.

## Every rejection reason

| Reason code | Meaning |
|---|---|
| `missing_message_id` | no transport message id, so the delivery cannot be deduplicated |
| `malformed_body` | not JSON, not an object, or an unknown field |
| `oversized_body` | larger than `Lab:MaxBodyBytes` |
| `unknown_type` | not `OrderExportRequested` |
| `unsupported_schema` | a `schemaVersion` this consumer does not implement |
| `event_id_mismatch` | the envelope's event id disagrees with the business id it carries |
| `invalid_payload` | a field failed its own rule (amount, currency, reference length) |
| `unknown_source_request` | no accepted request in this system produced this event |
| `source_hash_mismatch` | the payload disagrees with the request that was accepted |
| `identity_payload_mismatch` | the same identity already exists with different content |

## Where to look in the code

| Step | Code |
|---|---|
| Structural validation, no I/O, one reason code each | [`MessageValidator`](../../src/Integration.Worker/Consuming/MessageValidator.cs) |
| Quarantine + dead-letter in one transaction | [`InboxAcceptor.QuarantineAsync`](../../src/Integration.Worker/Consuming/InboxAcceptor.cs) |
| ACK only after that commit | [`InboxConsumer`](../../src/Integration.Worker/Consuming/InboxConsumer.cs) |
| No broker-side DLX or TTL | [`Topology`](../../src/Integration.Shared/Messaging/Topology.cs) |

## What this does not prove

Nothing here re-processes a quarantined message. Deciding when a poison message may be
replayed after a code fix is a real operational question this lab does not answer.
