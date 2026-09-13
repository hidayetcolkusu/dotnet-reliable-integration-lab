using System.Net;
using System.Text.Json;
using Integration.Shared.Persistence.Entities;
using IntegrationLab.Tests.Support;
using Xunit;

namespace IntegrationLab.Tests;

/// <summary>
/// Task 8 acceptance: the external system's persistent idempotency is what makes a retry safe.
/// A lost response is the dangerous case - the effect committed, the caller never learned the
/// receipt - and it must end as ONE applied row and the ORIGINAL receipt, not a second effect.
/// </summary>
[Collection("lab")]
public sealed class ExternalIdempotencyTests(LabFixture fixture)
{
    private readonly SqlAssertions _sql = fixture.CreateSql();

    private static string[] FastRetryArgs(params string[] extra) =>
    [
        "--Lab:PollIntervalMilliseconds=100",
        .. LabTestConfig.Indexed("Lab:OutboxRetryDelaysSeconds", 0, 0, 0, 0, 0, 0),
        .. LabTestConfig.Indexed("Lab:JobRetryDelaysSeconds", 0, 0, 0, 0),
        .. extra,
    ];

    private static string ApplyJson(Guid requestId, string externalReference = "PO-100", decimal amount = 160.00m) =>
        $$"""{"requestId":"{{requestId:D}}","externalReference":"{{externalReference}}","amount":{{amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}},"currency":"TRY"}""";

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    // ---------------------------------------------------- the external contract, on its own

    [Fact]
    public async Task TheSameOperationKeyAlwaysReturnsTheSameReceipt()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();
        var json = ApplyJson(requestId);

        using var first = await erp.ApplyDirectAsync(requestId, json);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await ReadJsonAsync(first);
        Assert.False(firstBody.GetProperty("replayed").GetBoolean());

        using var second = await erp.ApplyDirectAsync(requestId, json);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await ReadJsonAsync(second);

