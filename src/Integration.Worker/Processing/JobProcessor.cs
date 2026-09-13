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

namespace Integration.Worker.Processing;

/// <summary>
/// The durable retry state machine. Claim, attempt, result and schedule all live in SQL:
/// there is no in-memory queue and no Task.Delay-held retry state, so a worker restart
/// changes nothing about a job's budget or schedule.
///
/// A claim whose budget is already exhausted (the previous owner crashed after incrementing
/// AttemptsStarted) goes straight to the terminal state - it never opens a sixth external call.
/// Late results from an owner that lost its lease cannot overwrite the new owner's state:
/// every update is guarded by AND LeaseToken = @token.
/// </summary>
public sealed class JobProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ErpClient _erpClient;
    private readonly RabbitConnection _connection;
    private readonly IFaultHooks _faultHooks;
    private readonly LabOptions _options;
    private readonly ILogger<JobProcessor> _logger;
    private readonly TimeProvider _timeProvider;

    public JobProcessor(
        IServiceScopeFactory scopeFactory,
        ErpClient erpClient,
        RabbitConnection connection,
        IFaultHooks faultHooks,
        IOptions<LabOptions> options,
        ILogger<JobProcessor> logger,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _erpClient = erpClient;
        _connection = connection;
        _faultHooks = faultHooks;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = _connection.CreateBackoff();
        _logger.LogInformation("Job processor running.");

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

                if (claimed.BudgetExhausted)
                {
                    _logger.LogWarning(
                        "Job {RequestId} claimed with an exhausted budget (attempts {Attempts}); going terminal without another HTTP call.",
                        claimed.RequestId,
                        claimed.AttemptsStarted);
                    // The inherited attempt was started by an owner that never came back; it is
                    // closed as Abandoned in the same transaction as the terminal transition.
                    await WriteTerminalAsync(
                        scope,
                        claimed,
                        SafeErrors.AttemptBudgetExhausted,
                        "attempt_budget_exhausted",
                        AttemptOutcome.Abandoned,
                        SafeErrors.AttemptBudgetExhausted,
                        stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var result = await RunAttemptAsync(scope, claimed, stoppingToken).ConfigureAwait(false);
                await ApplyResultAsync(scope, claimed, result, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Job processing iteration failed: {Error}. Backing off; leases recover in-flight jobs.",
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

        _logger.LogInformation("Job processor stopped; in-flight jobs recover through their leases.");
    }

    private async Task<ClaimedJob?> ClaimNextAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        var jobStore = scope.ServiceProvider.GetRequiredService<JobStore>();
        var claimed = await jobStore.TryClaimAsync(Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        if (claimed is not null)
        {
            _logger.LogDebug(
                "Claimed job {RequestId} (attempt {AttemptNumber} of max {Max}).",
                claimed.RequestId,
                claimed.AttemptNumber,
                _options.MaxJobAttempts);
        }

        return claimed;
    }

    private async Task<ErpResult> RunAttemptAsync(IServiceScope scope, ClaimedJob claimed, CancellationToken cancellationToken)
    {
        var context = scope.ServiceProvider.GetRequiredService<LabDbContext>();
        var record = await context.ExportRequests
            .AsNoTracking()
            .AsSingleQuery()
            .SingleAsync(x => x.RequestId == claimed.RequestId, cancellationToken)
            .ConfigureAwait(false);
        var request = new ExportRequest(
            record.RequestId,
            record.ExternalReference,
            record.Amount,
            record.Currency);

        using var activity = LabTelemetry.StartLinkedTo(
            LabTelemetry.Spans.ExportHttpAttempt,
            claimed.TraceParent,
            claimed.TraceState);
        activity?.SetTag(LabTelemetry.Tags.RequestId, claimed.RequestId.ToString());
        activity?.SetTag(LabTelemetry.Tags.AttemptNumber, claimed.AttemptNumber);

        // Crash window the tests exercise: the claim (and its attempt counter) is committed,
        // the external call has not started yet.
        await _faultHooks.ReachAsync(FaultPoints.JobAfterClaim, cancellationToken).ConfigureAwait(false);

        var result = await _erpClient.ApplyAsync(request, cancellationToken).ConfigureAwait(false);

        // Crash window: the external call answered, the job row knows nothing yet.
        await _faultHooks.ReachAsync(FaultPoints.JobAfterHttpBeforeUpdate, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task ApplyResultAsync(
        IServiceScope scope,
        ClaimedJob claimed,
        ErpResult result,
        CancellationToken cancellationToken)
    {
        var jobStore = scope.ServiceProvider.GetRequiredService<JobStore>();

        if (result.Succeeded)
        {
            // One guarded unit: job state AND attempt result, or neither.
            var completed = await jobStore
                .CompleteAsync(claimed.RequestId, claimed.Token, result.ReceiptId!, claimed.AttemptNumber, cancellationToken)
                .ConfigureAwait(false);
            if (completed)
            {
                _logger.LogInformation(
                    "Job {RequestId} completed with receipt {ReceiptId} after {Attempts} attempt(s).",
                    claimed.RequestId,
                    result.ReceiptId,
                    claimed.AttemptNumber);
            }
            else
            {
                _logger.LogWarning(
                    "Job {RequestId} completion lost its lease; the current owner owns the state.",
                    claimed.RequestId);
            }

            return;
        }

        if (result.Retryable)
        {
            if (claimed.AttemptNumber >= _options.MaxJobAttempts)
            {
                _logger.LogWarning(
                    "Job {RequestId} exhausted its attempt budget ({Attempts}); going terminal.",
                    claimed.RequestId,
                    claimed.AttemptNumber);
                await WriteTerminalAsync(
                    scope,
                    claimed,
                    SafeErrors.AttemptBudgetExhausted,
                    "attempt_budget_exhausted",
                    AttemptOutcome.TransientFailure,
                    result.ErrorCode,
                    cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var delay = RetryPolicy.DelayFor(claimed.AttemptNumber, result.RetryAfter, _options);
            var rescheduled = await jobStore
                .ScheduleRetryAsync(
                    claimed.RequestId, claimed.Token, result.ErrorCode!, delay, claimed.AttemptNumber, cancellationToken)
                .ConfigureAwait(false);
            if (rescheduled)
            {
                _logger.LogWarning(
                    "Job {RequestId} transient failure ({ErrorCode}); retry scheduled in {Delay}s.",
                    claimed.RequestId,
                    result.ErrorCode,
                    delay.TotalSeconds);
            }
            else
            {
                _logger.LogWarning(
                    "Job {RequestId} retry scheduling lost its lease; the current owner owns the state.",
                    claimed.RequestId);
            }

            return;
        }

        _logger.LogWarning(
            "Job {RequestId} permanent failure ({ErrorCode}); going terminal.",
            claimed.RequestId,
            result.ErrorCode);
        await WriteTerminalAsync(
            scope,
            claimed,
            result.ErrorCode!,
            "permanent_failure",
            AttemptOutcome.PermanentFailure,
            result.ErrorCode,
            cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Terminal transition: job to DeadLetterPending, its ATTEMPT RESULT and its dead-letter
    /// outbox row in ONE transaction, guarded by the lease token. "Dead-lettered" is only
    /// claimed after the DLQ publish is confirmed (the OutboxDispatcher closes that loop).
    /// </summary>
    private async Task WriteTerminalAsync(
        IServiceScope scope,
        ClaimedJob claimed,
        string errorCode,
        string reason,
        string attemptOutcome,
        string? attemptErrorCode,
        CancellationToken cancellationToken)
    {
        var context = scope.ServiceProvider.GetRequiredService<LabDbContext>();
        var jobStore = scope.ServiceProvider.GetRequiredService<JobStore>();
        var deadLetterWriter = scope.ServiceProvider.GetRequiredService<DeadLetterWriter>();

        var record = await context.ExportRequests
            .AsNoTracking()
            .SingleAsync(x => x.RequestId == claimed.RequestId, cancellationToken)
            .ConfigureAwait(false);

        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var moved = await jobStore
            .MoveToDeadLetterPendingAsync(
                claimed.RequestId,
                claimed.Token,
                errorCode,
                claimed.AttemptNumber,
                attemptOutcome,
                attemptErrorCode,
                cancellationToken)
            .ConfigureAwait(false);
        if (!moved)
        {
            _logger.LogWarning(
                "Job {RequestId} terminal write lost its lease; the current owner owns the state.",
                claimed.RequestId);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await deadLetterWriter
            .WriteForJobAsync(context, claimed, record, reason, errorCode, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning(
            "Job {RequestId} moved to DeadLetterPending ({ErrorCode}); dead-letter publish is pending.",
            claimed.RequestId,
            errorCode);
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
