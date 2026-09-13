using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Integration.Shared.Contracts;
using Integration.Shared.Diagnostics;
using Integration.Shared.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Integration.Worker.Processing;

/// <summary>
/// The single external HTTP attempt. No hidden retry, no Polly, ONE budget.
/// Every failure mode is reduced to a safe ErpResult: status codes map to transient or
/// terminal, connection and timeout failures map to transient, and response parsing
/// problems map to a bounded, safe error code. Bodies are read with a hard cap.
///
/// The budget covers the WHOLE attempt - send, response headers, body stream and parse - not
/// just the part up to the headers. <see cref="HttpClient.Timeout"/> cannot express that:
/// under <see cref="HttpCompletionOption.ResponseHeadersRead"/> its timer stops once the
/// headers arrive, so an external system that flushes a 200 and then never finishes the body
/// could hold this worker's single job loop open indefinitely. The linked deadline below is
/// the one clock for the attempt, which is why HttpClient's own timeout is disabled.
/// </summary>
public sealed class ErpClient
{
    private const int MaxResponseBytes = 16 * 1024;

    private readonly HttpClient _httpClient;
    private readonly LabOptions _options;
    private readonly ILogger<ErpClient> _logger;

    public ErpClient(HttpClient httpClient, IOptions<LabOptions> options, ILogger<ErpClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The single deadline one attempt may consume, headers and body together.</summary>
    public TimeSpan AttemptBudget => TimeSpan.FromSeconds(_options.HttpTimeoutSeconds);

    public async Task<ErpResult> ApplyAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = LabTelemetry.Source.StartActivity(LabTelemetry.Spans.ExportHttpAttempt);
        activity?.SetTag(LabTelemetry.Tags.RequestId, request.RequestId.ToString());

        // Linked, not replaced: a host shutdown still cancels the attempt, and the catch blocks
        // below tell the two cases apart by asking which token fired.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(AttemptBudget);
        var deadline = attempt.Token;

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "erp/exports");
            message.Content = new StringContent(
                JsonSerializer.Serialize(request, LabJson.Options),
                Encoding.UTF8,
                "application/json");
            message.Headers.Add("Idempotency-Key", request.RequestId.ToString());

            using var response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline)
                .ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;

            if (statusCode is 408 or 429 or >= 500)
            {
                var retryAfter = statusCode == 429
                    ? RetryPolicy.ParseRetryAfter(response.Headers.RetryAfter?.ToString())
                    : null;

                _logger.LogWarning(
                    "ERP attempt for {RequestId} returned {StatusCode} (transient).",
                    request.RequestId,
                    statusCode);
                return new ErpResult(
                    Succeeded: false,
                    ReceiptId: null,
                    ErrorCode: SafeErrors.FromStatusCode(response.StatusCode),
                    Retryable: true,
                    RetryAfter: retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ERP attempt for {RequestId} returned {StatusCode} (terminal).",
                    request.RequestId,
                    statusCode);
                return new ErpResult(
                    Succeeded: false,
                    ReceiptId: null,
                    ErrorCode: SafeErrors.FromStatusCode(response.StatusCode),
                    Retryable: false,
                    RetryAfter: null);
            }

            // Still on the attempt deadline: a stalled or drip-fed body cannot outlast it.
            var receiptId = await ReadReceiptAsync(response, deadline).ConfigureAwait(false);
            if (receiptId is null)
            {
                // A 200 without a parsable receipt is treated as transient: the external
                // system's idempotency contract makes a safe retry possible.
                _logger.LogWarning(
                    "ERP attempt for {RequestId} returned 200 but no parsable receipt (transient).",
                    request.RequestId);
                return new ErpResult(
                    Succeeded: false,
                    ReceiptId: null,
                    ErrorCode: SafeErrors.HttpInvalidResponse,
                    Retryable: true,
                    RetryAfter: null);
            }

            activity?.SetTag(LabTelemetry.Tags.Outcome, "succeeded");
            _logger.LogInformation(
                "ERP attempt for {RequestId} succeeded with receipt {ReceiptId}.",
                request.RequestId,
                receiptId);
            return new ErpResult(true, receiptId, null, false, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The attempt deadline expired - anywhere from the connect to the last body byte.
            // The external effect is unknown, never "failed".
            _logger.LogWarning(
                "ERP attempt for {RequestId} exceeded its {Budget}s attempt budget (transient).",
                request.RequestId,
                AttemptBudget.TotalSeconds);
            return Transient(SafeErrors.HttpTimeout, null);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown: rethrow, the loop exits without recording a failure.
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "ERP attempt for {RequestId} connection failure: {Error} (transient).",
                request.RequestId,
                SafeErrors.Describe(ex));
            return Transient(SafeErrors.HttpConnection, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "ERP attempt for {RequestId} unexpected failure: {Error} (transient classification).",
                request.RequestId,
                SafeErrors.Describe(ex));
            return Transient(SafeErrors.HttpInvalidResponse, null);
        }
    }

    private static ErpResult Transient(string errorCode, TimeSpan? retryAfter) =>
        new(false, null, errorCode, true, retryAfter);

    private static async Task<string?> ReadReceiptAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body.Value);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("receiptId", out var receiptElement)
                || receiptElement.ValueKind is not JsonValueKind.String)
            {
                return null;
            }

            var receiptId = receiptElement.GetString();
            return string.IsNullOrWhiteSpace(receiptId) ? null : receiptId.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > MaxResponseBytes)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (buffer.Length > MaxResponseBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }
}
