using System.Data;
using Integration.Shared.Persistence.Entities;
using Integration.Shared.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Integration.Shared.Persistence;

/// <summary>
/// A claimed job plus everything the processor needs to run one attempt.
/// <paramref name="BudgetExhausted"/> means the attempt budget was already spent before this
/// claim - typically because the previous owner crashed after incrementing. Such a claim must
/// go straight to the terminal state and must NOT open another external call.
/// </summary>
public sealed record ClaimedJob(
    Guid RequestId,
    string PayloadHash,
    int AttemptsStarted,
    int AttemptNumber,
    bool BudgetExhausted,
    Guid Token,
    DateTimeOffset LeaseUntilUtc,
    string? TraceParent,
    string? TraceState);

/// <summary>
/// Atomic claim and owner-guarded state transitions for IntegrationJobs.
/// The retry state lives only here: there is no in-memory queue and no hidden HttpClient retry.
/// </summary>
public sealed class JobStore
{
    private const string ClaimSql = """
        ;WITH candidate AS (
            SELECT TOP (1) *
            FROM IntegrationJobs WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE (Status = 'Pending' AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= SYSUTCDATETIME()))
               OR (Status = 'RetryScheduled' AND NextAttemptAtUtc <= SYSUTCDATETIME())
               OR (Status = 'Processing' AND LeaseUntilUtc < SYSUTCDATETIME())
            ORDER BY NextAttemptAtUtc, CreatedAtUtc, RequestId
        )
        UPDATE candidate
        SET Status = 'Processing',
            LeaseToken = @token,
            LeaseUntilUtc = DATEADD(second, @leaseSeconds, SYSUTCDATETIME())
        OUTPUT inserted.RequestId, inserted.PayloadHash, inserted.AttemptsStarted,
               inserted.LeaseUntilUtc, inserted.TraceParent, inserted.TraceState;
        """;

    /// <summary>
    /// Starting an attempt also closes any attempt still marked Started.
    ///
    /// Reaching this point means a NEW owner holds the lease, so any earlier open attempt
    /// belongs to an owner that never came back - its outcome will never be learned. Recording
    /// that as <see cref="AttemptOutcome.Abandoned"/> is the honest history; leaving it Started
    /// forever would make "an attempt is in flight right now" unreadable from the table.
    /// Runs inside the claim transaction, so it is atomic with the counter and the new row.
    /// </summary>
    private const string StartAttemptSql = """
        UPDATE IntegrationJobs
        SET AttemptsStarted = AttemptsStarted + 1
        WHERE RequestId = @id AND LeaseToken = @token;

        UPDATE JobAttempts
        SET Outcome = 'Abandoned',
            FinishedAtUtc = SYSUTCDATETIME()
        WHERE RequestId = @id AND Outcome = 'Started';

        INSERT INTO JobAttempts (RequestId, AttemptNumber, StartedAtUtc, Outcome)
        VALUES (@id, @attemptNumber, SYSUTCDATETIME(), 'Started');
        """;

    /// <summary>
    /// Closes the attempt row, but ONLY when the job transition above it succeeded and only
    /// while that attempt is still open.
    ///
    /// <c>IF @@ROWCOUNT = 1</c> is what binds the two writes together: a stale owner whose job
    /// update matched nothing also writes no attempt result, so it cannot rewrite the history
    /// of the attempt the current owner is running. <c>AND Outcome = 'Started'</c> protects an
    /// attempt that was already closed - a reclaim must not overwrite a recorded outcome.
    /// The whole batch is one statement to SQL Server and runs inside the caller's
    /// transaction, so a crash between the two writes rolls both back.
    /// </summary>
    private const string FinishAttemptWhenGuardMatchedSql = """

        IF @@ROWCOUNT = 1
        BEGIN
            UPDATE JobAttempts
            SET FinishedAtUtc = SYSUTCDATETIME(),
                Outcome = @outcome,
                SafeErrorCode = @attemptErrorCode
            WHERE RequestId = @id AND AttemptNumber = @attemptNumber AND Outcome = 'Started';

            SELECT 1;
        END
        ELSE
        BEGIN
            SELECT 0;
        END
        """;

    private const string CompleteSql = """
        UPDATE IntegrationJobs
        SET Status = 'Completed',
            CompletedAtUtc = SYSUTCDATETIME(),
            ExternalReceiptId = @receiptId,
            LastErrorCode = NULL,
            NextAttemptAtUtc = NULL,
            LeaseToken = NULL,
            LeaseUntilUtc = NULL
        WHERE RequestId = @id AND LeaseToken = @token AND Status = 'Processing';
        """ + FinishAttemptWhenGuardMatchedSql;

