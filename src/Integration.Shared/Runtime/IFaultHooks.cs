using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Integration.Shared.Runtime;

/// <summary>
/// The single composition point for fault hooks, shared verbatim by every application, so the
/// Testing-only boundary cannot drift apart between the API, the worker and FakeErp.
/// </summary>
public static class FaultHookRegistration
{
    public static IServiceCollection AddLabFaultHooks(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        if (!LabEnvironmentGuard.AllowsFaultInjection(environment.EnvironmentName))
        {
            // Development normal runs included: configuration cannot arm a crash or a stall.
            services.AddSingleton<IFaultHooks>(NoOpFaultHooks.Instance);
            return services;
        }

        services.AddSingleton<IFaultHooks>(sp => new ConfigurableFaultHooks(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<ConfigurableFaultHooks>>()));
        return services;
    }
}

/// <summary>
/// Named points where a test can interrupt the process. Production paths call
/// <see cref="ReachAsync"/>; the default implementation does nothing.
/// Faults are never reachable from a client request body - only from configuration
/// that the test harness or a local developer sets explicitly.
/// </summary>
public interface IFaultHooks
{
    ValueTask ReachAsync(string hook, CancellationToken cancellationToken);
}

public static class FaultPoints
{
    public const string ApiAfterRequestInsert = "api.after-request-insert";
    public const string OutboxAfterClaim = "outbox.after-claim";
    public const string OutboxAfterConfirmBeforeUpdate = "outbox.after-confirm-before-update";
    public const string InboxAfterCommitBeforeAck = "inbox.after-commit-before-ack";
    public const string JobAfterClaim = "job.after-claim";
    public const string JobAfterHttpBeforeUpdate = "job.after-http-before-update";
    public const string DeadLetterAfterConfirmBeforeUpdate = "deadletter.after-confirm-before-update";
}

public sealed class NoOpFaultHooks : IFaultHooks
{
    public static readonly NoOpFaultHooks Instance = new();

    public ValueTask ReachAsync(string hook, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>
/// Reads <c>Lab:Faults:&lt;hook&gt;</c> from configuration. Supported values:
/// <c>crash</c> (exit the process hard), <c>throw</c>, <c>delay:&lt;ms&gt;</c>.
/// The prefix <c>once:</c> makes the fault fire a single time.
///
/// Composed ONLY in <see cref="LabEnvironmentGuard.FaultInjectionEnvironment"/>. Every other
/// environment - Development included - gets <see cref="NoOpFaultHooks"/>, so a configuration
/// value left behind in a developer's settings can never crash or stall a normal run.
/// </summary>
public sealed class ConfigurableFaultHooks : IFaultHooks
{
    /// <summary>Exit code used for injected hard crashes, so a test can tell it apart from a real failure.</summary>
    public const int CrashExitCode = 70;

    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigurableFaultHooks> _logger;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _fired = new(StringComparer.OrdinalIgnoreCase);

    public ConfigurableFaultHooks(IConfiguration configuration, ILogger<ConfigurableFaultHooks> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async ValueTask ReachAsync(string hook, CancellationToken cancellationToken)
    {
        var configured = _configuration[$"Lab:Faults:{hook}"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        var action = configured.Trim();
        if (action.StartsWith("once:", StringComparison.OrdinalIgnoreCase))
        {
            action = action["once:".Length..];
            lock (_gate)
            {
                if (!_fired.Add(hook))
                {
                    return;
                }
            }
        }

        if (action.StartsWith("delay:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(action["delay:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            _logger.LogWarning("Fault hook {Hook} delaying {Milliseconds} ms", hook, milliseconds);
            await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (action.Equals("throw", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Fault hook {Hook} throwing", hook);
            throw new FaultInjectedException(hook);
        }

        if (action.Equals("crash", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Fault hook {Hook} exiting the process with code {Code}", hook, CrashExitCode);
            await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
            Environment.Exit(CrashExitCode);
        }
    }
}

public sealed class FaultInjectedException : Exception
{
    public FaultInjectedException(string hook)
        : base($"Injected fault at '{hook}'.") => Hook = hook;

    public FaultInjectedException()
        : base("Injected fault.") => Hook = string.Empty;

    public FaultInjectedException(string message, Exception innerException)
        : base(message, innerException) => Hook = string.Empty;

    public string Hook { get; }
}
