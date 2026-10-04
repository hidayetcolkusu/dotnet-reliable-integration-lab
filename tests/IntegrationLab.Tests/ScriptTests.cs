using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntegrationLab.Tests.Support;
using Xunit;

namespace IntegrationLab.Tests;

/// <summary>
/// Task 11 acceptance for the setup surface: the scripts parse, their .env parsing and
/// connection-string building survive the characters a real password contains, a failing
/// native command is never mistaken for success, and the documented commands point at files
/// that exist.
///
/// These tests do not need SQL Server or RabbitMQ, so this class stays out of the "lab"
/// collection. They deliberately do not RUN init-lab or run-scenario end to end: those drive
/// `docker compose` and a developer's local .env, and executing them here would either
/// disturb a running lab or prove nothing.
/// </summary>
public sealed partial class ScriptTests
{
    private static readonly string[] ScriptFiles =
    [
        "LabCommon.ps1", "init-lab.ps1", "run-scenario.ps1", "verify-clean.ps1",
    ];

    private static readonly string[] DocumentedScenarios =
    [
        "broker-down", "duplicate-delivery", "erp-timeout", "poison-message", "worker-restart",
    ];

    [GeneratedRegex(@"ValidateSet\(([^)]*)\)", RegexOptions.Singleline)]
    private static partial Regex ValidateSetPattern();

    private static string RepositoryRoot => Path.GetDirectoryName(
        LabImages.FindRepoFile("dotnet-reliable-integration-lab.slnx"))!;

    private static string ScriptPath(string name) => Path.Combine(RepositoryRoot, "scripts", name);

    /// <summary>
    /// Runs a PowerShell snippet and returns stdout. Skips the test when PowerShell 7 is not
    /// installed: claiming the scripts are verified on a machine that cannot run them would be
    /// worse than saying nothing.
    /// </summary>
    private static string RunPowerShell(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = RepositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("PowerShell 7 (pwsh) is not on PATH, so the script behaviour cannot be verified here.");
            return string.Empty;
        }

        Assert.NotNull(process);
        using (process)
        {
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit(120_000);

            Assert.True(
                process.ExitCode == 0,
                $"pwsh exited with {process.ExitCode}.{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
            return standardOutput;
        }
    }

    // ------------------------------------------------------------------ the files themselves

    [Fact]
    public void EveryDocumentedScriptExists()
    {
        foreach (var name in ScriptFiles)
        {
            Assert.True(File.Exists(ScriptPath(name)), $"scripts/{name} is missing.");
        }
    }

