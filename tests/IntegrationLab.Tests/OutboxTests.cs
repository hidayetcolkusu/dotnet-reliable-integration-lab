using Integration.Shared.Contracts;
using Integration.Shared.Messaging;
using Integration.Shared.Persistence;
using Integration.Shared.Persistence.Entities;
using Integration.Shared.Runtime;
using Integration.Worker.Publishing;
using IntegrationLab.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IntegrationLab.Tests;

/// <summary>
/// Task 4 acceptance: Published is only ever written after a confirmed AND routed publish.
/// Broker outages, unroutable messages, nacks and lost confirms all leave the row retryable;
/// crash windows recover through the lease; two dispatchers never double-claim.
/// </summary>
[Collection("lab")]
public sealed class OutboxTests(LabFixture fixture)
{
    private readonly SqlAssertions _sql = fixture.CreateSql();

    private static string[] FastWorkerArgs(params string[] extra) =>
    [
        "--Lab:PollIntervalMilliseconds=100",
        .. LabTestConfig.Indexed("Lab:OutboxRetryDelaysSeconds", 0, 0, 0, 0, 0, 0),
        .. extra,
    ];

    /// <summary>
    /// Clears not-yet-published rows left behind by earlier tests in the shared database.
    /// The dispatcher claims the OLDEST due row first, so without this a backlog would starve
    /// the row under test and every timing assertion here would measure the leftovers instead.
    /// Called only at the START of a test, never during its own flow.
    /// </summary>
    private Task ResetOutboxAsync() => _sql.CleanupAbandonedOutboxAsync();

    /// <summary>
    /// Writes an accepted request and one Export outbox row with a chosen exchange and routing
    /// key - the API always writes the correct pair, so a deliberately unroutable row has to be
    /// seeded. Returns the outbox row id.
    /// </summary>
    private async Task<Guid> SeedExportOutboxAsync(Guid requestId, string exchange, string routingKey)
    {
        var request = new ExportRequest(requestId, "PO-100", 160.00m, "TRY");
        var envelope = new ExportEnvelope(
            requestId,
            MessageTypes.OrderExportRequested,
            MessageTypes.SchemaVersion,
            DateTimeOffset.UtcNow,
            request);
        var outboxId = Guid.NewGuid();

        await _sql.ExecuteAsync(
            useErpDatabase: false,
            "INSERT INTO ExportRequests (RequestId, PayloadHash, ExternalReference, Amount, Currency, CreatedAtUtc) " +
            "VALUES (@requestId, @hash, 'PO-100', 160.00, 'TRY', SYSUTCDATETIME());" +
            "INSERT INTO OutboxMessages (Id, Kind, SourceRequestId, RejectionId, Body, RoutingKey, Exchange, " +
            "CreatedAtUtc, Status, PublishAttempts, NextAttemptAtUtc) " +
            "VALUES (@outboxId, 'Export', @requestId, NULL, @body, @routingKey, @exchange, " +
            "SYSUTCDATETIME(), 'Pending', 0, SYSUTCDATETIME());",
            ("requestId", System.Data.SqlDbType.UniqueIdentifier, requestId),
            ("hash", System.Data.SqlDbType.VarChar, PayloadHash.Compute(request)),
            ("outboxId", System.Data.SqlDbType.UniqueIdentifier, outboxId),
            ("body", System.Data.SqlDbType.NVarChar, System.Text.Json.JsonSerializer.Serialize(envelope, LabJson.Options)),
            ("routingKey", System.Data.SqlDbType.VarChar, routingKey),
            ("exchange", System.Data.SqlDbType.VarChar, exchange));

        return outboxId;
    }

    private async Task<OutboxStore> CreateStoreAsync(int leaseSeconds = 1)
    {
        var options = new DbContextOptionsBuilder<LabDbContext>()
            .UseSqlServer(fixture.IntegrationConnectionString)
            .Options;
        var context = new LabDbContext(options);
        return new OutboxStore(context, Options.Create(new LabOptions { OutboxLeaseSeconds = leaseSeconds }));
    }

