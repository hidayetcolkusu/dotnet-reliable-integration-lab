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
/// Task 9 acceptance: "dead-lettered" is a claim about a CONFIRMED publish, so the terminal
/// state and its terminal event are written in one transaction and the job only reaches
/// DeadLettered once the broker confirmed the DLQ message. The envelope carries hashes,
/// sizes and reason codes - never a raw transport body and never exception text.
/// </summary>
[Collection("lab")]
public sealed class DeadLetterTests(LabFixture fixture)
{
    private readonly SqlAssertions _sql = fixture.CreateSql();

    private static string[] FastArgs(params string[] extra) =>
    [
        "--Lab:PollIntervalMilliseconds=100",
        .. LabTestConfig.Indexed("Lab:OutboxRetryDelaysSeconds", 0, 0, 0, 0, 0, 0),
        .. LabTestConfig.Indexed("Lab:JobRetryDelaysSeconds", 0, 0, 0, 0),
        .. extra,
    ];

    /// <summary>
    /// The dead-letter queue is shared by the whole collection, so every test starts from an
    /// empty queue and still matches its own message by correlation id.
    /// </summary>
    private static async Task<BrokerAssertions> CreateCleanBrokerAsync(LabFixture fixture)
    {
        var broker = await BrokerAssertions.CreateAsync(fixture);
        try
        {
            await broker.PurgeQueueAsync(broker.DeadQueue);
            return broker;
        }
        catch
        {
            await broker.DisposeAsync();
            throw;
        }
    }

    private static async Task<BrokerMessage> WaitForDeadLetterAsync(BrokerAssertions broker, Guid correlationId)
    {
        var collected = new List<BrokerMessage>();
        await Eventually.UntilAsync(
            async () =>
            {
                collected.AddRange(await broker.DrainDeadLetterAsync());
                return collected.Any(m => m.CorrelationId == correlationId.ToString());
            },
            timeout: TimeSpan.FromSeconds(45),
            diagnostics: () => Task.FromResult(
                $"drained {collected.Count} dead letters, none correlated to {correlationId}"));

        // First, not Single: a crash between confirm and update republishes the SAME outbox row,
        // so a duplicate dead letter is an expected outcome of this lab, not a test failure.
        return collected.First(m => m.CorrelationId == correlationId.ToString());
    }

    private static JsonElement ParseEnvelope(BrokerMessage message)
    {
        using var document = JsonDocument.Parse(message.Body);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task ATerminalJobPublishesExactlyOneDeadLetterEventAndOnlyThenIsDeadLettered()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var broker = await CreateCleanBrokerAsync(fixture);
        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        await erp.SetScenarioAsync(requestId, "permanent-422");
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        await using var worker = await TestWorkerHost.StartAsync(fixture, erp.BaseAddress, null, FastArgs());

        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(requestId) == JobStatus.DeadLettered,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(requestId));

        // DeadLettered is only reached through a confirmed DLQ publish.
        var deadLetterOutbox = await _sql.GetOutboxAsync(requestId, OutboxKind.DeadLetter);
        Assert.Equal(OutboxStatus.Published, deadLetterOutbox!.Status);
        Assert.NotNull(deadLetterOutbox.PublishedAtUtc);

        var message = await WaitForDeadLetterAsync(broker, requestId);
        Assert.Equal(MessageTypes.OrderExportFailed, message.Type);

        var envelope = ParseEnvelope(message);
        Assert.Equal(requestId.ToString(), envelope.GetProperty("requestId").GetString());
        Assert.Equal("permanent_failure", envelope.GetProperty("reason").GetString());
        Assert.Equal("http_422", envelope.GetProperty("errorCode").GetString());
        Assert.Equal(1, envelope.GetProperty("attemptsStarted").GetInt32());

