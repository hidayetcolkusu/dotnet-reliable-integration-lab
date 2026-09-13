using System.Globalization;
using Integration.Shared.Runtime;

namespace Integration.Worker.Processing;

/// <summary>
/// Pure retry classification and scheduling. No clocks, no I/O - so every rule
/// (transient vs terminal, Retry-After parsing and bounding) is exactly testable.
/// The HTTP client itself never retries; the only retry counter is the job row.
/// </summary>
public static class RetryPolicy
{
    public static bool IsTransientStatus(int statusCode) =>
        statusCode is 408 or 429 or >= 500;

    /// <summary>
    /// Parses a Retry-After header: delta-seconds or HTTP-date. Negative values, dates in
    /// the past, and anything unparseable are ignored (null) - the planned schedule applies.
    /// </summary>
    public static TimeSpan? ParseRetryAfter(string? headerValue, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        var value = headerValue.Trim();

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }

        if (DateTimeOffset.TryParseExact(
                value,
                "R",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var httpDate))
        {
            var delay = httpDate - (now ?? DateTimeOffset.UtcNow);
            return delay > TimeSpan.Zero ? delay : null;
        }

        return null;
    }

    /// <summary>
    /// Delay after a transient failure. The server's Retry-After is respected by taking the
    /// larger of planned and requested, but never beyond the configured cap (default 60s).
    /// </summary>
    public static TimeSpan DelayFor(int attemptsStarted, TimeSpan? retryAfter, LabOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var planned = options.JobRetryDelay(attemptsStarted);
        if (retryAfter is not { } requested)
        {
            return planned;
        }

        var cap = TimeSpan.FromSeconds(options.MaxRetryAfterSeconds);
        var bounded = requested > cap ? cap : requested;
        return bounded > planned ? bounded : planned;
    }
}
