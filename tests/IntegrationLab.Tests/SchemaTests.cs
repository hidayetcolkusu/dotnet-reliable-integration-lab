using System.Globalization;
using System.Net;
using System.Text;
using Integration.Shared.Runtime;
using IntegrationLab.Tests.Support;
using Xunit;
using ApiProgram = Integration.Api.Program;
using FakeErpProgram = FakeErp.Program;
using WorkerProgram = Integration.Worker.Program;

namespace IntegrationLab.Tests;

/// <summary>
/// Task 2 acceptance: the schema itself holds the invariants - even when a handler is
/// bypassed and rows are inserted straight into SQL. Environment guarding and the
/// "normal startup never migrates" rule are proven here too.
/// </summary>
[Collection("lab")]
public sealed class SchemaTests(LabFixture fixture)
{
    private readonly SqlAssertions _sql = fixture.CreateSql();

    private static (string Name, System.Data.SqlDbType Type, object? Value) P(string name, System.Data.SqlDbType type, object? value) =>
        (name, type, value);

    private async Task InsertExportRequestAsync(Guid requestId, decimal amount = 160.00m)
    {
        await _sql.ExecuteAsync(
            useErpDatabase: false,
            "INSERT INTO ExportRequests (RequestId, PayloadHash, ExternalReference, Amount, Currency, CreatedAtUtc) " +
            "VALUES (@id, @hash, 'PO-100', @amount, 'TRY', SYSUTCDATETIME())",
            P("id", System.Data.SqlDbType.UniqueIdentifier, requestId),
            P("hash", System.Data.SqlDbType.VarChar, new string('a', 64)),
            P("amount", System.Data.SqlDbType.Decimal, amount));
    }

    private async Task InsertOutboxAsync(Guid outboxId, Guid requestId, string kind)
    {
        await _sql.ExecuteAsync(
            useErpDatabase: false,
            "INSERT INTO OutboxMessages (Id, Kind, SourceRequestId, RejectionId, Body, RoutingKey, Exchange, " +
            "CreatedAtUtc, Status, PublishAttempts, NextAttemptAtUtc) " +
            "VALUES (@id, @kind, @source, NULL, '{}', 'rk', 'ex', SYSUTCDATETIME(), 'Pending', 0, SYSUTCDATETIME())",
            P("id", System.Data.SqlDbType.UniqueIdentifier, outboxId),
            P("kind", System.Data.SqlDbType.VarChar, kind),
            P("source", System.Data.SqlDbType.UniqueIdentifier, requestId));
    }

    [Fact]
    public async Task DuplicateRequestIsRejectedByThePrimaryKey()
    {
        var requestId = Guid.NewGuid();
        await InsertExportRequestAsync(requestId);

        var rejected = await _sql.ExpectSqlErrorAsync(
            useErpDatabase: false,
            expectedError: 2627,
            "INSERT INTO ExportRequests (RequestId, PayloadHash, ExternalReference, Amount, Currency, CreatedAtUtc) " +
            "VALUES (@id, @hash, 'PO-100', 160.00, 'TRY', SYSUTCDATETIME())",
            P("id", System.Data.SqlDbType.UniqueIdentifier, requestId),
            P("hash", System.Data.SqlDbType.VarChar, new string('b', 64)));

        Assert.True(rejected);
    }

    [Fact]
    public async Task OrphanJobIsRejectedByTheForeignKey()
    {
        var rejected = await _sql.ExpectSqlErrorAsync(
            useErpDatabase: false,
            expectedError: 547,
            "INSERT INTO IntegrationJobs (RequestId, PayloadHash, Status, AttemptsStarted, CreatedAtUtc) " +
            "VALUES (@id, @hash, 'Pending', 0, SYSUTCDATETIME())",
            P("id", System.Data.SqlDbType.UniqueIdentifier, Guid.NewGuid()),
            P("hash", System.Data.SqlDbType.VarChar, new string('c', 64)));

        Assert.True(rejected);
    }

