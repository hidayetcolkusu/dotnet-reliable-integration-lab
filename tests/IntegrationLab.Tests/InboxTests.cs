using System.Data;
using System.Text;
using System.Text.Json;
using Integration.Shared.Contracts;
using Integration.Shared.Messaging;
using Integration.Shared.Persistence;
using Integration.Shared.Persistence.Entities;
using Integration.Shared.Runtime;
using Integration.Worker.Consuming;
using Integration.Worker.Processing;
using IntegrationLab.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Xunit;

namespace IntegrationLab.Tests;

/// <summary>
/// Task 5 acceptance: ACK means "durably accepted", never "business done". Every delivery
/// ends as either (receipt + job) or (quarantine row + dead-letter outbox), and only then is
/// it ACKed - so a poison message is never silently dropped and never requeued forever.
/// Duplicates are absorbed by identity, not by luck.
///
/// Source requests are seeded straight into SQL rather than through the API, so the ONLY
/// deliveries on the queue are the ones each test crafts: duplicate counting stays exact.
/// </summary>
[Collection("lab")]
public sealed class InboxTests(LabFixture fixture)
{
    private readonly SqlAssertions _sql = fixture.CreateSql();

    /// <summary>
    /// Consumer-focused worker: the poll loop is fast, but a claimed job parks for an hour
    /// after its first (ERP-less) attempt, so job churn never competes with these assertions.
    /// </summary>
    private static string[] ConsumerWorkerArgs(params string[] extra) =>
    [
        "--Lab:PollIntervalMilliseconds=100",
        .. LabTestConfig.Indexed("Lab:OutboxRetryDelaysSeconds", 0, 0, 0, 0, 0, 0),
        .. LabTestConfig.Indexed("Lab:JobRetryDelaysSeconds", 3600),
        .. extra,
    ];

