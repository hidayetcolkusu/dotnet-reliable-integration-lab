using System.Text.Json;
using Integration.Api.Errors;
using Integration.Shared.Contracts;
using Integration.Shared.Diagnostics;
using Integration.Shared.Messaging;
using Integration.Shared.Persistence;
using Integration.Shared.Persistence.Entities;
using Integration.Shared.Runtime;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Integration.Api.Exports;

/// <summary>
/// Writes the accepted request and its Export outbox row in ONE SQL transaction.
/// The broker is not involved here at all: when RabbitMQ is down, the accepted work
/// simply waits in SQL until the dispatcher can publish it.
/// </summary>
public sealed class SubmitExportHandler
{
    private readonly LabDbContext _context;
    private readonly IDbContextFactory<LabDbContext> _contextFactory;
    private readonly Topology _topology;
    private readonly IFaultHooks _faultHooks;

    public SubmitExportHandler(
        LabDbContext context,
        IDbContextFactory<LabDbContext> contextFactory,
        Topology topology,
        IFaultHooks faultHooks)
    {
        _context = context;
        _contextFactory = contextFactory;
        _topology = topology;
        _faultHooks = faultHooks;
    }

    public async Task<StoredAcceptance> HandleAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payloadHash = PayloadHash.Compute(request);
        var now = DateTimeOffset.UtcNow;
        var (traceParent, traceState) = LabTelemetry.CaptureContext();

        var envelope = new ExportEnvelope(
            request.RequestId,
            MessageTypes.OrderExportRequested,
            MessageTypes.SchemaVersion,
            now,
            request);
        var body = JsonSerializer.Serialize(envelope, LabJson.Options);

        try
        {
            await using var transaction = await _context.Database
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            _context.ExportRequests.Add(new ExportRecord
            {
                RequestId = request.RequestId,
                PayloadHash = payloadHash,
                ExternalReference = request.ExternalReference.Trim(),
                Amount = request.Amount,
                Currency = request.Currency,
                CreatedAtUtc = now,
            });
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Crash window the tests exercise: after the request insert, before the outbox insert.
            await _faultHooks.ReachAsync(FaultPoints.ApiAfterRequestInsert, cancellationToken).ConfigureAwait(false);

            _context.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Kind = OutboxKind.Export,
                SourceRequestId = request.RequestId,
                RejectionId = null,
                Body = body,
                Exchange = _topology.EventsExchange,
                RoutingKey = Topology.ExportRoutingKey,
                CreatedAtUtc = now,
                Status = OutboxStatus.Pending,
                NextAttemptAtUtc = now,
                TraceParent = traceParent,
                TraceState = traceState,
            });
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The transaction and its context are dead after the unique violation. A fresh
            // context reads what actually got committed and compares payload hashes.
            await using var fresh = await _contextFactory
                .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = await fresh.ExportRequests
                .AsNoTracking()
                .SingleAsync(r => r.RequestId == request.RequestId, cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                throw new ApiException(
                    409,
                    "duplicate_conflict",
                    $"requestId {request.RequestId} was already accepted with a different payload. " +
                    $"Stored hash {existing.PayloadHash}, received hash {payloadHash}.");
            }

            // Same requestId, same normalized payload: replay of an acceptance, not new work.
            return Acceptance(request.RequestId);
        }

        return Acceptance(request.RequestId);
    }

    private static StoredAcceptance Acceptance(Guid requestId) =>
        new(requestId, $"/api/exports/{requestId}");

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
