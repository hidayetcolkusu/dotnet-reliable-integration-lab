using Integration.Shared.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Integration.Api;

/// <summary>
/// Readiness = SQL reachable. Liveness (/health/live) never checks dependencies, so a SQL
/// outage never kills the process or drops readiness-liveness coupling.
/// </summary>
public sealed class SqlReadyHealthCheck : IHealthCheck
{
    private readonly LabDbContext _context;

    public SqlReadyHealthCheck(LabDbContext context)
    {
        _context = context;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _context.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
            return canConnect
                ? HealthCheckResult.Healthy("SQL reachable.")
                : new HealthCheckResult(context.Registration.FailureStatus, "SQL unreachable.");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "SQL unreachable.",
                ex);
        }
    }
}
