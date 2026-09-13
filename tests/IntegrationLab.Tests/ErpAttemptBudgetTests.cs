using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Integration.Shared.Contracts;
using Integration.Shared.Diagnostics;
using Integration.Worker.Processing;
using IntegrationLab.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using WorkerProgram = Integration.Worker.Program;

namespace IntegrationLab.Tests;

/// <summary>
/// G2: one external attempt gets ONE budget, and it covers the whole attempt.
///
/// <c>HttpClient.Timeout</c> cannot express that. Under
/// <c>HttpCompletionOption.ResponseHeadersRead</c> its timer stops the moment the response
/// headers arrive, so an external system that flushes "200 OK" and then stalls its body holds
/// the worker's single job loop open for as long as it likes - past the configured timeout,
/// past the retry schedule, with nothing in the job row to show why.
///
/// The server below is a real loopback socket rather than a mock, because the defect lives in
/// the space between "headers received" and "body complete" - which only a real connection has.
/// No SQL and no broker, so this stays out of the "lab" collection.
/// </summary>
public sealed class ErpAttemptBudgetTests
{
    private const string UnusedConnectionString =
        "Server=127.0.0.1,11433;Database=NotUsed;User Id=sa;Password=Unused_Placeholder#2026;TrustServerCertificate=True";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Generous: the assertion is "the budget bounds the attempt", not "the clock is exact".
    /// It still fails loudly for the actual defect, where the attempt runs for the server's
    /// stall duration (8s+) instead of the budget.
    /// </summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(3);

    private static readonly ExportRequest Request = new(Guid.NewGuid(), "PO-100", 160.00m, "TRY");

    /// <summary>
    /// A raw HTTP/1.1 server on loopback. Each behaviour writes the status line and headers,
    /// flushes them, and only then decides what to do about the body.
    /// </summary>
    private sealed class StubErpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _accepting;

        private StubErpServer(TcpListener listener, Func<NetworkStream, CancellationToken, Task> respond)
        {
            _listener = listener;
            _accepting = Task.Run(() => AcceptAsync(respond, _stopping.Token));
        }

