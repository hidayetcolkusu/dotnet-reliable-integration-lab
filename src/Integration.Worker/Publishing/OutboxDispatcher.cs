using Integration.Shared.Contracts;
using Integration.Shared.Diagnostics;
using Integration.Shared.Messaging;
using Integration.Shared.Persistence;
using Integration.Shared.Persistence.Entities;
using Integration.Shared.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Integration.Worker.Publishing;

/// <summary>
/// Claims one due outbox row at a time (Export or DeadLetter kind), publishes it with
/// confirm+mandatory, and only then moves state. Broker and SQL outages are survived with
/// bounded backoff: rows simply wait in Pending/Publishing until the dependency returns.
///
/// The outbox has its own retry budget, deliberately separate from the job's HTTP budget:
/// a broker outage never consumes the five external attempts.
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfirmedPublisher _publisher;
    private readonly RabbitConnection _connection;
    private readonly IFaultHooks _faultHooks;
    private readonly LabOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly TimeProvider _timeProvider;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        IConfirmedPublisher publisher,
        RabbitConnection connection,
        IFaultHooks faultHooks,
        IOptions<LabOptions> options,
        ILogger<OutboxDispatcher> logger,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _connection = connection;
        _faultHooks = faultHooks;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = _connection.CreateBackoff();
        _logger.LogInformation("Outbox dispatcher running.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var claimed = await ClaimNextAsync(scope, stoppingToken).ConfigureAwait(false);
                if (claimed is null)
                {
                    backoff.Reset();
                    await WaitAsync(_options.PollIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                backoff.Reset();
                var outcome = await PublishClaimedAsync(claimed, stoppingToken).ConfigureAwait(false);
                if (outcome != PublishOutcome.Confirmed)
                {
                    await HandlePublishFailureAsync(scope, claimed, outcome, stoppingToken).ConfigureAwait(false);
                    await WaitAsync(backoff.NextDelay(), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await CompletePublishAsync(scope, claimed, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // SQL outage, unexpected broker failure: the loop survives, leases recover rows.
                _logger.LogWarning(
                    "Outbox dispatch iteration failed: {Error}. Backing off.",
                    SafeErrors.Describe(ex));
                try
                {
                    await WaitAsync(backoff.NextDelay(), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Outbox dispatcher stopped; no further claims are made.");
    }

    private async Task<OutboxMessage?> ClaimNextAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        var store = scope.ServiceProvider.GetRequiredService<OutboxStore>();
        var claimed = await store.TryClaimAsync(Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        if (claimed is not null)
        {
            _logger.LogDebug(
                "Claimed outbox {OutboxId} ({Kind}, attempt {Attempt}).",
                claimed.Id,
                claimed.Kind,
                claimed.PublishAttempts);
        }

        return claimed;
    }

    private async Task<PublishOutcome> PublishClaimedAsync(OutboxMessage claimed, CancellationToken cancellationToken)
    {
        // Crash window the tests exercise: claim committed, publish not yet attempted.
        await _faultHooks.ReachAsync(FaultPoints.OutboxAfterClaim, cancellationToken).ConfigureAwait(false);

        var request = new OutboxPublishRequest(
            claimed.Id,
            claimed.SourceRequestId,
            claimed.SourceRequestId ?? claimed.RejectionId ?? claimed.Id,
            claimed.Kind == OutboxKind.Export ? MessageTypes.OrderExportRequested : MessageTypes.OrderExportFailed,
            claimed.Exchange,
            claimed.RoutingKey,
            claimed.Body,
            claimed.TraceParent,
            claimed.TraceState,
            claimed.Kind == OutboxKind.Export
                ? LabTelemetry.Spans.OutboxPublish
                : LabTelemetry.Spans.DeadLetterPublish);

        var outcome = await _publisher.PublishAsync(request, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Published outbox {OutboxId} ({Kind}) with outcome {Outcome}.",
            claimed.Id,
            claimed.Kind,
            outcome);
        return outcome;
    }

    private async Task HandlePublishFailureAsync(
        IServiceScope scope,
        OutboxMessage claimed,
        PublishOutcome outcome,
        CancellationToken cancellationToken)
    {
        var errorCode = outcome switch
        {
            PublishOutcome.Returned => SafeErrors.BrokerReturned,
            PublishOutcome.Nacked => SafeErrors.BrokerNack,
            PublishOutcome.TimedOut => SafeErrors.BrokerConfirmTimeout,
            _ => SafeErrors.BrokerUnavailable,
        };

        _logger.LogWarning(
            "Outbox {OutboxId} publish outcome {Outcome} ({ErrorCode}); returning to Pending.",
            claimed.Id,
            outcome,
            errorCode);

        var store = scope.ServiceProvider.GetRequiredService<OutboxStore>();
        var token = claimed.LeaseToken ?? Guid.Empty;
        var rescheduled = await store.ScheduleRetryAsync(
            claimed.Id,
            token,
            errorCode,
            claimed.PublishAttempts,
            cancellationToken).ConfigureAwait(false);

        if (!rescheduled)
        {
            // A new owner already took over after lease expiry; this owner silently lost the race.
            _logger.LogDebug("Outbox {OutboxId} retry scheduling lost its lease; another owner owns it.", claimed.Id);
        }
    }

    private async Task CompletePublishAsync(IServiceScope scope, OutboxMessage claimed, CancellationToken cancellationToken)
    {
        // Crash window the tests exercise: broker confirmed, SQL state not yet updated.
        await _faultHooks.ReachAsync(
            claimed.Kind == OutboxKind.DeadLetter
                ? FaultPoints.DeadLetterAfterConfirmBeforeUpdate
                : FaultPoints.OutboxAfterConfirmBeforeUpdate,
            cancellationToken).ConfigureAwait(false);

        var context = scope.ServiceProvider.GetRequiredService<LabDbContext>();
        var outboxStore = scope.ServiceProvider.GetRequiredService<OutboxStore>();
        var jobStore = scope.ServiceProvider.GetRequiredService<JobStore>();
        var token = claimed.LeaseToken ?? Guid.Empty;

        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var markedPublished = await outboxStore
            .MarkPublishedAsync(claimed.Id, token, cancellationToken).ConfigureAwait(false);
        if (!markedPublished)
        {
            _logger.LogWarning(
                "Outbox {OutboxId} was confirmed but the lease was lost; the new owner will finish it.",
                claimed.Id);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (claimed.Kind == OutboxKind.DeadLetter)
        {
            if (claimed.SourceRequestId is { } requestId)
            {
                // Job-linked dead letter: the job is only DeadLettered after the DLQ publish was confirmed.
                await jobStore.MarkDeadLetteredAsync(requestId, cancellationToken).ConfigureAwait(false);
            }

            if (claimed.RejectionId is { } rejectionId)
            {
                await outboxStore.MarkRejectionPublishedAsync(rejectionId, cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Outbox {OutboxId} ({Kind}) marked Published{Terminal}.",
            claimed.Id,
            claimed.Kind,
            claimed.Kind == OutboxKind.DeadLetter ? " and terminal state closed" : string.Empty);
    }

    private async Task WaitAsync(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
}
