using Integration.Shared.Persistence;
using Integration.Shared.Persistence.Entities;
using Integration.Shared.Runtime;
using Integration.Worker.Processing;
using IntegrationLab.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace IntegrationLab.Tests;

/// <summary>
/// Task 6 acceptance: the retry state machine lives in SQL, not in memory. Transient failures
/// are retried on a durable schedule, permanent failures are not retried at all, the attempt
/// budget is counted in STARTED attempts, and a crash inside an attempt still consumes it -
/// which is precisely what keeps a crash loop from calling the external system forever.
/// </summary>
[Collection("lab")]
public sealed class JobRetryTests(LabFixture fixture)
{
    private readonly SqlAssertions _sql = fixture.CreateSql();

    /// <summary>Zero retry waits: the schedule itself is proven separately, in the pure RetryPolicy tests.</summary>
    private static string[] FastRetryArgs(params string[] extra) =>
    [
        "--Lab:PollIntervalMilliseconds=100",
        .. LabTestConfig.Indexed("Lab:OutboxRetryDelaysSeconds", 0, 0, 0, 0, 0, 0),
        .. LabTestConfig.Indexed("Lab:JobRetryDelaysSeconds", 0, 0, 0, 0),
        .. extra,
    ];

    private static (string Name, System.Data.SqlDbType Type, object? Value) P(string name, System.Data.SqlDbType type, object? value) =>
        (name, type, value);

    /// <summary>
    /// Seeds the accepted request and its job row directly, bypassing broker and inbox.
    /// The tests below are about the retry state machine, and a full transport round trip
    /// would race them: a healthy worker completes the job before the crash worker can claim it.
    /// </summary>
    private async Task SeedJobAsync(Guid requestId, string status, int attemptsStarted, bool leaseExpired)
    {
        var request = new Integration.Shared.Contracts.ExportRequest(requestId, "PO-100", 160.00m, "TRY");
        await _sql.ExecuteAsync(
            useErpDatabase: false,
            "INSERT INTO ExportRequests (RequestId, PayloadHash, ExternalReference, Amount, Currency, CreatedAtUtc) " +
            "VALUES (@id, @hash, 'PO-100', 160.00, 'TRY', SYSUTCDATETIME());" +
            "INSERT INTO IntegrationJobs (RequestId, PayloadHash, Status, AttemptsStarted, NextAttemptAtUtc, " +
            "LeaseToken, LeaseUntilUtc, CreatedAtUtc) " +
            "VALUES (@id, @hash, @status, @attempts, SYSUTCDATETIME(), @token, @leaseUntil, SYSUTCDATETIME());",
            P("id", System.Data.SqlDbType.UniqueIdentifier, requestId),
            P("hash", System.Data.SqlDbType.VarChar, Integration.Shared.Contracts.PayloadHash.Compute(request)),
            P("status", System.Data.SqlDbType.VarChar, status),
            P("attempts", System.Data.SqlDbType.Int, attemptsStarted),
            P("token", System.Data.SqlDbType.UniqueIdentifier, leaseExpired ? Guid.NewGuid() : (object?)null),
            P("leaseUntil", System.Data.SqlDbType.DateTimeOffset, leaseExpired ? DateTimeOffset.UtcNow.AddMinutes(-1) : (object?)null));
    }

