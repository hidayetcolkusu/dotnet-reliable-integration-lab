using System.Diagnostics;

namespace Integration.Shared.Diagnostics;

public static class LabTelemetry
{
    public const string ActivitySourceName = "IntegrationLab";
    public const string Version = "1.0.0";

    public static readonly ActivitySource Source = new(ActivitySourceName, Version);

    public static class Tags
    {
        public const string RequestId = "lab.request_id";
        public const string EventId = "lab.event_id";
        public const string OutboxId = "lab.outbox_id";
        public const string TransportMessageId = "lab.transport_message_id";
        public const string AttemptNumber = "lab.attempt_number";
        public const string State = "lab.state";
        public const string ErrorCode = "lab.error_code";
        public const string Outcome = "lab.outcome";
        public const string Kind = "lab.kind";
        public const string ReasonCode = "lab.reason_code";
    }

    public static class Spans
    {
        public const string ApiReceive = "lab.api.receive";
        public const string OutboxPublish = "lab.outbox.publish";
        public const string InboxPersist = "lab.inbox.persist";
        public const string ExportHttpAttempt = "lab.export.http_attempt";
        public const string DeadLetterPublish = "lab.deadletter.publish";
    }

    /// <summary>Serialises the current W3C trace context so it survives in SQL across processes.</summary>
    public static (string? TraceParent, string? TraceState) CaptureContext()
    {
        var activity = Activity.Current;
        return activity is null ? (null, null) : (activity.Id, activity.TraceStateString);
    }

    /// <summary>
    /// Starts an activity whose parent is a stored trace context. A malformed stored header must
    /// never poison the work, so an unparsable value simply produces a fresh root activity.
    /// </summary>
    public static Activity? StartLinkedTo(
        string name,
        string? traceParent,
        string? traceState,
        ActivityKind kind = ActivityKind.Internal)
    {
        if (!string.IsNullOrWhiteSpace(traceParent)
            && ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parent))
        {
            return Source.StartActivity(name, kind, parent);
        }

        return Source.StartActivity(name, kind);
    }
}
