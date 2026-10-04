using FakeErp;
using Integration.Shared.Persistence;
using Integration.Shared.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using ApiProgram = Integration.Api.Program;
using FakeErpProgram = FakeErp.Program;
using WorkerProgram = Integration.Worker.Program;

namespace IntegrationLab.Tests;

/// <summary>
/// Every application's DI graph must be valid in DEVELOPMENT, not merely in the environment
/// the test suite happens to use.
///
/// This class exists because of a real defect: the EF Core context factories were registered
/// as singletons while consuming the scoped <c>DbContextOptions</c> that <c>AddDbContext</c>
/// registers. Scope validation is enabled by default only in Development, so the whole suite —
/// which runs as Testing — stayed green while the documented `dotnet run` setup path failed on
/// the first command of `scripts/init-lab.ps1`.
///
/// Building the provider never opens a connection, so no container is needed and this class
/// stays out of the "lab" collection.
/// </summary>
public sealed class CompositionTests
{
    // Syntactically valid and never connected to.
    private const string UnusedConnectionString =
        "Server=127.0.0.1,11433;Database=NotUsed;User Id=sa;Password=Unused_Placeholder#2026;TrustServerCertificate=True";

    [Fact]
    public void TheApiCompositionRootIsValidInDevelopment()
    {
        // Development turns on ValidateScopes and ValidateOnBuild, so Build() itself is the
        // assertion: a singleton depending on a scoped service throws right here.
        var app = ApiProgram.BuildApp(
        [
            "--environment=Development",
            "--urls=http://127.0.0.1:0",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
        ]);

        using var scope = app.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDbContextFactory<LabDbContext>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<LabDbContext>());
    }

    [Fact]
    public void TheWorkerCompositionRootIsValidInDevelopment()
    {
        using var host = WorkerProgram.BuildHost(
        [
            "--environment=Development",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
        ]);

        using var scope = host.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDbContextFactory<LabDbContext>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<OutboxStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<JobStore>());
    }

    [Fact]
    public void FakeErpCompositionRootIsValidInDevelopment()
    {
        var app = FakeErpProgram.BuildApp(
        [
            "--environment=Development",
            "--urls=http://127.0.0.1:0",
            $"--ConnectionStrings:FakeErpLab={UnusedConnectionString}",
        ]);

        using var scope = app.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDbContextFactory<ErpDbContext>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ApplyExportHandler>());
    }

    // ---------------------------------------------------------------- loopback boundary
    //
    // The boundary is "loopback only", not "loopback by default". A default is what the
    // host uses when nothing else is configured; --urls, ASPNETCORE_URLS, ASPNETCORE_HTTP_PORTS
    // and a Kestrel:Endpoints section all replace it silently. These tests assert the boundary
    // at CONFIGURATION time, so nothing ever binds a real interface to prove the point.

    public static TheoryData<string> RefusedListenUrls() =>
    [
        "http://0.0.0.0:5099",
        "http://*:5099",
        "http://+:5099",
        "http://[::]:5099",
        "http://10.0.0.5:5099",
        "http://example.internal:5099",
        // A mixed list: one good entry must not excuse the other.
        "http://127.0.0.1:5099;http://0.0.0.0:5100",
    ];

    public static TheoryData<string> AcceptedListenUrls() =>
    [
        "http://127.0.0.1:5099",
        "http://127.0.0.2:5099",
        "http://localhost:5099",
        "http://LOCALHOST:5099",
        "http://[::1]:5099",
        // Port 0 is what the tests themselves use.
        "http://127.0.0.1:0",
        "http://127.0.0.1:5099;http://localhost:5100",
    ];

    [Theory]
    [MemberData(nameof(RefusedListenUrls))]
    public void TheApiRefusesToStartOnANonLoopbackUrl(string urls)
    {
        var failure = Assert.Throws<LabEnvironmentException>(() => ApiProgram.BuildApp(
        [
            "--environment=Development",
            $"--urls={urls}",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
        ]));

        Assert.Contains("loopback", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(RefusedListenUrls))]
    public void FakeErpRefusesToStartOnANonLoopbackUrl(string urls)
    {
        // FakeErp matters at least as much as the API here: its scenario control endpoints ride
        // on the very same listener.
        Assert.Throws<LabEnvironmentException>(() => FakeErpProgram.BuildApp(
        [
            "--environment=Development",
            $"--urls={urls}",
            $"--ConnectionStrings:FakeErpLab={UnusedConnectionString}",
        ]));
    }

    [Theory]
    [MemberData(nameof(AcceptedListenUrls))]
    public void ALoopbackUrlIsStillAccepted(string urls)
    {
        var app = ApiProgram.BuildApp(
        [
            "--environment=Development",
            $"--urls={urls}",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
        ]);

        Assert.NotNull(app);
    }

    [Fact]
    public void AKestrelEndpointOverrideCannotEscapeLoopbackEither()
    {
        // The Kestrel:Endpoints section is a second, independent way to choose an address, and
        // it is honoured even when `urls` is absent - so checking only `urls` would leave the
        // boundary open.
        Assert.Throws<LabEnvironmentException>(() => ApiProgram.BuildApp(
        [
            "--environment=Development",
            "--Kestrel:Endpoints:Public:Url=http://0.0.0.0:5099",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
        ]));

        var loopback = ApiProgram.BuildApp(
        [
            "--environment=Development",
            "--Kestrel:Endpoints:Local:Url=http://127.0.0.1:0",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
        ]);
        Assert.NotNull(loopback);
    }

    [Theory]
    [InlineData("http_ports")]
    [InlineData("https_ports")]
    public void APortOnlyOverrideIsRefusedBecauseItBindsEveryInterface(string key)
    {
        // ASPNETCORE_HTTP_PORTS has no host part at all: it always means every interface, so
        // there is no loopback reading of it to accept.
        Assert.Throws<LabEnvironmentException>(() => ApiProgram.BuildApp(
        [
            "--environment=Development",
            $"--{key}=5099",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
        ]));
    }

    [Fact]
    public void TheDefaultWithNoUrlConfigurationIsLoopback()
    {
        Assert.True(LoopbackGuard.IsLoopbackUrl(ApiProgram.DefaultLoopbackUrl, out _));
        Assert.True(LoopbackGuard.IsLoopbackUrl(FakeErpProgram.DefaultLoopbackUrl, out _));
    }

    // ------------------------------------------------------------- fault-hook boundary

    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    public void FaultHooksAreNotComposedOutsideTesting(string environment)
    {
        // Fault injection is scoped to the test harness. A `crash` fault is a hard
        // Environment.Exit, so a value left in a developer's configuration must be inert.
        using var host = WorkerProgram.BuildHost(
        [
            $"--environment={environment}",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
            $"--Lab:Faults:{FaultPoints.JobAfterClaim}=crash",
        ]);

        Assert.IsType<NoOpFaultHooks>(host.Services.GetRequiredService<IFaultHooks>());

        var api = ApiProgram.BuildApp(
        [
            $"--environment={environment}",
            "--urls=http://127.0.0.1:0",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
            $"--Lab:Faults:{FaultPoints.ApiAfterRequestInsert}=crash",
        ]);
        Assert.IsType<NoOpFaultHooks>(api.Services.GetRequiredService<IFaultHooks>());
    }

    [Fact]
    public void FaultHooksAreComposedInTesting()
    {
        using var host = WorkerProgram.BuildHost(
        [
            "--environment=Testing",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
        ]);

        Assert.IsType<ConfigurableFaultHooks>(host.Services.GetRequiredService<IFaultHooks>());
    }

    [Fact]
    public async Task ANoOpHookDoesNothingEvenWhenAFaultIsConfigured()
    {
        // Proof that the Development registration is inert rather than merely a different type:
        // reaching a hook configured to crash returns normally and this process stays alive.
        using var host = WorkerProgram.BuildHost(
        [
            "--environment=Development",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
            $"--Lab:Faults:{FaultPoints.JobAfterClaim}=crash",
            $"--Lab:Faults:{FaultPoints.JobAfterHttpBeforeUpdate}=delay:600000",
        ]);

        var hooks = host.Services.GetRequiredService<IFaultHooks>();
        await hooks.ReachAsync(FaultPoints.JobAfterClaim, TestContext.Current.CancellationToken);
        await hooks.ReachAsync(FaultPoints.JobAfterHttpBeforeUpdate, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The valueless <c>--initialize-db</c> flag must not swallow the next argument. The
    /// command-line configuration provider pairs a bare "--key" with the following token, so
    /// `--initialize-db --environment=Development` used to be read as
    /// initialize-db = "--environment=Development" — and the environment guard then refused to
    /// start the very command the README tells you to run.
    /// </summary>
    [Fact]
    public void TheInitializeDbFlagDoesNotSwallowTheArgumentAfterIt()
    {
        string[] args =
        [
            WorkerProgram.InitializeDbArg,
            "--environment=Development",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
        ];

        var stripped = WorkerProgram.WithoutFlag(args, WorkerProgram.InitializeDbArg);

        Assert.DoesNotContain(WorkerProgram.InitializeDbArg, stripped);
        Assert.Equal(2, stripped.Length);

        var configuration = new ConfigurationBuilder()
            .AddCommandLine(stripped)
            .Build();
        Assert.Equal("Development", configuration["environment"]);

        // And the unstripped form is exactly the failure this guards against.
        var naive = new ConfigurationBuilder()
            .AddCommandLine(args)
            .Build();
        Assert.NotEqual("Development", naive["environment"]);
    }
}
