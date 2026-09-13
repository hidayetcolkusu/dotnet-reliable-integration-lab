using System.Security.Cryptography;
using FakeErp;
using Integration.Shared.Persistence;
using IntegrationLab.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace IntegrationLab.Tests.Support;

/// <summary>
/// One SQL Server and one RabbitMQ for the whole sequential test collection, with a unique
/// database pair and a unique queue/exchange prefix so parallel runs never collide.
/// RabbitMQ credentials (AMQP and management API share them) are generated randomly here.
/// Nothing in this fixture uses the compose services: Testcontainers owns everything.
/// </summary>
public sealed class LabFixture : IAsyncLifetime
{
    private MsSqlContainer _sql = null!;
    private RabbitMqContainer _rabbit = null!;

    public string Suffix { get; } = Guid.NewGuid().ToString("N")[..8];

    public string IntegrationDatabase => $"IntegrationLab_{Suffix}";

    public string FakeErpDatabase => $"FakeErpLab_{Suffix}";

    public string IntegrationConnectionString { get; private set; } = string.Empty;

    public string FakeErpConnectionString { get; private set; } = string.Empty;

    public string RabbitHostName => _rabbit.Hostname;

    // Chosen before the container starts and bound explicitly, so the endpoint survives the
    // stop/start cycles the outage tests perform.
    public int RabbitAmqpPort { get; private set; }

    public int RabbitManagementPort { get; private set; }

    public string RabbitUserName { get; private set; } = string.Empty;

    public string RabbitPassword { get; private set; } = string.Empty;

    public string RabbitNamePrefix => $"lab{Suffix}-";

    public SqlAssertions CreateSql() => new(IntegrationConnectionString, FakeErpConnectionString);

    public string SqlHostName => _sql.Hostname;

    public int SqlPort => _sql.GetMappedPublicPort(1433);

    /// <summary>
    /// The integration connection string routed through another loopback port, so a test can put
    /// a controllable gate in front of SQL Server (see <see cref="LoopbackTcpGate"/>) instead of
    /// stopping the container every other test in the collection is using.
    ///
    /// The connect timeout is shortened: an outage test wants a prompt connection failure, not a
    /// 15-second stall on every retry.
    /// </summary>
    public string IntegrationConnectionStringVia(int port) =>
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(IntegrationConnectionString)
        {
            DataSource = $"127.0.0.1,{port}",
            ConnectTimeout = 3,
            ConnectRetryCount = 0,
        }.ConnectionString;

    /// <summary>Stops/starts the broker container for real outage scenarios. Ports are re-read after restart.</summary>
    public async Task StopRabbitAsync() => await _rabbit.StopAsync();

    public async Task StartRabbitAsync() => await _rabbit.StartAsync();

    /// <summary>
    /// Freezes the broker process without closing the TCP stack: frames are accepted by the
    /// kernel but never confirmed. The real confirm-timeout path (not a connection failure).
    /// </summary>
    /// <summary>
    /// Freezes the broker through the cgroup freezer (docker pause), which is what makes this a
    /// confirm-timeout test rather than a connection-failure test.
    ///
    /// Signalling from inside the container does not work: the Erlang VM IS pid 1 of the
    /// container's pid namespace, and the kernel discards SIGSTOP sent to a namespace's init
    /// from within that namespace - the broker keeps answering and no confirm is ever lost.
    /// </summary>
    public Task PauseRabbitProcessAsync() => _rabbit.PauseAsync();

    public Task ResumeRabbitProcessAsync() => _rabbit.UnpauseAsync();

    public async ValueTask InitializeAsync()
    {
        var images = LabImages.Load();
        var sqlPassword = GeneratePassword(24);

        _sql = new MsSqlBuilder(images.SqlPinned)
            .WithPassword(sqlPassword)
            .Build();
        await _sql.StartAsync();

        var baseConnectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
            $"Server={_sql.Hostname},{_sql.GetMappedPublicPort(1433)};User Id=sa;Password={sqlPassword};TrustServerCertificate=True;Encrypt=True")
        {
            ConnectTimeout = 15,
        };

