using System.Text;
using Integration.Shared.Persistence.Entities;
using Integration.Shared.Runtime;
using IntegrationLab.Tests.Support;
using Xunit;

namespace IntegrationLab.Tests;

/// <summary>
/// End-to-end recovery: the guarantees have to survive the failures happening TOGETHER, not
/// one at a time in a tidy unit test. A crash mid-transaction leaves nothing behind, and a
/// storm of broker outage plus worker death plus external failures still ends in exactly one
/// applied export per request.
/// </summary>
[Collection("lab")]
public sealed class RecoveryTests(LabFixture fixture)
{
    private readonly SqlAssertions _sql = fixture.CreateSql();

    private static string[] FastArgs(params string[] extra) =>
    [
        "--Lab:PollIntervalMilliseconds=100",
        .. LabTestConfig.Indexed("Lab:OutboxRetryDelaysSeconds", 0, 0, 0, 0, 0, 0),
        .. LabTestConfig.Indexed("Lab:JobRetryDelaysSeconds", 0, 0, 0, 0),
        .. extra,
    ];

    [Fact]
    public async Task ACrashBetweenTheRequestAndItsOutboxRowLeavesNoAcceptedWorkBehind()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        var port = LabTestConfig.GetFreeLoopbackPort();
        var requestId = Guid.NewGuid();

        // A real child process, because the fault is a hard exit: the request row is inserted,
        // then the process dies before the outbox row joins it in the same transaction.
        var api = ProcessHost.Start(
            "src/Integration.Api",
            "Integration.Api",
            LabTestConfig.ApiArgs(fixture, port, FaultController.CrashOnce(FaultPoints.ApiAfterRequestInsert)));

        await using (api)
        {
            var baseAddress = new Uri($"http://127.0.0.1:{port}");
            await api.WaitForHttpAsync(new Uri(baseAddress, "/health/live"));

            using var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
            using var content = new StringContent(
                LabTestConfig.ExportRequestJson(requestId),
                Encoding.UTF8,
                "application/json");

            // The caller never gets an answer - which is the honest outcome of a crash, and
            // exactly the case where a client must be free to retry the same requestId.
            await Assert.ThrowsAnyAsync<HttpRequestException>(
                async () => await client.PostAsync("/api/exports", content));

            await api.WaitForExitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(FaultController.CrashExitCode, api.ExitCode);
        }

