using Integration.Shared.Diagnostics;
using Integration.Shared.Messaging;
using Integration.Shared.Persistence;
using Integration.Shared.Runtime;
using Integration.Worker.Consuming;
using Integration.Worker.Processing;
using Integration.Worker.Publishing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Integration.Worker;

public static class Program
{
    public const string InitializeDbArg = "--initialize-db";
    public const string ReadyLogMarker = "integration-worker-host-ready";

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains(InitializeDbArg, StringComparer.OrdinalIgnoreCase))
        {
            // The bare flag is removed before configuration sees the arguments: the command-line
            // provider pairs a valueless "--key" with the NEXT token, so
            // `--initialize-db --environment=Development` would silently lose the environment.
            var builder = Host.CreateApplicationBuilder(WithoutFlag(args, InitializeDbArg));
            LabEnvironmentGuard.EnsureLabEnvironment(builder.Environment, "Integration.Worker");
            using var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
            var logger = loggerFactory.CreateLogger("Integration.Worker");
            try
            {
                var connectionString = builder.Configuration.GetConnectionString("IntegrationLab")
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
        }

        var host = BuildHost(args);
        await host.RunAsync();
        return 0;
    }

    /// <summary>Drops a valueless flag so the command-line configuration provider cannot eat the next argument.</summary>
    public static string[] WithoutFlag(string[] args, string flag) =>
        [.. args.Where(argument => !string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Composition root, shared verbatim with in-process test hosts. The optional
    /// <paramref name="configureServices"/> hook exists for test seams (for example
    /// replacing IConfirmedPublisher); production never passes it.
    /// </summary>
    public static IHost BuildHost(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder(args);

        LabEnvironmentGuard.EnsureLabEnvironment(builder.Environment, "Integration.Worker");

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

        // The receipt race path needs a fresh context after a failed transaction.
        // Scoped, not the default singleton: the factory resolves DbContextOptions, which
        // AddDbContext registers as scoped (CompositionTests validates the graph).
        builder.Services.AddDbContextFactory<LabDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(LabDbContext).Assembly.FullName);
                sql.CommandTimeout(30);
            }),
            lifetime: ServiceLifetime.Scoped);

        builder.Services.AddSingleton<RabbitConnection>();
        builder.Services.AddSingleton<Topology>();
        builder.Services.AddLabFaultHooks(builder.Environment);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ConfirmedPublisher>();
        builder.Services.AddSingleton<IConfirmedPublisher>(serviceProvider =>
            serviceProvider.GetRequiredService<ConfirmedPublisher>());

        builder.Services.AddHttpClient<ErpClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<LabOptions>>().Value;

            // Infinite ON PURPOSE: ErpClient owns the single attempt budget, which also covers
            // the response body. HttpClient.Timeout stops counting once the headers arrive
            // (HttpCompletionOption.ResponseHeadersRead), so leaving it armed would add a
            // second, weaker clock that hides the case the budget exists to bound.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.BaseAddress = new Uri(options.ErpBaseAddress);
        });

        builder.Services.AddScoped<OutboxStore>();
        builder.Services.AddScoped<JobStore>();
        builder.Services.AddScoped<MessageValidator>();
        builder.Services.AddScoped<DeadLetterWriter>();
        builder.Services.AddScoped<InboxAcceptor>();

        builder.Services.AddHostedService<OutboxDispatcher>();
        builder.Services.AddHostedService<InboxConsumer>();
        builder.Services.AddHostedService<JobProcessor>();
        // Registered last: its start marks "every hosted service began executing".
        builder.Services.AddHostedService<WorkerReadyMarker>();

        configureServices?.Invoke(builder.Services);

        return builder.Build();
    }
}

/// <summary>
/// Startup marker for process-based tests and scripts: once this line appears on stdout,
/// all three hosted loops are live. It does not mean any queue has been drained.
/// </summary>
public sealed class WorkerReadyMarker(ILogger<WorkerReadyMarker> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("{Marker}", Program.ReadyLogMarker);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