        IntegrationConnectionString = WithDatabase(baseConnectionString, IntegrationDatabase);
        FakeErpConnectionString = WithDatabase(baseConnectionString, FakeErpDatabase);

        await CreateDatabaseAsync(baseConnectionString, IntegrationDatabase);
        await CreateDatabaseAsync(baseConnectionString, FakeErpDatabase);

        await MigrateIntegrationAsync();
        await MigrateFakeErpAsync();

        RabbitUserName = $"lab{Suffix}";
        RabbitPassword = GeneratePassword(20);

        // Explicit host ports, not random ones: the outage tests stop and start this container,
        // and Testcontainers hands a restarted container a NEW random mapping - which the
        // already-running worker, configured once at startup, could never reconnect to.
        RabbitAmqpPort = LabTestConfig.GetFreeLoopbackPort();
        RabbitManagementPort = LabTestConfig.GetFreeLoopbackPort();

        _rabbit = new RabbitMqBuilder(images.RabbitPinned)
            .WithUsername(RabbitUserName)
            .WithPassword(RabbitPassword)
            .WithPortBinding(RabbitAmqpPort, 5672)
            .WithPortBinding(RabbitManagementPort, 15672)
            .Build();
        await _rabbit.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore();

        GC.SuppressFinalize(this);
    }

    private async Task DisposeAsyncCore()
    {
        if (_rabbit is not null)
        {
            await _rabbit.DisposeAsync();
        }

        if (_sql is not null)
        {
            await _sql.DisposeAsync();
        }
    }

    /// <summary>Creates an empty, unmigrated database for the "normal startup never migrates" proof.</summary>
    public async Task CreateEmptyDatabaseAsync(string name)
    {
        var baseConnectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(IntegrationConnectionString)
        {
            InitialCatalog = "master",
        };
        await CreateDatabaseAsync(baseConnectionString, name);
    }

    private async Task MigrateIntegrationAsync()
    {
        var options = new DbContextOptionsBuilder<LabDbContext>()
            .UseSqlServer(IntegrationConnectionString, sql => sql.MigrationsAssembly(typeof(LabDbContext).Assembly.FullName))
            .Options;
        await using var context = new LabDbContext(options);
        await context.Database.MigrateAsync();
    }

    private async Task MigrateFakeErpAsync()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(FakeErpConnectionString, sql => sql.MigrationsAssembly(typeof(ErpDbContext).Assembly.FullName))
            .Options;
        await using var context = new ErpDbContext(options);
        await context.Database.MigrateAsync();
    }

    private static async Task CreateDatabaseAsync(Microsoft.Data.SqlClient.SqlConnectionStringBuilder masterBuilder, string name)
    {
        masterBuilder.InitialCatalog = "master";
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(masterBuilder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}]";
        await command.ExecuteNonQueryAsync();
    }

    private static string WithDatabase(Microsoft.Data.SqlClient.SqlConnectionStringBuilder builder, string name)
    {
        var copy = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(builder.ConnectionString)
        {
            InitialCatalog = name,
        };
        return copy.ConnectionString;
    }

    private static string GeneratePassword(int length)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!#$%*+-=";

        var all = upper + lower + digits + symbols;
        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = all[bytes[i] % all.Length];
        }

        // Guarantee all four character classes for the SQL Server password policy.
        chars[0] = upper[bytes[0] % upper.Length];
        chars[1] = lower[bytes[1] % lower.Length];
        chars[2] = digits[bytes[2] % digits.Length];
        chars[3] = symbols[bytes[3] % symbols.Length];
        return new string(chars);
    }
}

/// <summary>
/// The entire lab test suite runs sequentially inside one collection: every test spawns
/// real hosts against the same broker, and cross-collection parallelism would steal each
/// other's deliveries.
/// </summary>
[CollectionDefinition("lab")]
public sealed class LabCollection : ICollectionFixture<LabFixture>;