        public Uri BaseAddress => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        public static StubErpServer Start(Func<NetworkStream, CancellationToken, Task> respond)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new StubErpServer(listener, respond);
        }

        private async Task AcceptAsync(Func<NetworkStream, CancellationToken, Task> respond, CancellationToken stopping)
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(stopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await using var stream = client.GetStream();
                            await DrainRequestAsync(stream, stopping);
                            await respond(stream, stopping);
                        }
                        catch (Exception)
                        {
                            // The client walking away mid-response is the point of these tests.
                        }
                    }
                }, CancellationToken.None);
            }
        }

        /// <summary>Reads the request head and its body, so the client's send always completes.</summary>
        private static async Task DrainRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var received = new StringBuilder();
            var contentLength = 0;
            var headerEnd = -1;
            var total = 0;

            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return;
                }

                total += read;
                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                headerEnd = received.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }

            foreach (var line in received.ToString()[..headerEnd].Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            var bodyAlreadyRead = total - (headerEnd + 4);
            while (bodyAlreadyRead < contentLength)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return;
                }

                bodyAlreadyRead += read;
            }
        }

        public static async Task WriteHeadersAsync(NetworkStream stream, int contentLength, CancellationToken cancellationToken)
        {
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {contentLength}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(head, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _accepting;
            }
            catch (Exception)
            {
                // Shutdown races are not interesting here.
            }

            _stopping.Dispose();
        }
    }

    /// <summary>
    /// Resolves the SHIPPED client - the composition root's HttpClient configuration included.
    /// Constructing an HttpClient here instead would let the production registration regress
    /// (for example back to a headers-only HttpClient.Timeout) without a single test noticing.
    /// </summary>
    private static (IHostDisposable Host, ErpClient Client) CreateClient(Uri erpBaseAddress)
    {
        var host = WorkerProgram.BuildHost(
        [
            "--environment=Testing",
            $"--ConnectionStrings:IntegrationLab={UnusedConnectionString}",
            $"--Lab:ErpBaseAddress={erpBaseAddress}",
            $"--Lab:HttpTimeoutSeconds={(int)Budget.TotalSeconds}",
        ]);

        return (new IHostDisposable(host), host.Services.GetRequiredService<ErpClient>());
    }

    private sealed class IHostDisposable(Microsoft.Extensions.Hosting.IHost host) : IDisposable
    {
        public void Dispose() => host.Dispose();
    }

    private static string ReceiptBody(string receiptId = "ERP-0001") =>
        $$"""{"receiptId":"{{receiptId}}"}""";

    // -------------------------------------------------------------------- the G2 defect

    [Fact]
    public async Task HeadersThatArriveBeforeAStalledBodyDoNotEscapeTheAttemptBudget()
    {
        var body = ReceiptBody();
        await using var server = StubErpServer.Start(async (stream, cancellationToken) =>
        {
            // A complete, successful response head - and then nothing. This is the exact shape
            // that used to run until the server gave up rather than until the budget expired.
            await StubErpServer.WriteHeadersAsync(stream, body.Length, cancellationToken);
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
        });

        var (host, client) = CreateClient(server.BaseAddress);
        using (host)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await client.ApplyAsync(Request, TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.False(result.Succeeded);
            Assert.True(result.Retryable);
            Assert.Equal(SafeErrors.HttpTimeout, result.ErrorCode);
            Assert.Null(result.ReceiptId);
            Assert.True(
                stopwatch.Elapsed < Budget + Tolerance,
                $"The attempt took {stopwatch.Elapsed} with a {Budget} budget: the body read is outside the deadline.");
        }
    }

    [Fact]
    public async Task ABodyDrippedOneByteAtATimeCannotOutlastTheBudgetEither()
    {
        var body = Encoding.UTF8.GetBytes(ReceiptBody());
        await using var server = StubErpServer.Start(async (stream, cancellationToken) =>
        {
            await StubErpServer.WriteHeadersAsync(stream, body.Length, cancellationToken);
            foreach (var singleByte in body)
            {
                // Each individual read succeeds, so a per-read timeout would never fire. Only a
                // budget for the whole attempt bounds this.
                await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
                await stream.WriteAsync(new[] { singleByte }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        });

        var (host, client) = CreateClient(server.BaseAddress);
        using (host)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await client.ApplyAsync(Request, TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.Equal(SafeErrors.HttpTimeout, result.ErrorCode);
            Assert.True(result.Retryable);
            Assert.True(
                stopwatch.Elapsed < Budget + Tolerance,
                $"The attempt took {stopwatch.Elapsed} with a {Budget} budget.");
        }
    }

    // ------------------------------------------------- the behaviour that must not regress

    [Fact]
    public async Task APromptResponseStillSucceeds()
    {
        var body = ReceiptBody("ERP-OK-1");
        await using var server = StubErpServer.Start(async (stream, cancellationToken) =>
        {
            await StubErpServer.WriteHeadersAsync(stream, body.Length, cancellationToken);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        });

        var (host, client) = CreateClient(server.BaseAddress);
        using (host)
        {
            var result = await client.ApplyAsync(Request, TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, result.ErrorCode);
            Assert.Equal("ERP-OK-1", result.ReceiptId);
        }
    }

    [Fact]
    public async Task AResponseLargerThanTheCapIsStillRefusedRatherThanBuffered()
    {
        // Bounded reading must survive the deadline change: a 16 KiB+ body is transient, not a
        // receipt, and certainly not something to hold in memory.
        var body = "{\"receiptId\":\"" + new string('x', 32 * 1024) + "\"}";
        await using var server = StubErpServer.Start(async (stream, cancellationToken) =>
        {
            await StubErpServer.WriteHeadersAsync(stream, body.Length, cancellationToken);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        });

        var (host, client) = CreateClient(server.BaseAddress);
        using (host)
        {
            var result = await client.ApplyAsync(Request, TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.True(result.Retryable);
            Assert.Equal(SafeErrors.HttpInvalidResponse, result.ErrorCode);
        }
    }

    [Fact]
    public async Task AConnectionDroppedMidFlightIsClassifiedAsConnectionNotTimeout()
    {
        // The server takes the request and then hangs up without answering. The distinction
        // matters operationally: "the connection died" and "we ran out of budget" are different
        // incidents, and the job's LastErrorCode is where an operator reads which one happened.
        await using var server = StubErpServer.Start((stream, _) =>
        {
            stream.Socket.LingerState = new LingerOption(true, 0);
            stream.Socket.Close();
            return Task.CompletedTask;
        });

        var (host, client) = CreateClient(server.BaseAddress);
        using (host)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await client.ApplyAsync(Request, TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.False(result.Succeeded);
            Assert.True(result.Retryable);
            Assert.Equal(SafeErrors.HttpConnection, result.ErrorCode);

            // And it is reported immediately rather than after the budget expires.
            Assert.True(stopwatch.Elapsed < Budget, $"A dropped connection took {stopwatch.Elapsed}.");
        }
    }

    [Fact]
    public async Task AHostShutdownIsRethrownInsteadOfBeingRecordedAsAFailedAttempt()
    {
        var body = ReceiptBody();
        await using var server = StubErpServer.Start(async (stream, cancellationToken) =>
        {
            await StubErpServer.WriteHeadersAsync(stream, body.Length, cancellationToken);
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
        });

        var (host, client) = CreateClient(server.BaseAddress);
        using (host)
        {
            // The caller's own cancellation is NOT a transient external failure: recording it as
            // one would spend an attempt from the budget every time the worker shuts down.
            using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await client.ApplyAsync(Request, shutdown.Token));
        }
    }
}
