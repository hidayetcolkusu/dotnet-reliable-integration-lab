using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Integration.Shared.Messaging;
using Integration.Shared.Runtime;
using IntegrationLab.Tests.Support;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Xunit;

namespace IntegrationLab.Tests;

/// <summary>
/// Task 3 acceptance plus the task 1 dependency smoke: the API accepts work while the
/// broker is down, writes request+outbox atomically, and every error path speaks
/// problem+json with a testable errorCode.
/// </summary>
[Collection("lab")]
public sealed class ApiTests(LabFixture fixture)
{
    private readonly SqlAssertions _sql = fixture.CreateSql();

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    // ---------------------------------------------------------------- task 1 smoke

    [Fact]
    public async Task BothDatabasesAndAllTablesExist()
    {
        var integrationTables = await _sql.ExecuteScalarAsync(
            useErpDatabase: false,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN " +
            "('ExportRequests','OutboxMessages','InboxReceipts','IntegrationJobs','JobAttempts','RejectedMessages')");
        var erpTables = await _sql.ExecuteScalarAsync(
            useErpDatabase: true,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'AppliedExports'");

        Assert.Equal(6, Convert.ToInt32(integrationTables, CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(erpTables, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task BrokerTopologyDeclaresIdempotently()
    {
        var topology = new Topology(Options.Create(new RabbitOptions
        {
            NamePrefix = fixture.RabbitNamePrefix,
        }));

        var factory = new ConnectionFactory
        {
            HostName = fixture.RabbitHostName,
            Port = fixture.RabbitAmqpPort,
            UserName = fixture.RabbitUserName,
            Password = fixture.RabbitPassword,
            VirtualHost = "/",
        };
        await using var connection = await factory.CreateConnectionAsync(
            "topology-smoke",
            TestContext.Current.CancellationToken);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                outstandingPublisherConfirmationsRateLimiter: null,
                consumerDispatchConcurrency: 1));

        await topology.DeclareAsync(channel, TestContext.Current.CancellationToken);
        await topology.DeclareAsync(channel, TestContext.Current.CancellationToken); // reconnects re-declare; must stay idempotent

        await using var broker = await BrokerAssertions.CreateAsync(fixture);

        // Re-declaring must not resurrect or duplicate messages. The queues are shared by the
        // whole collection, so they are emptied first: the claim under test is "declaring twice
        // changes nothing", not "no other test ever left a message here".
        await broker.PurgeQueueAsync(broker.ExportQueue);
        await broker.PurgeQueueAsync(broker.DeadQueue);
        await topology.DeclareAsync(channel, TestContext.Current.CancellationToken);

        // Polled, not read once: the management API's message counts are refreshed on a
        // statistics interval, so a fresh purge is not visible immediately.
        await Eventually.UntilAsync(
            async () =>
            {
                var export = await broker.GetQueueStatsAsync(broker.ExportQueue);
                var dead = await broker.GetQueueStatsAsync(broker.DeadQueue);
                return export.MessagesReady + export.MessagesUnacknowledged == 0
                    && dead.MessagesReady + dead.MessagesUnacknowledged == 0;
            },
            timeout: TimeSpan.FromSeconds(30),
            diagnostics: async () =>
            {
                var export = await broker.GetQueueStatsAsync(broker.ExportQueue);
                var dead = await broker.GetQueueStatsAsync(broker.DeadQueue);
                return $"export ready={export.MessagesReady}/unacked={export.MessagesUnacknowledged}, " +
                    $"dead ready={dead.MessagesReady}/unacked={dead.MessagesUnacknowledged}";
            });
    }

    /// <summary>
    /// SQL unreachable, proven by pointing the host at a closed loopback port rather than by
    /// stopping the shared container: deterministic, fast, and it exercises the same code path.
    /// Liveness must stay green (the process answers), readiness must go red (it cannot work),
    /// and the error body must carry a safe code - never a connection string.
    /// </summary>
    [Fact]
    public async Task WhenSqlIsUnreachableLivenessStaysGreenReadinessGoesRedAndNothingLeaks()
    {
        var deadPort = LabTestConfig.GetFreeLoopbackPort();
        var unreachable = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = $"127.0.0.1,{deadPort}",
            InitialCatalog = "IntegrationLab_does_not_exist",
            UserID = "sa",
            Password = "Unused_Placeholder#2026",
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 2,
        }.ConnectionString;

        await using var api = await TestApiHost.StartAsync(
            fixture,
            $"--ConnectionStrings:IntegrationLab={unreachable}");

        // Liveness deliberately has no dependencies: a database outage must not make an
        // orchestrator kill an otherwise healthy process.
        using var live = await api.Client.GetAsync("/health/live");
        Assert.True(live.IsSuccessStatusCode);

        using var ready = await api.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

        using var response = await api.SubmitExportAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await ReadBodyAsync(response);
        Assert.Contains("\"errorCode\":\"internal_error\"", body, StringComparison.Ordinal);

        // Nothing about the connection, the credentials or the driver internals may travel.
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unused_Placeholder", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Source", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"{deadPort}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthLiveHasNoDependenciesAndReadyChecksSql()
    {
        await using var api = await TestApiHost.StartAsync(fixture);

        using var live = await api.Client.GetAsync("/health/live");
        Assert.True(live.IsSuccessStatusCode);

        using var ready = await api.Client.GetAsync("/health/ready");
        Assert.True(ready.IsSuccessStatusCode);
    }

    // ---------------------------------------------------------------- task 3 acceptance

    [Fact]
    public async Task SubmitAcceptsWithBrokerDownAndWorkWaitsInSql()
    {
        // A real broker outage: the container is stopped. The API has no broker dependency,
        // so the accepted work waits in SQL until the dispatcher can publish it.
        await fixture.StopRabbitAsync();
        try
        {
            await using var api = await TestApiHost.StartAsync(fixture);
            var requestId = Guid.NewGuid();

            using var response = await api.SubmitExportAsync(requestId);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal($"/api/exports/{requestId:D}", response.Headers.Location?.ToString());

            Assert.Equal(1, await _sql.CountExportRequestsAsync(requestId));
            var outbox = await _sql.GetOutboxAsync(requestId, "Export");
            Assert.NotNull(outbox);
            Assert.Equal("Pending", outbox!.Status);
            Assert.Equal(0, await _sql.CountJobsAsync(requestId));
        }
        finally
        {
            await fixture.StartRabbitAsync();
        }
    }

    [Fact]
    public async Task FaultAfterRequestInsertRollsBackRequestAndOutbox()
    {
        await using var api = await TestApiHost.StartAsync(
            fixture,
            FaultController.ThrowOnce(FaultPoints.ApiAfterRequestInsert));
        var requestId = Guid.NewGuid();

        using var response = await api.SubmitExportAsync(requestId);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(response);
        Assert.Equal("internal_error", body.GetProperty("errorCode").GetString());

        Assert.Equal(0, await _sql.CountExportRequestsAsync(requestId));
        Assert.Null(await _sql.GetOutboxAsync(requestId, "Export"));
    }

    [Fact]
    public async Task TenParallelSameRequestIdProduceOneRequestAndOneOutbox()
    {
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => api.SubmitExportAsync(requestId)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        Assert.Equal(1, await _sql.CountExportRequestsAsync(requestId));

        var outbox = await _sql.GetOutboxAsync(requestId, "Export");
        Assert.NotNull(outbox);
        Assert.Equal("Pending", outbox!.Status);
    }

    [Fact]
    public async Task SameRequestIdWithDifferentPayloadIsAConflict()
    {
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        using var first = await api.SubmitExportAsync(requestId, amount: 160.00m);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        using var second = await api.SubmitExportAsync(requestId, amount: 200.00m);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(second);
        Assert.Equal("duplicate_conflict", body.GetProperty("errorCode").GetString());
        Assert.Equal(1, await _sql.CountExportRequestsAsync(requestId));
    }

    [Fact]
    public async Task SameRequestIdSamePayloadReplaysTheSameAcceptance()
    {
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        using var first = await api.SubmitExportAsync(requestId);
        using var second = await api.SubmitExportAsync(requestId);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(1, await _sql.CountExportRequestsAsync(requestId));
        var outbox = await _sql.GetOutboxAsync(requestId, "Export");
        Assert.NotNull(outbox);
        Assert.Equal("Pending", outbox!.Status);
    }

    [Fact]
    public async Task ValidationErrorsCarryStableErrorCodes()
    {
        await using var api = await TestApiHost.StartAsync(fixture);

        string Json(
            string requestIdField,
            string amountField,
            string referenceField = "\"PO-100\"",
            string currencyField = "\"TRY\"",
            string? extraField = null) =>
            "{\"requestId\":" + requestIdField
            + ",\"externalReference\":" + referenceField
            + ",\"amount\":" + amountField
            + ",\"currency\":" + currencyField
            + (extraField is null ? string.Empty : "," + extraField)
            + "}";

        var cases = new (string Json, string ExpectedErrorCode)[]
        {
            (Json($"\"{Guid.NewGuid():D}\"", "160.001"), "amount_precision"),
            (Json($"\"{Guid.NewGuid():D}\"", "160.00", currencyField: "\"EUR\""), "validation_failed"),
            (Json($"\"{Guid.NewGuid():D}\"", "0"), "validation_failed"),
            (Json($"\"{Guid.NewGuid():D}\"", "160.00", referenceField: "\"\""), "validation_failed"),
            (Json($"\"{Guid.NewGuid():D}\"", "160.00", extraField: "\"extra\":\"nope\""), "unknown_field"),
            (Json("\"not-a-guid\"", "160.00"), "validation_failed"),
            ("{\"requestId\":", "malformed_body"),
        };

        foreach (var (json, expectedErrorCode) in cases)
        {
            using var response = await api.SubmitAsync(json);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await ReadJsonAsync(response);
            Assert.Equal(expectedErrorCode, body.GetProperty("errorCode").GetString());
            Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        }
    }

    /// <summary>
    /// the durable column is decimal(18,2), and the contract has to say so at the edge.
    /// A larger CLR decimal used to pass "positive, two decimals" and then fail as a SQL
    /// arithmetic overflow inside the acceptance transaction - a 500 where the API's own error
    /// contract promises a safe, field-level 400, and an exception text where a reason code
    /// belongs.
    /// </summary>
    [Fact]
    public async Task AnAmountLargerThanTheExternalContractIsAValidationErrorNotAServerError()
    {
        await using var api = await TestApiHost.StartAsync(fixture);

        foreach (var amountLiteral in new[]
        {
            // One cent past decimal(18,2), and the largest value a CLR decimal can hold.
            "10000000000000000.00",
            "79228162514264337593543950335",
        })
        {
            var requestId = Guid.NewGuid();
            var json = "{\"requestId\":\"" + requestId.ToString("D")
                + "\",\"externalReference\":\"PO-100\",\"amount\":" + amountLiteral
                + ",\"currency\":\"TRY\"}";

            using var response = await api.SubmitAsync(json);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await ReadJsonAsync(response);
            Assert.Equal("validation_failed", body.GetProperty("errorCode").GetString());

            // Refused before anything durable exists: no request row, and therefore no outbox
            // row that a dispatcher would try to publish.
            Assert.Equal(0, await _sql.CountExportRequestsAsync(requestId));
            Assert.Null(await _sql.GetOutboxAsync(requestId, Integration.Shared.Persistence.Entities.OutboxKind.Export));
        }
    }

    [Fact]
    public async Task TheLargestAmountTheContractAllowsIsStillAccepted()
    {
        // The mirror image: the bound must be the column's limit, not an arbitrary smaller one.
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        using var response = await api.SubmitExportAsync(
            requestId, amount: Integration.Shared.Contracts.ExportLimits.MaxAmount);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await _sql.CountExportRequestsAsync(requestId));

        var stored = await _sql.ExecuteScalarAsync(
            useErpDatabase: false,
            "SELECT Amount FROM ExportRequests WHERE RequestId = @id",
            ("id", System.Data.SqlDbType.UniqueIdentifier, requestId));
        Assert.Equal(Integration.Shared.Contracts.ExportLimits.MaxAmount, Assert.IsType<decimal>(stored));
    }

    [Fact]
    public async Task ValidationProblemsIgnoreTheAcceptHeader()
    {
        await using var api = await TestApiHost.StartAsync(fixture);

        using var response = await api.SubmitAsync(
            $$"""{"requestId":"{{Guid.NewGuid():D}}","externalReference":"PO-100","amount":160.001,"currency":"TRY"}""",
            accept: "text/plain");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(response);
        Assert.Equal("amount_precision", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task MissingJsonContentTypeIsRejected()
    {
        await using var api = await TestApiHost.StartAsync(fixture);
        using var content = new StringContent("{}", Encoding.UTF8, "text/plain");
        using var response = await api.Client.PostAsync("/api/exports", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(response);
        Assert.Equal("unsupported_media_type", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task OversizedBodyIsRejectedBeforeParsing()
    {
        await using var api = await TestApiHost.StartAsync(fixture);
        var padding = new string('x', 70 * 1024);
        var json = $$"""{"requestId":"{{Guid.NewGuid():D}}","externalReference":"{{padding}}","amount":1.00,"currency":"TRY"}""";

        using var response = await api.SubmitAsync(json);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("payload_too_large", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task UnknownRoutesAndWrongMethodsGetProblemResponses()
    {
        await using var api = await TestApiHost.StartAsync(fixture);

        using var unknown = await api.Client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);
        var notFoundBody = await ReadJsonAsync(unknown);
        Assert.Equal("not_found", notFoundBody.GetProperty("errorCode").GetString());

        using var wrongMethod = await api.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/exports"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal("application/problem+json", wrongMethod.Content.Headers.ContentType?.MediaType);
        var methodBody = await ReadJsonAsync(wrongMethod);
        Assert.Equal("method_not_allowed", methodBody.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task StatusSeparatesAcceptancePublishJobAndDeadLetter()
    {
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);

        var statusOptional = await api.GetStatusAsync(requestId);
        Assert.True(statusOptional.HasValue);
        var status = statusOptional!.Value;
        Assert.Equal("PO-100", status.GetProperty("externalReference").GetString());
        Assert.Equal("TRY", status.GetProperty("currency").GetString());

        var publish = status.GetProperty("publish");
        Assert.Equal("Pending", publish.GetProperty("status").GetString());
        Assert.Equal(0, publish.GetProperty("publishAttempts").GetInt32());

        // No inbox record exists yet: the job field is null, NOT "Completed".
        Assert.True(status.TryGetProperty("job", out var job) && job.ValueKind == JsonValueKind.Null);
        Assert.True(status.TryGetProperty("deadLetter", out var deadLetter) && deadLetter.ValueKind == JsonValueKind.Null);

        using var missing = await api.Client.GetAsync($"/api/exports/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var missingBody = await ReadJsonAsync(missing);
        Assert.Equal("not_found", missingBody.GetProperty("errorCode").GetString());
    }
}
