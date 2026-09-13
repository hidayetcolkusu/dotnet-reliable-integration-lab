# Scenario: the same message is delivered twice

## What is being demonstrated

At-least-once delivery means duplicates are normal, not exceptional. A publisher that loses
a confirm must republish; a consumer that dies before its ACK will be redelivered. So the
question is never "how do I avoid duplicates" but "what makes a duplicate harmless".

Here the answer is a **durable inbox**: one transaction turns a delivery into a receipt plus
(at most) one job, and the receipt's primary key is what absorbs the repeat.

## Run it

```powershell
pwsh -File scripts/run-scenario.ps1 -Scenario duplicate-delivery
```

The script lets the normal flow deliver a request, then publishes the *same business event*
again under a new transport message id — exactly what a republish after a lost confirm looks
like.

## What you should see

```text
    [requestId=...] after the normal delivery: outbox=Published, job=Pending/attempts=0, receipts=1, applied=0
    [requestId=...] two receipts, ONE job: outbox=Published, job=Processing/attempts=1, receipts=2, applied=0
    [requestId=...] final: outbox=Published, job=Completed/attempts=1, receipts=2, applied=1
```

Two receipts, one job, one external effect. The receipt count is *supposed* to grow: it is
the audit trail of deliveries. The job count is what must not.

## The three duplicate shapes

| Shape | What happens | Why |
|---|---|---|
| Same transport id, same payload | extra receipt is rejected by the primary key; no new job | a redelivery after a lost ACK |
| Different transport id, same event id, same payload | new receipt, no new job | a republish that did not reuse the outbox id |
| Same event id, different payload | quarantined; the original job is never touched | this is not a duplicate, it is a conflict |

The third row matters most: silently accepting it would let a tampered or mis-generated
message overwrite work that was already accepted.

## Where to look in the code

| Step | Code |
|---|---|
| Receipt + job in one transaction | [`InboxAcceptor.AcceptAsync`](../../src/Integration.Worker/Consuming/InboxAcceptor.cs) |
| ACK only after that commit | [`InboxConsumer.HandleDeliveryAsync`](../../src/Integration.Worker/Consuming/InboxConsumer.cs) |
| Republish reuses the outbox id as the transport id | [`MessageProperties.CreatePublishProperties`](../../src/Integration.Shared/Messaging/MessageProperties.cs) |
| Receipt primary key | [`EntityConfigurations`](../../src/Integration.Shared/Persistence/EntityConfigurations.cs) |

## The same claim as a test

`InboxTests` covers all three shapes directly, and
`OutboxTests.CrashAfterConfirmRepublishesTheSameTransportMessageId` produces a real duplicate
by crashing the publisher after the broker confirmed.

## What this does not prove

Deduplication here is unbounded in time, because receipts are never pruned. A production
system needs a retention policy, and that policy is what decides how late a duplicate may
still arrive — this lab does not implement one.