        // Atomic acceptance: an uncommitted transaction leaves no half-accepted request that a
        // dispatcher would never publish and an operator would never find.
        Assert.Equal(0, await _sql.CountExportRequestsAsync(requestId));
        Assert.Null(await _sql.GetOutboxAsync(requestId, OutboxKind.Export));
    }

    [Fact]
    public async Task TheSameRequestIdCanBeRetriedAfterACrashWithoutCreatingASecondExport()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        var port = LabTestConfig.GetFreeLoopbackPort();
        var requestId = Guid.NewGuid();

        var crashing = ProcessHost.Start(
            "src/Integration.Api",
            "Integration.Api",
            LabTestConfig.ApiArgs(fixture, port, FaultController.CrashOnce(FaultPoints.ApiAfterRequestInsert)));
        await using (crashing)
        {
            var baseAddress = new Uri($"http://127.0.0.1:{port}");
            await crashing.WaitForHttpAsync(new Uri(baseAddress, "/health/live"));

            using var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
            using var content = new StringContent(
                LabTestConfig.ExportRequestJson(requestId), Encoding.UTF8, "application/json");
            await Assert.ThrowsAnyAsync<HttpRequestException>(
                async () => await client.PostAsync("/api/exports", content));
            await crashing.WaitForExitAsync(TimeSpan.FromSeconds(30));
        }

        // The client retries the identical request, as a client with an unknown outcome should.
        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        using var retry = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, retry.StatusCode);

        await using var worker = await TestWorkerHost.StartAsync(fixture, erp.BaseAddress, null, FastArgs());
        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(requestId) == JobStatus.Completed,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(requestId));

        Assert.Equal(1, await _sql.CountExportRequestsAsync(requestId));
        Assert.Equal(1, await _sql.CountJobsAsync(requestId));
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
    }

    [Fact]
    public async Task EveryDependencyFailingAtOnceStillAppliesEachRequestOnce()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        var requestIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

        // The external system rejects the first two NEW applies of every request.
        foreach (var requestId in requestIds)
        {
            await erp.SetScenarioAsync(requestId, "first-n-503", n: 2);
        }

        // Failure 1: the broker is down while the work is accepted. The API does not care.
        await fixture.StopRabbitAsync();
        try
        {
            await using var api = await TestApiHost.StartAsync(fixture);
            foreach (var requestId in requestIds)
            {
                using var response = await api.SubmitExportAsync(requestId);
                Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
            }

            foreach (var requestId in requestIds)
            {
                Assert.Equal(OutboxStatus.Pending, (await _sql.GetOutboxAsync(requestId, OutboxKind.Export))!.Status);
            }
        }
        finally
        {
            await fixture.StartRabbitAsync();
        }

        // Failure 2: a worker starts, does some of the work, and is killed hard mid-flight.
        var workerArgs = LabTestConfig.WorkerArgs(
            fixture,
            erp.BaseAddress,
            FastArgs("--Lab:OutboxLeaseSeconds=2", "--Lab:JobLeaseSeconds=2"));
        var doomed = ProcessHost.Start("src/Integration.Worker", "Integration.Worker", workerArgs);
        await using (doomed)
        {
            await doomed.WaitForWorkerReadyAsync();

            // Kill once there is real in-flight state to lose, not on an idle worker.
            await Eventually.UntilAsync(
                async () => await CountInStateAsync(requestIds, state => state is not null) >= 1,
                timeout: TimeSpan.FromSeconds(60),
                diagnostics: () => DescribeAllAsync(requestIds));
            doomed.KillHard();
            await doomed.WaitForExitAsync(TimeSpan.FromSeconds(30));
        }

        // Failure 3: the replacement worker competes with a second one, so leases and claims
        // are exercised while the backlog drains.
        await using var workerA = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastArgs("--Lab:OutboxLeaseSeconds=2", "--Lab:JobLeaseSeconds=2"));
        await using var workerB = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastArgs("--Lab:OutboxLeaseSeconds=2", "--Lab:JobLeaseSeconds=2"));

        foreach (var requestId in requestIds)
        {
            await Eventually.UntilAsync(
                async () => await _sql.GetJobStateAsync(requestId) == JobStatus.Completed,
                timeout: TimeSpan.FromSeconds(120),
                diagnostics: () => _sql.DescribeAsync(requestId));
        }

        // The only number that matters after all of that.
        foreach (var requestId in requestIds)
        {
            Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
            Assert.Equal(1, await _sql.CountJobsAsync(requestId));
            Assert.Equal(OutboxStatus.Published, (await _sql.GetOutboxAsync(requestId, OutboxKind.Export))!.Status);

            var job = await _sql.GetJobAsync(requestId);
            Assert.Equal(await _sql.GetAppliedReceiptAsync(requestId), job!.ExternalReceiptId);

            // Duplicate deliveries are expected in this storm; duplicate WORK is not.
            Assert.True(await _sql.CountInboxReceiptsAsync(requestId) >= 1);
        }
    }

    private async Task<int> CountInStateAsync(IEnumerable<Guid> requestIds, Func<string?, bool> predicate)
    {
        var matches = 0;
        foreach (var requestId in requestIds)
        {
            if (predicate(await _sql.GetJobStateAsync(requestId)))
            {
                matches++;
            }
        }

        return matches;
    }

    private async Task<string> DescribeAllAsync(IEnumerable<Guid> requestIds)
    {
        var lines = new List<string>();
        foreach (var requestId in requestIds)
        {
            lines.Add(await _sql.DescribeAsync(requestId));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
