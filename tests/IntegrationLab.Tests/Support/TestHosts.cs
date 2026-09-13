using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FakeErp;
using Integration.Api;
using Integration.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ApiProgram = Integration.Api.Program;
using FakeErpProgram = FakeErp.Program;
using WorkerProgram = Integration.Worker.Program;

namespace IntegrationLab.Tests.Support;

/// <summary>
/// One configuration mechanism for every host shape: command-line arguments that both the
/// in-process hosts (Program.BuildApp/BuildHost) and the child processes (ProcessHost)
/// accept verbatim. Verified: hierarchical keys with ':' work through the command-line
/// provider, including connection strings with semicolons (quoted) and hook names with dashes.
/// </summary>
public static class LabTestConfig
{
    public static string[] ApiArgs(LabFixture fixture, int port, params string[] extra) =>
    [
        "--environment=Testing",
        $"--urls=http://127.0.0.1:{port}",
        $"--ConnectionStrings:IntegrationLab={fixture.IntegrationConnectionString}",
        // The API never connects to the broker; it only needs the topology naming.
        $"--Lab:Rabbit:NamePrefix={fixture.RabbitNamePrefix}",
        .. extra,
    ];

    public static string[] WorkerArgs(LabFixture fixture, Uri? erpBaseAddress, params string[] extra)
    {
        var args = new List<string>
        {
            "--environment=Testing",
            $"--ConnectionStrings:IntegrationLab={fixture.IntegrationConnectionString}",
            $"--Lab:Rabbit:HostName={fixture.RabbitHostName}",
            $"--Lab:Rabbit:Port={fixture.RabbitAmqpPort}",
            $"--Lab:Rabbit:UserName={fixture.RabbitUserName}",
            $"--Lab:Rabbit:Password={fixture.RabbitPassword}",
            $"--Lab:Rabbit:NamePrefix={fixture.RabbitNamePrefix}",
        };
        if (erpBaseAddress is not null)
        {
            args.Add($"--Lab:ErpBaseAddress={erpBaseAddress}");
        }

        args.AddRange(extra);
        return [.. args];
    }

    public static string[] FakeErpArgs(LabFixture fixture, int port, params string[] extra) =>
    [
        "--environment=Testing",
        $"--urls=http://127.0.0.1:{port}",
        $"--ConnectionStrings:FakeErpLab={fixture.FakeErpConnectionString}",
        .. extra,
    ];

    public static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Configuration array elements bind through indexed keys: key:0, key:1, ...</summary>
    public static IEnumerable<string> Indexed(string key, params int[] values) =>
        values.Select((value, index) => $"--{key}:{index}={value}");

    public static string ExportRequestJson(Guid requestId, string externalReference = "PO-100", decimal amount = 160.00m, string currency = "TRY") =>
        $$"""{"requestId":"{{requestId:D}}","externalReference":"{{externalReference}}","amount":{{amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}},"currency":"{{currency}}"}""";
}

/// <summary>The API as a real Kestrel host in-process, on a dedicated loopback port.</summary>
public sealed class TestApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestApiHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public Uri BaseAddress => Client.BaseAddress!;

    public static async Task<TestApiHost> StartAsync(LabFixture fixture, params string[] extraArgs)
    {
        var app = ApiProgram.BuildApp(LabTestConfig.ApiArgs(fixture, LabTestConfig.GetFreeLoopbackPort(), extraArgs));
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new TestApiHost(app, client);
    }

    public Task<HttpResponseMessage> SubmitAsync(string json, string? accept = null) =>
        SendSubmitAsync(json, accept);

    public async Task<HttpResponseMessage> SubmitExportAsync(
        Guid requestId,
        string externalReference = "PO-100",
        decimal amount = 160.00m,
        string currency = "TRY",
        string? accept = null) =>
        await SendSubmitAsync(LabTestConfig.ExportRequestJson(requestId, externalReference, amount, currency), accept);

    private async Task<HttpResponseMessage> SendSubmitAsync(string json, string? accept)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/exports") { Content = content };
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        return await Client.SendAsync(request);
    }

    public async Task<JsonElement?> GetStatusAsync(Guid requestId)
    {
        using var response = await Client.GetAsync($"/api/exports/{requestId:D}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// The worker's three hosted loops as a real host in-process. Crash faults are forbidden here
/// (they would kill the test runner): use ProcessHost for those.
///
/// ALWAYS hold this in an <c>await using</c>. Every test in the lab collection shares one
/// database and one broker, so a worker that outlives its test does not merely leak a process:
/// it keeps publishing other tests' outbox rows and claiming their jobs with ITS configuration.
/// A single leaked host — from one assertion failing before an explicit StopAsync — corrupts
/// every test that follows it, and the symptoms appear nowhere near the cause.
/// </summary>
public sealed class TestWorkerHost : IAsyncDisposable
{
    private readonly IHost _host;

    private TestWorkerHost(IHost host)
    {
        _host = host;
    }

    public static async Task<TestWorkerHost> StartAsync(
        LabFixture fixture,
        Uri? erpBaseAddress = null,
        Action<IServiceCollection>? configureServices = null,
        params string[] extraArgs)
    {
        var host = WorkerProgram.BuildHost(
            LabTestConfig.WorkerArgs(fixture, erpBaseAddress, extraArgs),
            configureServices);
        await host.StartAsync();
        return new TestWorkerHost(host);
    }

    public async Task StopAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(15));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                _host.Dispose();
            }
        }
        catch (ObjectDisposedException)
        {
            // Already disposed via StopAsync in some test flows.
        }
    }
}

/// <summary>FakeErp as a real Kestrel host in-process, plus its scenario control helpers.</summary>
public sealed class TestFakeErpHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestFakeErpHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public Uri BaseAddress => Client.BaseAddress!;

    public static async Task<TestFakeErpHost> StartAsync(LabFixture fixture, params string[] extraArgs)
    {
        var app = FakeErpProgram.BuildApp(LabTestConfig.FakeErpArgs(fixture, LabTestConfig.GetFreeLoopbackPort(), extraArgs));
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new TestFakeErpHost(app, client);
    }

    public async Task SetScenarioAsync(Guid requestId, string type, int n = 0, int delayMs = 8000)
    {
        using var response = await Client.PostAsJsonAsync(
            "/erp/scenarios",
            new { requestId = requestId.ToString("D"), type, n, delayMs });
        response.EnsureSuccessStatusCode();
    }

    public async Task ResetScenariosAsync()
    {
        using var response = await Client.DeleteAsync("/erp/scenarios");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Direct external apply, as a client of the external system would do.</summary>
    public async Task<HttpResponseMessage> ApplyDirectAsync(Guid requestId, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/erp/exports") { Content = content };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", requestId.ToString("D"));
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
