using System.Globalization;
using System.Text.Json;
using Integration.Api.Errors;
using Integration.Api.Exports;
using Integration.Shared.Contracts;
using Integration.Shared.Diagnostics;
using Integration.Shared.Runtime;
using Microsoft.Extensions.Options;

namespace Integration.Api.Exports;
/// <summary>
/// Manual, bounded request parsing so every failure has a precise, testable errorCode:
/// unsupported_media_type, payload_too_large, malformed_body, unknown_field,
/// validation_failed, amount_precision.
/// </summary>
internal static class ExportRequestParser
{
    private const string RequestIdField = "requestId";
    private const string ExternalReferenceField = "externalReference";
    private const string AmountField = "amount";
    private const string CurrencyField = "currency";

    public static ExportRequest Parse(ReadOnlyMemory<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new ApiException(400, "malformed_body", "The request body is not valid JSON.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw new ApiException(400, "malformed_body", "The request body must be a JSON object.");
            }

            var fieldErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            Guid? requestId = null;
            string? externalReference = null;
            decimal? amount = null;
            string? currency = null;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case RequestIdField:
                        requestId = ReadRequestId(property.Value, fieldErrors);
                        break;
                    case ExternalReferenceField:
                        externalReference = ReadExternalReference(property.Value, fieldErrors);
                        break;
                    case AmountField:
                        amount = ReadAmount(property.Value, fieldErrors);
                        break;
                    case CurrencyField:
                        currency = ReadCurrency(property.Value, fieldErrors);
                        break;
                    default:
                        throw new ApiException(
                            400,
                            "unknown_field",
                            $"Unknown field '{property.Name}'. Allowed fields: {RequestIdField}, {ExternalReferenceField}, {AmountField}, {CurrencyField}.");
                }
            }

            if (requestId is null && !fieldErrors.ContainsKey(RequestIdField))
            {
                fieldErrors[RequestIdField] = ["required"];
            }

            if (externalReference is null && !fieldErrors.ContainsKey(ExternalReferenceField))
            {
                fieldErrors[ExternalReferenceField] = ["required"];
            }

            if (amount is null && !fieldErrors.ContainsKey(AmountField))
            {
                fieldErrors[AmountField] = ["required"];
            }

            if (currency is null && !fieldErrors.ContainsKey(CurrencyField))
            {
                fieldErrors[CurrencyField] = ["required"];
            }

            if (fieldErrors.Count > 0)
            {
                throw new ApiException(400, "validation_failed", "One or more fields are invalid.", fieldErrors);
            }

            return new ExportRequest(requestId!.Value, externalReference!, amount!.Value, currency!);
        }
    }

    private static Guid? ReadRequestId(JsonElement element, Dictionary<string, string[]> errors)
    {
        if (element.ValueKind is not JsonValueKind.String
            || !Guid.TryParse(element.GetString(), out var parsed)
            || parsed == Guid.Empty)
        {
            errors[RequestIdField] = ["must be a non-empty GUID string"];
            return null;
        }

        return parsed;
    }

    private static string? ReadExternalReference(JsonElement element, Dictionary<string, string[]> errors)
    {
        if (element.ValueKind is not JsonValueKind.String)
        {
            errors[ExternalReferenceField] = ["must be a string"];
            return null;
        }

        var value = element.GetString()!.Trim();
        if (value.Length is < 1 or > 80)
        {
            errors[ExternalReferenceField] = ["must be 1 to 80 characters after trimming"];
            return null;
        }

        return value;
    }

    private static decimal? ReadAmount(JsonElement element, Dictionary<string, string[]> errors)
    {
        if (element.ValueKind is not JsonValueKind.Number)
        {
            errors[AmountField] = ["must be a JSON number"];
            return null;
        }

        var raw = element.GetRawText();
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            // Exponent notation and similar forms are rejected as a precision risk.
            errors[AmountField] = ["must be a plain decimal literal without exponent notation"];
            return null;
        }

        if (parsed <= 0)
        {
            errors[AmountField] = ["must be greater than 0"];
            return null;
        }

        // The durable column is decimal(18,2). Without this bound a larger CLR decimal passes
        // every check here and fails as a SQL arithmetic overflow inside the acceptance
        // transaction - a 500 instead of the contract's safe 400.
        if (parsed > ExportLimits.MaxAmount)
        {
            errors[AmountField] = [
                $"must be at most {ExportLimits.MaxAmount.ToString("0.00", CultureInfo.InvariantCulture)} (the external contract is decimal(18,2))"
            ];
            return null;
        }

        if (decimal.Round(parsed, 2) != parsed)
        {
            throw new ApiException(
                400,
                "amount_precision",
                $"amount '{raw}' has more than two decimal places; the external contract is decimal(18,2).");
        }

        return parsed;
    }

    private static string? ReadCurrency(JsonElement element, Dictionary<string, string[]> errors)
    {
        if (element.ValueKind is not JsonValueKind.String)
        {
            errors[CurrencyField] = ["must be a string"];
            return null;
        }

        var value = element.GetString()!.Trim().ToUpperInvariant();
        if (value != "TRY")
        {
            errors[CurrencyField] = ["only TRY is accepted in this lab"];
            return null;
        }

        return value;
    }

    /// <summary>Reads the whole body, never more than the configured cap, without trusting Content-Length.</summary>
    public static async Task<byte[]> ReadBodyBoundedAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength is long declared && declared > maxBytes)
        {
            throw new ApiException(413, "payload_too_large", $"The request body exceeds the {maxBytes}-byte limit.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (buffer.Length > maxBytes)
            {
                throw new ApiException(413, "payload_too_large", $"The request body exceeds the {maxBytes}-byte limit.");
            }
        }

        return buffer.ToArray();
    }
}

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/exports", SubmitExportAsync);
        app.MapGet("/api/exports/{requestId:guid}", GetExportStatusAsync);
    }

    private static async Task<IResult> SubmitExportAsync(
        HttpContext context,
        SubmitExportHandler handler,
        IOptions<LabOptions> options)
    {
        if (!MediaTypeIsJson(context.Request))
        {
            throw new ApiException(415, "unsupported_media_type", "Content-Type must be application/json.");
        }

        var body = await ExportRequestParser.ReadBodyBoundedAsync(
            context.Request,
            options.Value.MaxBodyBytes,
            context.RequestAborted).ConfigureAwait(false);

        var request = ExportRequestParser.Parse(body);

        using var activity = LabTelemetry.Source.StartActivity(LabTelemetry.Spans.ApiReceive);
        activity?.SetTag(LabTelemetry.Tags.RequestId, request.RequestId.ToString());

        var acceptance = await handler.HandleAsync(request, context.RequestAborted).ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.Response.Headers.Location = acceptance.StatusUrl;
        return Results.Json(acceptance, contentType: "application/json");
    }

    private static async Task<IResult> GetExportStatusAsync(
        HttpContext context,
        Guid requestId,
        GetExportStatusHandler handler)
    {
        var status = await handler.GetAsync(requestId, context.RequestAborted).ConfigureAwait(false);
        if (status is null)
        {
            throw new ApiException(404, "not_found", $"No export request with requestId {requestId}.");
        }

        return Results.Json(status, contentType: "application/json");
    }

    private static bool MediaTypeIsJson(HttpRequest request) =>
        request.ContentType is not null
        && request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);
}
