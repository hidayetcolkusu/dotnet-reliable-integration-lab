using System.Security.Cryptography;
using System.Text;
using Integration.Shared.Contracts;
using Integration.Shared.Diagnostics;
using Integration.Shared.Messaging;
using Integration.Shared.Persistence;
using Integration.Shared.Persistence.Entities;
using Integration.Shared.Runtime;
using Integration.Worker.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Integration.Worker.Consuming;

/// <summary>What the consumer should do after AcceptAsync completes or fails.</summary>
public enum AcceptOutcome
{
    /// <summary>Inbox receipt (and job if new) committed durably. Safe to ACK.</summary>
    Accepted,

    /// <summary>Durable quarantine row + dead-letter outbox committed. Safe to ACK.</summary>
    Quarantined,

    /// <summary>Nothing durable was written. Do NOT ACK; reconnect and let the broker redeliver.</summary>
    Failed,
}

/// <summary>
/// The durable inbox. One transaction turns a delivery into either (receipt + job) or
/// (quarantine row + dead-letter outbox). Only once that transaction commits may the
/// consumer ACK - which is why "acked" means "durably accepted", never "business done".
///
/// Duplicate rules:
///  - same transport MessageId, same payload hash: replay, no second job, ACK;
///  - different transport MessageId, same EventId, same hash: extra receipt, no second job, ACK;
///  - same identity, different payload: quarantine, the original job is never touched, ACK.
/// </summary>
public sealed class InboxAcceptor
{
    private readonly LabDbContext _context;
    private readonly IDbContextFactory<LabDbContext> _contextFactory;
    private readonly MessageValidator _validator;
    private readonly DeadLetterWriter _deadLetterWriter;
    private readonly LabOptions _options;
    private readonly ILogger<InboxAcceptor> _logger;

