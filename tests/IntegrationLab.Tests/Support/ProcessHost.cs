using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace IntegrationLab.Tests.Support;

/// <summary>
/// Real process lifecycle for crash/restart proofs: a child `dotnet &lt;app&gt;.dll` with the
/// same command-line configuration as the in-process hosts, captured stdout/stderr, a
/// readiness barrier (HTTP poll or a log marker) and hard-kill support.
///
/// Only processes this class started are ever stopped - never containers or hosts a test
/// did not create. Output is captured to a bounded buffer; no credentials are echoed
/// (configuration travels as arguments, and arguments are never printed).
/// </summary>
public sealed partial class ProcessHost : IAsyncDisposable
{
    [GeneratedRegex("integration-worker-host-ready")]
    private static partial Regex WorkerReadyMarker();

    private readonly Process _process;
    private readonly string _name;
    private readonly ConcurrentQueue<string> _output = new();
    private readonly Task _readStdout;
    private readonly Task _readStderr;

    private ProcessHost(Process process, string name)
    {
        _process = process;
        _name = name;
        _readStdout = Task.Run(() => CopyOutput(process.StandardOutput, _output));
        _readStderr = Task.Run(() => CopyOutput(process.StandardError, _output));
    }

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.HasExited ? _process.ExitCode : -1;

    public string RecentOutput(int maxLines = 40)
    {
        var lines = _output.ToArray();
        return string.Join(Environment.NewLine, lines[^Math.Min(maxLines, lines.Length)..]);
    }

    /// <summary>Waits until an HTTP endpoint answers; the readiness barrier for web apps.</summary>
    public async Task WaitForHttpAsync(Uri url, TimeSpan? timeout = null)
    {
        using var client = new HttpClient();
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (HasExited)
            {
                throw new Xunit.Sdk.XunitException($"{_name} exited early with code {ExitCode}.{Environment.NewLine}{RecentOutput()}");
            }

            try
            {
                using var response = await client.GetAsync(url);
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not up yet.
            }

            await Task.Delay(250);
        }

        throw new Xunit.Sdk.XunitException($"{_name} did not become ready at {url}.{Environment.NewLine}{RecentOutput()}");
    }

    /// <summary>Waits until the worker's ready marker appears on stdout.</summary>
    public Task WaitForWorkerReadyAsync(TimeSpan? timeout = null) =>
        WaitForOutputAsync(WorkerReadyMarker(), timeout ?? TimeSpan.FromSeconds(60));

    public async Task WaitForOutputAsync(Regex pattern, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var line = _output.ToArray().FirstOrDefault(pattern.IsMatch);
            if (line is not null)
            {
                return;
            }

            if (HasExited)
            {
                throw new Xunit.Sdk.XunitException($"{_name} exited early with code {ExitCode}.{Environment.NewLine}{RecentOutput()}");
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException($"{_name} never logged the expected marker.{Environment.NewLine}{RecentOutput()}");
    }

    public async Task WaitForExitAsync(TimeSpan timeout)
    {
        if (!_process.HasExited)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                await _process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"{_name} did not exit within {timeout}.");
            }
        }
    }

    public void KillHard()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public static ProcessHost Start(string projectRelativePath, string assemblyName, params string[] arguments)
    {
        var dll = ProjectBinary.Locate(projectRelativePath, assemblyName);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(dll);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {assemblyName}.");
        return new ProcessHost(process, assemblyName);
    }

    public async ValueTask DisposeAsync()
    {
        KillHard();
        if (!_process.HasExited)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // Already killed with the entire tree; nothing more to do.
            }
        }

        _process.Dispose();
    }

    private static void CopyOutput(StreamReader reader, ConcurrentQueue<string> output)
    {
        try
        {
            while (reader.ReadLine() is { } line)
            {
                output.Enqueue(line);
                while (output.Count > 500)
                {
                    output.TryDequeue(out _);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Process disposed; reader closed.
        }
    }

    /// <summary>Locates a project's built DLL by walking up from the test bin dir to the repo root.</summary>
    public static class ProjectBinary
    {
        public static string Locate(string projectRelativePath, string assemblyName)
        {
            var repositoryRoot = FindRepositoryRoot()
                ?? throw new InvalidOperationException(
                    "Could not find the repository root (dotnet-reliable-integration-lab.slnx). Run tests from the repo.");

            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(
                    repositoryRoot,
                    projectRelativePath,
                    "bin",
                    configuration,
                    "net10.0",
                    $"{assemblyName}.dll");
                if (File.Exists(candidate) && File.Exists(Path.ChangeExtension(candidate, ".runtimeconfig.json")))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                $"No built output found for {assemblyName}. Run `dotnet build` before the tests.");
        }

        private static string? FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null
                && !File.Exists(Path.Combine(directory.FullName, "dotnet-reliable-integration-lab.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName;
        }
    }
}
