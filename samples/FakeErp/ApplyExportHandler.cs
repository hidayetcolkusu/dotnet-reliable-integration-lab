using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FakeErp;

/// <summary>
/// POST /erp/exports: the external apply endpoint with persistent idempotency.
///
/// OperationKey = requestId (and the Idempotency-Key header must agree with the body).
/// Same key + same payload hash returns the SAME receipt forever. Same key + different
/// payload is a 409 contract violation. The insert is its own ERP transaction - there is
/// no transaction that spans back into the caller's database.
/// </summary>
public sealed class ApplyExportHandler(
    ErpDbContext context,
    IDbContextFactory<ErpDbContext> contextFactory,
    ScenarioRegistry scenarios,
    ILogger<ApplyExportHandler> logger)
{
    private const int MaxBodyBytes = 64 * 1024;

    public async Task HandleAsync(HttpContext http)
    {
        if (!MediaTypeIsJson(http.Request))
        {
            await WriteErrorAsync(http, 415, "unsupported_media_type", "Content-Type must be application/json.");
            return;
        }

        var body = await ReadBodyBoundedAsync(http.Request, http.RequestAborted);
        if (body is null)
        {
            await WriteErrorAsync(http, 413, "payload_too_large", $"The request body exceeds the {MaxBodyBytes}-byte limit.");
            return;
        }

        if (!TryParse(body, out var requestId, out var externalReference, out var amount, out var error))
        {
            await WriteErrorAsync(http, 400, error.Code, error.Detail);
            return;
        }

        var idempotencyKey = http.Request.Headers["Idempotency-Key"].ToString();
        if (!string.Equals(idempotencyKey, requestId.ToString("D"), StringComparison.Ordinal))
        {
            await WriteErrorAsync(
                http,
                400,
                "idempotency_mismatch",
                "The Idempotency-Key header must equal the body's requestId.");
            return;
        }

        // Computed before the replay check, because deciding whether a repeat is a replay or a
        // conflict requires the hash of what THIS caller sent.
        var payloadHash = ErpPayload.Hash(requestId, externalReference, amount, "TRY");

        // Replay first: an applied operation always answers with its original receipt,
        // no matter which scenario is configured - the durable contract outranks the knobs.
        var existing = await context.AppliedExports
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationKey == requestId.ToString("D"), http.RequestAborted);
        if (existing is not null)
        {
            await ReplayAsync(http, existing, payloadHash);
            return;
        }

        var interception = scenarios.InterceptNewApply(requestId);
        switch (interception)
        {
            case "http_503":
                logger.LogWarning("Scenario first-n-503: answering 503 for {RequestId}.", requestId);
                await WriteErrorAsync(http, 503, "erp_temporarily_unavailable", "The external system is temporarily unavailable.");
                return;

            case "http_422":
                logger.LogWarning("Scenario permanent-422: answering 422 for {RequestId}.", requestId);
                await WriteErrorAsync(http, 422, "erp_rejected", "The external system rejected this operation permanently.");
                return;

            case "hang":
                logger.LogWarning("Scenario unavailable: never answering {RequestId}.", requestId);
                await Task.Delay(Timeout.InfiniteTimeSpan, http.RequestAborted);
                return;
        }

        var appliedAt = DateTimeOffset.UtcNow;
        var receiptId = $"ERP-{Guid.NewGuid():N}";

        try
        {
            context.AppliedExports.Add(new AppliedExport
            {
                OperationKey = requestId.ToString("D"),
                PayloadHash = payloadHash,
                ExternalReceiptId = receiptId,
                ExternalReference = externalReference,
                Amount = amount,
                Currency = "TRY",
                AppliedAtUtc = appliedAt,
            });
            await context.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Lost a race against a concurrent identical apply (or a replay storm).
            // The failed context is dead; a fresh context reads the committed winner.
            logger.LogInformation("Apply race for {RequestId}; resolving with a fresh context.", requestId);
            await using var fresh = await contextFactory.CreateDbContextAsync(http.RequestAborted);
            var winner = await fresh.AppliedExports
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationKey == requestId.ToString("D"), http.RequestAborted);

            if (winner is not null)
            {
                if (string.Equals(winner.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    await ReplayAsync(http, winner, payloadHash);
                }
                else
                {
                    await WriteErrorAsync(
                        http,
                        409,
                        "payload_conflict",
                        "This operation key was already applied with a different payload.");
                }

                return;
            }

            await WriteErrorAsync(http, 500, "apply_conflict", "Unexpected apply conflict.");
            return;
        }

        logger.LogInformation(
            "Applied export {RequestId} with receipt {ReceiptId}.",
            requestId,
            receiptId);

        var delayMs = scenarios.TakeResponseDelay(requestId);
        if (delayMs is { } delay)
        {
            // The effect is committed; only the FIRST new response is held back long
            // enough for a typical client timeout. Replays answer immediately.
            logger.LogWarning(
                "Scenario apply-then-delay-response: holding the first response for {RequestId} by {DelayMs} ms.",
                requestId,
                delay);
            await Task.Delay(delay, http.RequestAborted);
        }

        await WriteAcceptedAsync(http, requestId, receiptId, appliedAt, replayed: false);
    }

    /// <summary>
    /// Answers a repeat of an operation that was already applied. The comparison is between the
    /// STORED hash and the hash of the payload this caller just sent - hashing the stored row's
    /// own fields would compare it to itself and could never detect a conflict.
    /// </summary>
    private static async Task ReplayAsync(HttpContext http, AppliedExport applied, string incomingPayloadHash)
    {
        if (string.Equals(applied.PayloadHash, incomingPayloadHash, StringComparison.Ordinal))
        {
            await WriteAcceptedAsync(http, Guid.Parse(applied.OperationKey), applied.ExternalReceiptId, applied.AppliedAtUtc, replayed: true);
            return;
        }

        await WriteErrorAsync(
            http,
            409,
            "payload_conflict",
            "This operation key was already applied with a different payload.");
    }

    private static async Task WriteAcceptedAsync(
        HttpContext http,
        Guid requestId,
        string receiptId,
        DateTimeOffset appliedAtUtc,
        bool replayed)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsJsonAsync(new
        {
            requestId = requestId.ToString("D"),
            receiptId,
            appliedAtUtc,
            replayed,
        }, http.RequestAborted);
    }

    private static async Task WriteErrorAsync(HttpContext http, int statusCode, string code, string detail)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsJsonAsync(new { errorCode = code, detail }, http.RequestAborted);
    }

    private static bool MediaTypeIsJson(HttpRequest request) =>
        request.ContentType is not null
        && request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bounded body read; returns null when the limit is exceeded.</summary>
    private static async Task<byte[]?> ReadBodyBoundedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is long declared && declared > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            if (buffer.Length > MaxBodyBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }

    private static bool TryParse(
        byte[] body,
        out Guid requestId,
        out string externalReference,
        out decimal amount,
        out (string Code, string Detail) error)
    {
        requestId = Guid.Empty;
        externalReference = string.Empty;
        amount = 0;
        error = ("malformed_body", "The request body is not valid JSON.");

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return false;
            }

            var root = document.RootElement;
            if (!root.TryGetProperty("requestId", out var requestIdElement)
                || requestIdElement.ValueKind is not JsonValueKind.String
                || !Guid.TryParse(requestIdElement.GetString(), out requestId)
                || requestId == Guid.Empty)
            {
                error = ("invalid_payload", "requestId must be a non-empty GUID string.");
                return false;
            }

            if (!root.TryGetProperty("externalReference", out var referenceElement)
                || referenceElement.ValueKind is not JsonValueKind.String)
            {
                error = ("invalid_payload", "externalReference must be a string.");
                return false;
            }

            externalReference = referenceElement.GetString()!.Trim();
            if (externalReference.Length is < 1 or > 80)
            {
                error = ("invalid_payload", "externalReference must be 1 to 80 characters after trimming.");
                return false;
            }

            if (!root.TryGetProperty("amount", out var amountElement)
                || amountElement.ValueKind is not JsonValueKind.Number
                || !decimal.TryParse(amountElement.GetRawText(), NumberStyles.Number, CultureInfo.InvariantCulture, out amount)
                || !Integration.Shared.Contracts.ExportLimits.IsStorableAmount(amount))
            {
                // The upper bound is part of the contract, not a database detail: AppliedExports
                // stores decimal(18,2), so a larger value must be an invalid payload here rather
                // than an arithmetic overflow at insert time.
                error = ("invalid_payload", "amount must be a positive decimal(18,2) with at most two decimal places.");
                return false;
            }

            if (!root.TryGetProperty("currency", out var currencyElement)
                || currencyElement.ValueKind is not JsonValueKind.String
                || currencyElement.GetString()?.Trim().ToUpperInvariant() != "TRY")
            {
                error = ("invalid_payload", "only TRY is accepted.");
                return false;
            }

            error = (string.Empty, string.Empty);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