    [Fact]
    public async Task SecondExportOutboxForTheSameRequestIsRejected()
    {
        var requestId = Guid.NewGuid();
        await InsertExportRequestAsync(requestId);
        await InsertOutboxAsync(Guid.NewGuid(), requestId, "Export");

        var rejected = await _sql.ExpectSqlErrorAsync(
            useErpDatabase: false,
            expectedError: 2601,
            "INSERT INTO OutboxMessages (Id, Kind, SourceRequestId, RejectionId, Body, RoutingKey, Exchange, " +
            "CreatedAtUtc, Status, PublishAttempts, NextAttemptAtUtc) " +
            "VALUES (@id, 'Export', @source, NULL, '{}', 'rk', 'ex', SYSUTCDATETIME(), 'Pending', 0, SYSUTCDATETIME())",
            P("id", System.Data.SqlDbType.UniqueIdentifier, Guid.NewGuid()),
            P("source", System.Data.SqlDbType.UniqueIdentifier, requestId));

        Assert.True(rejected);
    }

    [Fact]
    public async Task SecondTerminalOutboxForTheSameRequestIsRejected()
    {
        var requestId = Guid.NewGuid();
        await InsertExportRequestAsync(requestId);
        await InsertOutboxAsync(Guid.NewGuid(), requestId, "Export");
        await InsertOutboxAsync(Guid.NewGuid(), requestId, "DeadLetter");

        // Marking a job terminal twice must not create a second terminal event.
        var rejected = await _sql.ExpectSqlErrorAsync(
            useErpDatabase: false,
            expectedError: 2601,
            "INSERT INTO OutboxMessages (Id, Kind, SourceRequestId, RejectionId, Body, RoutingKey, Exchange, " +
            "CreatedAtUtc, Status, PublishAttempts, NextAttemptAtUtc) " +
            "VALUES (@id, 'DeadLetter', @source, NULL, '{}', 'rk', 'ex', SYSUTCDATETIME(), 'Pending', 0, SYSUTCDATETIME())",
            P("id", System.Data.SqlDbType.UniqueIdentifier, Guid.NewGuid()),
            P("source", System.Data.SqlDbType.UniqueIdentifier, requestId));

        Assert.True(rejected);
    }

    [Fact]
    public async Task KindLinkCheckConstraintsAreEnforced()
    {
        // Export rows must link a source request and never a rejection.
        var exportWithRejection = await _sql.ExpectSqlErrorAsync(
            useErpDatabase: false,
            expectedError: 547,
            "INSERT INTO OutboxMessages (Id, Kind, SourceRequestId, RejectionId, Body, RoutingKey, Exchange, " +
            "CreatedAtUtc, Status, PublishAttempts, NextAttemptAtUtc) " +
            "VALUES (@id, 'Export', NULL, @rejection, '{}', 'rk', 'ex', SYSUTCDATETIME(), 'Pending', 0, SYSUTCDATETIME())",
            P("id", System.Data.SqlDbType.UniqueIdentifier, Guid.NewGuid()),
            P("rejection", System.Data.SqlDbType.UniqueIdentifier, Guid.NewGuid()));
        Assert.True(exportWithRejection);

        // A DeadLetter row without either link is rejected too.
        var deadLetterWithoutLink = await _sql.ExpectSqlErrorAsync(
            useErpDatabase: false,
            expectedError: 547,
            "INSERT INTO OutboxMessages (Id, Kind, SourceRequestId, RejectionId, Body, RoutingKey, Exchange, " +
            "CreatedAtUtc, Status, PublishAttempts, NextAttemptAtUtc) " +
            "VALUES (@id, 'DeadLetter', NULL, NULL, '{}', 'rk', 'ex', SYSUTCDATETIME(), 'Pending', 0, SYSUTCDATETIME())",
            P("id", System.Data.SqlDbType.UniqueIdentifier, Guid.NewGuid()));
        Assert.True(deadLetterWithoutLink);
    }