    [Fact]
    public async Task BrokerOutageKeepsRowsRetryableUntilTheBrokerReturns()
    {
        await ResetOutboxAsync();

        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        await fixture.StopRabbitAsync();
        try
        {
            await using var worker = await TestWorkerHost.StartAsync(
                fixture,
                null,
                null,
                FastWorkerArgs("--Lab:OutboxLeaseSeconds=1"));
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Broker down: the row cycles Pending/Publishing but never becomes Published.
            var duringOutage = await _sql.GetOutboxAsync(requestId, "Export");
            Assert.NotNull(duringOutage);
            Assert.True(duringOutage!.Status is "Pending" or "Publishing");
            Assert.NotEqual("Published", duringOutage.Status);

            await fixture.StartRabbitAsync();

            // Short lease so a claim that died mid-outage recovers in seconds, not 30.
            await Eventually.UntilAsync(
                async () => (await _sql.GetOutboxAsync(requestId, "Export"))?.Status == "Published",
                timeout: TimeSpan.FromSeconds(60),
                diagnostics: () => _sql.DescribeAsync(requestId));

            // Delivery accepted durably: one receipt, one job (ERP intentionally absent here).
            await Eventually.UntilAsync(
                async () => await _sql.CountInboxReceiptsAsync(requestId) == 1,
                timeout: TimeSpan.FromSeconds(30),
                diagnostics: () => _sql.DescribeAsync(requestId));
            Assert.Equal(1, await _sql.CountJobsAsync(requestId));
        }
        finally
        {
            await fixture.StartRabbitAsync();
        }
    }

    [Fact]
    public async Task AnUnroutableMessageIsReturnedNotMarkedPublished()
    {
        await ResetOutboxAsync();

        await using var broker = await BrokerAssertions.CreateAsync(fixture);

        // The routing key, not the binding, is what makes this message unroutable. Deleting the
        // binding would not work: every publisher and consumer channel re-declares the whole
        // topology on connect, so the binding is back before the first publish leaves.
        var requestId = Guid.NewGuid();
        var outboxId = await SeedExportOutboxAsync(requestId, broker.EventsExchange, "order.export.nowhere");

        await using var worker = await TestWorkerHost.StartAsync(fixture, null, null, FastWorkerArgs());

        // mandatory:true means the broker hands an unroutable message back instead of dropping
        // it, and a returned message is never Published - not even when its confirm arrives.
        await Eventually.UntilAsync(
            async () =>
            {
                var outbox = await _sql.GetOutboxByIdAsync(outboxId);
                return outbox?.Status == "Pending" && outbox.LastErrorCode == "broker_unroutable";
            },
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: () => _sql.DescribeAsync(requestId));

        Assert.Null((await _sql.GetOutboxByIdAsync(outboxId))!.PublishedAtUtc);
        Assert.Equal(0, await _sql.CountInboxReceiptsAsync(requestId));

        // Making it routable again lets the retry succeed on the same durable row.
        await _sql.ExecuteAsync(
            useErpDatabase: false,
            "UPDATE OutboxMessages SET RoutingKey = @key, NextAttemptAtUtc = SYSUTCDATETIME() WHERE Id = @id",
            ("key", System.Data.SqlDbType.VarChar, Topology.ExportRoutingKey),
            ("id", System.Data.SqlDbType.UniqueIdentifier, outboxId));

        await Eventually.UntilAsync(
            async () => (await _sql.GetOutboxByIdAsync(outboxId))?.Status == "Published",
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: () => _sql.DescribeAsync(requestId));

        await Eventually.UntilAsync(
            async () => await _sql.CountInboxReceiptsAsync(requestId) == 1,
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: () => _sql.DescribeAsync(requestId));
    }

    /// <summary>
    /// Nack classification through the publishing seam: a broker-side basic.nack leaves the
    /// row Pending with the broker_nack code. (A healthy node cannot be forced to nack
    /// deterministically; the confirm/return paths are proven against the real broker.)
    /// </summary>
    [Fact]
    public async Task NackOutcomeKeepsTheRowPending()
    {
        await ResetOutboxAsync();

        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        var seam = new SeamPublisher(PublishOutcome.Nacked);
        await using var worker = await TestWorkerHost.StartAsync(
            fixture,
            null,
            services => services.Replace(ServiceDescriptor.Singleton<IConfirmedPublisher>(implementationInstance: seam)),
            FastWorkerArgs());

        await Eventually.UntilAsync(
            async () =>
            {
                var outbox = await _sql.GetOutboxAsync(requestId, "Export");
                return outbox?.Status == "Pending" && outbox.LastErrorCode == "broker_nack";
            },
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: () => _sql.DescribeAsync(requestId));
    }

