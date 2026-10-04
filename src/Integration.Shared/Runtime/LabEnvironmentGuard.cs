using Microsoft.Extensions.Hosting;

namespace Integration.Shared.Runtime;

/// <summary>
/// This lab has no authentication and exposes control endpoints. It must refuse to start
/// anywhere except Development and Testing, whatever else is configured.
/// </summary>
public static class LabEnvironmentGuard
{
    public static readonly string[] AllowedEnvironments = ["Development", "Testing"];

    /// <summary>
    /// The ONLY environment in which fault injection is composed. The lab draws this line at
    /// the test harness on purpose: a `crash` fault is a hard <c>Environment.Exit</c>, and a
    /// developer running the lab normally (Development) must not be able to arm one through a
    /// stray configuration value. Tests and the scripted crash scenarios pass
    /// <c>--environment=Testing</c> explicitly.
    /// </summary>
    public const string FaultInjectionEnvironment = "Testing";

    public static bool IsLabEnvironment(string environmentName) =>
        AllowedEnvironments.Contains(environmentName, StringComparer.OrdinalIgnoreCase);

    /// <summary>True only in <see cref="FaultInjectionEnvironment"/>; everything else gets NoOp hooks.</summary>
    public static bool AllowsFaultInjection(string environmentName) =>
        string.Equals(environmentName, FaultInjectionEnvironment, StringComparison.OrdinalIgnoreCase);

    public static void EnsureLabEnvironment(IHostEnvironment environment, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (IsLabEnvironment(environment.EnvironmentName))
        {
            return;
        }

        throw new LabEnvironmentException(
            $"{applicationName} refuses to start in environment '{environment.EnvironmentName}'. " +
            $"This lab is unauthenticated and only runs in: {string.Join(", ", AllowedEnvironments)}.");
    }
}

public sealed class LabEnvironmentException : InvalidOperationException
{
    public LabEnvironmentException(string message)
        : base(message)
    {
    }

    public LabEnvironmentException()
        : base("Not a lab environment.")
    {
    }

    public LabEnvironmentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
