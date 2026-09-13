using System.Text.Json;
using Integration.Shared.Contracts;
using Integration.Shared.Diagnostics;
using Integration.Shared.Messaging;
using Integration.Shared.Persistence;
using Integration.Shared.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Integration.Worker.Processing;

/// <summary>
/// Creates the terminal dead-letter outbox rows. Always called INSIDE the caller's SQL
/// transaction, so "job becomes terminal" and "a dead-letter event exists" are atomic -
/// there is no window where a job is dead without its DLQ message being on the way.
///
/// Payload safety rule: the DLQ envelope carries hashes, sizes, reason codes and the
/// synthetic business summary - never a raw transport body or exception text.
/// </summary>
public sealed class DeadLetterWriter
{
    private readonly Topology _topology;

    public DeadLetterWriter(Topology topology)
    {
        _topology = topology;
    }

    /// <summary>Terminal job path: exactly one DeadLetter event per source request (filtered unique index).</summary>
    public async Task WriteForJobAsync(
        LabDbContext context,
        ClaimedJob job,
        ExportRecord record,
        string reason,
        string errorCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(record);

        var envelope = new DeadLetterEnvelope(
            job.RequestId,
            job.RequestId,
            reason,
            errorCode,
            job.AttemptsStarted,
            DateTimeOffset.UtcNow,
            job.PayloadHash,
            BodyLength: null,
            Data: new ExportRequest(record.RequestId, record.ExternalReference, record.Amount, record.Currency));

        context.OutboxMessages.Add(BuildRow(
            sourceRequestId: job.RequestId,
            rejectionId: null,
            JsonSerializer.Serialize(envelope, LabJson.Options)));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Quarantine path: exactly one DeadLetter event per rejection (filtered unique index).</summary>
    public async Task WriteForRejectionAsync(
        LabDbContext context,
        RejectedMessage rejection,
        DeadLetterEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rejection);

        context.OutboxMessages.Add(BuildRow(
            sourceRequestId: null,
            rejectionId: rejection.Id,
            JsonSerializer.Serialize(envelope, LabJson.Options)));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private OutboxMessage BuildRow(Guid? sourceRequestId, Guid? rejectionId, string body) => new()
    {
        Id = Guid.NewGuid(),
        Kind = OutboxKind.DeadLetter,
        SourceRequestId = sourceRequestId,
        RejectionId = rejectionId,
        Body = body,
        Exchange = _topology.DeadExchange,
        RoutingKey = Topology.DeadRoutingKey,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Status = OutboxStatus.Pending,
        NextAttemptAtUtc = DateTimeOffset.UtcNow,
    };
}
