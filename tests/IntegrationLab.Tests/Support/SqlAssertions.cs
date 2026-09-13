using System.Data;
using Microsoft.Data.SqlClient;

namespace IntegrationLab.Tests.Support;

public sealed record JobSnapshot(
    string? Status,
    int AttemptsStarted,
    DateTimeOffset? NextAttemptAtUtc,
    string? ExternalReceiptId,
    string? LastErrorCode,
    Guid? LeaseToken,
    string? TraceParent);

public sealed record OutboxSnapshot(
    string? Status,
    int PublishAttempts,
    string? LastErrorCode,
    DateTimeOffset? PublishedAtUtc);

/// <summary>
/// Every assertion reads real rows by requestId through its own connection - in the
/// IntegrationLab database and in the FakeErpLab database. No shared table COUNT tricks:
/// tests only ever count rows they created themselves.
/// </summary>
public sealed class SqlAssertions(string integrationConnectionString, string fakeErpConnectionString)
{
    public Task<JobSnapshot?> GetJobAsync(Guid requestId) => QueryAsync(
        integrationConnectionString,
        "SELECT Status, AttemptsStarted, NextAttemptAtUtc, ExternalReceiptId, LastErrorCode, LeaseToken, TraceParent " +
        "FROM IntegrationJobs WHERE RequestId = @id",
        command => command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = requestId,
        ReadJob);

    public Task<string?> GetJobStateAsync(Guid requestId) => QueryAsync(
        integrationConnectionString,
        "SELECT Status FROM IntegrationJobs WHERE RequestId = @id",
        command => command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = requestId,
        reader => reader.GetString(0));

    public Task<int> CountJobsAsync(Guid requestId) => CountAsync(
        integrationConnectionString,
        "SELECT COUNT(*) FROM IntegrationJobs WHERE RequestId = @id",
        requestId);

    public Task<int> CountJobAttemptsAsync(Guid requestId) => CountAsync(
        integrationConnectionString,
        "SELECT COUNT(*) FROM JobAttempts WHERE RequestId = @id",
        requestId);

