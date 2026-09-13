using Integration.Api.Errors;
using Integration.Api.Exports;
using Integration.Shared.Diagnostics;
using Integration.Shared.Messaging;
using Integration.Shared.Persistence;
using Integration.Shared.Runtime;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

namespace Integration.Api;

public static class Program
{
    public const string InitializeDbArg = "--initialize-db";
    public const string DefaultLoopbackUrl = "http://127.0.0.1:5099";

    public static async Task<int> Main(string[] args)
    {
        var app = BuildApp(args);

        if (args.Contains(InitializeDbArg, StringComparer.OrdinalIgnoreCase))
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Integration.Api");
            try
            {
                var connectionString = app.Configuration.GetConnectionString("IntegrationLab")
                    ?? throw new InvalidOperationException("ConnectionStrings:IntegrationLab is not configured.");
                await DatabaseInitializer.EnsureCreatedAndMigratedAsync(connectionString, CancellationToken.None);
                logger.LogInformation("IntegrationLab database created and migrated.");
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Database initialization failed.");
                return 1;
            }
            finally
            {
                await app.DisposeAsync();
            }
        }

        await app.RunAsync();
        return 0;
    }

    /// <summary>Composition root, shared verbatim with in-process test hosts.</summary>
    public static WebApplication BuildApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        LabEnvironmentGuard.EnsureLabEnvironment(builder.Environment, "Integration.Api");

        // Loopback-only by default. Tests and scripts pass an explicit urls argument - and the
        // guard below is what makes "loopback only" a boundary rather than a default, because
        // --urls, ASPNETCORE_URLS, ASPNETCORE_HTTP_PORTS and Kestrel:Endpoints all override it.
        if (builder.Configuration[LoopbackGuard.UrlsKey] is null)
        {
            builder.WebHost.UseUrls(DefaultLoopbackUrl);
        }

        LoopbackGuard.EnsureLoopbackOnly(builder.Configuration, "Integration.Api");

        builder.AddLabTelemetry();

        builder.Services.AddOptions<LabOptions>()
            .Bind(builder.Configuration.GetSection(LabOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddOptions<RabbitOptions>()
            .Bind(builder.Configuration.GetSection(RabbitOptions.SectionName));

        var connectionString = builder.Configuration.GetConnectionString("IntegrationLab")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:IntegrationLab is not configured. " +
                "Provide it via user-secrets, environment or command line.");

        builder.Services.AddDbContext<LabDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(LabDbContext).Assembly.FullName);
                sql.CommandTimeout(30);
            }));

        // The duplicate-acceptance path needs a fresh context after a failed transaction.
        // Scoped, not the default singleton: the factory resolves DbContextOptions, which
        // AddDbContext registers as SCOPED. A singleton consuming a scoped service fails DI
        // validation - and that validation is only enabled in Development, so this stayed
        // invisible to the test suite (which runs as Testing) while breaking the documented
        // `dotnet run` setup path.
        builder.Services.AddDbContextFactory<LabDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(LabDbContext).Assembly.FullName);
                sql.CommandTimeout(30);
            }),
            lifetime: ServiceLifetime.Scoped);

        builder.Services.AddSingleton<Topology>();
        builder.Services.AddLabFaultHooks(builder.Environment);
        builder.Services.AddScoped<SubmitExportHandler>();
        builder.Services.AddScoped<GetExportStatusHandler>();

        builder.Services.AddHealthChecks()
            .AddCheck<SqlReadyHealthCheck>("sql", tags: ["ready"]);

        var app = builder.Build();

        app.UseExceptionHandler(errorApp => errorApp.Run(ProblemDetailsSetup.HandleUnhandledAsync));
        app.Use(ProblemDetailsSetup.RewriteBareStatusCodes);

        app.MapExportEndpoints();
        // Liveness must run NO checks. Without an explicit predicate this endpoint runs every
        // registered check - including the SQL one - so a database outage would report the
        // process as dead and an orchestrator would restart a perfectly healthy API.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
        });

        // Readiness is the opposite: it exists precisely to say "this instance cannot work
        // right now", so it does depend on SQL.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
        });

        return app;
    }
}