    [Fact]
    public void EveryScriptParsesAndRequiresPowerShell7()
    {
        var output = RunPowerShell(
            """
            $ErrorActionPreference = 'Stop'
            foreach ($file in Get-ChildItem -Path scripts -Filter *.ps1 | Sort-Object Name) {
                $errors = $null
                [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$null, [ref]$errors) | Out-Null
                Write-Output "$($file.Name)=$($errors.Count)"
            }
            """);
        if (output.Length == 0)
        {
            return; // skipped
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('=');
            Assert.Equal("0", parts[1]);
        }

        // The scripts use PowerShell 7 syntax and say so, rather than failing obscurely on 5.1.
        foreach (var name in ScriptFiles)
        {
            Assert.Contains("#Requires -Version 7.0", File.ReadAllText(ScriptPath(name)), StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ .env parsing

    [Fact]
    public void TheEnvParserPreservesEveryCharacterAPasswordMayContain()
    {
        // A password with '=', ';', '#', '$', a double quote and a single quote: each one
        // breaks a different naive parser, and the last three also break naive interpolation.
        const string password = "a=b;c#d$e\"f'g";

        // Written from here rather than embedded in the PowerShell snippet, so the test proves
        // the PARSER handles these characters instead of proving that quoting a literal works.
        var envFile = Path.Combine(Path.GetTempPath(), $"lab-env-{Guid.NewGuid():N}.env");
        File.WriteAllLines(
            envFile,
            [
                "# a comment line",
                string.Empty,
                $"LAB_SQL_PASSWORD={password}",
                "LAB_SQL_PORT=11433",
                "  LAB_RABBIT_USER = spaced  ",
                "LAB_QUOTED=\"quoted value\"",
                "NOT_A_PAIR",
            ]);

        try
        {
            var output = RunPowerShell(
                $$"""
                $ErrorActionPreference = 'Stop'
                . ./scripts/LabCommon.ps1
                $settings = Read-LabEnvFile -Path '{{envFile.Replace("'", "''")}}'
                Write-Output "password=$($settings['LAB_SQL_PASSWORD'])"
                Write-Output "port=$($settings['LAB_SQL_PORT'])"
                Write-Output "user=$($settings['LAB_RABBIT_USER'])"
                Write-Output "quoted=$($settings['LAB_QUOTED'])"
                Write-Output "count=$($settings.Count)"
                """);
            if (output.Length == 0)
            {
                return; // skipped
            }

            var values = ParseKeyValues(output);
            Assert.Equal(password, values["password"]);
            Assert.Equal("11433", values["port"]);
            Assert.Equal("spaced", values["user"]);

            // Wrapping quotes are stripped; the inner text is not touched.
            Assert.Equal("quoted value", values["quoted"]);

            // The comment, the blank line and the line without '=' are all ignored.
            Assert.Equal("4", values["count"]);
        }
        finally
        {
            File.Delete(envFile);
        }
    }

    [Fact]
    public void AMissingOrEmptySettingIsReportedInsteadOfSilentlyDefaulted()
    {
        var output = RunPowerShell(
            """
            $ErrorActionPreference = 'Stop'
            . ./scripts/LabCommon.ps1
            try { Get-LabRequiredSetting -Settings @{} -Name 'LAB_SQL_PASSWORD'; Write-Output 'missing=nothrow' }
            catch { Write-Output 'missing=threw' }
            try { Get-LabRequiredSetting -Settings @{ 'LAB_SQL_PASSWORD' = '   ' } -Name 'LAB_SQL_PASSWORD'; Write-Output 'blank=nothrow' }
            catch { Write-Output 'blank=threw' }
            try { Read-LabEnvFile -Path './does-not-exist.env'; Write-Output 'nofile=nothrow' }
            catch { Write-Output 'nofile=threw' }
            """);
        if (output.Length == 0)
        {
            return; // skipped
        }

        var values = ParseKeyValues(output);
        Assert.Equal("threw", values["missing"]);
        Assert.Equal("threw", values["blank"]);
        Assert.Equal("threw", values["nofile"]);
    }

    // ------------------------------------------------------------------ connection strings

    [Theory]
    [InlineData("pa;ss\"word=1")]
    [InlineData("semi;colon")]
    [InlineData("equals=sign")]
    [InlineData("quote\"inside")]
    [InlineData("apostrophe'inside")]
    [InlineData("plain-password-123")]
    public void TheConnectionStringBuilderQuotesEveryValueAParserMustReadBack(string password)
    {
        // The script builds the string with no .NET data provider (that cannot be loaded
        // reliably inside PowerShell), so the round trip is checked HERE, against the real
        // parser the applications use.
        var output = RunPowerShell(
            $$"""
            $ErrorActionPreference = 'Stop'
            . ./scripts/LabCommon.ps1
            $password = [System.Text.Encoding]::UTF8.GetString(
                [System.Convert]::FromBase64String('{{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password))}}'))
            Write-Output (New-LabSqlConnectionString -ServerHost '127.0.0.1' -Port 11433 `
                -Password $password -Database 'IntegrationLab')
            """);
        if (output.Length == 0)
        {
            return; // skipped
        }

        var connectionString = output.Trim();
        var parsed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);

        Assert.Equal(password, parsed.Password);
        Assert.Equal("IntegrationLab", parsed.InitialCatalog);
        Assert.Equal("127.0.0.1,11433", parsed.DataSource);
        Assert.Equal("sa", parsed.UserID);
        Assert.True(parsed.TrustServerCertificate);

        // A value that needed quoting must not have leaked an extra keyword into the string.
        Assert.Equal(7, parsed.Count);
    }

    // ------------------------------------------------------------------ exit codes

    [Fact]
    public void ANonZeroNativeExitCodeStopsTheScript()
    {
        var output = RunPowerShell(
            """
            $ErrorActionPreference = 'Stop'
            . ./scripts/LabCommon.ps1
            try {
                Invoke-LabNative -FilePath 'dotnet' -Arguments @('--this-switch-does-not-exist') -Quiet | Out-Null
                Write-Output 'failure=nothrow'
            }
            catch { Write-Output 'failure=threw' }

            # An expected failure is declared, never hidden.
            $result = Invoke-LabNative -FilePath 'dotnet' -Arguments @('--this-switch-does-not-exist') -Quiet -AllowExitCodes @(0, 1)
            Write-Output "allowed=$($result.ExitCode -ne 0)"

            $ok = Invoke-LabNative -FilePath 'dotnet' -Arguments @('--version') -Quiet
            Write-Output "success=$($ok.ExitCode)"
            """);
        if (output.Length == 0)
        {
            return; // skipped
        }

        var values = ParseKeyValues(output);
        Assert.Equal("threw", values["failure"]);
        Assert.Equal("True", values["allowed"]);
        Assert.Equal("0", values["success"]);
    }

    /// <summary>
    /// Regression guard: the helper must return ONE object, not an array. A bare native call
    /// leaves the command's own output on the pipeline, and the caller's $result.ExitCode then
    /// fails under Set-StrictMode - after the work already succeeded, which is the worst place
    /// for a reporting bug to live.
    /// </summary>
    [Fact]
    public void InvokeLabNativeReturnsASingleResultObjectEvenWhenTheCommandPrints()
    {
        var output = RunPowerShell(
            """
            $ErrorActionPreference = 'Stop'
            . ./scripts/LabCommon.ps1
            $result = Invoke-LabNative -FilePath 'dotnet' -Arguments @('--version')
            Write-Output "count=$(@($result).Count)"
            Write-Output "exit=$($result.ExitCode)"
            """);
        if (output.Length == 0)
        {
            return; // skipped
        }

        var values = ParseKeyValues(output);
        Assert.Equal("1", values["count"]);
        Assert.Equal("0", values["exit"]);
    }

    // ------------------------------------------------------------------ scenarios

    [Fact]
    public void RunScenarioOffersExactlyTheDocumentedScenarios()
    {
        var script = File.ReadAllText(ScriptPath("run-scenario.ps1"));
        var match = ValidateSetPattern().Match(script);
        Assert.True(match.Success, "run-scenario.ps1 does not constrain -Scenario with a ValidateSet.");

        var offered = match.Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim('\'', '"', ' ', '\r', '\n'))
            .ToList();

        Assert.Equal(DocumentedScenarios.Order(), offered.Order());

        // Each scenario is also handled and documented, so -Scenario cannot offer a no-op.
        foreach (var scenario in DocumentedScenarios)
        {
            Assert.Contains($"'{scenario}' {{", script, StringComparison.Ordinal);
            Assert.True(
                File.Exists(Path.Combine(RepositoryRoot, "docs", "scenarios", $"{scenario}.md")),
                $"docs/scenarios/{scenario}.md is missing for the '{scenario}' scenario.");
        }
    }

    [Fact]
    public void NoScenarioDeletesRowsFromTheDevelopmentDatabase()
    {
        var script = File.ReadAllText(ScriptPath("run-scenario.ps1"));

        // A demo owns its databases outright (see the isolation tests below), so it has no
        // reason to reach into rows or tables at all - its cleanup drops whole run-scoped
        // databases through the name-guarded helper in LabCommon.ps1, never SQL written here.
        Assert.DoesNotContain("DELETE FROM", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP DATABASE", script, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ demo isolation
    //
    // G6: a prefix on the broker topology is NOT isolation. The scenarios also claim outbox
    // rows and jobs, and those claims take every due row in the database they are pointed at.
    // These tests pin the properties that make a demo safe to run next to your own apps.

    [Fact]
    public void AScenarioRunNeverPointsItsApplicationsAtTheDevelopmentDatabases()
    {
        var script = File.ReadAllText(ScriptPath("run-scenario.ps1"));

        // The connection strings a run hands its child processes are built from the RUN-SCOPED
        // names. If they were built from the shared ones, the demo's OutboxStore/JobStore
        // claims would pick up work belonging to the developer's own running worker.
        Assert.Contains(
            "-Database $integrationDatabase",
            script,
            StringComparison.Ordinal);
        Assert.Contains("-Database $fakeErpDatabase", script, StringComparison.Ordinal);
        Assert.Matches(
            @"\$integrationDatabase\s*=\s*""\$\(\$script:LabIntegrationDatabase\)_scn\$runId""",
            script);
        Assert.Matches(
            @"\$fakeErpDatabase\s*=\s*""\$\(\$script:LabFakeErpDatabase\)_scn\$runId""",
            script);

        // And no query is ever aimed at a shared database by name.
        Assert.DoesNotContain("-Database 'IntegrationLab'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Database 'FakeErpLab'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AScenarioRunNeverStopsTheSharedComposeBroker()
    {
        var script = File.ReadAllText(ScriptPath("run-scenario.ps1"));

        // `docker compose stop rabbit` takes the broker away from every other process on the
        // machine. The broker-down demo must stop a container it started itself.
        Assert.DoesNotContain("compose', 'stop'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("compose stop", script, StringComparison.Ordinal);
        Assert.DoesNotContain("compose start", script, StringComparison.Ordinal);
        Assert.Contains("Start-LabScenarioBroker", script, StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-LabScenarioBrokerCommand -Verb 'stop' -ContainerName $brokerContainer",
            script,
            StringComparison.Ordinal);
        Assert.Matches(@"\$brokerContainer\s*=\s*""lab-scn\$runId-rabbit""", script);
    }

    [Theory]
    // The developer's own resources: every one of these must be refused.
    [InlineData("Remove-LabScenarioDatabase", "IntegrationLab")]
    [InlineData("Remove-LabScenarioDatabase", "FakeErpLab")]
    [InlineData("Remove-LabScenarioDatabase", "master")]
    [InlineData("Invoke-LabScenarioBrokerCommand", "lab-rabbit")]
    [InlineData("Invoke-LabScenarioBrokerCommand", "lab-sql")]
    public void TheCleanupHelpersRefuseAnythingThisRunDidNotCreate(string helper, string name)
    {
        var argument = helper == "Invoke-LabScenarioBrokerCommand"
            ? $"-Verb 'rm' -ContainerName '{name}'"
            : $"-Name '{name}'";

        var output = RunPowerShell($$"""
            . ./scripts/LabCommon.ps1
            try {
                {{helper}} {{argument}}
                'NOT-REFUSED'
            }
            catch {
                'REFUSED'
            }
            """);

        // The guard is a name check that runs BEFORE any docker or sqlcmd call, so this test
        // needs neither container - which is also why a failure here cannot damage anything.
        Assert.Equal("REFUSED", output.Trim());
    }

    [Fact]
    public void TheCleanupHelpersAcceptARunScopedName()
    {
        // The mirror image of the test above: the guard must not be so strict that the demo
        // cannot clean up after itself, which would leave databases behind on every run.
        var output = RunPowerShell("""
            . ./scripts/LabCommon.ps1
            if ('IntegrationLab_scn0a1b2c3d' -match $script:LabScenarioDatabasePattern) { 'DB-OK' }
            if ('FakeErpLab_scn0a1b2c3d' -match $script:LabScenarioDatabasePattern) { 'ERP-OK' }
            if ('lab-scn0a1b2c3d-rabbit' -match $script:LabScenarioContainerPattern) { 'BROKER-OK' }
            """);

        Assert.Contains("DB-OK", output, StringComparison.Ordinal);
        Assert.Contains("ERP-OK", output, StringComparison.Ordinal);
        Assert.Contains("BROKER-OK", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScenarioBrokerUsesThePinnedImageRatherThanAFloatingTag()
    {
        var common = File.ReadAllText(ScriptPath("LabCommon.ps1"));

        // A demo that pulls `rabbitmq:latest` proves something about a different broker than
        // the one compose and the test fixture use.
        Assert.Contains("Get-LabPinnedImage -Name 'rabbitMq'", common, StringComparison.Ordinal);
        Assert.Contains("config/lab-images.json", common, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ pinned images

    [Fact]
    public void ComposeAndLabImagesAgreeOnEveryPinnedDigest()
    {
        var imagesPath = Path.Combine(RepositoryRoot, "config", "lab-images.json");
        var composePath = Path.Combine(RepositoryRoot, "compose.yaml");

        using var document = JsonDocument.Parse(File.ReadAllText(imagesPath));
        var compose = File.ReadAllText(composePath);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('$'))
            {
                continue;
            }

            var pinned = property.Value.GetProperty("pinned").GetString();
            Assert.False(string.IsNullOrWhiteSpace(pinned), $"{property.Name} has no pinned image.");
            Assert.Contains("@sha256:", pinned!, StringComparison.Ordinal);

            // One source of truth: compose must name the very same digest the tests use.
            Assert.Contains(pinned!, compose, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ComposePinsEveryImageByDigestAndBindsPortsToLoopback()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot, "compose.yaml"));

        foreach (var line in compose.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("image:", StringComparison.Ordinal))
            {
                Assert.Contains("@sha256:", line, StringComparison.Ordinal);
            }

            // A published port without an explicit address would listen on every interface.
            if (line.StartsWith("- \"", StringComparison.Ordinal) && line.Contains(':') && line.Contains("${", StringComparison.Ordinal))
            {
                Assert.StartsWith("- \"127.0.0.1:", line, StringComparison.Ordinal);
            }
        }
    }

    // ------------------------------------------------------------------ documented commands

    [Fact]
    public void EveryProjectAndScriptTheReadmeTellsYouToRunExists()
    {
        var readmePath = Path.Combine(RepositoryRoot, "README.md");
        Assert.True(File.Exists(readmePath), "README.md is missing.");
        var readme = File.ReadAllText(readmePath);

        foreach (var match in ProjectPathPattern().Matches(readme).Cast<Match>())
        {
            var relative = match.Value.Replace('/', Path.DirectorySeparatorChar);
            Assert.True(
                File.Exists(Path.Combine(RepositoryRoot, relative)),
                $"README references '{match.Value}', which does not exist.");
        }

        foreach (var match in ScriptPathPattern().Matches(readme).Cast<Match>())
        {
            var relative = match.Value.Replace('/', Path.DirectorySeparatorChar);
            Assert.True(
                File.Exists(Path.Combine(RepositoryRoot, relative)),
                $"README references '{match.Value}', which does not exist.");
        }
    }

    /// <summary>
    /// `dotnet run` applies Properties/launchSettings.json. A profile's applicationUrl would
    /// override each app's DefaultLoopbackUrl - the API would leave the README's 5099 and
    /// FakeErp the worker's ErpBaseAddress - and an app without a profile starts as
    /// Production, which the environment guard refuses. Found by a fresh-clone run of the
    /// README setup, where nothing else sets the environment.
    /// </summary>
    [Theory]
    [InlineData("src/Integration.Api", "ASPNETCORE_ENVIRONMENT")]
    [InlineData("src/Integration.Worker", "DOTNET_ENVIRONMENT")]
    [InlineData("samples/FakeErp", "ASPNETCORE_ENVIRONMENT")]
    public void DotnetRunStartsEachAppInDevelopmentOnItsDocumentedUrl(string project, string environmentVariable)
    {
        var path = Path.Combine(RepositoryRoot, project.Replace('/', Path.DirectorySeparatorChar), "Properties", "launchSettings.json");
        Assert.True(File.Exists(path), $"{project} has no launchSettings.json, so `dotnet run` starts it as Production.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var profile in document.RootElement.GetProperty("profiles").EnumerateObject())
        {
            Assert.False(
                profile.Value.TryGetProperty("applicationUrl", out _),
                $"{project} profile '{profile.Name}' sets applicationUrl, which overrides the app's DefaultLoopbackUrl.");
            Assert.Equal(
                "Development",
                profile.Value.GetProperty("environmentVariables").GetProperty(environmentVariable).GetString());
        }
    }

    [Fact]
    public void TheWorkerCallsFakeErpWhereFakeErpListensByDefault()
    {
        Assert.Equal(FakeErp.Program.DefaultLoopbackUrl, new Integration.Shared.Runtime.LabOptions().ErpBaseAddress);

        using var settings = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "Integration.Worker", "appsettings.json")));
        Assert.Equal(
            FakeErp.Program.DefaultLoopbackUrl,
            settings.RootElement.GetProperty("Lab").GetProperty("ErpBaseAddress").GetString());
    }

    [GeneratedRegex(@"(?:src|samples|tests)/[A-Za-z.]+/[A-Za-z.]+\.csproj")]
    private static partial Regex ProjectPathPattern();

    [GeneratedRegex(@"scripts/[A-Za-z-]+\.ps1")]
    private static partial Regex ScriptPathPattern();

    private static Dictionary<string, string> ParseKeyValues(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                values[trimmed[..separator]] = trimmed[(separator + 1)..];
            }
        }

        return values;
    }
}
