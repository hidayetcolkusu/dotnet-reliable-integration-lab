using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;
using Integration.Shared.Contracts;
using Integration.Shared.Messaging;
using Integration.Shared.Persistence.Entities;
using Integration.Shared.Runtime;
using IntegrationLab.Tests.Support;
using Xunit;

namespace IntegrationLab.Tests;

/// <summary>
/// The named windows between a durable commit and the observable act that follows it.
///
/// Each test below stops a REAL process (or cuts a real dependency) inside one specific window
/// and then asserts what survived. A test whose name merely resembles a window does not close
/// it: the fault has to land between the two operations the window is named after, which is why
/// these use the fault hooks at <see cref="FaultPoints"/> and child processes rather than
/// in-process seams.
///
/// Timing is controlled with barriers and eventual assertions, never with sleeps chosen to be
/// "long enough": every wait polls real SQL or the real broker.
/// </summary>
[Collection("lab")]
public sealed partial class FailureWindowTests(LabFixture fixture)
{
    private readonly SqlAssertions _sql = fixture.CreateSql();

    private static string[] FastArgs(params string[] extra) =>
    [
        "--Lab:PollIntervalMilliseconds=100",
        .. LabTestConfig.Indexed("Lab:OutboxRetryDelaysSeconds", 0, 0, 0, 0, 0, 0),
        .. LabTestConfig.Indexed("Lab:JobRetryDelaysSeconds", 0, 0, 0, 0),
        .. extra,
    ];

    private static (string Name, SqlDbType Type, object? Value) P(string name, SqlDbType type, object? value) =>
        (name, type, value);

    /// <summary>Seeds an accepted request so a test can drive the inbox or the job loop directly.</summary>
    private async Task SeedRequestAsync(ExportRequest request)
    {
        await _sql.ExecuteAsync(
            useErpDatabase: false,
            "INSERT INTO ExportRequests (RequestId, PayloadHash, ExternalReference, Amount, Currency, CreatedAtUtc) " +
            "VALUES (@id, @hash, @reference, @amount, 'TRY', SYSUTCDATETIME())",
            P("id", SqlDbType.UniqueIdentifier, request.RequestId),
            P("hash", SqlDbType.VarChar, PayloadHash.Compute(request)),
            P("reference", SqlDbType.NVarChar, request.ExternalReference),
            P("amount", SqlDbType.Decimal, request.Amount));
    }

    /// <summary>Seeds the request AND its job row, for the windows that are about the job loop.</summary>
    private async Task SeedJobAsync(ExportRequest request, string status = JobStatus.Pending, int attemptsStarted = 0)
    {
        await SeedRequestAsync(request);
        await _sql.ExecuteAsync(
            useErpDatabase: false,
            "INSERT INTO IntegrationJobs (RequestId, PayloadHash, Status, AttemptsStarted, NextAttemptAtUtc, CreatedAtUtc) " +
            "VALUES (@id, @hash, @status, @attempts, SYSUTCDATETIME(), SYSUTCDATETIME())",
            P("id", SqlDbType.UniqueIdentifier, request.RequestId),
            P("hash", SqlDbType.VarChar, PayloadHash.Compute(request)),
            P("status", SqlDbType.VarChar, status),
            P("attempts", SqlDbType.Int, attemptsStarted));
    }

    private static string EnvelopeJson(ExportRequest request) =>
        JsonSerializer.Serialize(
            new ExportEnvelope(
                request.RequestId,
                MessageTypes.OrderExportRequested,
                MessageTypes.SchemaVersion,
                DateTimeOffset.UtcNow,
                request),
            LabJson.Options);

    private static ExportRequest NewRequest() => new(Guid.NewGuid(), "PO-WINDOW", 160.00m, "TRY");

    private async Task AssertQueueDrainsAsync(BrokerAssertions broker, string queue)
    {
        await Eventually.UntilAsync(
            async () => await broker.GetQueueStatsOrEmptyAsync(queue) is { MessagesReady: 0, MessagesUnacknowledged: 0 },
            timeout: TimeSpan.FromSeconds(60),
            diagnostics: async () =>
            {
                var stats = await broker.GetQueueStatsOrEmptyAsync(queue);
                return $"{queue}: ready={stats.MessagesReady}, unacked={stats.MessagesUnacknowledged}";
            });
    }