    [Fact]
    public async Task ConfirmTimeoutAgainstAFrozenBrokerKeepsTheRowPendingThenRecovers()
    {
        await ResetOutboxAsync();

        await using var api = await TestApiHost.StartAsync(fixture);
        var warmUpId = Guid.NewGuid();
        var victimId = Guid.NewGuid();
        using var warmUp = await api.SubmitExportAsync(warmUpId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, warmUp.StatusCode);

        var freezeArgs = new List<string>
        {
            "--Lab:PollIntervalMilliseconds=100",
            "--Lab:ConfirmTimeoutSeconds=2",
        };
        freezeArgs.AddRange(LabTestConfig.Indexed("Lab:OutboxRetryDelaysSeconds", 0, 0, 0, 0, 0, 0));
        await using var worker = await TestWorkerHost.StartAsync(fixture, null, null, [.. freezeArgs]);

        // First publish on a live broker proves the channel and confirms work.
        await Eventually.UntilAsync(
            async () => (await _sql.GetOutboxAsync(warmUpId, "Export"))?.Status == "Published",
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: () => _sql.DescribeAsync(warmUpId));

        // Freeze the broker process FIRST: TCP stays open, confirms stop arriving. Submitting
        // before the freeze would race the 100 ms dispatcher loop, which usually wins and
        // publishes the victim against a live broker.
        await fixture.PauseRabbitProcessAsync();
        try
        {
            // The API only writes SQL, so it is unaffected by the frozen broker.
            using var victim = await api.SubmitExportAsync(victimId);
            Assert.Equal(System.Net.HttpStatusCode.Accepted, victim.StatusCode);

            await Eventually.UntilAsync(
                async () =>
                {
                    var outbox = await _sql.GetOutboxAsync(victimId, "Export");
                    return outbox?.Status == "Pending" && outbox.LastErrorCode == "broker_confirm_timeout";
                },
                timeout: TimeSpan.FromSeconds(30),
                diagnostics: () => _sql.DescribeAsync(victimId));
        }
        finally
        {
            await fixture.ResumeRabbitProcessAsync();
        }

        // After thawing, the retry confirms. A duplicate delivery from the frozen window is
        // possible and expected - the inbox receipt primary key absorbs it.
        await Eventually.UntilAsync(
            async () => (await _sql.GetOutboxAsync(victimId, "Export"))?.Status == "Published",
            timeout: TimeSpan.FromSeconds(45),
            diagnostics: () => _sql.DescribeAsync(victimId));
        Assert.Equal(1, await _sql.CountJobsAsync(victimId));
    }

    [Fact]
    public async Task CrashAfterConfirmRepublishesTheSameTransportMessageId()
    {
        // The child process claims the OLDEST due row first: leftovers from earlier tests
        // must not steal the crash window from this test's row.
        await ResetOutboxAsync();

        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        // Real child process: crash after the broker confirmed, before SQL was updated.
        var crashArgs = LabTestConfig.WorkerArgs(
            fixture,
            null,
            "--Lab:PollIntervalMilliseconds=100",
            "--Lab:OutboxLeaseSeconds=1",
            FaultController.CrashOnce(FaultPoints.OutboxAfterConfirmBeforeUpdate));
        var crashed = ProcessHost.Start("src/Integration.Worker", "Integration.Worker", crashArgs);
        await using (crashed)
        {
            await crashed.WaitForExitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(FaultController.CrashExitCode, crashed.ExitCode);
        }

        // The confirmed row was never marked Published: the lease is the recovery path.
        var afterCrash = await _sql.GetOutboxAsync(requestId, "Export");
        Assert.NotNull(afterCrash);
        Assert.Equal("Publishing", afterCrash!.Status);

        var restartArgs = LabTestConfig.WorkerArgs(
            fixture,
            null,
            FastWorkerArgs("--Lab:OutboxLeaseSeconds=1"));
        var restarted = ProcessHost.Start("src/Integration.Worker", "Integration.Worker", restartArgs);
        await using (restarted)
        {
            await restarted.WaitForWorkerReadyAsync();

            await Eventually.UntilAsync(
                async () => (await _sql.GetOutboxAsync(requestId, "Export"))?.Status == "Published",
                timeout: TimeSpan.FromSeconds(45),
                diagnostics: () => _sql.DescribeAsync(requestId));

            var outbox = await _sql.GetOutboxAsync(requestId, "Export");
            Assert.True(outbox!.PublishAttempts >= 2, $"expected a republish, attempts were {outbox.PublishAttempts}");

            // The republish reuses the SAME outbox id as the broker MessageId, so the inbox
            // receipt primary key collapses the duplicate delivery into one accepted job.
            // Delivery and inbox acceptance are eventual facts that follow the publish, so this
            // has to stay INSIDE the restarted worker's lifetime: it owns the only consumer.
            await Eventually.UntilAsync(
                async () => await _sql.CountInboxReceiptsAsync(requestId) == 1
                    && await _sql.CountJobsAsync(requestId) == 1,
                timeout: TimeSpan.FromSeconds(30),
                diagnostics: () => _sql.DescribeAsync(requestId));
        }
    }

