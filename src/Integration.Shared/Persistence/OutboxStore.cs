using System.Data;
using Integration.Shared.Persistence.Entities;
using Integration.Shared.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Integration.Shared.Persistence;

/// <summary>
/// Atomic claim and owner-guarded result updates for OutboxMessages.
///
/// The claim runs in its own short transaction. The broker call happens outside it, so a slow
/// or dead broker never holds a SQL transaction open. Every result update carries
/// <c>AND LeaseToken = @token</c>, so a previous owner that wakes up late cannot overwrite the
/// state a new owner already wrote.
/// </summary>
public sealed class OutboxStore
{
    private const string ClaimSql = """
        ;WITH candidate AS (
            SELECT TOP (1) *
            FROM OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE (Status = 'Pending' AND NextAttemptAtUtc <= SYSUTCDATETIME())
               OR (Status = 'Publishing' AND LeaseUntilUtc < SYSUTCDATETIME())
            ORDER BY CreatedAtUtc, Id
        )
        UPDATE candidate
        SET Status = 'Publishing',
            LeaseToken = @token,
            LeaseUntilUtc = DATEADD(second, @leaseSeconds, SYSUTCDATETIME()),
            PublishAttempts = PublishAttempts + 1
        OUTPUT inserted.Id, inserted.Kind, inserted.SourceRequestId, inserted.RejectionId,
               inserted.Body, inserted.RoutingKey, inserted.Exchange, inserted.CreatedAtUtc,
               inserted.Status, inserted.PublishAttempts, inserted.NextAttemptAtUtc,
               inserted.LeaseToken, inserted.LeaseUntilUtc, inserted.PublishedAtUtc,
               inserted.LastErrorCode, inserted.TraceParent, inserted.TraceState;
        """;

    private const string MarkPublishedSql = """
        UPDATE OutboxMessages
        SET Status = 'Published',
            PublishedAtUtc = SYSUTCDATETIME(),
            LeaseToken = NULL,
            LeaseUntilUtc = NULL,
            LastErrorCode = NULL
        WHERE Id = @id AND LeaseToken = @token AND Status = 'Publishing';
        """;

    private const string ScheduleRetrySql = """
        UPDATE OutboxMessages
        SET Status = 'Pending',
            NextAttemptAtUtc = DATEADD(second, @delaySeconds, SYSUTCDATETIME()),
            LeaseToken = NULL,
            LeaseUntilUtc = NULL,
            LastErrorCode = @errorCode
        WHERE Id = @id AND LeaseToken = @token AND Status = 'Publishing';
        """;

    private const string MarkRejectionPublishedSql = """
        UPDATE RejectedMessages
        SET DeadLetterPublishedAtUtc = SYSUTCDATETIME()
        WHERE Id = @rejectionId AND DeadLetterPublishedAtUtc IS NULL;
        """;

    private readonly LabDbContext _context;
    private readonly LabOptions _options;

    public OutboxStore(LabDbContext context, IOptions<LabOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _context = context;
        _options = options.Value;
    }

    /// <summary>Claims one due message, or one whose lease expired. Returns null when there is nothing to do.</summary>
    public async Task<OutboxMessage?> TryClaimAsync(Guid token, CancellationToken cancellationToken)
    {
        await SqlClaim.EnsureOpenAsync(_context, cancellationToken).ConfigureAwait(false);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        OutboxMessage? claimed;
        await using (var command = SqlClaim.CreateCommand(_context, ClaimSql))
        {
            command.Add("@token", DbType.Guid, token);
            command.Add("@leaseSeconds", DbType.Int32, _options.OutboxLeaseSeconds);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            claimed = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new OutboxMessage
                {
                    Id = reader.GetGuid(0),
                    Kind = reader.GetString(1),
                    SourceRequestId = reader.GetNullableGuid(2),
                    RejectionId = reader.GetNullableGuid(3),
                    Body = reader.GetString(4),
                    RoutingKey = reader.GetString(5),
                    Exchange = reader.GetString(6),
                    CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(7),
                    Status = reader.GetString(8),
                    PublishAttempts = reader.GetInt32(9),
                    NextAttemptAtUtc = reader.GetFieldValue<DateTimeOffset>(10),
                    LeaseToken = reader.GetNullableGuid(11),
                    LeaseUntilUtc = reader.GetNullableDateTimeOffset(12),
                    PublishedAtUtc = reader.GetNullableDateTimeOffset(13),
                    LastErrorCode = reader.GetNullableString(14),
                    TraceParent = reader.GetNullableString(15),
                    TraceState = reader.GetNullableString(16),
                }
                : null;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    /// <summary>
    /// Marks a message published. Only the current lease owner may do so, and only from
    /// the Publishing state - a stale owner silently loses the race and gets false back.
    /// </summary>
    public async Task<bool> MarkPublishedAsync(Guid id, Guid token, CancellationToken cancellationToken)
    {
        var affected = await ExecuteAsync(
            MarkPublishedSql,
            command =>
            {
                command.Add("@id", DbType.Guid, id);
                command.Add("@token", DbType.Guid, token);
            },
            cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    /// <summary>Returns the message to Pending with a capped backoff. Its own budget, not the job's.</summary>
    public async Task<bool> ScheduleRetryAsync(
        Guid id,
        Guid token,
        string errorCode,
        int publishAttempts,
        CancellationToken cancellationToken)
    {
        var delay = _options.OutboxRetryDelay(publishAttempts);
        var affected = await ExecuteAsync(
            ScheduleRetrySql,
            command =>
            {
                command.Add("@id", DbType.Guid, id);
                command.Add("@token", DbType.Guid, token);
                command.Add("@delaySeconds", DbType.Int32, (int)Math.Ceiling(delay.TotalSeconds));
                command.Add("@errorCode", DbType.AnsiString, errorCode);
            },
            cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    /// <summary>
    /// Quarantine-linked dead letters stamp the rejection once their DLQ publish was confirmed.
    /// No job row is involved for rejections, so no FK to a job is ever written.
    /// </summary>
    public async Task<bool> MarkRejectionPublishedAsync(Guid rejectionId, CancellationToken cancellationToken)
    {
        var affected = await ExecuteAsync(
            MarkRejectionPublishedSql,
            command => command.Add("@rejectionId", DbType.Guid, rejectionId),
            cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    /// <summary>Diagnostics: how many messages are waiting and how old the oldest one is.</summary>
    public async Task<(int Pending, TimeSpan? OldestAge)> GetBacklogAsync(CancellationToken cancellationToken)
    {
        var pending = await _context.OutboxMessages
            .AsNoTracking()
            .Where(x => x.Status != OutboxStatus.Published)
            .CountAsync(cancellationToken).ConfigureAwait(false);

        if (pending == 0)
        {
            return (0, null);
        }

        var oldest = await _context.OutboxMessages
            .AsNoTracking()
            .Where(x => x.Status != OutboxStatus.Published)
            .MinAsync(x => x.CreatedAtUtc, cancellationToken).ConfigureAwait(false);

        return (pending, DateTimeOffset.UtcNow - oldest);
    }

    private async Task<int> ExecuteAsync(
        string sql,
        Action<System.Data.Common.DbCommand> configure,
        CancellationToken cancellationToken)
    {
        await SqlClaim.EnsureOpenAsync(_context, cancellationToken).ConfigureAwait(false);
        await using var command = SqlClaim.CreateCommand(_context, sql);
        configure(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
