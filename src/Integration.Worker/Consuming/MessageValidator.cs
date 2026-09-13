using System.Globalization;
using System.Text.Json;
using Integration.Shared.Contracts;
using Integration.Shared.Persistence;
using Integration.Shared.Persistence.Entities;
using Integration.Shared.Runtime;
using Microsoft.Extensions.Options;

namespace Integration.Worker.Consuming;

/// <summary>A delivery that passed every structural check and is ready for the inbox.</summary>
public sealed record ValidatedDelivery(ExportEnvelope Envelope, string PayloadHash, ExportRequest Request);

/// <summary>Result of validating one raw delivery.</summary>
public sealed record ValidationResult(bool Valid, ValidatedDelivery? Delivery, string? ReasonCode)
{
    public static ValidationResult Accept(ValidatedDelivery delivery) => new(true, delivery, null);

    public static ValidationResult Reject(string reasonCode) => new(false, null, reasonCode);
}

/// <summary>
/// Pure, allocation-bounded structural validation: no DB access, no clocks, no I/O.
/// Every rejection reason maps to one RejectedMessages.ReasonCode.
/// The source-request check (does ExportRequests know this EventId, does the hash match)
/// is a database fact and therefore lives in the InboxAcceptor, not here.
/// </summary>
public sealed class MessageValidator
{
    private static readonly HashSet<string> EnvelopeFields = new(StringComparer.Ordinal)
    {
        "eventId", "type", "schemaVersion", "occurredAtUtc", "data",
    };

    private static readonly HashSet<string> DataFields = new(StringComparer.Ordinal)
    {
        "requestId", "externalReference", "amount", "currency",
    };

    private readonly LabOptions _options;

    public MessageValidator(IOptions<LabOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// The durable contract for a transport MessageId: it must fit
    /// <c>varchar(<see cref="ColumnLengths.TransportMessageId"/>)</c> without loss. Non-ASCII
    /// is refused because the column is non-Unicode: SQL Server would substitute unmappable
    /// characters silently, and two distinct identities could then share one receipt key.
    /// Control characters are refused because they are never a legitimate identity.
    /// </summary>
    public static bool IsStorableTransportMessageId(string transportMessageId)
    {
        ArgumentNullException.ThrowIfNull(transportMessageId);

        if (transportMessageId.Length > ColumnLengths.TransportMessageId)
        {
            return false;
        }

        foreach (var character in transportMessageId)
        {
            if (character > (char)0x7E || character < ' ')
            {
                return false;
            }
        }

        return true;
    }

    public ValidationResult Validate(string? transportMessageId, ReadOnlyMemory<byte> body)
    {
        if (string.IsNullOrWhiteSpace(transportMessageId))
        {
            return ValidationResult.Reject(RejectionReason.MissingMessageId);
        }

        // The transport identity is not just a log value: it is half of the InboxReceipts
        // primary key, stored as varchar(128). An identity that does not fit that durable
        // contract must be quarantined HERE, with a reason, and then ACKed - otherwise the
        // insert fails deep inside the acceptance transaction, nothing is ACKed, and the
        // broker redelivers the same unstorable message forever. Truncating instead would be
        // worse: two different deliveries would collide on one receipt key.
        if (!IsStorableTransportMessageId(transportMessageId))
        {
            return ValidationResult.Reject(RejectionReason.UnstorableMessageId);
        }

        if (body.Length > _options.MaxBodyBytes)
        {
            return ValidationResult.Reject(RejectionReason.OversizedBody);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return ValidationResult.Reject(RejectionReason.MalformedBody);
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return ValidationResult.Reject(RejectionReason.MalformedBody);
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!EnvelopeFields.Contains(property.Name))
                {
                    return ValidationResult.Reject(RejectionReason.MalformedBody);
                }
            }

            var root = document.RootElement;
            if (!root.TryGetProperty("eventId", out var eventIdElement)
                || eventIdElement.ValueKind is not JsonValueKind.String
                || !Guid.TryParse(eventIdElement.GetString(), out var eventId)
                || eventId == Guid.Empty)
            {
                return ValidationResult.Reject(RejectionReason.MalformedBody);
            }

            if (!root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind is not JsonValueKind.String
                || typeElement.GetString() != MessageTypes.OrderExportRequested)
            {
                return ValidationResult.Reject(RejectionReason.UnknownType);
            }

            if (!root.TryGetProperty("schemaVersion", out var versionElement)
                || versionElement.ValueKind is not JsonValueKind.Number
                || !versionElement.TryGetInt32(out var schemaVersion)
                || schemaVersion != MessageTypes.SchemaVersion)
            {
                return ValidationResult.Reject(RejectionReason.UnsupportedSchema);
            }

            if (!root.TryGetProperty("occurredAtUtc", out var occurredElement)
                || occurredElement.ValueKind is not JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    occurredElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out _))
            {
                return ValidationResult.Reject(RejectionReason.MalformedBody);
            }

            if (!root.TryGetProperty("data", out var dataElement)
                || dataElement.ValueKind is not JsonValueKind.Object)
            {
                return ValidationResult.Reject(RejectionReason.MalformedBody);
            }

            foreach (var property in dataElement.EnumerateObject())
            {
                if (!DataFields.Contains(property.Name))
                {
                    return ValidationResult.Reject(RejectionReason.MalformedBody);
                }
            }

            if (!dataElement.TryGetProperty("requestId", out var requestIdElement)
                || requestIdElement.ValueKind is not JsonValueKind.String
                || !Guid.TryParse(requestIdElement.GetString(), out var requestId)
                || requestId == Guid.Empty)
            {
                return ValidationResult.Reject(RejectionReason.InvalidPayload);
            }

            if (requestId != eventId)
            {
                // Transport MessageId and EventId are different concepts; but the event's own
                // identity must equal the business request it carries.
                return ValidationResult.Reject(RejectionReason.EventIdMismatch);
            }

            if (!dataElement.TryGetProperty("externalReference", out var referenceElement)
                || referenceElement.ValueKind is not JsonValueKind.String)
            {
                return ValidationResult.Reject(RejectionReason.InvalidPayload);
            }

            var externalReference = referenceElement.GetString()!.Trim();
            if (externalReference.Length is < 1 or > 80)
            {
                return ValidationResult.Reject(RejectionReason.InvalidPayload);
            }

            if (!dataElement.TryGetProperty("amount", out var amountElement)
                || amountElement.ValueKind is not JsonValueKind.Number
                || !decimal.TryParse(
                    amountElement.GetRawText(),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var amount)
                || !ExportLimits.IsStorableAmount(amount))
            {
                return ValidationResult.Reject(RejectionReason.InvalidPayload);
            }

            if (!dataElement.TryGetProperty("currency", out var currencyElement)
                || currencyElement.ValueKind is not JsonValueKind.String
                || currencyElement.GetString()?.Trim().ToUpperInvariant() != "TRY")
            {
                return ValidationResult.Reject(RejectionReason.InvalidPayload);
            }

            var request = new ExportRequest(requestId, externalReference, amount, "TRY");
            var envelope = new ExportEnvelope(
                eventId,
                MessageTypes.OrderExportRequested,
                schemaVersion,
                DateTimeOffset.TryParse(
                    occurredElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var occurredAt)
                    ? occurredAt.ToUniversalTime()
                    : DateTimeOffset.UtcNow,
                request);

            return ValidationResult.Accept(new ValidatedDelivery(envelope, PayloadHash.Compute(request), request));
        }
    }
}
