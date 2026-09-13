namespace Integration.Shared.Persistence.Entities;

public static class OutboxKind
{
    public const string Export = "Export";
    public const string DeadLetter = "DeadLetter";
}

public static class OutboxStatus
{
    public const string Pending = "Pending";
    public const string Publishing = "Publishing";
    public const string Published = "Published";
}

public static class JobStatus
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string RetryScheduled = "RetryScheduled";
    public const string Completed = "Completed";
    public const string DeadLetterPending = "DeadLetterPending";
    public const string DeadLettered = "DeadLettered";
}

public static class AttemptOutcome
{
    public const string Started = "Started";
    public const string Succeeded = "Succeeded";
    public const string TransientFailure = "TransientFailure";
    public const string PermanentFailure = "PermanentFailure";
    public const string Abandoned = "Abandoned";
}

public static class RejectionReason
{
    public const string MissingMessageId = "missing_message_id";

    /// <summary>
    /// The transport MessageId is present but cannot be stored as the receipt key without
    /// loss (too long, or not representable in the non-Unicode column).
    /// </summary>
    public const string UnstorableMessageId = "unstorable_message_id";

    public const string MalformedBody = "malformed_body";
    public const string OversizedBody = "oversized_body";
    public const string UnknownType = "unknown_type";
    public const string UnsupportedSchema = "unsupported_schema";
    public const string EventIdMismatch = "event_id_mismatch";
    public const string InvalidPayload = "invalid_payload";
    public const string UnknownSourceRequest = "unknown_source_request";
    public const string SourceHashMismatch = "source_hash_mismatch";
    public const string IdentityPayloadMismatch = "identity_payload_mismatch";
}

/// <summary>The accepted business request. Written in the same transaction as its outbox row.</summary>
public sealed class ExportRecord
{
    public Guid RequestId { get; set; }

    public string PayloadHash { get; set; } = string.Empty;

    public string ExternalReference { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>One message waiting to be published, with its lease and publish budget.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = OutboxKind.Export;

    public Guid? SourceRequestId { get; set; }

    public Guid? RejectionId { get; set; }

    public string Body { get; set; } = string.Empty;

    public string RoutingKey { get; set; } = string.Empty;

    public string Exchange { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string Status { get; set; } = OutboxStatus.Pending;

    public int PublishAttempts { get; set; }

    public DateTimeOffset NextAttemptAtUtc { get; set; }

    public Guid? LeaseToken { get; set; }

    public DateTimeOffset? LeaseUntilUtc { get; set; }

    public DateTimeOffset? PublishedAtUtc { get; set; }

    public string? LastErrorCode { get; set; }

    public string? TraceParent { get; set; }

    public string? TraceState { get; set; }
}

/// <summary>Proof that one transport delivery was durably accepted. ACK follows its commit.</summary>
public sealed class InboxReceipt
{
    public string ConsumerName { get; set; } = string.Empty;

    public string TransportMessageId { get; set; } = string.Empty;

    public Guid EventId { get; set; }

    public string PayloadHash { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public Guid IntegrationJobId { get; set; }
}

/// <summary>The unit of work that survives broker ACK, worker restart and lease expiry.</summary>
public sealed class IntegrationJob
{
    public Guid RequestId { get; set; }

    public string PayloadHash { get; set; } = string.Empty;

    public string Status { get; set; } = JobStatus.Pending;

    public int AttemptsStarted { get; set; }

    public DateTimeOffset? NextAttemptAtUtc { get; set; }

    public Guid? LeaseToken { get; set; }

    public DateTimeOffset? LeaseUntilUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public string? LastErrorCode { get; set; }

    public string? ExternalReceiptId { get; set; }

    public string? TraceParent { get; set; }

    public string? TraceState { get; set; }
}

/// <summary>One started attempt. Started attempts, not completed HTTP calls.</summary>
public sealed class JobAttempt
{
    public Guid RequestId { get; set; }

    public int AttemptNumber { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset? FinishedAtUtc { get; set; }

    public string Outcome { get; set; } = AttemptOutcome.Started;

    public string? SafeErrorCode { get; set; }
}

/// <summary>Quarantined delivery. Carries hashes and sizes, never a raw body.</summary>
public sealed class RejectedMessage
{
    public Guid Id { get; set; }

    public string Fingerprint { get; set; } = string.Empty;

    public string? TransportMessageId { get; set; }

    public string BodySha256 { get; set; } = string.Empty;

    public int BodyLength { get; set; }

    public string ReasonCode { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public DateTimeOffset? DeadLetterPublishedAtUtc { get; set; }
}
