using Integration.Shared.Runtime;

namespace IntegrationLab.Tests.Support;

/// <summary>
/// Fault hooks are test-only configuration (never client input). These helpers build the
/// exact configuration arguments that ConfigurableFaultHooks reads, so in-process hosts
/// and child processes share one mechanism.
/// </summary>
public static class FaultController
{
    /// <summary>A one-shot hard process exit at the hook point (child processes only).</summary>
    public static string CrashOnce(string hook) => $"--Lab:Faults:{hook}=once:crash";

    /// <summary>An exception at the hook point; safe for in-process hosts.</summary>
    public static string Throw(string hook) => $"--Lab:Faults:{hook}=throw";

    /// <summary>A one-shot exception; safe for in-process hosts.</summary>
    public static string ThrowOnce(string hook) => $"--Lab:Faults:{hook}=once:throw";

    /// <summary>A delay at the hook point, to widen a crash window from the outside.</summary>
    public static string Delay(string hook, int milliseconds) => $"--Lab:Faults:{hook}=delay:{milliseconds}";

    /// <summary>The exit code a `crash` fault produces, so tests can tell injected crashes from real failures.</summary>
    public const int CrashExitCode = ConfigurableFaultHooks.CrashExitCode;
}