    private const string ScheduleRetrySql = """
        UPDATE IntegrationJobs
        SET Status = 'RetryScheduled',
            NextAttemptAtUtc = DATEADD(second, @delaySeconds, SYSUTCDATETIME()),
            LastErrorCode = @errorCode,
            LeaseToken = NULL,
            LeaseUntilUtc = NULL
        WHERE RequestId = @id AND LeaseToken = @token AND Status = 'Processing';
        """ + FinishAttemptWhenGuardMatchedSql;

    private const string TerminalSql = """
        UPDATE IntegrationJobs
        SET Status = 'DeadLetterPending',
            LastErrorCode = @errorCode,
            NextAttemptAtUtc = NULL,
            LeaseToken = NULL,
            LeaseUntilUtc = NULL
        WHERE RequestId = @id AND LeaseToken = @token AND Status = 'Processing';
        """ + FinishAttemptWhenGuardMatchedSql;

    private const string DeadLetteredSql = """
        UPDATE IntegrationJobs
        SET Status = 'DeadLettered'
        WHERE RequestId = @id AND Status = 'DeadLetterPending';
        """;

    private readonly LabDbContext _context;
    private readonly LabOptions _options;

    public JobStore(LabDbContext context, IOptions<LabOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _context = context;
        _options = options.Value;
    }

    /// <summary>
    /// Claims one due job in a short transaction. The attempt counter and the JobAttempts row
    /// move inside that same transaction, so a crash right after the claim still consumed a
    /// started attempt - which is exactly what the durable counter is supposed to record.
    /// </summary>
    public async Task<ClaimedJob?> TryClaimAsync(Guid token, CancellationToken cancellationToken)
    {
        await SqlClaim.EnsureOpenAsync(_context, cancellationToken).ConfigureAwait(false);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid requestId = Guid.Empty;
        string payloadHash = string.Empty;
        var attemptsStarted = 0;
        var leaseUntil = default(DateTimeOffset);
        string? traceParent = null;
        string? traceState = null;
        bool claimedARow;

        await using (var command = SqlClaim.CreateCommand(_context, ClaimSql))
        {
            command.Add("@token", DbType.Guid, token);
            command.Add("@leaseSeconds", DbType.Int32, _options.JobLeaseSeconds);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            claimedARow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (claimedARow)
            {
                requestId = reader.GetGuid(0);
                payloadHash = reader.GetString(1);
                attemptsStarted = reader.GetInt32(2);
                leaseUntil = reader.GetFieldValue<DateTimeOffset>(3);
                traceParent = reader.GetNullableString(4);
                traceState = reader.GetNullableString(5);
            }
        }

        // Committed only once the reader is closed: committing while a DataReader is still open
        // on the same connection throws (JobRetryTests covers the idle-worker case).
        if (!claimedARow)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        // The budget is already spent: reclaim it only to write the terminal state.
        if (attemptsStarted >= _options.MaxJobAttempts)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ClaimedJob(requestId, payloadHash, attemptsStarted, attemptsStarted, true, token, leaseUntil, traceParent, traceState);
        }

        var attemptNumber = attemptsStarted + 1;
        await using (var command = SqlClaim.CreateCommand(_context, StartAttemptSql))
        {
            command.Add("@id", DbType.Guid, requestId);
            command.Add("@token", DbType.Guid, token);
            command.Add("@attemptNumber", DbType.Int32, attemptNumber);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ClaimedJob(requestId, payloadHash, attemptNumber, attemptNumber, false, token, leaseUntil, traceParent, traceState);
    }

    /// <summary>
    /// Completes the job AND closes its attempt as one owner-guarded unit. Either the job is
    /// Completed and the attempt records <see cref="AttemptOutcome.Succeeded"/>, or neither
    /// happened: a Completed job can never be left carrying an attempt still marked Started.
    /// </summary>
    public Task<bool> CompleteAsync(
        Guid requestId,
        Guid token,
        string receiptId,
        int attemptNumber,
        CancellationToken cancellationToken) =>
        ExecuteGuardedAsync(
            CompleteSql,
            command =>
            {
                command.Add("@id", DbType.Guid, requestId);
                command.Add("@token", DbType.Guid, token);
                command.Add("@receiptId", DbType.AnsiString, receiptId);
                AddAttemptResult(command, attemptNumber, AttemptOutcome.Succeeded, null);
            },
            cancellationToken);