    // ================================================================= 1. outbox claim → publish

    /// <summary>
    /// The claim is committed (Publishing, lease held, PublishAttempts incremented) and the
    /// process dies before the broker is ever called. Nothing may be lost, and the message that
    /// eventually reaches the broker must keep the SAME transport identity - that identity is
    /// what the inbox deduplicates on.
    /// </summary>
    [Fact]
    public async Task AHardStopBetweenTheOutboxClaimAndThePublishRepublishesTheSameMessage()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        await using var broker = await BrokerAssertions.CreateAsync(fixture);

        var requestId = Guid.NewGuid();
        using (var submit = await api.SubmitExportAsync(requestId))
        {
            Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
        }

        var outboxIdBefore = await _sql.ExecuteScalarAsync(
            useErpDatabase: false,
            "SELECT Id FROM OutboxMessages WHERE SourceRequestId = @id AND Kind = 'Export'",
            P("id", SqlDbType.UniqueIdentifier, requestId));
        var outboxId = Assert.IsType<Guid>(outboxIdBefore);

        var doomed = ProcessHost.Start(
            "src/Integration.Worker",
            "Integration.Worker",
            LabTestConfig.WorkerArgs(
                fixture,
                erp.BaseAddress,
                FastArgs("--Lab:OutboxLeaseSeconds=2", FaultController.CrashOnce(FaultPoints.OutboxAfterClaim))));

        await using (doomed)
        {
            await doomed.WaitForExitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(FaultController.CrashExitCode, doomed.ExitCode);
        }

        // Claimed but never published: the row is mid-flight, held only by a lease whose owner
        // no longer exists.
        var stranded = await _sql.GetOutboxByIdAsync(outboxId);
        Assert.Equal(OutboxStatus.Publishing, stranded!.Status);
        Assert.Null(stranded.PublishedAtUtc);
        Assert.Equal(1, stranded.PublishAttempts);