    /// <summary>Attempt rows for one request, in attempt order: outcome plus its safe error code.</summary>
    public async Task<IReadOnlyList<(int AttemptNumber, string Outcome, string? SafeErrorCode)>> GetJobAttemptsAsync(Guid requestId)
    {
        await using var connection = new SqlConnection(integrationConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT AttemptNumber, Outcome, SafeErrorCode FROM JobAttempts WHERE RequestId = @id ORDER BY AttemptNumber";
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = requestId;
        await using var reader = await command.ExecuteReaderAsync();

        var attempts = new List<(int, string, string?)>();
        while (await reader.ReadAsync())
        {
            attempts.Add((reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return attempts;
    }

    public Task<int> CountExportRequestsAsync(Guid requestId) => CountAsync(
        integrationConnectionString,
        "SELECT COUNT(*) FROM ExportRequests WHERE RequestId = @id",
        requestId);

    public Task<int> CountInboxReceiptsAsync(Guid eventId) => CountAsync(
        integrationConnectionString,
        "SELECT COUNT(*) FROM InboxReceipts WHERE EventId = @id",
        eventId);

    public Task<OutboxSnapshot?> GetOutboxAsync(Guid requestId, string kind) => QueryAsync(
        integrationConnectionString,
        "SELECT Status, PublishAttempts, LastErrorCode, PublishedAtUtc FROM OutboxMessages " +
        "WHERE SourceRequestId = @id AND Kind = @kind",
        command =>
        {
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = requestId;
            command.Parameters.Add("@kind", SqlDbType.VarChar, 16).Value = kind;
        },
        reader => new OutboxSnapshot(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDateTimeOffset(3)));

    /// <summary>The trace context the API committed alongside the work, as stored in SQL.</summary>
    public Task<string?> GetOutboxTraceParentAsync(Guid requestId) => QueryAsync(
        integrationConnectionString,
        "SELECT TraceParent FROM OutboxMessages WHERE SourceRequestId = @id AND Kind = 'Export'",
        command => command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = requestId,
        reader => reader.IsDBNull(0) ? null : reader.GetString(0));

    public Task<OutboxSnapshot?> GetOutboxByIdAsync(Guid outboxId) => QueryAsync(
        integrationConnectionString,
        "SELECT Status, PublishAttempts, LastErrorCode, PublishedAtUtc FROM OutboxMessages WHERE Id = @id",
        command => command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = outboxId,
        reader => new OutboxSnapshot(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDateTimeOffset(3)));

    public Task<int> CountOutboxByRejectionAsync(Guid rejectionId) => CountPlainAsync(
        integrationConnectionString,
        "SELECT COUNT(*) FROM OutboxMessages WHERE RejectionId = @id",
        command => command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = rejectionId);

    public Task<int> CountRejectedByBodyShaAsync(string bodySha256) => CountPlainAsync(
        integrationConnectionString,
        "SELECT COUNT(*) FROM RejectedMessages WHERE BodySha256 = @sha",
        command => command.Parameters.Add("@sha", SqlDbType.VarChar, 64).Value = bodySha256);

    public Task<Guid?> GetRejectionIdAsync(string bodySha256) => QueryAsync<Guid?>(
        integrationConnectionString,
        "SELECT Id FROM RejectedMessages WHERE BodySha256 = @sha",
        command => command.Parameters.Add("@sha", SqlDbType.VarChar, 64).Value = bodySha256,
        reader => reader.GetGuid(0));

    /// <summary>Null until the quarantine's dead-letter event was confirmed by the broker.</summary>
    public Task<DateTimeOffset?> GetRejectionPublishedAtAsync(Guid rejectionId) => QueryAsync<DateTimeOffset?>(
        integrationConnectionString,
        "SELECT DeadLetterPublishedAtUtc FROM RejectedMessages WHERE Id = @id",
        command => command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = rejectionId,
        reader => reader.IsDBNull(0) ? null : reader.GetDateTimeOffset(0));

    public Task<string?> GetRejectedReasonAsync(string bodySha256) => QueryAsync(
        integrationConnectionString,
        "SELECT ReasonCode FROM RejectedMessages WHERE BodySha256 = @sha",
        command => command.Parameters.Add("@sha", SqlDbType.VarChar, 64).Value = bodySha256,
        reader => reader.GetString(0));

    public Task<int> CountAppliedExportsAsync(Guid requestId) => CountAsync(
        fakeErpConnectionString,
        "SELECT COUNT(*) FROM AppliedExports WHERE OperationKey = @id",
        requestId);

    public Task<string?> GetAppliedReceiptAsync(Guid requestId) => QueryAsync(
        fakeErpConnectionString,
        "SELECT ExternalReceiptId FROM AppliedExports WHERE OperationKey = @id",
        command => command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = requestId,
        reader => reader.GetString(0));

    /// <summary>
    /// Removes abandoned, not-yet-published outbox rows left behind by earlier tests in the
    /// shared database. Called at the START of tests whose assertions depend on dispatch
    /// order or attempt counts; never during a test's own flow.
    /// </summary>
    public Task CleanupAbandonedOutboxAsync() => ExecuteAsync(
        useErpDatabase: false,
        "DELETE FROM OutboxMessages WHERE Status IN ('Pending', 'Publishing')",
        []);

    /// <summary>
    /// Parks every job left unfinished by an earlier test far in the future instead of deleting
    /// it, so a worker under test claims only the row that test created.
    ///
    /// Without this, a job-focused test shares its worker with a backlog of jobs whose FakeErp
    /// host is long gone: each of those burns a full HTTP timeout, and the row under test
    /// advances one attempt every several seconds. Parking changes only the SCHEDULE of
    /// unrelated rows - no attempt counter, status or result is touched.
    /// </summary>
    public Task ParkAbandonedJobsAsync() => ExecuteAsync(
        useErpDatabase: false,
        "UPDATE IntegrationJobs SET NextAttemptAtUtc = DATEADD(hour, 24, SYSUTCDATETIME()), " +
        "Status = CASE WHEN Status = 'Processing' THEN 'RetryScheduled' ELSE Status END, " +
        "LeaseToken = NULL, LeaseUntilUtc = NULL " +
        "WHERE Status IN ('Pending', 'Processing', 'RetryScheduled')",
        []);

    /// <summary>
    /// Attempt numbers with their start and finish times, relative to the first attempt.
    /// A timing gap here is what separates "the retry schedule is wrong" from "the external
    /// call itself was slow" when an eventual-consistency wait times out.
    /// </summary>
    public async Task<string> GetJobAttemptTimingsAsync(Guid requestId)
    {
        await using var connection = new SqlConnection(integrationConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT AttemptNumber, StartedAtUtc, FinishedAtUtc, Outcome, SafeErrorCode FROM JobAttempts " +
            "WHERE RequestId = @id ORDER BY AttemptNumber";
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = requestId;
        await using var reader = await command.ExecuteReaderAsync();

        var parts = new List<string>();
        DateTimeOffset? first = null;
        while (await reader.ReadAsync())
        {
            var started = reader.GetDateTimeOffset(1);
            first ??= started;
            var finished = reader.IsDBNull(2) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(2);
            var startOffset = (started - first.Value).TotalSeconds;
            var duration = finished is null ? "unfinished" : $"{(finished.Value - started).TotalSeconds:0.0}s";
            var errorCode = reader.IsDBNull(4) ? "-" : reader.GetString(4);
            parts.Add($"#{reader.GetInt32(0)} +{startOffset:0.0}s {duration} {reader.GetString(3)}/{errorCode}");
        }

        return string.Join("; ", parts);
    }

    /// <summary>Diagnostics for eventual-consistency timeout failures: safe, requestId-scoped state.</summary>
    public async Task<string> DescribeAsync(Guid requestId)
    {
        var job = await GetJobAsync(requestId);
        var exportOutbox = await GetOutboxAsync(requestId, "Export");
        var deadLetterOutbox = await GetOutboxAsync(requestId, "DeadLetter");
        var receipts = await CountInboxReceiptsAsync(requestId);
        var applied = await CountAppliedExportsAsync(requestId);
        var jobText = job is null
            ? "none"
            : $"{job.Status}/attempts={job.AttemptsStarted}/error={job.LastErrorCode ?? "-"}";
        var attempts = await GetJobAttemptTimingsAsync(requestId);
        var exportText = exportOutbox is null
            ? "none"
            : $"{exportOutbox.Status}/tries={exportOutbox.PublishAttempts}/error={exportOutbox.LastErrorCode ?? "-"}";
        var deadText = deadLetterOutbox is null ? "none" : deadLetterOutbox.Status;
        return
            $"requestId={requestId}: receipts={receipts}, " +
            $"attemptTimings=[{attempts}], " +
            $"job={jobText}, " +
            $"outbox(Export)={exportText}, " +
            $"outbox(DeadLetter)={deadText}, " +
            $"appliedExports={applied}";
    }

    /// <summary>Direct SQL for schema-level constraint tests: the invariant must hold even when handlers are bypassed.</summary>
    public Task<int> ExecuteAsync(bool useErpDatabase, string sql, params (string Name, SqlDbType Type, object? Value)[] parameters) =>
        ExecuteCoreAsync(useErpDatabase ? fakeErpConnectionString : integrationConnectionString, sql, parameters);

    public Task<object?> ExecuteScalarAsync(bool useErpDatabase, string sql, params (string Name, SqlDbType Type, object? Value)[] parameters) =>
        ExecuteScalarCoreAsync(useErpDatabase ? fakeErpConnectionString : integrationConnectionString, sql, parameters);

    /// <summary>Runs an action expecting a specific SQL Server error number; returns false when no error surfaced.</summary>
    public async Task<bool> ExpectSqlErrorAsync(bool useErpDatabase, int expectedError, string sql, params (string Name, SqlDbType Type, object? Value)[] parameters)
    {
        try
        {
            await ExecuteAsync(useErpDatabase, sql, parameters);
            return false;
        }
        catch (SqlException ex) when (ex.Number == expectedError)
        {
            return true;
        }
    }

    private static JobSnapshot? ReadJob(SqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt32(1),
        reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetGuid(5),
        reader.IsDBNull(6) ? null : reader.GetString(6));

    private static async Task<int> CountAsync(string connectionString, string sql, Guid id) =>
        (int)(await ExecuteScalarCoreAsync(
            connectionString,
            sql,
            [("id", SqlDbType.UniqueIdentifier, id)]) ?? 0L)!;

    private static async Task<int> CountPlainAsync(string connectionString, string sql, Action<SqlCommand> configure)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        return (int)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<T?> QueryAsync<T>(string connectionString, string sql, Action<SqlCommand> configure, Func<SqlDataReader, T> project)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? project(reader) : default;
    }

    private static async Task<int> ExecuteCoreAsync(string connectionString, string sql, (string Name, SqlDbType Type, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, type, value) in parameters)
        {
            var parameter = command.Parameters.Add(name, type);
            parameter.Value = value ?? DBNull.Value;
        }

        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ExecuteScalarCoreAsync(string connectionString, string sql, (string Name, SqlDbType Type, object? Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, type, value) in parameters)
        {
            var parameter = command.Parameters.Add(name, type);
            parameter.Value = value ?? DBNull.Value;
        }

        return await command.ExecuteScalarAsync();
    }
}
