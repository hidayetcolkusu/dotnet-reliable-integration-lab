#Requires -Version 7.0
<#
.SYNOPSIS
    Shared helpers for the lab scripts: .env parsing, safe connection-string building,
    native exit-code checking and process lifetime.

.NOTES
    PowerShell 7 only. These scripts have been run on Windows 11 with PowerShell 7.5;
    they use no Windows-only API, but no other platform has been verified, so no
    cross-platform claim is made in the README.
#>

Set-StrictMode -Version Latest

# The lab's own databases. Nothing here ever touches another database name.
$script:LabIntegrationDatabase = 'IntegrationLab'
$script:LabFakeErpDatabase = 'FakeErpLab'

# Run-scoped resources a demo creates and owns. Every destructive helper below checks a name
# against these patterns first, so a demo can only ever remove something it made itself.
$script:LabScenarioDatabasePattern = '^(IntegrationLab|FakeErpLab)_scn[0-9a-f]{8}$'
$script:LabScenarioContainerPattern = '^lab-scn[0-9a-f]{8}-rabbit$'
$script:LabQueryableDatabasePattern = '^(IntegrationLab|FakeErpLab)(_scn[0-9a-f]{8})?$'

function Get-LabRoot {
    <#  The repository root, located by the solution file rather than by $PWD. #>
    $directory = Get-Item -LiteralPath $PSScriptRoot
    while ($null -ne $directory) {
        if (Test-Path -LiteralPath (Join-Path $directory.FullName 'dotnet-reliable-integration-lab.slnx')) {
            return $directory.FullName
        }
        $directory = $directory.Parent
    }
    throw 'Could not locate the repository root (dotnet-reliable-integration-lab.slnx).'
}

function Read-LabEnvFile {
    <#
    .SYNOPSIS
        Parses a .env file into a hashtable.
    .DESCRIPTION
        Splits on the FIRST '=' only, so a password containing '=' survives. Quotes are
        stripped only when they wrap the whole value. Values are never expanded, so '$'
        and ';' in a password stay literal.
    #>
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Environment file '$Path' was not found. Copy .env.example to .env and set local passwords."
    }

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) { continue }

        $separator = $trimmed.IndexOf('=')
        if ($separator -lt 1) { continue }

        $name = $trimmed.Substring(0, $separator).Trim()
        $value = $trimmed.Substring($separator + 1).Trim()

        if ($value.Length -ge 2 -and
            (($value.StartsWith('"') -and $value.EndsWith('"')) -or
             ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        $values[$name] = $value
    }

    return $values
}

function Get-LabRequiredSetting {
    param(
        [Parameter(Mandatory)] [hashtable] $Settings,
        [Parameter(Mandatory)] [string] $Name
    )

    if (-not $Settings.ContainsKey($Name) -or [string]::IsNullOrWhiteSpace($Settings[$Name])) {
        throw "Required setting '$Name' is missing or empty in the environment file."
    }

    return $Settings[$Name]
}

function ConvertTo-LabConnectionStringValue {
    <#
    .SYNOPSIS
        Quotes one connection-string value.
    .DESCRIPTION
        SQL Server connection strings are key=value pairs separated by ';'. A value containing
        ';', '=', a quote or leading/trailing spaces must be wrapped in double quotes, and an
        embedded double quote is escaped by doubling it. Doing this explicitly keeps the scripts
        free of any .NET data provider, which cannot be loaded reliably inside PowerShell.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Value)

    # ';' '=' and '"' are the separators/quote of the format itself; a single quote is included
    # because it appears in real passwords and must survive unchanged.
    $needsQuoting = $Value.Contains(';') -or $Value.Contains('=') -or $Value.Contains('"') -or
        $Value.Contains("'") -or $Value -ne $Value.Trim()
    if ($needsQuoting) {
        return '"' + $Value.Replace('"', '""') + '"'
    }

    return $Value
}