    [Fact]
    public async Task InvalidAmountAndCurrencyAreRejectedByCheckConstraints()
    {
        var negativeAmount = await _sql.ExpectSqlErrorAsync(
            useErpDatabase: false,
            expectedError: 547,
            "INSERT INTO ExportRequests (RequestId, PayloadHash, ExternalReference, Amount, Currency, CreatedAtUtc) " +
            "VALUES (@id, @hash, 'PO-100', -5.00, 'TRY', SYSUTCDATETIME())",
            P("id", System.Data.SqlDbType.UniqueIdentifier, Guid.NewGuid()),
            P("hash", System.Data.SqlDbType.VarChar, new string('d', 64)));
        Assert.True(negativeAmount);

        var wrongCurrency = await _sql.ExpectSqlErrorAsync(
            useErpDatabase: false,
            expectedError: 547,
            "INSERT INTO ExportRequests (RequestId, PayloadHash, ExternalReference, Amount, Currency, CreatedAtUtc) " +
            "VALUES (@id, @hash, 'PO-100', 5.00, 'EUR', SYSUTCDATETIME())",
            P("id", System.Data.SqlDbType.UniqueIdentifier, Guid.NewGuid()),
            P("hash", System.Data.SqlDbType.VarChar, new string('e', 64)));
        Assert.True(wrongCurrency);
    }

    [Fact]
    public async Task TheTwoDatabasesShareNoForeignKeyBoundary()
    {
        var requestId = Guid.NewGuid();

        // An external effect can exist without the application knowing, and vice versa:
        // no FK, no join, no shared transaction crosses this boundary by design.
        await _sql.ExecuteAsync(
            useErpDatabase: true,
            "INSERT INTO AppliedExports (OperationKey, PayloadHash, ExternalReceiptId, ExternalReference, Amount, Currency, AppliedAtUtc) " +
            "VALUES (@key, @hash, @receipt, 'PO-100', 160.00, 'TRY', SYSUTCDATETIME())",
            P("key", System.Data.SqlDbType.VarChar, requestId.ToString("D")),
            P("hash", System.Data.SqlDbType.VarChar, new string('f', 64)),
            P("receipt", System.Data.SqlDbType.VarChar, $"ERP-{Guid.NewGuid():N}"));

        await InsertExportRequestAsync(requestId);

        Assert.Equal(1, await _sql.CountExportRequestsAsync(requestId));
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
    }

    [Fact]
    public async Task ProductionEnvironmentIsRefusedFast()
    {
        Assert.Throws<LabEnvironmentException>(() =>
            ApiProgram.BuildApp(["--environment=Production"]));
        Assert.Throws<LabEnvironmentException>(() =>
            WorkerProgram.BuildHost(["--environment=Production"]));
        Assert.Throws<LabEnvironmentException>(() =>
            FakeErpProgram.BuildApp(["--environment=Production"]));
    }

    [Fact]
    public async Task NormalStartupNeverMigrates()
    {
        var bareDatabase = $"IntegrationLab_Bare{fixture.Suffix}";
        await fixture.CreateEmptyDatabaseAsync(bareDatabase);

        var bareConnectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(fixture.IntegrationConnectionString)
        {
            InitialCatalog = bareDatabase,
        }.ConnectionString;

        var app = ApiProgram.BuildApp(
        [
            "--environment=Testing",
            $"--urls=http://127.0.0.1:{LabTestConfig.GetFreeLoopbackPort()}",
            $"--ConnectionStrings:IntegrationLab={bareConnectionString}",
        ]);
        await using (app)
        {
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            using var response = await client.PostAsync(
                "/api/exports",
                new StringContent(
                    $$"""{"requestId":"{{Guid.NewGuid():D}}","externalReference":"PO-100","amount":160.00,"currency":"TRY"}""",
                    Encoding.UTF8,
                    "application/json"));

            // The table does not exist and startup did not create it.
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            var bareSql = new SqlAssertions(bareConnectionString, fixture.FakeErpConnectionString);
            var tables = await bareSql.ExecuteScalarAsync(
                useErpDatabase: false,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'ExportRequests'");
            Assert.Equal(0, Convert.ToInt32(tables, CultureInfo.InvariantCulture));
        }
    }
}
