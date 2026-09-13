using System.Diagnostics;
using System.Text;
using Integration.Shared.Persistence;
using RabbitMQ.Client;

namespace Integration.Shared.Messaging;

/// <summary>
/// Builds and reads the AMQP basic properties this lab puts on the wire.
///
/// Broker MessageId is always the OutboxMessages.Id, so a republished outbox row keeps the
/// same transport identity and the inbox can deduplicate confirm-loss duplicates.
/// W3C trace context travels in explicit traceparent/tracestate headers.
/// </summary>
public static class MessageProperties
{
    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";

    public static BasicProperties CreatePublishProperties(
        Guid outboxId,
        Guid requestId,
        string type,
        string? traceParent,
        string? traceState)
    {
        var properties = new BasicProperties
        {
            MessageId = outboxId.ToString(),
            CorrelationId = requestId.ToString(),
            Type = type,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };

        if (!string.IsNullOrEmpty(traceParent) || !string.IsNullOrEmpty(traceState))
        {
            var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(traceParent))
            {
                headers[TraceParentHeader] = traceParent;
            }

            if (!string.IsNullOrEmpty(traceState))
            {
                headers[TraceStateHeader] = traceState;
            }

            properties.Headers = headers;
        }

        return properties;
    }

    /// <summary>
    /// Reads a header value as text. Values may arrive as string or as a byte[] payload,
    /// depending on the producing client; neither may ever throw into message processing.
    /// </summary>
    public static string? ReadHeaderText(IReadOnlyBasicProperties properties, string name)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Headers is not { Count: > 0 } headers || !headers.TryGetValue(name, out var value))
        {
            return null;
        }

        return value switch
        {
            null => null,
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
            _ => null,
        };
    }

    /// <summary>
    /// Normalises the W3C trace context a delivery carries to something that is BOTH parsable
    /// and storable.
    ///
    /// Diagnostic data must never decide whether business work can proceed, and these values
    /// are persisted (OutboxMessages/IntegrationJobs carry them in bounded varchar columns), so
    /// an oversized or unparsable header has to be dropped here rather than fail an INSERT
    /// inside the acceptance transaction - which would leave the delivery unACKed and looping.
    ///
    /// The two headers are degraded independently: an unusable tracestate costs only the
    /// vendor state, not the parent link. A tracestate WITHOUT a parent is meaningless on its
    /// own and is dropped with it.
    /// </summary>
    public static (string? TraceParent, string? TraceState) ReadTraceContext(IReadOnlyBasicProperties properties)
    {
        var traceParent = ReadHeaderText(properties, TraceParentHeader);
        var traceState = ReadHeaderText(properties, TraceStateHeader);

        if (string.IsNullOrWhiteSpace(traceParent)
            || traceParent.Length > ColumnLengths.TraceParent
            || !ActivityContext.TryParse(traceParent, null, isRemote: true, out _))
        {
            return (null, null);
        }

        if (!string.IsNullOrEmpty(traceState)
            && (traceState.Length > ColumnLengths.TraceState || !IsWellFormedTraceState(traceState)))
        {
            traceState = null;
        }

        return (traceParent, traceState);
    }

    /// <summary>
    /// W3C tracestate: up to 32 comma-separated <c>key=value</c> members, values printable
    /// ASCII without ',' or '='.
    ///
    /// This is checked explicitly because <see cref="ActivityContext.TryParse"/> does NOT
    /// validate tracestate - it accepts any string and carries it through. Storing an
    /// arbitrary header value in a diagnostic column and then propagating it onward is how a
    /// malformed value spreads; dropping it costs only vendor state.
    /// </summary>
    public static bool IsWellFormedTraceState(string traceState)
    {
        ArgumentNullException.ThrowIfNull(traceState);

        var members = traceState.Split(',');
        if (members.Length > 32)
        {
            return false;
        }

        foreach (var member in members)
        {
            var entry = member.Trim(' ', '\t');
            if (entry.Length == 0)
            {
                // The grammar allows empty list members; they simply carry nothing.
                continue;
            }

            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1)
            {
                return false;
            }

            if (!IsTraceStateKey(entry.AsSpan(..separator)) || !IsTraceStateValue(entry.AsSpan((separator + 1)..)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTraceStateKey(ReadOnlySpan<char> key)
    {
        // Simple key, or the multi-tenant 'tenant@vendor' form. Length limits come from W3C.
        if (key.Length is 0 or > 256)
        {
            return false;
        }

        var at = key.IndexOf('@');
        if (at >= 0)
        {
            return IsTraceStateKeySegment(key[..at], 241) && IsTraceStateKeySegment(key[(at + 1)..], 14);
        }

        return IsTraceStateKeySegment(key, 256);
    }

    private static bool IsTraceStateKeySegment(ReadOnlySpan<char> segment, int maxLength)
    {
        if (segment.Length == 0 || segment.Length > maxLength)
        {
            return false;
        }

        if (!IsLowerAscii(segment[0]) && !char.IsAsciiDigit(segment[0]))
        {
            return false;
        }

        foreach (var character in segment)
        {
            if (!IsLowerAscii(character)
                && !char.IsAsciiDigit(character)
                && character is not ('_' or '-' or '*' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerAscii(char character) => character is >= 'a' and <= 'z';

    private static bool IsTraceStateValue(ReadOnlySpan<char> value)
    {
        if (value.Length is 0 or > 256 || value[^1] == ' ')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character < ' ' || character > '~' || character is ',' or '=')
            {
                return false;
            }
        }

        return true;
    }
}
