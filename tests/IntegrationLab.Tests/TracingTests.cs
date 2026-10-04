using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Integration.Shared.Contracts;
using Integration.Shared.Diagnostics;
using Integration.Shared.Messaging;
using Integration.Shared.Persistence.Entities;
using IntegrationLab.Tests.Support;
using Xunit;

namespace IntegrationLab.Tests;

/// <summary>
/// Task 10 acceptance: one request is one trace, even though it crosses an HTTP hop, a SQL
/// table, a broker and a retry loop. The trace context is carried DURABLY (an outbox column,
/// a job column) rather than in memory, which is why it survives a restart - and a malformed
/// stored value degrades to a fresh root span instead of poisoning the work.
/// </summary>
[Collection("lab")]
public sealed class TracingTests(LabFixture fixture)
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
    /// Records this repo's spans in-process. Without a listener an ActivitySource produces
    /// nothing at all, so the listener is what makes the spans observable to the assertions -
    /// it is not what makes the trace context propagate.
    /// </summary>
    private sealed class SpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;

        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == LabTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Stopped.Enqueue(activity),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentQueue<Activity> Stopped { get; } = new();

        public IEnumerable<Activity> For(Guid requestId) => Stopped
            .Where(activity => activity.GetTagItem(LabTelemetry.Tags.RequestId) as string == requestId.ToString());

        public void Dispose() => _listener.Dispose();
    }

    private static string TraceIdOf(string traceParent) => traceParent.Split('-')[1];

    [Fact]
    public async Task OneRequestProducesOneTraceAcrossApiOutboxInboxAndTheExternalCall()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        using var spans = new SpanRecorder();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        // The API's trace context is committed with the outbox row, in the same transaction
        // as the work itself - so it cannot be lost independently of the message.
        var outbox = await _sql.GetOutboxTraceParentAsync(requestId);
        Assert.False(string.IsNullOrWhiteSpace(outbox));
        var traceId = TraceIdOf(outbox!);

        await using var worker = await TestWorkerHost.StartAsync(fixture, erp.BaseAddress, null, FastArgs());

        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(requestId) == JobStatus.Completed,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(requestId));

        // The consumer read the context off the wire and stored it on the job row, so a later
        // retry - in another process, hours later - still belongs to the original trace.
        var job = await _sql.GetJobAsync(requestId);
        Assert.False(string.IsNullOrWhiteSpace(job!.TraceParent));
        Assert.Equal(traceId, TraceIdOf(job.TraceParent!));

        var recorded = spans.For(requestId).ToList();
        var names = recorded.Select(activity => activity.OperationName).Distinct(StringComparer.Ordinal).ToList();
        Assert.Contains(LabTelemetry.Spans.ApiReceive, names);
        Assert.Contains(LabTelemetry.Spans.OutboxPublish, names);
        Assert.Contains(LabTelemetry.Spans.InboxPersist, names);
        Assert.Contains(LabTelemetry.Spans.ExportHttpAttempt, names);

        // Every stage of the hop belongs to the SAME trace, which is the whole point.
        Assert.Equal([traceId], recorded.Select(activity => activity.TraceId.ToHexString()).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryStageTagsTheRequestIdSoATraceCanBeFoundFromABusinessId()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        using var spans = new SpanRecorder();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);

        await using var worker = await TestWorkerHost.StartAsync(fixture, erp.BaseAddress, null, FastArgs());
        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(requestId) == JobStatus.Completed,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(requestId));

        // The publish span additionally carries the transport identity, which is what turns a
        // broker-side message id back into a trace during an incident.
        var publish = Assert.Single(
            spans.For(requestId),
            a => a.OperationName == LabTelemetry.Spans.OutboxPublish);
        var outboxId = publish.GetTagItem(LabTelemetry.Tags.OutboxId) as string;
        Assert.False(string.IsNullOrWhiteSpace(outboxId));
        Assert.Equal(outboxId, publish.GetTagItem(LabTelemetry.Tags.TransportMessageId) as string);

        var inbox = Assert.Single(
            spans.For(requestId),
            a => a.OperationName == LabTelemetry.Spans.InboxPersist);
        Assert.Equal(outboxId, inbox.GetTagItem(LabTelemetry.Tags.TransportMessageId) as string);
        Assert.Equal(requestId.ToString(), inbox.GetTagItem(LabTelemetry.Tags.EventId) as string);
    }

    /// <summary>
    /// trace metadata that cannot be stored is degraded, never fatal.
    ///
    /// A tracestate longer than its column (256) and a tracestate arriving WITHOUT a parent are
    /// both realistic on a wire this lab does not control. Either one reaching an INSERT ends
    /// the acceptance transaction, and since the ACK waits for that commit, the delivery is
    /// redelivered forever - diagnostics would have stopped the business work. The parent link
    /// is kept where it is usable, because losing vendor state is cheaper than losing the trace.
    /// </summary>
    [Fact]
    public async Task TraceMetadataTooLargeForItsColumnIsDroppedWithoutStoppingTheWork()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var broker = await BrokerAssertions.CreateAsync(fixture);
        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var worker = await TestWorkerHost.StartAsync(fixture, erp.BaseAddress, null, FastArgs());

        // Case 1: a usable parent carrying a tracestate far past the column's 256 characters.
        var oversized = new ExportRequest(Guid.NewGuid(), "PO-100", 160.00m, "TRY");
        // Case 2: vendor state with no parent to attach it to.
        var orphanState = new ExportRequest(Guid.NewGuid(), "PO-100", 160.00m, "TRY");

        foreach (var request in new[] { oversized, orphanState })
        {
            await _sql.ExecuteAsync(
                useErpDatabase: false,
                "INSERT INTO ExportRequests (RequestId, PayloadHash, ExternalReference, Amount, Currency, CreatedAtUtc) " +
                "VALUES (@id, @hash, 'PO-100', 160.00, 'TRY', SYSUTCDATETIME())",
                ("id", System.Data.SqlDbType.UniqueIdentifier, request.RequestId),
                ("hash", System.Data.SqlDbType.VarChar, PayloadHash.Compute(request)));
        }

        const string validParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var hugeState = "vendor=" + new string('x', 4000);

        await broker.PublishAsync(
            broker.EventsExchange,
            Topology.ExportRoutingKey,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new ExportEnvelope(
                    oversized.RequestId, MessageTypes.OrderExportRequested, MessageTypes.SchemaVersion,
                    DateTimeOffset.UtcNow, oversized),
                LabJson.Options)),
            Guid.NewGuid().ToString(),
            MessageTypes.OrderExportRequested,
            traceParent: validParent,
            traceState: hugeState);

        await broker.PublishAsync(
            broker.EventsExchange,
            Topology.ExportRoutingKey,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new ExportEnvelope(
                    orphanState.RequestId, MessageTypes.OrderExportRequested, MessageTypes.SchemaVersion,
                    DateTimeOffset.UtcNow, orphanState),
                LabJson.Options)),
            Guid.NewGuid().ToString(),
            MessageTypes.OrderExportRequested,
            traceState: "vendor=orphan");

        foreach (var request in new[] { oversized, orphanState })
        {
            await Eventually.UntilAsync(
                async () => await _sql.GetJobStateAsync(request.RequestId) == JobStatus.Completed,
                timeout: TimeSpan.FromSeconds(90),
                diagnostics: () => _sql.DescribeAsync(request.RequestId));
            Assert.Equal(1, await _sql.CountAppliedExportsAsync(request.RequestId));

            // One delivery, one receipt: no truncation error and therefore no requeue loop.
            Assert.Equal(1, await _sql.CountInboxReceiptsAsync(request.RequestId));
        }

        // The usable half of the context survived; the unusable half was dropped, not stored.
        var kept = await _sql.GetJobAsync(oversized.RequestId);
        Assert.Equal(validParent, kept!.TraceParent);
        var storedState = await _sql.ExecuteScalarAsync(
            useErpDatabase: false,
            "SELECT TraceState FROM IntegrationJobs WHERE RequestId = @id",
            ("id", System.Data.SqlDbType.UniqueIdentifier, oversized.RequestId));
        Assert.True(storedState is null or DBNull);

        // A tracestate with no parent is meaningless on its own and is dropped with it.
        Assert.Null((await _sql.GetJobAsync(orphanState.RequestId))!.TraceParent);
    }

    [Fact]
    public async Task AMalformedTraceParentDegradesToAFreshTraceInsteadOfPoisoningTheWork()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        using var spans = new SpanRecorder();

        var request = new ExportRequest(Guid.NewGuid(), "PO-100", 160.00m, "TRY");
        await _sql.ExecuteAsync(
            useErpDatabase: false,
            "INSERT INTO ExportRequests (RequestId, PayloadHash, ExternalReference, Amount, Currency, CreatedAtUtc) " +
            "VALUES (@id, @hash, 'PO-100', 160.00, 'TRY', SYSUTCDATETIME())",
            ("id", System.Data.SqlDbType.UniqueIdentifier, request.RequestId),
            ("hash", System.Data.SqlDbType.VarChar, PayloadHash.Compute(request)));

        await using var broker = await BrokerAssertions.CreateAsync(fixture);
        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var worker = await TestWorkerHost.StartAsync(fixture, erp.BaseAddress, null, FastArgs());

        var envelope = new ExportEnvelope(
            request.RequestId,
            MessageTypes.OrderExportRequested,
            MessageTypes.SchemaVersion,
            DateTimeOffset.UtcNow,
            request);

        // A header no W3C parser can accept. It must be dropped, not propagated and not fatal.
        await broker.PublishAsync(
            broker.EventsExchange,
            Topology.ExportRoutingKey,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, LabJson.Options)),
            Guid.NewGuid().ToString(),
            MessageTypes.OrderExportRequested,
            traceParent: "definitely-not-a-traceparent");

        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(request.RequestId) == JobStatus.Completed,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(request.RequestId));

        // The work completed, and the unusable context was discarded rather than stored.
        var job = await _sql.GetJobAsync(request.RequestId);
        Assert.Null(job!.TraceParent);
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(request.RequestId));

        // The inbox still produced a span; it is simply a fresh root rather than a child.
        var inbox = Assert.Single(
            spans.For(request.RequestId),
            a => a.OperationName == LabTelemetry.Spans.InboxPersist);
        Assert.Equal(default(ActivitySpanId).ToHexString(), inbox.ParentSpanId.ToHexString());
    }
}
