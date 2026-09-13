using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Integration.Api.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Integration.Api.Errors;

/// <summary>
/// Every error path in this API writes an RFC 9457 problem+json document with a stable
/// errorCode and a traceable traceId - regardless of the request's Accept header, and
/// without ever leaking raw exceptions, stack traces, SQL text or credentials.
/// </summary>
public static class ProblemDetailsSetup
{
    public const string ContentType = "application/problem+json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string TitleFor(int statusCode) => statusCode switch
    {
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        415 => "Unsupported Media Type",
        500 => "Internal Server Error",
        _ => "Error",
    };

    /// <summary>Writes the canonical problem response. The caller has already decided status and code.</summary>
    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string errorCode,
        string detail,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = TitleFor(statusCode),
            Detail = detail,
            Instance = context.Request.Path,
        };

        problem.Extensions["errorCode"] = errorCode;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        if (fieldErrors is { Count: > 0 })
        {
            problem.Extensions["errors"] = fieldErrors;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = ContentType;

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            JsonOptions,
            context.RequestAborted).ConfigureAwait(false);
    }

    public static Task WriteAsync(HttpContext context, ApiException error) =>
        WriteAsync(context, error.StatusCode, error.ErrorCode, error.Detail, error.FieldErrors);

    /// <summary>
    /// Terminal handler for UseExceptionHandler. Known ApiExceptions keep their status and
    /// errorCode; everything else becomes a generic internal_error problem. Raw exception
    /// text, stack frames, SQL text and credentials never reach the client.
    /// </summary>
    public static async Task HandleUnhandledAsync(HttpContext context)
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var error = feature?.Error;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Integration.Api.ProblemDetails");

        if (context.Response.HasStarted)
        {
            logger.LogError(error, "The response had already started; no problem document can be written.");
            return;
        }

        switch (error)
        {
            case ApiException apiError:
                logger.LogInformation(
                    "Request {Method} {Path} failed with {StatusCode} {ErrorCode}",
                    context.Request.Method,
                    context.Request.Path,
                    apiError.StatusCode,
                    apiError.ErrorCode);
                await WriteAsync(context, apiError).ConfigureAwait(false);
                break;
            case null:
                await WriteAsync(context, 500, "internal_error", "An unexpected error occurred.").ConfigureAwait(false);
                break;
            default:
                logger.LogError(
                    error,
                    "Unhandled exception while processing {Method} {Path}",
                    context.Request.Method,
                    context.Request.Path);
                await WriteAsync(context, 500, "internal_error", "An unexpected error occurred.").ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Covers responses the framework produced without a body: unknown routes (404) and
    /// method-not-allowed (405). Bozuk HTTP paketleri that Kestrel rejects before the
    /// application pipeline are out of scope by design.
    /// Register with: app.Use(ProblemDetailsSetup.RewriteBareStatusCodes).
    /// </summary>
    public static async Task RewriteBareStatusCodes(HttpContext context, Func<Task> next)
    {
        await next().ConfigureAwait(false);

        if (context.Response.HasStarted
            || context.Response.StatusCode is not (404 or 405)
            || context.Response.ContentLength > 0
            || !string.IsNullOrEmpty(context.Response.ContentType))
        {
            return;
        }

        var errorCode = context.Response.StatusCode switch
        {
            404 => "not_found",
            405 => "method_not_allowed",
            _ => "error",
        };

        var detail = context.Response.StatusCode == 404
            ? $"No endpoint matches '{context.Request.Method} {context.Request.Path}'."
            : $"Method '{context.Request.Method}' is not allowed for '{context.Request.Path}'.";

        await WriteAsync(context, context.Response.StatusCode, errorCode, detail).ConfigureAwait(false);
    }
}