    /// <summary>Schedules the retry and closes the failed attempt in the same guarded unit.</summary>
    public Task<bool> ScheduleRetryAsync(
        Guid requestId,
        Guid token,
        string errorCode,
        TimeSpan delay,
        int attemptNumber,
        CancellationToken cancellationToken) =>
        ExecuteGuardedAsync(
            ScheduleRetrySql,
            command =>
            {
                command.Add("@id", DbType.Guid, requestId);
                command.Add("@token", DbType.Guid, token);
                command.Add("@delaySeconds", DbType.Int32, Math.Max(0, (int)Math.Ceiling(delay.TotalSeconds)));
                command.Add("@errorCode", DbType.AnsiString, errorCode);
                AddAttemptResult(command, attemptNumber, AttemptOutcome.TransientFailure, errorCode);
            },
            cancellationToken);

    /// <summary>
    /// Token-guarded move to DeadLetterPending, together with the attempt result. The caller
    /// wraps this and the DeadLetter outbox insert in one transaction, so a terminal job always
    /// has exactly one terminal event and exactly one closed attempt.
    ///
    /// <paramref name="attemptOutcome"/> is <see cref="AttemptOutcome.Abandoned"/> when the
    /// claim inherited an exhausted budget: that attempt was started by an owner that died, and
    /// "abandoned" is the honest record. The SQL only touches attempts still marked Started, so
    /// an attempt that really did finish keeps its own outcome.
    /// </summary>
    public Task<bool> MoveToDeadLetterPendingAsync(
        Guid requestId,
        Guid token,
        string errorCode,
        int attemptNumber,
        string attemptOutcome,
        string? attemptErrorCode,
        CancellationToken cancellationToken) =>
        ExecuteGuardedAsync(
            TerminalSql,
            command =>
            {
                command.Add("@id", DbType.Guid, requestId);
                command.Add("@token", DbType.Guid, token);
                command.Add("@errorCode", DbType.AnsiString, errorCode);
                AddAttemptResult(command, attemptNumber, attemptOutcome, attemptErrorCode);
            },
            cancellationToken);

    /// <summary>Called only after the dead-letter publish was confirmed and not returned.</summary>
    public Task<bool> MarkDeadLetteredAsync(Guid requestId, CancellationToken cancellationToken) =>
        ExecuteAffectedAsync(
            DeadLetteredSql,
            command => command.Add("@id", DbType.Guid, requestId),
            cancellationToken);

    /// <summary>
    /// An attempt number of 0 (or less) means "no attempt belongs to this transition"; the
    /// parameters still have to be bound because the batch text always mentions them.
    /// </summary>
    private static void AddAttemptResult(
        System.Data.Common.DbCommand command,
        int attemptNumber,
        string outcome,
        string? attemptErrorCode)
    {
        command.Add("@attemptNumber", DbType.Int32, attemptNumber > 0 ? attemptNumber : -1);
        command.Add("@outcome", DbType.AnsiString, outcome);
        command.Add("@attemptErrorCode", DbType.AnsiString, attemptErrorCode);
    }

    /// <summary>Diagnostics: how many jobs sit in each state right now.</summary>
    public async Task<IReadOnlyDictionary<string, int>> GetStateCountsAsync(CancellationToken cancellationToken)
    {
        var rows = await _context.IntegrationJobs
            .AsNoTracking()
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(x => x.Status, x => x.Count, StringComparer.Ordinal);
    }

    /// <summary>
    /// Runs one state transition + attempt-result batch and reports whether the owner guard
    /// matched.
    ///
    /// The batch is atomic by construction: when the caller already holds a transaction (the
    /// terminal path, which also inserts the dead-letter outbox row) it joins that one, and
    /// otherwise it opens a short transaction of its own. Either way the two writes commit
    /// together or not at all - a process killed between them leaves neither behind.
    /// </summary>
    private async Task<bool> ExecuteGuardedAsync(
        string sql,
        Action<System.Data.Common.DbCommand> configure,
        CancellationToken cancellationToken)
    {
        await SqlClaim.EnsureOpenAsync(_context, cancellationToken).ConfigureAwait(false);

        if (_context.Database.CurrentTransaction is not null)
        {
            return await RunGuardedAsync(sql, configure, cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await _context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var guardMatched = await RunGuardedAsync(sql, configure, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return guardMatched;
    }

    private async Task<bool> RunGuardedAsync(
        string sql,
        Action<System.Data.Common.DbCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = SqlClaim.CreateCommand(_context, sql);
        configure(command);
        var guardMatched = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return guardMatched is int matched && matched == 1;
    }

    /// <summary>A plain single-statement update; used where no attempt result is involved.</summary>
    private async Task<bool> ExecuteAffectedAsync(
        string sql,
        Action<System.Data.Common.DbCommand> configure,
        CancellationToken cancellationToken)
    {
        await SqlClaim.EnsureOpenAsync(_context, cancellationToken).ConfigureAwait(false);
        await using var command = SqlClaim.CreateCommand(_context, sql);
        configure(command);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }
}
