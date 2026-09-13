using System.Text.Json;
using FakeErp;
using Integration.Shared.Diagnostics;
using Integration.Shared.Persistence;
using Integration.Shared.Runtime;
using Microsoft.EntityFrameworkCore;

namespace FakeErp;

public static class Program
{
    public const string InitializeDbArg = "--initialize-db";
    public const string DefaultLoopbackUrl = "http://127.0.0.1:5199";

    public static async Task<int> Main(string[] args)
    {
        // The bare flag is removed before configuration sees the arguments. The command-line
        // configuration provider pairs a valueless "--key" with the NEXT token, so
        // `--initialize-db --environment=Development` is read as
        // initialize-db = "--environment=Development" and the environment is silently lost -
        // which made this documented setup path fail the lab's own environment guard.
        var app = BuildApp(WithoutFlag(args, InitializeDbArg));

        if (args.Contains(InitializeDbArg, StringComparer.OrdinalIgnoreCase))
        {
            using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
            var logger = loggerFactory.CreateLogger("FakeErp");
            try
            {
                var connectionString = app.Configuration.GetConnectionString("FakeErpLab")
                    ?? throw new InvalidOperationException("ConnectionStrings:FakeErpLab is not configured.");
                await EnsureErpDatabaseAsync(connectionString, CancellationToken.None);
                logger.LogInformation("FakeErpLab database created and migrated.");
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

    /// <summary>Drops a valueless flag so the command-line configuration provider cannot eat the next argument.</summary>
    public static string[] WithoutFlag(string[] args, string flag) =>
        [.. args.Where(argument => !string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Composition root, shared verbatim with in-process test hosts.</summary>
    public static WebApplication BuildApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        LabEnvironmentGuard.EnsureLabEnvironment(builder.Environment, "FakeErp");

        // Loopback-only by default; tests pass an explicit urls argument. The guard turns that
        // default into an enforced boundary: this process also exposes the scenario control
        // endpoints on the same listener, so an override onto a real interface would publish
        // them too.
        if (builder.Configuration[LoopbackGuard.UrlsKey] is null)
        {
            builder.WebHost.UseUrls(DefaultLoopbackUrl);
        }

        LoopbackGuard.EnsureLoopbackOnly(builder.Configuration, "FakeErp");

        builder.AddLabTelemetry();

        var connectionString = builder.Configuration.GetConnectionString("FakeErpLab")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:FakeErpLab is not configured. " +
                "Provide it via user-secrets, environment or command line.");

        builder.Services.AddDbContext<ErpDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(ErpDbContext).Assembly.FullName);
                sql.CommandTimeout(30);
            }));
        // Scoped, not the default singleton: the factory resolves DbContextOptions, which
        // AddDbContext registers as SCOPED. A singleton consuming a scoped service fails DI
        // validation - and that validation is only enabled in Development, so this stayed
        // invisible to the test suite (which runs as Testing) while breaking the documented
        // `dotnet run` setup path.
        builder.Services.AddDbContextFactory<ErpDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(ErpDbContext).Assembly.FullName);
                sql.CommandTimeout(30);
            }),
            lifetime: ServiceLifetime.Scoped);

        // Liveness only: whether this process answers, never whether SQL is reachable. A
        // readiness probe that fails on a database outage would take the external system out
        // of rotation exactly when the retry path is supposed to be exercised.
        builder.Services.AddHealthChecks();

        builder.Services.AddSingleton<ScenarioRegistry>();
        builder.Services.AddScoped<ApplyExportHandler>();

        var app = builder.Build();

        app.MapPost("/erp/exports", async (HttpContext http) =>
        {
            var handler = http.RequestServices.GetRequiredService<ApplyExportHandler>();
            await handler.HandleAsync(http);
        });

        MapScenarioEndpoints(app);
        // Explicitly runs no checks, for the same reason as the API's liveness endpoint.
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => false,
        });

        return app;
    }

    /// <summary>Loopback lab control endpoints. Meaningless and absent outside Development/Testing (guard).</summary>
    private static void MapScenarioEndpoints(WebApplication app)
    {
        app.MapGet("/erp/scenarios", (ScenarioRegistry registry) => Results.Json(
            registry.Snapshot().ToDictionary(
                pair => pair.Key.ToString("D"),
                pair => new { type = pair.Value.Type, n = pair.Value.N, delayMs = pair.Value.DelayMs })));

        app.MapPost("/erp/scenarios", async (HttpContext http, ScenarioRegistry registry) =>
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("requestId", out var requestIdElement)
                || !Guid.TryParse(requestIdElement.GetString(), out var requestId)
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind is not JsonValueKind.String
                || !ScenarioRegistry.IsValidType(typeElement.GetString()!))
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { errorCode = "invalid_scenario" }, http.RequestAborted);
                return;
            }

            var n = root.TryGetProperty("n", out var nElement) && nElement.TryGetInt32(out var parsedN) ? parsedN : 0;
            var delayMs = root.TryGetProperty("delayMs", out var delayElement) && delayElement.TryGetInt32(out var parsedDelay) ? parsedDelay : 8000;

            registry.Set(requestId, new ScenarioRegistry.Scenario(typeElement.GetString()!, n, delayMs));
            http.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        app.MapDelete("/erp/scenarios", (ScenarioRegistry registry) =>
        {
            registry.Reset();
            return Results.NoContent();
        });
    }

    private static async Task EnsureErpDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        var applicationBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
        var databaseName = applicationBuilder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName)
            || !System.Text.RegularExpressions.Regex.IsMatch(databaseName, "^[A-Za-z0-9_]{1,100}$"))
        {
            throw new ArgumentException(
                $"The connection string must target a simple database name (letters, digits, underscore): '{databaseName}'.",
                nameof(connectionString));
        }

        applicationBuilder.InitialCatalog = "master";
        await using (var master = new Microsoft.Data.SqlClient.SqlConnection(applicationBuilder.ConnectionString))
        {
            await master.OpenAsync(cancellationToken);
            await using var command = master.CreateCommand();
            command.CommandText = $"IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{databaseName}]";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(ErpDbContext).Assembly.FullName);
                sql.CommandTimeout(120);
            })
            .Options;

        await using (var context = new ErpDbContext(options))
        {
            await context.Database.MigrateAsync(cancellationToken);
        }
    }
}