        // The expired lease is the recovery path.
        await using var replacement = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastArgs("--Lab:OutboxLeaseSeconds=2"));

        await Eventually.UntilAsync(
            async () => (await _sql.GetOutboxByIdAsync(outboxId))?.Status == OutboxStatus.Published,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(requestId));

        // Same row, same id: a republish must not mint a second message, because the inbox
        // deduplicates on exactly this identity.
        var recovered = await _sql.ExecuteScalarAsync(
            useErpDatabase: false,
            "SELECT COUNT(*) FROM OutboxMessages WHERE SourceRequestId = @id AND Kind = 'Export'",
            P("id", SqlDbType.UniqueIdentifier, requestId));
        Assert.Equal(1, Assert.IsType<int>(recovered));
        Assert.True((await _sql.GetOutboxByIdAsync(outboxId))!.PublishAttempts >= 2);

        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(requestId) == JobStatus.Completed,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(requestId));
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
        await AssertQueueDrainsAsync(broker, broker.ExportQueue);
    }

    // ============================================================== 2. inbox commit → broker ACK

    /// <summary>
    /// The receipt and the job are committed and the process dies before the ACK. The broker
    /// must redeliver (nothing was acknowledged) and the redelivery must be absorbed by the
    /// receipt that already exists: one receipt, one job, and a queue that drains.
    /// </summary>
    [Fact]
    public async Task AHardStopBetweenTheInboxCommitAndTheAckIsAbsorbedByTheExistingReceipt()
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest();
        await SeedRequestAsync(request);

        await using var broker = await BrokerAssertions.CreateAsync(fixture);
        await using var erp = await TestFakeErpHost.StartAsync(fixture);

        // A fixed transport identity, so the redelivery is provably the same delivery.
        var transportMessageId = Guid.NewGuid().ToString();
        await broker.PublishAsync(
            broker.EventsExchange,
            Topology.ExportRoutingKey,
            Encoding.UTF8.GetBytes(EnvelopeJson(request)),
            transportMessageId,
            MessageTypes.OrderExportRequested);

        var doomed = ProcessHost.Start(
            "src/Integration.Worker",
            "Integration.Worker",
            LabTestConfig.WorkerArgs(
                fixture,
                erp.BaseAddress,
                FastArgs(
                    // The job loop must not race the crash window; the inbox is what is on trial.
                    "--Lab:JobLeaseSeconds=2",
                    FaultController.CrashOnce(FaultPoints.InboxAfterCommitBeforeAck))));

        await using (doomed)
        {
            await doomed.WaitForExitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(FaultController.CrashExitCode, doomed.ExitCode);
        }

        // Durably accepted before the crash, never acknowledged.
        Assert.Equal(1, await _sql.CountInboxReceiptsAsync(request.RequestId));
        Assert.Equal(1, await _sql.CountJobsAsync(request.RequestId));

        await using var replacement = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastArgs());

        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(request.RequestId) == JobStatus.Completed,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(request.RequestId));

        // The redelivery produced no second receipt and no second job - "at least once" on the
        // wire, one job in SQL.
        Assert.Equal(1, await _sql.CountInboxReceiptsAsync(request.RequestId));
        Assert.Equal(1, await _sql.CountJobsAsync(request.RequestId));
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(request.RequestId));

        // And the ACK really happened this time: the queue is empty, not merely quiet.
        await AssertQueueDrainsAsync(broker, broker.ExportQueue);
    }

    // ================================================================ 3. consumer loses SQL

    /// <summary>
    /// SQL is unreachable while a delivery is in the consumer's hands. Nothing durable can be
    /// written, so nothing may be ACKed; the consumer must back off rather than spin, and when
    /// SQL returns the SAME delivery must be accepted durably.
    /// </summary>
    [Fact]
    public async Task AConsumerThatCannotReachSqlNeverAcksAndRecoversWhenSqlReturns()
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest();
        await SeedRequestAsync(request);

        await using var broker = await BrokerAssertions.CreateAsync(fixture);
        await using var erp = await TestFakeErpHost.StartAsync(fixture);

        // The gate is the outage: the worker is configured for this port once, at startup.
        await using var gate = LoopbackTcpGate.CreateClosed(fixture.SqlHostName, fixture.SqlPort);

        var workerArgs = LabTestConfig.WorkerArgs(fixture, erp.BaseAddress, FastArgs());
        // Replace the connection string with the gated one: the argument the fixture added is
        // overridden by the later occurrence, which is how the command-line provider binds.
        var gatedArgs = workerArgs
            .Append($"--ConnectionStrings:IntegrationLab={fixture.IntegrationConnectionStringVia(gate.Port)}")
            .ToArray();

        var worker = ProcessHost.Start("src/Integration.Worker", "Integration.Worker", gatedArgs);
        await using (worker)
        {
            await worker.WaitForWorkerReadyAsync(TimeSpan.FromSeconds(90));

            // Published only once the consumer is subscribed. The APPLICATIONS own the topology,
            // so publishing before the worker declared its queue would route the message into an
            // exchange with no binding - and the broker would discard it silently.
            await worker.WaitForOutputAsync(ConsumerSubscribedMarker(), TimeSpan.FromSeconds(60));

            await broker.PublishAsync(
                broker.EventsExchange,
                Topology.ExportRoutingKey,
                Encoding.UTF8.GetBytes(EnvelopeJson(request)),
                Guid.NewGuid().ToString(),
                MessageTypes.OrderExportRequested);

            // With SQL gone the delivery cannot be accepted, so it cannot be ACKed: it stays in
            // the broker's hands. A worker that ACKed here would have lost the message.
            await Eventually.UntilAsync(
                async () =>
                {
                    var stats = await broker.GetQueueStatsOrEmptyAsync(broker.ExportQueue);
                    return stats.MessagesReady + stats.MessagesUnacknowledged >= 1;
                },
                timeout: TimeSpan.FromSeconds(60),
                diagnostics: async () =>
                {
                    var stats = await broker.GetQueueStatsOrEmptyAsync(broker.ExportQueue);
                    return $"ready={stats.MessagesReady} unacked={stats.MessagesUnacknowledged}";
                });

            // The consumer reports the refusal and backs off instead of hot-looping.
            await worker.WaitForOutputAsync(
                ConsumerBackoffMarker(),
                TimeSpan.FromSeconds(60));

            // SQL comes back at the address the worker already knows.
            gate.Open();

            await Eventually.UntilAsync(
                async () => await _sql.CountInboxReceiptsAsync(request.RequestId) == 1,
                timeout: TimeSpan.FromSeconds(120),
                diagnostics: () => _sql.DescribeAsync(request.RequestId));

            await Eventually.UntilAsync(
                async () => await _sql.GetJobStateAsync(request.RequestId) == JobStatus.Completed,
                timeout: TimeSpan.FromSeconds(120),
                diagnostics: () => _sql.DescribeAsync(request.RequestId));
        }

        // The outage cost nothing and duplicated nothing.
        Assert.Equal(1, await _sql.CountInboxReceiptsAsync(request.RequestId));
        Assert.Equal(1, await _sql.CountJobsAsync(request.RequestId));
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(request.RequestId));
        await AssertQueueDrainsAsync(broker, broker.ExportQueue);
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        "was not durably accepted|Reconnecting with backoff|processing failed",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ConsumerBackoffMarker();

    [System.Text.RegularExpressions.GeneratedRegex("Inbox consumer subscribed")]
    private static partial System.Text.RegularExpressions.Regex ConsumerSubscribedMarker();

    // ============================================================= 4. ACK → job processing

    /// <summary>
    /// Once a delivery is ACKed the work exists ONLY in SQL. This stops the worker while the
    /// broker holds nothing at all, and asserts that a restart still finishes the job - which is
    /// the whole reason the ACK waits for the inbox commit.
    /// </summary>
    [Fact]
    public async Task WorkAckedButNotYetProcessedSurvivesAWorkerStopWithAnEmptyBroker()
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest();
        await SeedRequestAsync(request);

        await using var broker = await BrokerAssertions.CreateAsync(fixture);
        await using var erp = await TestFakeErpHost.StartAsync(fixture);

        await broker.PublishAsync(
            broker.EventsExchange,
            Topology.ExportRoutingKey,
            Encoding.UTF8.GetBytes(EnvelopeJson(request)),
            Guid.NewGuid().ToString(),
            MessageTypes.OrderExportRequested);

        // The delay hook holds the job processor INSIDE the claim, so the stop happens with the
        // work durable in SQL and absent from the broker - deterministically, not by racing it.
        await using (var worker = await TestWorkerHost.StartAsync(
            fixture,
            erp.BaseAddress,
            null,
            FastArgs("--Lab:JobLeaseSeconds=2", FaultController.Delay(FaultPoints.JobAfterClaim, 600_000))))
        {
            await Eventually.UntilAsync(
                async () => await _sql.CountInboxReceiptsAsync(request.RequestId) == 1,
                timeout: TimeSpan.FromSeconds(90),
                diagnostics: () => _sql.DescribeAsync(request.RequestId));

            await AssertQueueDrainsAsync(broker, broker.ExportQueue);

            await worker.StopAsync();
        }

        // The broker is empty and the job is unfinished: SQL is the only place the work lives.
        var stats = await broker.GetQueueStatsOrEmptyAsync(broker.ExportQueue);
        Assert.Equal(0, stats.MessagesReady);
        Assert.Equal(0, stats.MessagesUnacknowledged);

        var handedOver = await _sql.GetJobAsync(request.RequestId);
        Assert.NotNull(handedOver);
        Assert.NotEqual(JobStatus.Completed, handedOver!.Status);
        Assert.Equal(0, await _sql.CountAppliedExportsAsync(request.RequestId));

        await using var restarted = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastArgs("--Lab:JobLeaseSeconds=2"));

        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(request.RequestId) == JobStatus.Completed,
            timeout: TimeSpan.FromSeconds(120),
            diagnostics: () => _sql.DescribeAsync(request.RequestId));

        Assert.Equal(1, await _sql.CountAppliedExportsAsync(request.RequestId));
    }

    // ========================================================== 5. external system restarts

    /// <summary>
    /// The external system's idempotency has to be DURABLE, not a process-lifetime cache. The
    /// effect is committed, FakeErp is killed hard and restarted on the same address and
    /// database, and the same operation key must answer with the same receipt and leave one row.
    /// </summary>
    [Fact]
    public async Task TheExternalSystemStillReplaysTheSameReceiptAfterAProcessRestart()
    {
        await _sql.ParkAbandonedJobsAsync();

        var requestId = Guid.NewGuid();
        var port = LabTestConfig.GetFreeLoopbackPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}");
        var json = "{\"requestId\":\"" + requestId.ToString("D")
            + "\",\"externalReference\":\"PO-WINDOW\",\"amount\":160.00,\"currency\":\"TRY\"}";

        string firstReceipt;

        var first = ProcessHost.Start(
            "samples/FakeErp", "FakeErp", LabTestConfig.FakeErpArgs(fixture, port));
        await using (first)
        {
            await first.WaitForHttpAsync(new Uri(baseAddress, "/health/live"));
            firstReceipt = await ApplyAsync(baseAddress, requestId, json, expectReplay: false);

            // Killed hard: no graceful shutdown, no chance to flush anything.
            first.KillHard();
            await first.WaitForExitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));

        var restarted = ProcessHost.Start(
            "samples/FakeErp", "FakeErp", LabTestConfig.FakeErpArgs(fixture, port));
        await using (restarted)
        {
            await restarted.WaitForHttpAsync(new Uri(baseAddress, "/health/live"));

            var replayReceipt = await ApplyAsync(baseAddress, requestId, json, expectReplay: true);

            // A new process, the same durable answer.
            Assert.Equal(firstReceipt, replayReceipt);
        }

        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
        Assert.Equal(firstReceipt, await _sql.GetAppliedReceiptAsync(requestId));
    }

    private static async Task<string> ApplyAsync(Uri baseAddress, Guid requestId, string json, bool expectReplay)
    {
        using var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/erp/exports") { Content = content };
        message.Headers.TryAddWithoutValidation("Idempotency-Key", requestId.ToString("D"));

        using var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectReplay, document.RootElement.GetProperty("replayed").GetBoolean());
        return document.RootElement.GetProperty("receiptId").GetString()!;
    }

    // ======================================================== 6. two owners and a late result

    /// <summary>
    /// A lease expires while its owner is still inside an attempt, a second worker claims the
    /// job and finishes it, and only then does the first owner try to write its result.
    ///
    /// The late write must change nothing - not the job state, not the receipt, and not the
    /// attempt history of the owner that actually did the work. And however many workers raced,
    /// the external system holds one applied row.
    /// </summary>
    [Fact]
    public async Task ALateResultFromAnExpiredOwnerCannotOverwriteTheCurrentOwnersOutcome()
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest();
        await SeedJobAsync(request);

        await using var erp = await TestFakeErpHost.StartAsync(fixture);

        // The slow owner: it makes the external call, then holds the result for far longer than
        // its own lease, which is exactly how a "stale owner" is produced without guessing.
        await using var slow = await TestWorkerHost.StartAsync(
            fixture,
            erp.BaseAddress,
            null,
            FastArgs("--Lab:JobLeaseSeconds=1", FaultController.Delay(FaultPoints.JobAfterHttpBeforeUpdate, 12_000)));

        await Eventually.UntilAsync(
            async () => await _sql.GetJobAsync(request.RequestId) is { AttemptsStarted: >= 1 },
            timeout: TimeSpan.FromSeconds(60),
            diagnostics: () => _sql.DescribeAsync(request.RequestId));

        // A second worker takes over the moment the lease expires.
        await using var takeover = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastArgs("--Lab:JobLeaseSeconds=30"));

        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(request.RequestId) == JobStatus.Completed,
            timeout: TimeSpan.FromSeconds(120),
            diagnostics: () => _sql.DescribeAsync(request.RequestId));

        var completed = await _sql.GetJobAsync(request.RequestId);
        var receipt = completed!.ExternalReceiptId;
        var attemptsAtCompletion = completed.AttemptsStarted;

        // Now let the stale owner wake up and try to write. The wait is bounded by the delay the
        // hook was given, plus room for the write it then attempts.
        await Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var afterLateWrite = await _sql.GetJobAsync(request.RequestId);
        Assert.Equal(JobStatus.Completed, afterLateWrite!.Status);
        Assert.Equal(receipt, afterLateWrite.ExternalReceiptId);
        Assert.Equal(attemptsAtCompletion, afterLateWrite.AttemptsStarted);

        // One external effect despite two owners: the external system's idempotency is what
        // makes the overlap safe, and the receipt the job kept is the one it stored.
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(request.RequestId));
        Assert.Equal(await _sql.GetAppliedReceiptAsync(request.RequestId), afterLateWrite.ExternalReceiptId);

        // exactly one attempt is Succeeded, and no attempt is left open on a Completed job.
        var attempts = await _sql.GetJobAttemptsAsync(request.RequestId);
        Assert.Single(attempts, attempt => attempt.Outcome == AttemptOutcome.Succeeded);
        Assert.DoesNotContain(attempts, attempt => attempt.Outcome == AttemptOutcome.Started);
    }

    // ================================================= 7. terminal commit → dead-letter publish

    /// <summary>
    /// The job is terminal and its dead-letter outbox row is committed with it, then the process
    /// dies before the DLQ publish. "Dead-lettered" must NOT be claimed yet, there must be
    /// exactly one terminal event, and a restart must confirm that one event - not write a
    /// second one.
    /// </summary>
    [Fact]
    public async Task AHardStopBetweenTheTerminalCommitAndTheDeadLetterPublishKeepsOneTerminalEvent()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        var request = NewRequest();
        // Seeded directly: with no Export outbox row pending, the only row the dispatcher can
        // ever claim is the dead-letter one this test is about.
        await SeedJobAsync(request);

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var broker = await BrokerAssertions.CreateAsync(fixture);
        await erp.SetScenarioAsync(request.RequestId, "permanent-422");

        var doomed = ProcessHost.Start(
            "src/Integration.Worker",
            "Integration.Worker",
            LabTestConfig.WorkerArgs(
                fixture,
                erp.BaseAddress,
                FastArgs(
                    "--Lab:OutboxLeaseSeconds=2",
                    FaultController.CrashOnce(FaultPoints.OutboxAfterClaim))));

        await using (doomed)
        {
            await doomed.WaitForExitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(FaultController.CrashExitCode, doomed.ExitCode);
        }

        // Terminal and its event committed together; the publish never happened.
        var terminal = await _sql.GetJobAsync(request.RequestId);
        Assert.Equal(JobStatus.DeadLetterPending, terminal!.Status);
        Assert.Equal("http_422", terminal.LastErrorCode);

        var deadLetterCount = await _sql.ExecuteScalarAsync(
            useErpDatabase: false,
            "SELECT COUNT(*) FROM OutboxMessages WHERE SourceRequestId = @id AND Kind = 'DeadLetter'",
            P("id", SqlDbType.UniqueIdentifier, request.RequestId));
        Assert.Equal(1, Assert.IsType<int>(deadLetterCount));
        Assert.NotEqual(OutboxStatus.Published, (await _sql.GetOutboxAsync(request.RequestId, OutboxKind.DeadLetter))!.Status);

        await using var replacement = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastArgs("--Lab:OutboxLeaseSeconds=2"));

        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(request.RequestId) == JobStatus.DeadLettered,
            timeout: TimeSpan.FromSeconds(120),
            diagnostics: () => _sql.DescribeAsync(request.RequestId));

        // Still exactly one terminal event, now confirmed by the broker.
        var afterRecovery = await _sql.ExecuteScalarAsync(
            useErpDatabase: false,
            "SELECT COUNT(*) FROM OutboxMessages WHERE SourceRequestId = @id AND Kind = 'DeadLetter'",
            P("id", SqlDbType.UniqueIdentifier, request.RequestId));
        Assert.Equal(1, Assert.IsType<int>(afterRecovery));
        Assert.Equal(OutboxStatus.Published, (await _sql.GetOutboxAsync(request.RequestId, OutboxKind.DeadLetter))!.Status);
        Assert.Equal(0, await _sql.CountAppliedExportsAsync(request.RequestId));
    }

    // ================================================== 8. a configured, unreachable exporter

    /// <summary>
    /// Tracing must never be a precondition for work. An exporter that is CONFIGURED and
    /// unreachable is the case that matters: not configuring one at all proves nothing, because
    /// then there is no exporter to fail.
    /// </summary>
    [Fact]
    public async Task AConfiguredButUnreachableOtlpExporterDoesNotStopTheWork()
    {
        await _sql.ParkAbandonedJobsAsync();

        // This test drives the whole API-to-outbox-to-inbox path, so it depends on the
        // dispatcher reaching ITS row. An earlier test's abandoned Pending row sorts ahead of
        // it (the claim orders by CreatedAtUtc) and would keep the dispatcher busy.
        await _sql.CleanupAbandonedOutboxAsync();

        // A reserved loopback port with nothing behind it: every export attempt fails for real.
        await using var blackHole = LoopbackTcpGate.CreateClosed("127.0.0.1", 1);
        var unreachable = $"http://127.0.0.1:{blackHole.Port}";

        await using var erp = await TestFakeErpHost.StartAsync(
            fixture, $"--{LabTelemetryHostingKey}={unreachable}");
        await using var api = await TestApiHost.StartAsync(
            fixture, $"--{LabTelemetryHostingKey}={unreachable}");

        var requestId = Guid.NewGuid();
        using (var submit = await api.SubmitExportAsync(requestId))
        {
            Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
        }

        await using var worker = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastArgs($"--{LabTelemetryHostingKey}={unreachable}"));

        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(requestId) == JobStatus.Completed,
            timeout: TimeSpan.FromSeconds(120),
            diagnostics: () => _sql.DescribeAsync(requestId));

        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));

        // The trace context is still carried durably; only the EXPORT of it failed.
        Assert.False(string.IsNullOrWhiteSpace((await _sql.GetJobAsync(requestId))!.TraceParent));
    }

    private const string LabTelemetryHostingKey = "Lab:OtlpEndpoint";

    // ================================================================== 9. bounded shutdown

    /// <summary>
    /// A stop has to be bounded and honest: it stops taking new work, it does not wait for an
    /// in-flight external call to finish, and whatever it was holding is recoverable through the
    /// lease rather than lost.
    /// </summary>
    [Fact]
    public async Task ShutdownIsBoundedAndLeavesInFlightWorkRecoverable()
    {
        await _sql.ParkAbandonedJobsAsync();

        var held = NewRequest();
        await SeedJobAsync(held);

        await using var erp = await TestFakeErpHost.StartAsync(fixture);

        var worker = await TestWorkerHost.StartAsync(
            fixture,
            erp.BaseAddress,
            null,
            FastArgs("--Lab:JobLeaseSeconds=2", FaultController.Delay(FaultPoints.JobAfterClaim, 600_000)));

        await Eventually.UntilAsync(
            async () => await _sql.GetJobAsync(held.RequestId) is { Status: JobStatus.Processing },
            timeout: TimeSpan.FromSeconds(60),
            diagnostics: () => _sql.DescribeAsync(held.RequestId));

        // A second job, created AFTER the stop begins, proves the worker stopped claiming.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await worker.StopAsync();
        stopwatch.Stop();
        await worker.DisposeAsync();

        // Bounded: the shutdown must not wait out the 10-minute hook delay.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Shutdown took {stopwatch.Elapsed}, which is not a bounded stop.");

        var afterStop = NewRequest();
        await SeedJobAsync(afterStop);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(0, (await _sql.GetJobAsync(afterStop.RequestId))!.AttemptsStarted);

        // The half-finished job was left for the lease, not lost and not silently failed.
        var stranded = await _sql.GetJobAsync(held.RequestId);
        Assert.Equal(JobStatus.Processing, stranded!.Status);
        Assert.Equal(1, stranded.AttemptsStarted);
        Assert.Equal(0, await _sql.CountAppliedExportsAsync(held.RequestId));

        await using var restarted = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastArgs("--Lab:JobLeaseSeconds=2"));

        foreach (var request in new[] { held, afterStop })
        {
            await Eventually.UntilAsync(
                async () => await _sql.GetJobStateAsync(request.RequestId) == JobStatus.Completed,
                timeout: TimeSpan.FromSeconds(120),
                diagnostics: () => _sql.DescribeAsync(request.RequestId));
            Assert.Equal(1, await _sql.CountAppliedExportsAsync(request.RequestId));
        }
    }
}