        // The business summary travels; the raw transport body never does.
        Assert.Equal("PO-100", envelope.GetProperty("data").GetProperty("externalReference").GetString());
        Assert.Equal(0, await _sql.CountAppliedExportsAsync(requestId));
    }

    [Fact]
    public async Task ACrashBetweenDeadLetterConfirmAndUpdateStillClosesTheTerminalState()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var broker = await CreateCleanBrokerAsync(fixture);
        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        await erp.SetScenarioAsync(requestId, "permanent-422");
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        // Real child process: the DLQ publish is confirmed, then the process dies before the
        // job and the outbox row learn about it.
        var crashArgs = LabTestConfig.WorkerArgs(
            fixture,
            erp.BaseAddress,
            [
                .. FastArgs("--Lab:OutboxLeaseSeconds=1"),
                FaultController.CrashOnce(FaultPoints.DeadLetterAfterConfirmBeforeUpdate),
            ]);
        var crashed = ProcessHost.Start("src/Integration.Worker", "Integration.Worker", crashArgs);
        await using (crashed)
        {
            await crashed.WaitForExitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(FaultController.CrashExitCode, crashed.ExitCode);
        }

        // The confirmed event did not make the job terminal: the claim is the only authority.
        Assert.Equal(JobStatus.DeadLetterPending, await _sql.GetJobStateAsync(requestId));

        await using var restarted = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastArgs("--Lab:OutboxLeaseSeconds=1"));

        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(requestId) == JobStatus.DeadLettered,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(requestId));

        Assert.Equal(OutboxStatus.Published, (await _sql.GetOutboxAsync(requestId, OutboxKind.DeadLetter))!.Status);

        // The republish reuses the same outbox id as the transport MessageId, so a duplicate
        // dead letter is recognisable as the same event rather than a second failure.
        var message = await WaitForDeadLetterAsync(broker, requestId);
        Assert.Equal(MessageTypes.OrderExportFailed, message.Type);
    }

    [Fact]
    public async Task AQuarantinedMessageProducesOneDeadLetterEventWithoutItsRawBody()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var broker = await CreateCleanBrokerAsync(fixture);
        await using var worker = await TestWorkerHost.StartAsync(fixture, null, null, FastArgs());

        // The topology is declared by the worker, and this test publishes straight to the
        // exchange without mandatory:true. Publishing before the binding exists would be
        // silently discarded by the broker and the quarantine table would stay empty.
        await Eventually.UntilAsync(
            () => broker.HasBindingAsync(broker.ExportQueue, broker.EventsExchange, Topology.ExportRoutingKey),
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: () => Task.FromResult("the worker never declared the export binding"));

        // A poison body with a distinctive marker: whatever reaches the DLQ must not contain it.
        const string secret = "TOTALLY-SECRET-PAYLOAD-MARKER";
        var json = $$"""{"eventId":"not-a-guid","type":"OrderExportRequested","secret":"{{secret}}"}""";
        var bodySha = PayloadHash.OfBytes(Encoding.UTF8.GetBytes(json));
        var transportId = Guid.NewGuid();

        await broker.PublishAsync(
            broker.EventsExchange,
            Topology.ExportRoutingKey,
            Encoding.UTF8.GetBytes(json),
            transportId.ToString(),
            MessageTypes.OrderExportRequested);

        await Eventually.UntilAsync(
            async () => await _sql.GetRejectedReasonAsync(bodySha) == RejectionReason.MalformedBody,
            timeout: TimeSpan.FromSeconds(45),
            diagnostics: async () => $"quarantine rows for {bodySha}: {await _sql.CountRejectedByBodyShaAsync(bodySha)}");

        var rejectionId = await _sql.GetRejectionIdAsync(bodySha);
        Assert.NotNull(rejectionId);

        // Exactly one terminal event per rejection, and the rejection is only marked published
        // once that event was confirmed.
        await Eventually.UntilAsync(
            async () => await _sql.GetRejectionPublishedAtAsync(rejectionId!.Value) is not null,
            timeout: TimeSpan.FromSeconds(45),
            diagnostics: async () => $"rejection {rejectionId}: outbox rows={await _sql.CountOutboxByRejectionAsync(rejectionId!.Value)}");
        Assert.Equal(1, await _sql.CountOutboxByRejectionAsync(rejectionId!.Value));

        var deadLetters = new List<BrokerMessage>();
        await Eventually.UntilAsync(
            async () =>
            {
                deadLetters.AddRange(await broker.DrainDeadLetterAsync());
                return deadLetters.Count > 0;
            },
            timeout: TimeSpan.FromSeconds(45),
            diagnostics: () => Task.FromResult("no dead letter arrived for the quarantined message"));

        var message = Assert.Single(deadLetters);
        var text = Encoding.UTF8.GetString(message.Body);
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-guid", text, StringComparison.Ordinal);

        // What travels instead: the reason code, the body hash and the body length.
        var envelope = ParseEnvelope(message);
        Assert.Equal(RejectionReason.MalformedBody, envelope.GetProperty("reason").GetString());
        Assert.Equal(bodySha, envelope.GetProperty("payloadHash").GetString());
        Assert.Equal(json.Length, envelope.GetProperty("bodyLength").GetInt64());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task ABrokerOutageDelaysTheDeadLetterEventButNeverLosesIt()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        // Purged through a handle that is then released: this test stops the broker container,
        // and an AMQP connection held across that restart is closed by the peer and cannot be
        // reused afterwards.
        // The purge is the point; its effect is not asserted here, because the management API
        // refreshes message counts on a statistics interval and would report a stale depth.
        // This test identifies its own dead letter by correlation id instead.
        await using (var setup = await CreateCleanBrokerAsync(fixture))
        {
            await Task.CompletedTask;
        }

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        await erp.SetScenarioAsync(requestId, "permanent-422");
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        await using var worker = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastArgs("--Lab:OutboxLeaseSeconds=1"));

        // Let the job reach its terminal state first; the DLQ event is now durable in SQL.
        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(requestId)
                is JobStatus.DeadLetterPending or JobStatus.DeadLettered,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(requestId));

        await fixture.StopRabbitAsync();
        try
        {
            // With the broker gone the job cannot be closed out - and it must not be.
            await Task.Delay(TimeSpan.FromSeconds(2));
            await _sql.ExecuteAsync(
                useErpDatabase: false,
                "UPDATE IntegrationJobs SET Status = 'DeadLetterPending' WHERE RequestId = @id AND Status = 'DeadLettered'",
                ("id", System.Data.SqlDbType.UniqueIdentifier, requestId));
            await _sql.ExecuteAsync(
                useErpDatabase: false,
                "UPDATE OutboxMessages SET Status = 'Pending', PublishedAtUtc = NULL, LeaseToken = NULL, " +
                "LeaseUntilUtc = NULL, NextAttemptAtUtc = SYSUTCDATETIME() WHERE SourceRequestId = @id AND Kind = 'DeadLetter'",
                ("id", System.Data.SqlDbType.UniqueIdentifier, requestId));

            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.NotEqual(OutboxStatus.Published, (await _sql.GetOutboxAsync(requestId, OutboxKind.DeadLetter))!.Status);
            Assert.Equal(JobStatus.DeadLetterPending, await _sql.GetJobStateAsync(requestId));
        }
        finally
        {
            await fixture.StartRabbitAsync();
        }

        // The event was never lost: it was waiting in SQL for the broker to come back.
        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(requestId) == JobStatus.DeadLettered,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(requestId));

        // A fresh handle, because the broker restarted in between.
        await using var broker = await BrokerAssertions.CreateAsync(fixture);
        var message = await WaitForDeadLetterAsync(broker, requestId);
        Assert.Equal(MessageTypes.OrderExportFailed, message.Type);
    }
}