    private Task WaitForJobStateAsync(Guid requestId, string[] states)
    {
        // A set, not the array: an array's Contains binds to the span overload, which cannot
        // live across the await inside the polling lambda.
        var accepted = new HashSet<string>(states, StringComparer.Ordinal);
        return Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(requestId) is { } state && accepted.Contains(state),
            timeout: TimeSpan.FromSeconds(60),
            diagnostics: () => _sql.DescribeAsync(requestId));
    }

    // ------------------------------------------------------- pure policy (no I/O, no clock)

    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(404, false)]
    [InlineData(409, false)]
    [InlineData(422, false)]
    public void StatusCodesAreClassifiedTransientOrTerminal(int statusCode, bool transient) =>
        Assert.Equal(transient, RetryPolicy.IsTransientStatus(statusCode));

    [Fact]
    public void RetryAfterIsParsedFromDeltaSecondsAndHttpDate()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(7), RetryPolicy.ParseRetryAfter("7", now));
        Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.ParseRetryAfter(now.AddSeconds(30).ToString("R"), now));

        // Anything that cannot be trusted is ignored, so the planned schedule applies instead.
        Assert.Null(RetryPolicy.ParseRetryAfter(null, now));
        Assert.Null(RetryPolicy.ParseRetryAfter("   ", now));
        Assert.Null(RetryPolicy.ParseRetryAfter("-5", now));
        Assert.Null(RetryPolicy.ParseRetryAfter("0", now));
        Assert.Null(RetryPolicy.ParseRetryAfter("soon", now));
        Assert.Null(RetryPolicy.ParseRetryAfter(now.AddSeconds(-30).ToString("R"), now));
    }

    [Fact]
    public void RetryAfterIsRespectedButNeverBeyondTheCap()
    {
        var options = new LabOptions { JobRetryDelaysSeconds = [2, 5, 15, 30], MaxRetryAfterSeconds = 60 };

        // No header: the planned schedule, indexed by attempts already started.
        Assert.Equal(TimeSpan.FromSeconds(2), RetryPolicy.DelayFor(1, null, options));
        Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.DelayFor(4, null, options));

        // A shorter Retry-After never shortens the planned wait...
        Assert.Equal(TimeSpan.FromSeconds(15), RetryPolicy.DelayFor(3, TimeSpan.FromSeconds(1), options));

        // ...a longer one is honoured...
        Assert.Equal(TimeSpan.FromSeconds(45), RetryPolicy.DelayFor(1, TimeSpan.FromSeconds(45), options));

        // ...but a hostile or absurd value cannot park the worker for an hour.
        Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.DelayFor(1, TimeSpan.FromHours(1), options));
    }

    [Fact]
    public void AnUnconfiguredScheduleFallsBackToTheDocumentedDefault()
    {
        var options = new LabOptions();

        Assert.Equal(TimeSpan.FromSeconds(2), options.JobRetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(30), options.JobRetryDelay(4));

        // Past the end of the schedule the last wait repeats; the budget is what stops a job.
        Assert.Equal(TimeSpan.FromSeconds(30), options.JobRetryDelay(9));
        Assert.Equal(TimeSpan.FromSeconds(1), options.OutboxRetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(30), options.OutboxRetryDelay(9));
    }

    /// <summary>
    /// Regression guard. Array binding APPENDS to whatever the property already holds, so a
    /// non-empty default would make this configuration a no-op: an operator setting the first
    /// wait to 0 would still wait the default 2 seconds, with nothing in the logs to say so.
    /// </summary>
    [Fact]
    public void AConfiguredScheduleReplacesTheDefaultInsteadOfExtendingIt()
    {
        var configuration = new ConfigurationBuilder()
            .AddCommandLine([.. LabTestConfig.Indexed("Lab:JobRetryDelaysSeconds", 0, 0, 0, 0)])
            .Build();

        var options = new LabOptions();
        configuration.GetSection(LabOptions.SectionName).Bind(options);

        Assert.Equal([0, 0, 0, 0], options.JobRetryDelaysSeconds);
        Assert.Equal(TimeSpan.Zero, options.JobRetryDelay(1));
        Assert.Equal(TimeSpan.Zero, options.JobRetryDelay(4));
    }

    /// <summary>
    /// Regression guard: claiming when there is nothing to claim must return null quietly.
    ///
    /// The claim used to commit its transaction while the DataReader was still open, which
    /// throws — so an idle worker spent every poll interval throwing and backing off instead of
    /// waiting. It stayed invisible because the tests always had work waiting; only a worker
    /// running against an empty queue (as the scenario scripts do) shows it.
    /// </summary>
    [Fact]
    public async Task ClaimingWithNothingDueReturnsNullInsteadOfThrowing()
    {
        await _sql.ParkAbandonedJobsAsync();

        var options = new DbContextOptionsBuilder<LabDbContext>()
            .UseSqlServer(fixture.IntegrationConnectionString)
            .Options;
        await using var context = new LabDbContext(options);
        var store = new JobStore(context, Options.Create(new LabOptions()));

        // Repeated, because the failure mode was a loop: the second call must be as quiet as
        // the first, on the same context and connection.
        Assert.Null(await store.TryClaimAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Null(await store.TryClaimAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Null(await store.TryClaimAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------------------------ durable behaviour

    [Fact]
    public async Task TransientFailuresAreRetriedUntilTheExternalSystemAccepts()
    {
        await _sql.ParkAbandonedJobsAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        // The first two NEW applies answer 503 and leave no external effect.
        await erp.SetScenarioAsync(requestId, "first-n-503", n: 2);
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        await using var worker = await TestWorkerHost.StartAsync(fixture, erp.BaseAddress, null, FastRetryArgs());

        await WaitForJobStateAsync(requestId, [JobStatus.Completed]);

        var job = await _sql.GetJobAsync(requestId);
        Assert.Equal(3, job!.AttemptsStarted);
        Assert.NotNull(job.ExternalReceiptId);
        Assert.Null(job.LastErrorCode);

        // One durable attempt row per STARTED attempt, with the transient ones classified.
        var attempts = await _sql.GetJobAttemptsAsync(requestId);
        Assert.Equal(3, attempts.Count);
        Assert.Equal([AttemptOutcome.TransientFailure, AttemptOutcome.TransientFailure, AttemptOutcome.Succeeded], attempts.Select(a => a.Outcome));
        Assert.All(attempts.Take(2), attempt => Assert.Equal("http_503", attempt.SafeErrorCode));

        // Exactly one external effect, carrying the receipt the job recorded.
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
        Assert.Equal(await _sql.GetAppliedReceiptAsync(requestId), job.ExternalReceiptId);
    }

    [Fact]
    public async Task PermanentFailureIsNeverRetried()
    {
        await _sql.ParkAbandonedJobsAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        await erp.SetScenarioAsync(requestId, "permanent-422");
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        await using var worker = await TestWorkerHost.StartAsync(fixture, erp.BaseAddress, null, FastRetryArgs());

        await WaitForJobStateAsync(requestId, [JobStatus.DeadLetterPending, JobStatus.DeadLettered]);

        var job = await _sql.GetJobAsync(requestId);
        Assert.Equal(1, job!.AttemptsStarted);
        Assert.Equal("http_422", job.LastErrorCode);
        Assert.Null(job.ExternalReceiptId);
        Assert.Null(job.NextAttemptAtUtc);

        var attempts = await _sql.GetJobAttemptsAsync(requestId);
        Assert.Single(attempts);
        Assert.Equal(AttemptOutcome.PermanentFailure, attempts[0].Outcome);

        // A terminal answer means the external system never applied anything.
        Assert.Equal(0, await _sql.CountAppliedExportsAsync(requestId));
    }

    [Fact]
    public async Task TheAttemptBudgetIsAHardCeiling()
    {
        await _sql.ParkAbandonedJobsAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        // Never recovers: the budget, not the external system, has to stop the loop.
        await erp.SetScenarioAsync(requestId, "first-n-503", n: 1000);
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        await using var worker = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastRetryArgs("--Lab:MaxJobAttempts=5"));

        await WaitForJobStateAsync(requestId, [JobStatus.DeadLetterPending, JobStatus.DeadLettered]);

        var job = await _sql.GetJobAsync(requestId);
        Assert.Equal(5, job!.AttemptsStarted);
        Assert.Equal("attempt_budget_exhausted", job.LastErrorCode);
        Assert.Equal(5, (await _sql.GetJobAttemptsAsync(requestId)).Count);
        Assert.Equal(0, await _sql.CountAppliedExportsAsync(requestId));

        // Give a sixth attempt every chance to appear; the budget must still hold.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(5, (await _sql.GetJobAsync(requestId))!.AttemptsStarted);
        Assert.Equal(5, (await _sql.GetJobAttemptsAsync(requestId)).Count);
    }

    [Fact]
    public async Task RetryStateSurvivesAWorkerRestartInsteadOfStartingOver()
    {
        await _sql.ParkAbandonedJobsAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        await erp.SetScenarioAsync(requestId, "first-n-503", n: 2);
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        // A few seconds' wait after the first failure, so the restart happens with a retry
        // SCHEDULED rather than mid-attempt. The wait must stay short, because the schedule is
        // durable: the replacement worker inherits the stored NextAttemptAtUtc and cannot
        // shorten it, however impatient its own configuration is.
        // Held in a using block, not a bare local: an assertion failure before an explicit
        // StopAsync would leak a live worker into the REST OF THE RUN, where it would publish
        // other tests' outbox rows and claim their jobs with this test's configuration.
        await using (var worker = await TestWorkerHost.StartAsync(
            fixture,
            erp.BaseAddress,
            null,
            [
                "--Lab:PollIntervalMilliseconds=100",
                .. LabTestConfig.Indexed("Lab:OutboxRetryDelaysSeconds", 0, 0, 0, 0, 0, 0),
                .. LabTestConfig.Indexed("Lab:JobRetryDelaysSeconds", 3),
            ]))
        {
            // Waits for the attempt to be FINISHED, not merely started: the counter is
            // incremented by the claim, so `AttemptsStarted == 1` is already true while the
            // external call is still in flight and the row is still Processing.
            await Eventually.UntilAsync(
                async () => await _sql.GetJobAsync(requestId)
                    is { AttemptsStarted: 1, Status: JobStatus.RetryScheduled },
                timeout: TimeSpan.FromSeconds(60),
                diagnostics: () => _sql.DescribeAsync(requestId));

            var beforeRestart = await _sql.GetJobAsync(requestId);
            Assert.Equal(JobStatus.RetryScheduled, beforeRestart!.Status);
            Assert.Equal("http_503", beforeRestart.LastErrorCode);

            await worker.StopAsync();
        }

        // The state the restarted worker inherits is exactly what SQL held: one attempt spent,
        // no lease held, and a due time the replacement must respect.
        var afterStop = await _sql.GetJobAsync(requestId);
        Assert.Equal(1, afterStop!.AttemptsStarted);
        Assert.Null(afterStop.LeaseToken);
        Assert.NotNull(afterStop.NextAttemptAtUtc);

        await using var restarted = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastRetryArgs());

        await WaitForJobStateAsync(requestId, [JobStatus.Completed]);

        // Attempt 1 was spent before the restart; the budget continued from there.
        var completed = await _sql.GetJobAsync(requestId);
        Assert.Equal(3, completed!.AttemptsStarted);
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
    }

    [Fact]
    public async Task ACrashInsideAnAttemptStillConsumesThatAttempt()
    {
        await _sql.ParkAbandonedJobsAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();
        await SeedJobAsync(requestId, JobStatus.Pending, attemptsStarted: 0, leaseExpired: false);

        // Real child process: the claim (and its attempt counter) is committed, then the
        // process dies before the external call starts.
        var crashArgs = LabTestConfig.WorkerArgs(
            fixture,
            erp.BaseAddress,
            "--Lab:PollIntervalMilliseconds=100",
            "--Lab:JobLeaseSeconds=1",
            FaultController.CrashOnce(FaultPoints.JobAfterClaim));
        var crashed = ProcessHost.Start("src/Integration.Worker", "Integration.Worker", crashArgs);
        await using (crashed)
        {
            await crashed.WaitForExitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(FaultController.CrashExitCode, crashed.ExitCode);
        }

        // The durable counter records STARTED attempts, so the crashed attempt is spent and
        // the row is left mid-flight for the lease to recover.
        var afterCrash = await _sql.GetJobAsync(requestId);
        Assert.Equal(1, afterCrash!.AttemptsStarted);
        Assert.Equal(JobStatus.Processing, afterCrash.Status);
        Assert.Single(await _sql.GetJobAttemptsAsync(requestId));
        Assert.Equal(0, await _sql.CountAppliedExportsAsync(requestId));

        // The expired lease is the recovery path; the work still finishes exactly once.
        await using var restarted = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastRetryArgs("--Lab:JobLeaseSeconds=1"));

        await WaitForJobStateAsync(requestId, [JobStatus.Completed]);
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
        Assert.True((await _sql.GetJobAsync(requestId))!.AttemptsStarted >= 2);
    }

    [Fact]
    public async Task AClaimWithAnAlreadySpentBudgetNeverOpensAnotherExternalCall()
    {
        await _sql.ParkAbandonedJobsAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        // The crash-after-increment window at its worst: the counter is already at the ceiling
        // and the row is stranded in Processing with a lease nobody renews.
        await SeedJobAsync(requestId, JobStatus.Processing, attemptsStarted: 5, leaseExpired: true);

        await using var worker = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastRetryArgs("--Lab:MaxJobAttempts=5", "--Lab:JobLeaseSeconds=1"));

        await WaitForJobStateAsync(requestId, [JobStatus.DeadLetterPending, JobStatus.DeadLettered]);

        // Reclaimed only to write the terminal state: no sixth attempt, no external call.
        var job = await _sql.GetJobAsync(requestId);
        Assert.Equal(5, job!.AttemptsStarted);
        Assert.Equal("attempt_budget_exhausted", job.LastErrorCode);
        Assert.Equal(0, await _sql.CountAppliedExportsAsync(requestId));
    }
}