    public InboxAcceptor(
        LabDbContext context,
        IDbContextFactory<LabDbContext> contextFactory,
        MessageValidator validator,
        DeadLetterWriter deadLetterWriter,
        IOptions<LabOptions> options,
        ILogger<InboxAcceptor> logger)
    {
        _context = context;
        _contextFactory = contextFactory;
        _validator = validator;
        _deadLetterWriter = deadLetterWriter;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AcceptOutcome> AcceptAsync(
        string? transportId,
        ReadOnlyMemory<byte> body,
        IReadOnlyBasicProperties properties,
        CancellationToken cancellationToken)
    {
        // Structural validation first: pure, cheap, reason-coded.
        var validation = _validator.Validate(transportId, body);
        if (!validation.Valid)
        {
            await QuarantineAsync(
                transportId,
                body,
                validation.ReasonCode!,
                requestId: null,
                eventId: null,
                cancellationToken).ConfigureAwait(false);
            return AcceptOutcome.Quarantined;
        }

        var delivery = validation.Delivery!;
        var payloadHash = delivery.PayloadHash;

        // Source check: this lab only accepts events its own API produced.
        var source = await _context.ExportRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == delivery.Envelope.EventId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            await QuarantineAsync(
                transportId,
                body,
                RejectionReason.UnknownSourceRequest,
                delivery.Envelope.EventId,
                delivery.Envelope.EventId,
                cancellationToken).ConfigureAwait(false);
            return AcceptOutcome.Quarantined;
        }

        if (!string.Equals(source.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            await QuarantineAsync(
                transportId,
                body,
                RejectionReason.SourceHashMismatch,
                delivery.Envelope.EventId,
                delivery.Envelope.EventId,
                cancellationToken).ConfigureAwait(false);
            return AcceptOutcome.Quarantined;
        }

        var (traceParent, traceState) = MessageProperties.ReadTraceContext(properties);

        using var activity = LabTelemetry.StartLinkedTo(
            LabTelemetry.Spans.InboxPersist,
            traceParent,
            traceState);
        activity?.SetTag(LabTelemetry.Tags.RequestId, delivery.Request.RequestId.ToString());
        activity?.SetTag(LabTelemetry.Tags.EventId, delivery.Envelope.EventId.ToString());
        activity?.SetTag(LabTelemetry.Tags.TransportMessageId, transportId);

        try
        {
            await using var transaction = await _context.Database
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var existingReceipt = await _context.InboxReceipts
                .SingleOrDefaultAsync(
                    x => x.ConsumerName == _options.ConsumerName && x.TransportMessageId == transportId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existingReceipt is not null)
            {
                if (string.Equals(existingReceipt.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    // Same delivery again (redelivery after a lost ACK): the receipt already
                    // exists, the job already exists. Nothing to write.
                    _logger.LogInformation(
                        "Duplicate delivery of transport message {TransportMessageId} for event {EventId}; no new job.",
                        transportId,
                        delivery.Envelope.EventId);
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return AcceptOutcome.Accepted;
                }

                // Same transport identity carrying different content: quarantine, original untouched.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                await QuarantineAsync(
                    transportId,
                    body,
                    RejectionReason.IdentityPayloadMismatch,
                    delivery.Envelope.EventId,
                    delivery.Envelope.EventId,
                    cancellationToken).ConfigureAwait(false);
                return AcceptOutcome.Quarantined;
            }

            var existingJob = await _context.IntegrationJobs
                .FindAsync([delivery.Envelope.EventId], cancellationToken).ConfigureAwait(false);

            if (existingJob is not null
                && !string.Equals(existingJob.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                // Same EventId, different content: the original job must not be modified.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                await QuarantineAsync(
                    transportId,
                    body,
                    RejectionReason.IdentityPayloadMismatch,
                    delivery.Envelope.EventId,
                    delivery.Envelope.EventId,
                    cancellationToken).ConfigureAwait(false);
                return AcceptOutcome.Quarantined;
            }

            if (existingJob is null)
            {
                _context.IntegrationJobs.Add(new IntegrationJob
                {
                    RequestId = delivery.Envelope.EventId,
                    PayloadHash = payloadHash,
                    Status = JobStatus.Pending,
                    AttemptsStarted = 0,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    TraceParent = traceParent,
                    TraceState = traceState,
                });
            }

            _context.InboxReceipts.Add(new InboxReceipt
            {
                ConsumerName = _options.ConsumerName,
                TransportMessageId = transportId!,
                EventId = delivery.Envelope.EventId,
                PayloadHash = payloadHash,
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                IntegrationJobId = delivery.Envelope.EventId,
            });

            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Accepted delivery {TransportMessageId} for event {EventId}; job {JobState}.",
                transportId,
                delivery.Envelope.EventId,
                existingJob is null ? JobStatus.Pending : existingJob.Status);
            return AcceptOutcome.Accepted;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two deliveries of the same message raced on the receipt primary key.
            // The failed transaction's context is dead; a fresh context reads the winner.
            _logger.LogInformation(
                "Receipt race for transport message {TransportMessageId}; resolving with a fresh context.",
                transportId);
            return await ResolveDuplicateRaceAsync(transportId!, payloadHash, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AcceptOutcome> ResolveDuplicateRaceAsync(
        string transportId,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        await using var fresh = await _contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await fresh.InboxReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.ConsumerName == _options.ConsumerName && x.TransportMessageId == transportId,
                cancellationToken)
            .ConfigureAwait(false);

        if (receipt is not null)
        {
            if (string.Equals(receipt.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return AcceptOutcome.Accepted;
            }

            await QuarantineAsync(transportId, default, RejectionReason.IdentityPayloadMismatch, receipt.EventId, receipt.EventId, cancellationToken).ConfigureAwait(false);
            return AcceptOutcome.Quarantined;
        }

        // The winner rolled back or the row vanished: treat as failure, do not ACK;
        // redelivery re-runs the whole acceptance logic.
        return AcceptOutcome.Failed;
    }

    /// <summary>
    /// Writes the quarantine row and its dead-letter outbox row in one transaction.
    /// The fingerprint deduplicates repeat deliveries of the same poisoned message, so a
    /// redelivered poison message never creates a second quarantine row or second DLQ event.
    /// </summary>
    private async Task QuarantineAsync(
        string? transportId,
        ReadOnlyMemory<byte> body,
        string reasonCode,
        Guid? requestId,
        Guid? eventId,
        CancellationToken cancellationToken)
    {
        var bodySha256 = PayloadHash.OfBytes(body.Span);
        var fingerprint = ComputeFingerprint(transportId, bodySha256, reasonCode);

        _logger.LogWarning(
            "Quarantining delivery {TransportMessageId}: reason {ReasonCode}, body sha256 {BodySha256}, {BodyLength} bytes.",
            transportId ?? "<missing>",
            reasonCode,
            bodySha256,
            body.Length);

        var existing = await _context.RejectedMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Fingerprint == fingerprint, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Poison fingerprint {Fingerprint} already quarantined; not duplicating quarantine or DLQ event.",
                fingerprint);
            return;
        }

        await using var transaction = await _context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var rejection = new RejectedMessage
        {
            Id = Guid.NewGuid(),
            Fingerprint = fingerprint,
            TransportMessageId = transportId is null ? null : Truncate(transportId, 128),
            BodySha256 = bodySha256,
            BodyLength = body.Length,
            ReasonCode = reasonCode,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
        };
        _context.RejectedMessages.Add(rejection);

        var envelope = new DeadLetterEnvelope(
            requestId,
            eventId,
            reasonCode,
            reasonCode,
            AttemptsStarted: 0,
            DateTimeOffset.UtcNow,
            bodySha256,
            body.Length,
            Data: null);

        await _deadLetterWriter
            .WriteForRejectionAsync(_context, rejection, envelope, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent delivery quarantined the same fingerprint first. Fine.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string ComputeFingerprint(string? transportId, string bodySha256, string reasonCode)
    {
        var canonical = $"{transportId ?? "<missing>"}|{bodySha256}|{reasonCode}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
