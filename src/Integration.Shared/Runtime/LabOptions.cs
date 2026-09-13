using System.ComponentModel.DataAnnotations;

namespace Integration.Shared.Runtime;

/// <summary>Timing and budget knobs. Tests shorten these; the defaults document the intent.</summary>
public sealed class LabOptions
{
    public const string SectionName = "Lab";

    /// <summary>Identifies the logical consumer in InboxReceipts. Part of the receipt primary key.</summary>
    [Required]
    public string ConsumerName { get; set; } = "integration.worker";

    /// <summary>Total started attempts allowed for one job, including the first one.</summary>
    [Range(1, 20)]
    public int MaxJobAttempts { get; set; } = 5;

    /// <summary>
    /// Waits after attempt 1, 2, 3, 4. Empty means <see cref="DefaultJobRetryDelaysSeconds"/>.
    /// </summary>
    /// <remarks>
    /// The property intentionally starts EMPTY rather than holding the defaults. Configuration
    /// binding of an array APPENDS the configured elements to whatever the property already
    /// contains, so a non-empty default could never be overridden: setting
    /// <c>Lab:JobRetryDelaysSeconds:0=0</c> would produce <c>[2, 5, 15, 30, 0]</c> and the first
    /// wait would silently stay at 2 seconds. Keeping the default in
    /// <see cref="JobRetryDelay"/> makes configuration authoritative and still documents intent.
    /// </remarks>
    public int[] JobRetryDelaysSeconds { get; set; } = [];

    /// <summary>
    /// Separate budget: a broker outage must not consume the HTTP attempt budget.
    /// Empty means <see cref="DefaultOutboxRetryDelaysSeconds"/>; see the remarks above for why.
    /// </summary>
    public int[] OutboxRetryDelaysSeconds { get; set; } = [];

    /// <summary>The documented job retry schedule, used when none is configured.</summary>
    public static ReadOnlySpan<int> DefaultJobRetryDelaysSeconds => [2, 5, 15, 30];

    /// <summary>The documented publish retry schedule, used when none is configured.</summary>
    public static ReadOnlySpan<int> DefaultOutboxRetryDelaysSeconds => [1, 2, 5, 10, 30];

    [Range(1, 3600)] public int OutboxLeaseSeconds { get; set; } = 30;

    [Range(1, 3600)] public int JobLeaseSeconds { get; set; } = 30;

    [Range(1, 600)] public int ConfirmTimeoutSeconds { get; set; } = 10;

    [Range(1, 600)] public int HttpTimeoutSeconds { get; set; } = 5;

    [Range(1, 600)] public int MaxRetryAfterSeconds { get; set; } = 60;

    [Range(1, 1000)] public ushort PrefetchCount { get; set; } = 8;

    [Range(10, 60000)] public int PollIntervalMilliseconds { get; set; } = 250;

    [Range(10, 60000)] public int ReconnectBaseDelayMilliseconds { get; set; } = 500;

    [Range(100, 300000)] public int ReconnectMaxDelayMilliseconds { get; set; } = 10_000;

    /// <summary>Maximum accepted body size for an API request and for a consumed message.</summary>
    [Range(1024, 4 * 1024 * 1024)]
    public int MaxBodyBytes { get; set; } = 64 * 1024;

    public string ErpBaseAddress { get; set; } = "http://127.0.0.1:5199";

    /// <summary>Wait before the next job attempt, given how many attempts already started.</summary>
    public TimeSpan JobRetryDelay(int attemptsStarted)
    {
        var schedule = JobRetryDelaysSeconds.Length > 0
            ? JobRetryDelaysSeconds
            : DefaultJobRetryDelaysSeconds;

        // Past the end of the schedule the last wait repeats: the ATTEMPT BUDGET stops a job,
        // never the schedule running out.
        var index = Math.Max(0, attemptsStarted - 1);
        var seconds = index < schedule.Length ? schedule[index] : schedule[^1];
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Capped backoff for publish retries. Independent of the HTTP attempt budget.</summary>
    public TimeSpan OutboxRetryDelay(int publishAttempts)
    {
        var schedule = OutboxRetryDelaysSeconds.Length > 0
            ? OutboxRetryDelaysSeconds
            : DefaultOutboxRetryDelaysSeconds;

        var index = Math.Max(0, publishAttempts - 1);
        var seconds = index < schedule.Length ? schedule[index] : schedule[^1];
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>Broker connection settings. Names are prefixed so parallel test groups stay isolated.</summary>
public sealed class RabbitOptions
{
    public const string SectionName = "Lab:Rabbit";

    public string HostName { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public string VirtualHost { get; set; } = "/";

    /// <summary>Prefix applied to every exchange and queue name. Empty outside tests.</summary>
    public string NamePrefix { get; set; } = string.Empty;
}