    /// <summary>
    /// A broker handle whose export queue starts empty. The queue is shared by the whole
    /// collection, and a leftover delivery from an earlier test would be counted by the
    /// duplicate and drain assertions below as if this test had produced it.
    /// </summary>
    private async Task<BrokerAssertions> CreateCleanBrokerAsync()
    {
        var broker = await BrokerAssertions.CreateAsync(fixture);
        try
        {
            await broker.PurgeQueueAsync(broker.ExportQueue);
            return broker;
        }
        catch
        {
            await broker.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Starts the consumer and waits until the export binding exists.
    ///
    /// The applications declare the topology, and these tests publish straight to the
    /// exchange without <c>mandatory</c>. Publishing before the binding is declared would
    /// therefore be silently DISCARDED by the broker, and the test would fail with an empty
    /// quarantine table for a reason that has nothing to do with the inbox.
    /// </summary>
    private async Task<TestWorkerHost> StartConsumerAsync(BrokerAssertions broker, params string[] extra)
    {
        var worker = await TestWorkerHost.StartAsync(fixture, null, null, ConsumerWorkerArgs(extra));
        try
        {
            await Eventually.UntilAsync(
                () => broker.HasBindingAsync(broker.ExportQueue, broker.EventsExchange, Topology.ExportRoutingKey),
                timeout: TimeSpan.FromSeconds(30),
                diagnostics: () => Task.FromResult("the worker never declared the export binding"));
            return worker;
        }
        catch
        {
            // The caller never received the handle, so nothing else will dispose it. A worker
            // left running here would publish and claim for the rest of the run.
            await worker.DisposeAsync();
            throw;
        }
    }

    private static (string Name, SqlDbType Type, object? Value) P(string name, SqlDbType type, object? value) =>
        (name, type, value);

    private static ExportRequest NewRequest(Guid requestId, string reference = "PO-100", decimal amount = 160.00m) =>
        new(requestId, reference, amount, "TRY");

    private static string EnvelopeJson(ExportRequest request) => JsonSerializer.Serialize(
        new ExportEnvelope(
            request.RequestId,
            MessageTypes.OrderExportRequested,
            MessageTypes.SchemaVersion,
            DateTimeOffset.UtcNow,
            request),
        LabJson.Options);

    /// <summary>Seeds the accepted request the inbox's source check requires, without an outbox row.</summary>
    private async Task SeedSourceRequestAsync(ExportRequest request)
    {
        await _sql.ExecuteAsync(
            useErpDatabase: false,
            "INSERT INTO ExportRequests (RequestId, PayloadHash, ExternalReference, Amount, Currency, CreatedAtUtc) " +
            "VALUES (@id, @hash, @reference, @amount, @currency, SYSUTCDATETIME())",
            P("id", SqlDbType.UniqueIdentifier, request.RequestId),
            P("hash", SqlDbType.VarChar, PayloadHash.Compute(request)),
            P("reference", SqlDbType.NVarChar, request.ExternalReference),
            P("amount", SqlDbType.Decimal, request.Amount),
            P("currency", SqlDbType.VarChar, request.Currency));
    }

    private async Task PublishRawAsync(
        BrokerAssertions broker,
        string json,
        string? messageId,
        string? traceParent = null)
    {
        await broker.PublishAsync(
            broker.EventsExchange,
            Topology.ExportRoutingKey,
            Encoding.UTF8.GetBytes(json),
            messageId,
            MessageTypes.OrderExportRequested,
            traceParent);
    }

    /// <summary>Waits until the queue is empty in both senses: nothing ready, nothing unacknowledged.</summary>
    private static async Task WaitForQueueDrainedAsync(BrokerAssertions broker) =>
        await Eventually.UntilAsync(
            async () =>
            {
                var stats = await broker.GetQueueStatsAsync(broker.ExportQueue);
                return stats is { MessagesReady: 0, MessagesUnacknowledged: 0 };
            },
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: async () =>
            {
                var stats = await broker.GetQueueStatsAsync(broker.ExportQueue);
                return $"queue depth ready={stats.MessagesReady}, unacked={stats.MessagesUnacknowledged}";
            });

    private Task WaitForQuarantineAsync(string json, string reasonCode)
    {
        var bodySha = PayloadHash.OfBytes(Encoding.UTF8.GetBytes(json));
        return Eventually.UntilAsync(
            async () => await _sql.GetRejectedReasonAsync(bodySha) == reasonCode,
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: async () =>
                $"body sha256 {bodySha}: rows={await _sql.CountRejectedByBodyShaAsync(bodySha)}, " +
                $"reason={await _sql.GetRejectedReasonAsync(bodySha) ?? "<none>"}");
    }

    // ------------------------------------------------------------------ happy path

    [Fact]
    public async Task ValidDeliveryBecomesOneReceiptAndOneJob()
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest(Guid.NewGuid());
        await SeedSourceRequestAsync(request);

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        await PublishRawAsync(broker, EnvelopeJson(request), Guid.NewGuid().ToString());

        await Eventually.UntilAsync(
            async () => await _sql.CountInboxReceiptsAsync(request.RequestId) == 1,
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: () => _sql.DescribeAsync(request.RequestId));

        Assert.Equal(1, await _sql.CountJobsAsync(request.RequestId));
        await WaitForQueueDrainedAsync(broker);
    }

    // ------------------------------------------------------------------ duplicates

    [Fact]
    public async Task SameTransportMessageIdTwiceCreatesNoSecondJob()
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest(Guid.NewGuid());
        await SeedSourceRequestAsync(request);
        var json = EnvelopeJson(request);
        var transportId = Guid.NewGuid().ToString();

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        await PublishRawAsync(broker, json, transportId);
        await Eventually.UntilAsync(
            async () => await _sql.CountInboxReceiptsAsync(request.RequestId) == 1,
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: () => _sql.DescribeAsync(request.RequestId));

        // Byte-identical redelivery, as a lost ACK would produce.
        await PublishRawAsync(broker, json, transportId);
        await WaitForQueueDrainedAsync(broker);

        // The receipt primary key (ConsumerName, TransportMessageId) absorbed it.
        Assert.Equal(1, await _sql.CountInboxReceiptsAsync(request.RequestId));
        Assert.Equal(1, await _sql.CountJobsAsync(request.RequestId));
    }

    [Fact]
    public async Task DifferentTransportIdSameEventIdAddsAReceiptButNoSecondJob()
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest(Guid.NewGuid());
        await SeedSourceRequestAsync(request);
        var json = EnvelopeJson(request);

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        // Two transport identities carrying the same business event - what a republish after a
        // lost confirm looks like when the publisher does NOT reuse the outbox id.
        await PublishRawAsync(broker, json, Guid.NewGuid().ToString());
        await PublishRawAsync(broker, json, Guid.NewGuid().ToString());

        await Eventually.UntilAsync(
            async () => await _sql.CountInboxReceiptsAsync(request.RequestId) == 2,
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: () => _sql.DescribeAsync(request.RequestId));

