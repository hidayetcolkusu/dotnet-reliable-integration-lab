using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Integration.Shared.Contracts;

/// <summary>The single synthetic business request this lab moves to an external system.</summary>
public sealed record ExportRequest(
    Guid RequestId,
    string ExternalReference,
    decimal Amount,
    string Currency);

/// <summary>Transport envelope written to the outbox and published to the broker.</summary>
public sealed record ExportEnvelope(
    Guid EventId,
    string Type,
    int SchemaVersion,
    DateTimeOffset OccurredAtUtc,
    ExportRequest Data);

/// <summary>What the API returns once the request and its outbox row are committed.</summary>
public sealed record StoredAcceptance(Guid RequestId, string StatusUrl);

/// <summary>An owned claim over a single outbox row or job row.</summary>
public sealed record Lease(Guid RecordId, Guid Token, DateTimeOffset UntilUtc);

/// <summary>Outcome of one external HTTP attempt. The client itself never retries.</summary>
public sealed record ErpResult(
    bool Succeeded,
    string? ReceiptId,
    string? ErrorCode,
    bool Retryable,
    TimeSpan? RetryAfter);

/// <summary>Safe metadata published to the dead-letter queue. Never carries raw bodies.</summary>
public sealed record DeadLetterEnvelope(
    Guid? RequestId,
    Guid? EventId,
    string Reason,
    string? ErrorCode,
    int AttemptsStarted,
    DateTimeOffset FailedAtUtc,
    string? PayloadHash,
    long? BodyLength,
    ExportRequest? Data);

/// <summary>
/// The durable bounds of the export contract, shared by every validator so the API, the
/// message validator and the external system agree on what is representable.
///
/// <see cref="MaxAmount"/> is the largest value <c>decimal(18,2)</c> can hold. Checking only
/// "positive, at most two decimals" lets a CLR decimal such as 10000000000000000.00 through
/// every handler and turns it into a SQL arithmetic-overflow deep inside a transaction - a 500
/// where the contract calls for a safe 400 (and, in the external system, its invalid-payload
/// answer).
/// </summary>
public static class ExportLimits
{
    /// <summary>Largest value a <c>decimal(18,2)</c> column can store: 16 integral digits.</summary>
    public const decimal MaxAmount = 9_999_999_999_999_999.99m;

    public const int MaxExternalReferenceLength = 80;

    /// <summary>True for a value the database and the whole pipeline can actually carry.</summary>
    public static bool IsStorableAmount(decimal amount) =>
        amount > 0 && amount <= MaxAmount && decimal.Round(amount, 2) == amount;
}

public static class MessageTypes
{
    public const string OrderExportRequested = "OrderExportRequested";
    public const string OrderExportFailed = "OrderExportFailed";
    public const int SchemaVersion = 1;
}

/// <summary>
/// The one JSON configuration shared by every writer and reader of this repo's messages.
/// The API writes the envelope, the worker reads it; any change is a contract change.
/// Unknown members are rejected by the explicit field-by-field validators, not by serializer
/// magic, so every rejection carries a precise reason code.
/// </summary>
public static class LabJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Canonical payload hashing. The same request always produces the same hash in the API,
/// in the inbox and in FakeErp, so a mismatch is a real content difference, not formatting.
/// </summary>
public static class PayloadHash
{
    public static string Compute(ExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{request.RequestId:D}|{request.ExternalReference.Trim()}|{request.Amount.ToString("F2", CultureInfo.InvariantCulture)}|{request.Currency.Trim().ToUpperInvariant()}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string OfBytes(ReadOnlySpan<byte> body) => Convert.ToHexStringLower(SHA256.HashData(body));
}