        // A replay is answered from the durable row, not by applying anything again.
        Assert.True(secondBody.GetProperty("replayed").GetBoolean());
        Assert.Equal(firstBody.GetProperty("receiptId").GetString(), secondBody.GetProperty("receiptId").GetString());
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
    }

    [Fact]
    public async Task TheSameOperationKeyWithADifferentPayloadIsAConflict()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        using var first = await erp.ApplyDirectAsync(requestId, ApplyJson(requestId, amount: 160.00m));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var conflicting = await erp.ApplyDirectAsync(requestId, ApplyJson(requestId, amount: 999.00m));
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        Assert.Equal("payload_conflict", (await ReadJsonAsync(conflicting)).GetProperty("errorCode").GetString());

        // The original effect is untouched: idempotency is not "last write wins".
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
        Assert.Equal(
            (await ReadJsonAsync(first)).GetProperty("receiptId").GetString(),
            await _sql.GetAppliedReceiptAsync(requestId));
    }

    /// <summary>
    /// G4 on the external side. The ERP stores decimal(18,2) too, so an amount past that bound
    /// has to meet the documented invalid-payload contract rather than an arithmetic overflow
    /// at insert time - and it must leave no AppliedExports row behind.
    /// </summary>
    [Theory]
    [InlineData("10000000000000000.00")]
    [InlineData("79228162514264337593543950335")]
    public async Task AnAmountLargerThanTheExternalContractIsAnInvalidPayload(string amountLiteral)
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();
        var json = "{\"requestId\":\"" + requestId.ToString("D")
            + "\",\"externalReference\":\"PO-100\",\"amount\":" + amountLiteral
            + ",\"currency\":\"TRY\"}";

        using var response = await erp.ApplyDirectAsync(requestId, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_payload", (await ReadJsonAsync(response)).GetProperty("errorCode").GetString());
        Assert.Equal(0, await _sql.CountAppliedExportsAsync(requestId));
    }

    [Fact]
    public async Task TheLargestAmountTheExternalContractAllowsIsStillApplied()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        using var response = await erp.ApplyDirectAsync(
            requestId, ApplyJson(requestId, amount: Integration.Shared.Contracts.ExportLimits.MaxAmount));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
    }

    [Fact]
    public async Task TheIdempotencyKeyMustAgreeWithTheBody()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        // The header names a different operation than the body: ambiguous, so it is refused
        // rather than silently applied under one of the two keys.
        using var response = await erp.ApplyDirectAsync(Guid.NewGuid(), ApplyJson(requestId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency_mismatch", (await ReadJsonAsync(response)).GetProperty("errorCode").GetString());
        Assert.Equal(0, await _sql.CountAppliedExportsAsync(requestId));
    }

    [Fact]
    public async Task ConcurrentIdenticalAppliesProduceOneRowAndOneReceipt()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();
        var json = ApplyJson(requestId);

        // Eight callers racing on the same operation key: the unique index decides the winner
        // and every loser reads the winner's receipt instead of applying again.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => erp.ApplyDirectAsync(requestId, json)));

        var receipts = new List<string>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            receipts.Add((await ReadJsonAsync(response)).GetProperty("receiptId").GetString()!);
            response.Dispose();
        }

        Assert.Single(receipts.Distinct(StringComparer.Ordinal));
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
        Assert.Equal(receipts[0], await _sql.GetAppliedReceiptAsync(requestId));
    }

    // ------------------------------------------------- the dangerous case, end to end

    [Fact]
    public async Task ALostResponseDoesNotApplyTheOperationTwice()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        // The effect commits, then the response is held past the client timeout: the worker
        // learns nothing, and the only safe reading of that is "unknown", never "failed".
        await erp.SetScenarioAsync(requestId, "apply-then-delay-response", delayMs: 8000);
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);

        await using var worker = await TestWorkerHost.StartAsync(
            fixture, erp.BaseAddress, null, FastRetryArgs("--Lab:HttpTimeoutSeconds=2"));

        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(requestId) == JobStatus.Completed,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(requestId));

        var job = await _sql.GetJobAsync(requestId);

        // The timed-out attempt was classified transient and retried...
        Assert.True(job!.AttemptsStarted >= 2, $"expected a retry after the lost response, attempts were {job.AttemptsStarted}");
        var attempts = await _sql.GetJobAttemptsAsync(requestId);
        Assert.Equal(AttemptOutcome.TransientFailure, attempts[0].Outcome);
        Assert.Equal("http_timeout", attempts[0].SafeErrorCode);

        // ...and the retry replayed the ORIGINAL effect instead of creating a second one.
        Assert.Equal(1, await _sql.CountAppliedExportsAsync(requestId));
        Assert.Equal(await _sql.GetAppliedReceiptAsync(requestId), job.ExternalReceiptId);
    }

    [Fact]
    public async Task AnUnresponsiveExternalSystemNeverProducesAnEffect()
    {
        await _sql.ParkAbandonedJobsAsync();
        await _sql.CleanupAbandonedOutboxAsync();

        await using var erp = await TestFakeErpHost.StartAsync(fixture);
        await using var api = await TestApiHost.StartAsync(fixture);
        var requestId = Guid.NewGuid();

        // Never answers at all: every attempt times out client-side and nothing is applied.
        await erp.SetScenarioAsync(requestId, "unavailable");
        using var submit = await api.SubmitExportAsync(requestId);
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);

        await using var worker = await TestWorkerHost.StartAsync(
            fixture,
            erp.BaseAddress,
            null,
            FastRetryArgs("--Lab:HttpTimeoutSeconds=1", "--Lab:MaxJobAttempts=3"));

        await Eventually.UntilAsync(
            async () => await _sql.GetJobStateAsync(requestId) is JobStatus.DeadLetterPending or JobStatus.DeadLettered,
            timeout: TimeSpan.FromSeconds(90),
            diagnostics: () => _sql.DescribeAsync(requestId));

        var job = await _sql.GetJobAsync(requestId);
        Assert.Equal(3, job!.AttemptsStarted);
        Assert.Equal("attempt_budget_exhausted", job.LastErrorCode);
        Assert.Equal(0, await _sql.CountAppliedExportsAsync(requestId));
        Assert.All(
            (await _sql.GetJobAttemptsAsync(requestId)).Take(3),
            attempt => Assert.Equal("http_timeout", attempt.SafeErrorCode));
    }
}