        // Two receipts, but the job is keyed by EventId: the work is created once.
        Assert.Equal(1, await _sql.CountJobsAsync(request.RequestId));
        await WaitForQueueDrainedAsync(broker);
    }

    // ------------------------------------------------------------------ quarantine

    [Fact]
    public async Task SameEventIdWithDifferentPayloadIsQuarantinedAndLeavesTheJobUntouched()
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest(Guid.NewGuid(), amount: 160.00m);
        await SeedSourceRequestAsync(request);

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        await PublishRawAsync(broker, EnvelopeJson(request), Guid.NewGuid().ToString());
        await Eventually.UntilAsync(
            async () => await _sql.CountJobsAsync(request.RequestId) == 1,
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: () => _sql.DescribeAsync(request.RequestId));

        // Same event identity, different money. The accepted request in SQL is the authority,
        // so this is caught by the source-hash check - the outer guard in front of the
        // identity/payload branch that protects an already-created job.
        var tampered = EnvelopeJson(request with { Amount = 999.00m });
        await PublishRawAsync(broker, tampered, Guid.NewGuid().ToString());

        await WaitForQuarantineAsync(tampered, RejectionReason.SourceHashMismatch);

        // The original job is never modified and never duplicated, and the tampered delivery
        // produced no receipt of its own.
        Assert.Equal(1, await _sql.CountJobsAsync(request.RequestId));
        Assert.Equal(1, await _sql.CountInboxReceiptsAsync(request.RequestId));
        await WaitForQueueDrainedAsync(broker);
    }

    [Fact]
    public async Task MissingMessageIdIsQuarantinedAndAcked()
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest(Guid.NewGuid());
        await SeedSourceRequestAsync(request);
        var json = EnvelopeJson(request);

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        await PublishRawAsync(broker, json, messageId: null);

        await WaitForQuarantineAsync(json, RejectionReason.MissingMessageId);
        Assert.Equal(0, await _sql.CountJobsAsync(request.RequestId));
        await WaitForQueueDrainedAsync(broker);
    }

    /// <summary>
    /// a transport identity the receipt key cannot store is a QUARANTINE, not a crash.
    ///
    /// The identity is half of the InboxReceipts primary key (varchar(128)). Letting an
    /// oversized one reach the insert fails the acceptance transaction, which means no ACK,
    /// which means the broker redelivers the same unstorable message forever - a queue that
    /// never drains and a worker that never makes progress, with no quarantine row to explain
    /// it. Truncating instead would be worse: two different deliveries would collide on one key.
    /// </summary>
    [Theory]
    [InlineData(129)]
    [InlineData(255)]
    public async Task AnOversizedTransportMessageIdIsQuarantinedAndAckedInsteadOfLoopingForever(int length)
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest(Guid.NewGuid());
        await SeedSourceRequestAsync(request);
        var json = EnvelopeJson(request);
        var bodySha = PayloadHash.OfBytes(Encoding.UTF8.GetBytes(json));
        var oversizedId = new string('a', length);

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        await PublishRawAsync(broker, json, oversizedId);
        await WaitForQuarantineAsync(json, RejectionReason.UnstorableMessageId);

        // Delivered again, exactly as a redelivery would arrive: one rejection, one terminal
        // event, still no job - and the queue drains rather than cycling the message.
        await PublishRawAsync(broker, json, oversizedId);
        await WaitForQueueDrainedAsync(broker);

        var rejectionId = await _sql.GetRejectionIdAsync(bodySha);
        Assert.NotNull(rejectionId);
        Assert.Equal(1, await _sql.CountRejectedByBodyShaAsync(bodySha));
        Assert.Equal(1, await _sql.CountOutboxByRejectionAsync(rejectionId!.Value));
        Assert.Equal(0, await _sql.CountJobsAsync(request.RequestId));
        Assert.Equal(0, await _sql.CountInboxReceiptsAsync(request.RequestId));
    }

    [Fact]
    public async Task ATransportMessageIdTheReceiptColumnCannotRepresentIsQuarantined()
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest(Guid.NewGuid());
        await SeedSourceRequestAsync(request);
        var json = EnvelopeJson(request);

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        // Non-ASCII in a varchar key: SQL Server substitutes what it cannot map, so the STORED
        // identity would no longer be the identity that arrived - and two distinct ids could
        // then land on the same receipt.
        await PublishRawAsync(broker, json, "mesaj-kimliği-ç-メッセージ");

        await WaitForQuarantineAsync(json, RejectionReason.UnstorableMessageId);
        await WaitForQueueDrainedAsync(broker);
        Assert.Equal(0, await _sql.CountJobsAsync(request.RequestId));
    }

    [Fact]
    public async Task AnIdentityThatExactlyFillsTheReceiptColumnIsStillAccepted()
    {
        // The bound must be the column's, not an arbitrary smaller one: a 128-character
        // identity is legitimate and must produce ordinary work.
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest(Guid.NewGuid());
        await SeedSourceRequestAsync(request);

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        await PublishRawAsync(broker, EnvelopeJson(request), new string('b', 128));

        await Eventually.UntilAsync(
            async () => await _sql.CountInboxReceiptsAsync(request.RequestId) == 1,
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: () => _sql.DescribeAsync(request.RequestId));
        Assert.Equal(1, await _sql.CountJobsAsync(request.RequestId));
        await WaitForQueueDrainedAsync(broker);
    }

    [Fact]
    public async Task MalformedBodyIsQuarantinedNotRequeuedForever()
    {
        await _sql.ParkAbandonedJobsAsync();

        const string json = "{ this is not json";

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        await PublishRawAsync(broker, json, Guid.NewGuid().ToString());

        await WaitForQuarantineAsync(json, RejectionReason.MalformedBody);

        // Quarantined THEN acked: the queue drains instead of cycling the poison message.
        await WaitForQueueDrainedAsync(broker);
    }

    [Fact]
    public async Task UnknownTypeIsQuarantined()
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest(Guid.NewGuid());
        await SeedSourceRequestAsync(request);
        var json = EnvelopeJson(request).Replace(
            $"\"{MessageTypes.OrderExportRequested}\"",
            "\"SomethingElse\"",
            StringComparison.Ordinal);

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        await PublishRawAsync(broker, json, Guid.NewGuid().ToString());

        await WaitForQuarantineAsync(json, RejectionReason.UnknownType);
        Assert.Equal(0, await _sql.CountJobsAsync(request.RequestId));
        await WaitForQueueDrainedAsync(broker);
    }

    [Fact]
    public async Task UnsupportedSchemaVersionIsQuarantined()
    {
        await _sql.ParkAbandonedJobsAsync();

        var request = NewRequest(Guid.NewGuid());
        await SeedSourceRequestAsync(request);
        var json = EnvelopeJson(request).Replace(
            $"\"schemaVersion\":{MessageTypes.SchemaVersion}",
            "\"schemaVersion\":99",
            StringComparison.Ordinal);

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        await PublishRawAsync(broker, json, Guid.NewGuid().ToString());

        await WaitForQuarantineAsync(json, RejectionReason.UnsupportedSchema);
        Assert.Equal(0, await _sql.CountJobsAsync(request.RequestId));
        await WaitForQueueDrainedAsync(broker);
    }

    [Fact]
    public async Task EventForAnUnknownSourceRequestIsQuarantined()
    {
        await _sql.ParkAbandonedJobsAsync();

        // Deliberately NOT seeded: this lab only accepts events its own API produced.
        var request = NewRequest(Guid.NewGuid());
        var json = EnvelopeJson(request);

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        await PublishRawAsync(broker, json, Guid.NewGuid().ToString());

        await WaitForQuarantineAsync(json, RejectionReason.UnknownSourceRequest);
        Assert.Equal(0, await _sql.CountJobsAsync(request.RequestId));
        await WaitForQueueDrainedAsync(broker);
    }

    [Fact]
    public async Task RepeatedPoisonDeliveryProducesOneQuarantineRowAndOneDeadLetterEvent()
    {
        await _sql.ParkAbandonedJobsAsync();

        var json = EnvelopeJson(NewRequest(Guid.NewGuid())).Replace(
            $"\"{MessageTypes.OrderExportRequested}\"",
            "\"NotOurType\"",
            StringComparison.Ordinal);
        var bodySha = PayloadHash.OfBytes(Encoding.UTF8.GetBytes(json));
        var transportId = Guid.NewGuid().ToString();

        await using var broker = await CreateCleanBrokerAsync();
        await using var worker = await StartConsumerAsync(broker);

        await PublishRawAsync(broker, json, transportId);
        await WaitForQuarantineAsync(json, RejectionReason.UnknownType);

        var rejectionId = await _sql.GetRejectionIdAsync(bodySha);
        Assert.NotNull(rejectionId);

        // The same poison message again: same transport id, same body, same reason - the
        // fingerprint must collapse it instead of growing the quarantine and the DLQ.
        await PublishRawAsync(broker, json, transportId);
        await WaitForQueueDrainedAsync(broker);

        Assert.Equal(1, await _sql.CountRejectedByBodyShaAsync(bodySha));
        Assert.Equal(1, await _sql.CountOutboxByRejectionAsync(rejectionId!.Value));
    }

    /// <summary>
    /// Two consumers can receive the same poison message at once; both pass the "already
    /// quarantined?" read and race on the fingerprint's unique index. The loser must treat
    /// that as "already quarantined" and be ACKable, not throw and leave the delivery
    /// unacknowledged. The interceptor commits the competing row from another connection
    /// right before the acceptor's own insert, so the race happens on every run.
    /// </summary>
    [Fact]
    public async Task LosingAQuarantineRaceIsTreatedAsAlreadyQuarantined()
    {
        var body = Encoding.UTF8.GetBytes($"not json {Guid.NewGuid()}");
        var bodySha = PayloadHash.OfBytes(body);
        var competitor = new CompetingQuarantineInterceptor(fixture.IntegrationConnectionString);
        await using var context = CreateContext(competitor);
        var acceptor = CreateAcceptor(context, new NotUsedContextFactory());

        var outcome = await acceptor.AcceptAsync(
            Guid.NewGuid().ToString(), body, new BasicProperties(), CancellationToken.None);

        Assert.True(competitor.Fired, "The competing quarantine row was never inserted; the race did not happen.");
        Assert.Equal(AcceptOutcome.Quarantined, outcome);
        Assert.Equal(1, await _sql.CountRejectedByBodyShaAsync(bodySha));

        // The winner's row stands; the loser's dead-letter event was rolled back with its row.
        var rejectionId = await _sql.GetRejectionIdAsync(bodySha);
        Assert.Equal(0, await _sql.CountOutboxByRejectionAsync(rejectionId!.Value));
    }

    /// <summary>
    /// Two consumers receive the same delivery (same transport MessageId) at once; both find
    /// no receipt and no job, and race on the job and receipt keys. The loser must resolve
    /// the conflict as a replay - ACKable, no second job, no second receipt. The interceptor
    /// commits the winner's job and receipt from another context right before the acceptor's
    /// own insert, so the race happens on every run.
    /// </summary>
    [Fact]
    public async Task LosingAReceiptRaceToTheSameDeliveryIsAcceptedWithoutASecondJob()
    {
        var request = NewRequest(Guid.NewGuid());
        await SeedSourceRequestAsync(request);
        var transportId = Guid.NewGuid().ToString();

        var competitor = new CompetingDeliveryInterceptor(PlainContextOptions(), transportId);
        await using var context = CreateContext(competitor);
        var acceptor = CreateAcceptor(context, new PlainContextFactory(PlainContextOptions()));

        var outcome = await acceptor.AcceptAsync(
            transportId, Encoding.UTF8.GetBytes(EnvelopeJson(request)), new BasicProperties(), CancellationToken.None);

        Assert.True(competitor.Fired, "The competing delivery was never committed; the race did not happen.");
        Assert.Equal(AcceptOutcome.Accepted, outcome);
        Assert.Equal(1, await _sql.CountJobsAsync(request.RequestId));
        Assert.Equal(1, await _sql.CountInboxReceiptsAsync(request.RequestId));
    }

    /// <summary>
    /// A republish (new transport MessageId, same EventId) races the original delivery on the
    /// job key. The loser has no receipt of its own yet, so it must NOT be ACKed: nothing
    /// durable records it. Its redelivery then finds the winner's job and adds only a receipt.
    /// </summary>
    [Fact]
    public async Task LosingAJobKeyRaceToARepublishIsNotAckedUntilItsRedeliveryAddsAReceipt()
    {
        var request = NewRequest(Guid.NewGuid());
        await SeedSourceRequestAsync(request);
        var body = Encoding.UTF8.GetBytes(EnvelopeJson(request));
        var transportId = Guid.NewGuid().ToString();

        var competitor = new CompetingDeliveryInterceptor(PlainContextOptions(), Guid.NewGuid().ToString());
        await using var context = CreateContext(competitor);
        var acceptor = CreateAcceptor(context, new PlainContextFactory(PlainContextOptions()));

        var first = await acceptor.AcceptAsync(transportId, body, new BasicProperties(), CancellationToken.None);

        Assert.True(competitor.Fired, "The competing delivery was never committed; the race did not happen.");
        Assert.Equal(AcceptOutcome.Failed, first);
        Assert.Equal(1, await _sql.CountJobsAsync(request.RequestId));
        Assert.Equal(1, await _sql.CountInboxReceiptsAsync(request.RequestId));

        // The broker redelivers the unacknowledged message to the same acceptor.
        var redelivery = await acceptor.AcceptAsync(transportId, body, new BasicProperties(), CancellationToken.None);

        Assert.Equal(AcceptOutcome.Accepted, redelivery);
        Assert.Equal(1, await _sql.CountJobsAsync(request.RequestId));
        Assert.Equal(2, await _sql.CountInboxReceiptsAsync(request.RequestId));
    }

    private DbContextOptions<LabDbContext> PlainContextOptions() =>
        new DbContextOptionsBuilder<LabDbContext>()
            .UseSqlServer(fixture.IntegrationConnectionString)
            .Options;

    private LabDbContext CreateContext(IInterceptor competitor) =>
        new(new DbContextOptionsBuilder<LabDbContext>()
            .UseSqlServer(fixture.IntegrationConnectionString)
            .AddInterceptors(competitor)
            .Options);

    private static InboxAcceptor CreateAcceptor(LabDbContext context, IDbContextFactory<LabDbContext> contextFactory)
    {
        var labOptions = Options.Create(new LabOptions());
        return new InboxAcceptor(
            context,
            contextFactory,
            new MessageValidator(labOptions),
            new DeadLetterWriter(new Topology(Options.Create(new RabbitOptions()))),
            labOptions,
            NullLogger<InboxAcceptor>.Instance);
    }

    /// <summary>
    /// Commits a competing delivery's job and receipt from a separate context just before the
    /// acceptor's own insert. The receipt uses <paramref name="competitorTransportId"/>: the
    /// acceptor's own id for a redelivery race, a different one for a republish race.
    /// </summary>
    private sealed class CompetingDeliveryInterceptor(
        DbContextOptions<LabDbContext> plainOptions,
        string competitorTransportId) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var pending = eventData.Context!.ChangeTracker.Entries<InboxReceipt>()
                .FirstOrDefault(entry => entry.State == EntityState.Added)?.Entity;
            if (pending is null || Fired)
            {
                return result;
            }

            Fired = true;
            await using var winner = new LabDbContext(plainOptions);
            winner.IntegrationJobs.Add(new IntegrationJob
            {
                RequestId = pending.EventId,
                PayloadHash = pending.PayloadHash,
                Status = JobStatus.Pending,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            winner.InboxReceipts.Add(new InboxReceipt
            {
                ConsumerName = pending.ConsumerName,
                TransportMessageId = competitorTransportId,
                EventId = pending.EventId,
                PayloadHash = pending.PayloadHash,
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                IntegrationJobId = pending.EventId,
            });
            await winner.SaveChangesAsync(cancellationToken);
            return result;
        }
    }

    private sealed class PlainContextFactory(DbContextOptions<LabDbContext> options) : IDbContextFactory<LabDbContext>
    {
        public LabDbContext CreateDbContext() => new(options);
    }

    private sealed class CompetingQuarantineInterceptor(string connectionString) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var pending = eventData.Context!.ChangeTracker.Entries<RejectedMessage>()
                .FirstOrDefault(entry => entry.State == EntityState.Added)?.Entity;
            if (pending is null || Fired)
            {
                return result;
            }

            Fired = true;
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO RejectedMessages (Id, Fingerprint, TransportMessageId, BodySha256, BodyLength, ReasonCode, ReceivedAtUtc)
                VALUES (@id, @fingerprint, @transportId, @bodySha, @bodyLength, @reason, SYSUTCDATETIME());
                """;
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            command.Parameters.Add("@fingerprint", SqlDbType.VarChar, 128).Value = pending.Fingerprint;
            command.Parameters.Add("@transportId", SqlDbType.VarChar, 128).Value = (object?)pending.TransportMessageId ?? DBNull.Value;
            command.Parameters.Add("@bodySha", SqlDbType.VarChar, 64).Value = pending.BodySha256;
            command.Parameters.Add("@bodyLength", SqlDbType.Int).Value = pending.BodyLength;
            command.Parameters.Add("@reason", SqlDbType.VarChar, 48).Value = pending.ReasonCode;
            await command.ExecuteNonQueryAsync(cancellationToken);
            return result;
        }
    }

    private sealed class NotUsedContextFactory : IDbContextFactory<LabDbContext>
    {
        public LabDbContext CreateDbContext() =>
            throw new InvalidOperationException("The quarantine path must not need a second context.");
    }
}
