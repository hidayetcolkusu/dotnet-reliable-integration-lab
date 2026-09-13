using Integration.Shared.Persistence;
using Integration.Shared.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Integration.Api.Exports;

public sealed record PublishStatusView(
    string Status,
    int PublishAttempts,
    DateTimeOffset? PublishedAtUtc,
    string? LastErrorCode);

public sealed record JobStatusView(
    string Status,
    int AttemptsStarted,
    DateTimeOffset? NextAttemptAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ExternalReceiptId,
    string? LastErrorCode);

public sealed record DeadLetterStatusView(
    string Status,
    int PublishAttempts,
    DateTimeOffset? PublishedAtUtc);

public sealed record ExportStatusResponse(
    Guid RequestId,
    string ExternalReference,
    decimal Amount,
    string Currency,
    DateTimeOffset CreatedAtUtc,
    string PayloadHash,
    PublishStatusView? Publish,
    JobStatusView? Job,
    DeadLetterStatusView? DeadLetter);

/// <summary>
/// Status is deliberately split into fields: acceptance, publish, job and dead-letter are
/// different facts. "Published" never means "Completed"; a missing inbox record means
/// job = null, not "done".
/// </summary>
public sealed class GetExportStatusHandler
{
    private readonly LabDbContext _context;

    public GetExportStatusHandler(LabDbContext context)
    {
        _context = context;
    }

    public async Task<ExportStatusResponse?> GetAsync(Guid requestId, CancellationToken cancellationToken)
    {
        var record = await _context.ExportRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == requestId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        var exportOutbox = await _context.OutboxMessages
            .AsNoTracking()
            .Where(x => x.SourceRequestId == requestId && x.Kind == OutboxKind.Export)
            .Select(x => new PublishStatusView(x.Status, x.PublishAttempts, x.PublishedAtUtc, x.LastErrorCode))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // No inbox record yet means no delivery was accepted: job is null, NOT "Completed".
        var job = await _context.IntegrationJobs
            .AsNoTracking()
            .Where(x => x.RequestId == requestId)
            .Select(x => new JobStatusView(
                x.Status,
                x.AttemptsStarted,
                x.NextAttemptAtUtc,
                x.CompletedAtUtc,
                x.ExternalReceiptId,
                x.LastErrorCode))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var deadLetter = await _context.OutboxMessages
            .AsNoTracking()
            .Where(x => x.SourceRequestId == requestId && x.Kind == OutboxKind.DeadLetter)
            .Select(x => new DeadLetterStatusView(x.Status, x.PublishAttempts, x.PublishedAtUtc))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ExportStatusResponse(
            record.RequestId,
            record.ExternalReference,
            record.Amount,
            record.Currency,
            record.CreatedAtUtc,
            record.PayloadHash,
            exportOutbox,
            job,
            deadLetter);
    }
}