function New-LabSqlConnectionString {
    <#
    .SYNOPSIS
        Builds a SQL Server connection string with every value correctly quoted.
    .DESCRIPTION
        Plain concatenation would break on a password containing ';' or '"' and could be used to
        inject extra keywords. The quoting rule is applied per value instead.
    #>
    param(
        [Parameter(Mandatory)] [string] $ServerHost,
        [Parameter(Mandatory)] [int] $Port,
        [Parameter(Mandatory)] [string] $Password,
        [Parameter(Mandatory)] [string] $Database,
        [string] $UserId = 'sa'
    )

    $pairs = [ordered]@{
        'Data Source'            = "$ServerHost,$Port"
        'Initial Catalog'        = $Database
        'User ID'                = $UserId
        'Password'               = $Password
        'Encrypt'                = 'True'
        'TrustServerCertificate' = 'True'
        'Connect Timeout'        = '15'
    }

    return (($pairs.GetEnumerator() | ForEach-Object {
        "$($_.Key)=$(ConvertTo-LabConnectionStringValue -Value $_.Value)"
    }) -join ';')
}

function Invoke-LabNative {
    <#
    .SYNOPSIS
        Runs a native command and throws unless its exit code is allowed.
    .DESCRIPTION
        PowerShell does not fail on a non-zero native exit code, so every call in these
        scripts goes through here. -AllowExitCodes documents a deliberately expected
        failure instead of hiding it.
    #>
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [string] $WorkingDirectory,
        [int[]] $AllowExitCodes = @(0),
        [switch] $Quiet
    )

    $location = if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) { (Get-Location).Path } else { $WorkingDirectory }

    Push-Location -LiteralPath $location
    try {
        if ($Quiet) {
            $output = & $FilePath @Arguments 2>&1
        }
        else {
            # Out-Host, not a bare call: a bare call leaves the command's own output on the
            # pipeline, so this function would return an ARRAY and $result.ExitCode would fail.
            & $FilePath @Arguments | Out-Host
            $output = $null
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($AllowExitCodes -notcontains $exitCode) {
        if ($null -ne $output) { $output | ForEach-Object { Write-Host $_ } }

        # The arguments are NOT echoed: connection strings travel this way, and an error
        # message is exactly where a password must not appear. Only the first argument is
        # shown, which is enough to identify the command.
        $identity = if ($Arguments.Count -gt 0) { "$FilePath $($Arguments[0])" } else { $FilePath }
        throw "'$identity ...' failed with exit code $exitCode."
    }

    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Invoke-LabSqlScalar {
    <#
    .SYNOPSIS
        Runs a single-value query against a lab database through sqlcmd inside the SQL container.
    .DESCRIPTION
        Deliberately does NOT use Microsoft.Data.SqlClient from PowerShell: Add-Type cannot
        resolve the driver's native SNI component or its dependency closure, and the failure
        surfaces late and obscurely ("the type initializer for TdsParser threw an exception").
        The container already ships sqlcmd - the compose health check uses it - so the query
        runs where the driver is known to work.

        The password is read from the container's own environment, so it never appears in a
        command line, a process list or an error message.
    #>
    param(
        [Parameter(Mandatory)] [string] $Database,
        [Parameter(Mandatory)] [string] $Query,
        [string] $Container = 'lab-sql'
    )

    # The name is inlined into the sqlcmd command line, so it is constrained to the shared lab
    # databases and the run-scoped copies a demo creates - never an arbitrary caller value.
    if ($Database -notmatch $script:LabQueryableDatabasePattern) {
        throw "Refusing to query '$Database': not a lab database name."
    }

    if ($Query -match '"') {
        throw 'Queries passed to Invoke-LabSqlScalar must not contain double quotes.'
    }

    $command = "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P `"`$MSSQL_SA_PASSWORD`" -C -d $Database -h -1 -W -Q `"$Query`""
    $result = Invoke-LabNative -FilePath 'docker' -Arguments @('exec', $Container, 'sh', '-c', $command) -Quiet

    $lines = @($result.Output | ForEach-Object { "$_".Trim() } | Where-Object { $_.Length -gt 0 -and $_ -notmatch '^\(\d+ rows affected\)$' })
    if ($lines.Count -eq 0) { return $null }
    if ($lines[0] -eq 'NULL') { return $null }
    return $lines[0]
}

function Assert-LabGuid {
    <#  Guards every identifier that is inlined into a query. #>
    param([Parameter(Mandatory)] [string] $Value)

    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($Value, [ref]$parsed)) {
        throw "Expected a GUID but got '$Value'."
    }

    return $parsed.ToString('D')
}

function Get-LabFreePort {
    <#  A free loopback port, so a scenario run never collides with the user's own apps. #>
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return [int]$listener.LocalEndpoint.Port
    }
    finally {
        $listener.Stop()
    }
}

function Wait-LabHttp {
    <#  Readiness barrier for a web app: poll until it answers anything but a connect error. #>
    param(
        [Parameter(Mandatory)] [string] $Url,
        [int] $TimeoutSeconds = 60
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-WebRequest -Uri $Url -TimeoutSec 5 -SkipHttpErrorCheck | Out-Null
            return $true
        }
        catch {
            Start-Sleep -Milliseconds 400
        }
    }

    return $false
}

function Start-LabProcess {
    <#
    .SYNOPSIS
        Starts one of the lab's apps as a background child process.
    .DESCRIPTION
        No new console window is opened and stdout/stderr are redirected to files the
        caller owns, so a scenario run leaves an inspectable log instead of a popup.
        Only processes started here are ever stopped by Stop-LabProcess.
    #>
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [string] $LogPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.WorkingDirectory = $Root
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    foreach ($argument in @('run', '--project', $Project, '--no-build', '--')) {
        $startInfo.ArgumentList.Add($argument)
    }
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start $Project."
    }

    New-Item -ItemType File -Path $LogPath -Force | Out-Null
    $stdout = $process.StandardOutput.BaseStream
    $stderr = $process.StandardError.BaseStream

    # Drain both pipes on background threads: a full pipe buffer would block the child.
    $reader = [pscustomobject]@{
        Process = $process
        LogPath = $LogPath
        Jobs    = @(
            (Start-ThreadJob -ScriptBlock {
                param($stream, $path)
                $sr = [System.IO.StreamReader]::new($stream)
                while ($null -ne ($line = $sr.ReadLine())) { Add-Content -LiteralPath $path -Value $line }
            } -ArgumentList $stdout, $LogPath),
            (Start-ThreadJob -ScriptBlock {
                param($stream, $path)
                $sr = [System.IO.StreamReader]::new($stream)
                while ($null -ne ($line = $sr.ReadLine())) { Add-Content -LiteralPath $path -Value $line }
            } -ArgumentList $stderr, $LogPath)
        )
    }

    return $reader
}

function Stop-LabProcess {
    <#  Stops only a process this script started, and never a container or a user's app. #>
    param([Parameter(Mandatory)] [AllowNull()] $Handle)

    if ($null -eq $Handle) { return }

    try {
        if (-not $Handle.Process.HasExited) {
            $Handle.Process.Kill($true)
            $Handle.Process.WaitForExit(10000) | Out-Null
        }
    }
    catch {
        Write-Verbose "Ignoring error while stopping a lab process: $($_.Exception.GetType().Name)"
    }
    finally {
        foreach ($job in $Handle.Jobs) {
            Stop-Job -Job $job -ErrorAction SilentlyContinue
            Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        }
        $Handle.Process.Dispose()
    }
}

function Wait-LabLogMarker {
    param(
        [Parameter(Mandatory)] $Handle,
        [Parameter(Mandatory)] [string] $Marker,
        [int] $TimeoutSeconds = 90
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Handle.LogPath) {
            if (Select-String -LiteralPath $Handle.LogPath -SimpleMatch -Pattern $Marker -Quiet) {
                return $true
            }
        }
        if ($Handle.Process.HasExited) {
            throw "The process exited with code $($Handle.Process.ExitCode) before logging '$Marker'. See $($Handle.LogPath)."
        }
        Start-Sleep -Milliseconds 400
    }

    return $false
}

function Wait-LabCondition {
    <#  Polls a scriptblock until it returns $true, or fails with the caller's diagnostics. #>
    param(
        [Parameter(Mandatory)] [scriptblock] $Condition,
        [Parameter(Mandatory)] [string] $Description,
        [int] $TimeoutSeconds = 90,
        [scriptblock] $Diagnostics
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        if (& $Condition) { return }

        if ((Get-Date) -ge $deadline) {
            $state = if ($Diagnostics) { & $Diagnostics } else { 'no diagnostics provided' }
            throw "Timed out after ${TimeoutSeconds}s waiting for: $Description. State: $state"
        }

        Start-Sleep -Milliseconds 500
    }
}

# --------------------------------------------------------------- run-scoped demo resources
#
# A demo must be able to stop a broker and fill a database without touching anything the
# developer is running. Everything below therefore creates resources named after the run and
# refuses, by name, to remove anything else.

function Get-LabPinnedImage {
    <#  One pinned image reference from config/lab-images.json, the single source of truth. #>
    param(
        [Parameter(Mandatory)] [ValidateSet('sqlServer', 'rabbitMq', 'traceViewer')] [string] $Name,
        [string] $Root
    )

    if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Get-LabRoot }
    $path = Join-Path $Root 'config/lab-images.json'
    $images = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    if (-not $images.ContainsKey($Name)) {
        throw "config/lab-images.json has no '$Name' entry."
    }

    return $images[$Name]['pinned']
}

function New-LabScenarioDatabase {
    <#  Creates one run-scoped database. The name must match the run-scoped pattern. #>
    param(
        [Parameter(Mandatory)] [string] $Name,
        [string] $Container = 'lab-sql'
    )

    if ($Name -notmatch $script:LabScenarioDatabasePattern) {
        throw "Refusing to create '$Name': not a run-scoped scenario database name."
    }

    Invoke-LabSqlScalar -Database 'IntegrationLab' -Container $Container `
        -Query "IF DB_ID(N'$Name') IS NULL CREATE DATABASE [$Name]" | Out-Null
}

function Remove-LabScenarioDatabase {
    <#
    .SYNOPSIS
        Drops one run-scoped database created by this run.
    .DESCRIPTION
        The name guard is the whole point: the pattern only matches 'IntegrationLab_scnXXXXXXXX'
        and 'FakeErpLab_scnXXXXXXXX', so this can never be pointed at the developer's
        IntegrationLab or FakeErpLab. Failures are reported and swallowed - cleanup must not
        mask the scenario's own result.
    #>
    param(
        [Parameter(Mandatory)] [string] $Name,
        [string] $Container = 'lab-sql'
    )

    if ($Name -notmatch $script:LabScenarioDatabasePattern) {
        throw "Refusing to remove '$Name': not a run-scoped scenario database name."
    }

    try {
        # SINGLE_USER WITH ROLLBACK IMMEDIATE: a child process that has not fully exited would
        # otherwise keep a connection and block the drop.
        Invoke-LabSqlScalar -Database 'IntegrationLab' -Container $Container -Query (
            "IF DB_ID(N'$Name') IS NOT NULL BEGIN " +
            "ALTER DATABASE [$Name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            "DROP DATABASE [$Name]; END") | Out-Null
    }
    catch {
        Write-Warning "Could not remove the scenario database '$Name': $($_.Exception.Message)"
    }
}

function Start-LabScenarioBroker {
    <#
    .SYNOPSIS
        Starts a RabbitMQ container dedicated to one demo run.
    .DESCRIPTION
        A demo that stops the SHARED compose broker stops it for every other process on the
        machine, which is the opposite of an isolated demonstration. This starts the same
        pinned image under a run-scoped container name, on free loopback ports, with no
        volume - so stopping it, starting it and finally removing it affects nothing else.
    #>
    param(
        [Parameter(Mandatory)] [string] $ContainerName,
        [Parameter(Mandatory)] [int] $AmqpPort,
        [Parameter(Mandatory)] [int] $ManagementPort,
        [Parameter(Mandatory)] [string] $UserName,
        [Parameter(Mandatory)] [AllowEmptyString()] [string] $Secret,
        [string] $Root
    )

    if ($ContainerName -notmatch $script:LabScenarioContainerPattern) {
        throw "Refusing to start '$ContainerName': not a run-scoped scenario container name."
    }

    if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Get-LabRoot }
    $image = Get-LabPinnedImage -Name 'rabbitMq' -Root $Root
    $configPath = (Resolve-Path (Join-Path $Root 'config/rabbitmq.conf')).Path

    # Credentials travel as environment variables of the container, not on the command line of
    # anything this script prints, and Invoke-LabNative never echoes arguments.
    Invoke-LabNative -FilePath 'docker' -WorkingDirectory $Root -Quiet -Arguments @(
        'run', '--detach',
        '--name', $ContainerName,
        '--env', "RABBITMQ_DEFAULT_USER=$UserName",
        '--env', "RABBITMQ_DEFAULT_PASS=$Secret",
        '--publish', "127.0.0.1:${AmqpPort}:5672",
        '--publish', "127.0.0.1:${ManagementPort}:15672",
        '--volume', "${configPath}:/etc/rabbitmq/conf.d/20-lab.conf:ro",
        $image
    ) | Out-Null
}

function Invoke-LabScenarioBrokerCommand {
    <#  Runs `docker <verb> <container>` for a run-scoped broker only. #>
    param(
        [Parameter(Mandatory)] [ValidateSet('stop', 'start', 'rm')] [string] $Verb,
        [Parameter(Mandatory)] [string] $ContainerName,
        [switch] $IgnoreFailure
    )

    if ($ContainerName -notmatch $script:LabScenarioContainerPattern) {
        throw "Refusing to '$Verb' '$ContainerName': not a run-scoped scenario container name."
    }

    $arguments = if ($Verb -eq 'rm') { @('rm', '--force', $ContainerName) } else { @($Verb, $ContainerName) }

    if ($IgnoreFailure) {
        try {
            Invoke-LabNative -FilePath 'docker' -Arguments $arguments -Quiet | Out-Null
        }
        catch {
            Write-Warning "Could not '$Verb' the scenario broker: $($_.Exception.Message)"
        }

        return
    }

    Invoke-LabNative -FilePath 'docker' -Arguments $arguments -Quiet | Out-Null
}

function Wait-LabBrokerReady {
    <#  Polls a broker's management API until it answers as the given user. #>
    param(
        [Parameter(Mandatory)] [string] $ManagementBase,
        [Parameter(Mandatory)] [hashtable] $Headers,
        [int] $TimeoutSeconds = 120
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Method Get -Headers $Headers -Uri "$ManagementBase/overview" `
                -TimeoutSec 5 -SkipHttpErrorCheck
            if ([int]$response.StatusCode -eq 200) { return $true }
        }
        catch {
            # Not listening yet.
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Write-LabStep {
    param([Parameter(Mandatory)] [string] $Message)
    Write-Host "==> $Message"
}

function Write-LabEvidence {
    param(
        [Parameter(Mandatory)] [string] $RequestId,
        [Parameter(Mandatory)] [string] $Message
    )
    Write-Host "    [requestId=$RequestId] $Message"
}