    [Fact]
    public async Task TwoDispatchersClaimDisjointRows()
    {
        // This test asserts on ALL due rows, so leftovers from earlier tests must go.
        await ResetOutboxAsync();

        // Store-level: parallel claim loops never hand the same row to two owners.
        var requestIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        await using (var api = await TestApiHost.StartAsync(fixture))
        {
            foreach (var requestId in requestIds)
            {
                using var response = await api.SubmitExportAsync(requestId);
                Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
            }
        }

        var storeA = await CreateStoreAsync();
        var storeB = await CreateStoreAsync();
        var claimsA = new List<Guid>();
        var claimsB = new List<Guid>();

        await Task.WhenAll(
            Task.Run(async () =>
            {
                while (true)
                {
                    var claimed = await storeA.TryClaimAsync(Guid.NewGuid(), CancellationToken.None);
                    if (claimed is null)
                    {
                        break;
                    }

                    claimsA.Add(claimed.Id);
                }
            }),
            Task.Run(async () =>
            {
                while (true)
                {
                    var claimed = await storeB.TryClaimAsync(Guid.NewGuid(), CancellationToken.None);
                    if (claimed is null)
                    {
                        break;
                    }

                    claimsB.Add(claimed.Id);
                }
            }));

        var all = claimsA.Concat(claimsB).ToList();
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Distinct().Count());
    }

    [Fact]
    public async Task TwoCompetingWorkersPublishEachRowOnce()
    {
        await ResetOutboxAsync();

        var requestIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        await using (var api = await TestApiHost.StartAsync(fixture))
        {
            foreach (var requestId in requestIds)
            {
                using var response = await api.SubmitExportAsync(requestId);
                Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
            }
        }

        await using var workerA = await TestWorkerHost.StartAsync(fixture, null, null, FastWorkerArgs());
        await using var workerB = await TestWorkerHost.StartAsync(fixture, null, null, FastWorkerArgs());

        foreach (var requestId in requestIds)
        {
            await Eventually.UntilAsync(
                async () => (await _sql.GetOutboxAsync(requestId, "Export"))?.Status == "Published",
                timeout: TimeSpan.FromSeconds(45),
                diagnostics: () => _sql.DescribeAsync(requestId));
            Assert.Equal(1, await _sql.CountInboxReceiptsAsync(requestId));
            Assert.Equal(1, await _sql.CountJobsAsync(requestId));
        }
    }

    [Fact]
    public async Task StaleLeaseOwnerCannotMarkPublished()
    {
        await ResetOutboxAsync();

        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        var storeA = await CreateStoreAsync();
        var storeB = await CreateStoreAsync();

        var staleToken = Guid.NewGuid();
        OutboxMessage? claimedByStale = null;
        await Eventually.UntilAsync(
            async () =>
            {
                // Tolerates client/SQL clock skew on NextAttemptAtUtc by retrying the claim.
                claimedByStale = await storeA.TryClaimAsync(staleToken, CancellationToken.None);
                return claimedByStale is not null;
            },
            timeout: TimeSpan.FromSeconds(15),
            diagnostics: () => _sql.DescribeAsync(requestId));
        Assert.NotNull(claimedByStale);

        // The stale owner's lease must EXPIRE before another owner may claim (1s lease).
        OutboxMessage? newOwner = null;
        await Eventually.UntilAsync(
            async () =>
            {
                newOwner = await storeB.TryClaimAsync(Guid.NewGuid(), CancellationToken.None);
                return newOwner is not null;
            },
            timeout: TimeSpan.FromSeconds(15),
            diagnostics: () => _sql.DescribeAsync(requestId));
        Assert.NotNull(newOwner);
        Assert.Equal(claimedByStale!.Id, newOwner!.Id);

        var staleMarked = await storeA.MarkPublishedAsync(claimedByStale.Id, staleToken, CancellationToken.None);
        Assert.False(staleMarked);

        var currentMarked = await storeB.MarkPublishedAsync(newOwner.Id, newOwner.LeaseToken!.Value, CancellationToken.None);
        Assert.True(currentMarked);

        var outbox = await _sql.GetOutboxAsync(requestId, "Export");
        Assert.Equal("Published", outbox!.Status);
        Assert.Equal(2, outbox.PublishAttempts);
    }

    private sealed class SeamPublisher(PublishOutcome outcome) : IConfirmedPublisher
    {
        public Task<PublishOutcome> PublishAsync(OutboxPublishRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(outcome);
    }
}
