#Requires -Version 7.0
<#
.SYNOPSIS
    Creates and migrates the two lab databases, then stores the connection strings as
    per-project user-secrets.

.DESCRIPTION
    Reads .env (never committed), quotes each connection-string value so a password
    containing ';', '=' or a quote cannot break or inject, and runs each application's own
    `--initialize-db` entry point - the same migration code the tests use. Normal startup
    never migrates; this script is the only migration path.

    Verification queries run through sqlcmd inside the SQL container rather than through a
    .NET driver loaded into PowerShell, which cannot resolve its native components reliably.

    Every native command's exit code is checked. A failed migration stops the script; it
    does not leave a half-created database behind and then claim success.

.PARAMETER EnvFile
    Path to the environment file. Defaults to .env in the repository root.

.PARAMETER SkipUserSecrets
    Create and migrate the databases but do not write user-secrets. Used by CI, where
    configuration is passed on the command line instead.

.EXAMPLE
    pwsh -File scripts/init-lab.ps1
#>
[CmdletBinding()]
param(
    [string] $EnvFile,
    [switch] $SkipUserSecrets
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'LabCommon.ps1')

$root = Get-LabRoot
if ([string]::IsNullOrWhiteSpace($EnvFile)) {
    $EnvFile = Join-Path $root '.env'
}

Write-LabStep "Reading environment from $EnvFile"
$settings = Read-LabEnvFile -Path $EnvFile

$sqlPassword = Get-LabRequiredSetting -Settings $settings -Name 'LAB_SQL_PASSWORD'
$sqlPort = [int](Get-LabRequiredSetting -Settings $settings -Name 'LAB_SQL_PORT')
$rabbitUser = Get-LabRequiredSetting -Settings $settings -Name 'LAB_RABBIT_USER'
$rabbitPassword = Get-LabRequiredSetting -Settings $settings -Name 'LAB_RABBIT_PASSWORD'
$rabbitPort = [int](Get-LabRequiredSetting -Settings $settings -Name 'LAB_RABBIT_PORT')

if ($sqlPassword -eq 'ChangeMe_Local#2026' -or $rabbitPassword -eq 'ChangeMe_Local#2026') {
    Write-Warning 'The .env file still contains the example passwords. Change them before using this lab for anything beyond a local experiment.'
}

# Loopback only. This lab never talks to a remote database or broker.
$sqlHost = '127.0.0.1'

$integrationConnectionString = New-LabSqlConnectionString `
    -ServerHost $sqlHost -Port $sqlPort -Password $sqlPassword -Database $script:LabIntegrationDatabase
$fakeErpConnectionString = New-LabSqlConnectionString `
    -ServerHost $sqlHost -Port $sqlPort -Password $sqlPassword -Database $script:LabFakeErpDatabase

Write-LabStep 'Restoring local tools and packages (locked mode)'
Invoke-LabNative -FilePath 'dotnet' -Arguments @('tool', 'restore') -WorkingDirectory $root | Out-Null
Invoke-LabNative -FilePath 'dotnet' -Arguments @('restore', '--locked-mode') -WorkingDirectory $root | Out-Null

Write-LabStep 'Building the solution'
Invoke-LabNative -FilePath 'dotnet' -Arguments @('build', '--no-restore', '-v', 'quiet', '--nologo') -WorkingDirectory $root | Out-Null

# --initialize-db is a first-class entry point of each app, so the script cannot drift
# away from the migration code that actually ships.
Write-LabStep "Creating and migrating $($script:LabIntegrationDatabase)"
Invoke-LabNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
    'run', '--project', 'src/Integration.Worker/Integration.Worker.csproj', '--no-build', '--',
    '--initialize-db',
    '--environment=Development',
    "--ConnectionStrings:IntegrationLab=$integrationConnectionString"
) | Out-Null

Write-LabStep "Creating and migrating $($script:LabFakeErpDatabase)"
Invoke-LabNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
    'run', '--project', 'samples/FakeErp/FakeErp.csproj', '--no-build', '--',
    '--initialize-db',
    '--environment=Development',
    "--ConnectionStrings:FakeErpLab=$fakeErpConnectionString"
) | Out-Null

Write-LabStep 'Verifying the schema exists'

$expectedTables = @(
    'ExportRequests', 'OutboxMessages', 'InboxReceipts',
    'IntegrationJobs', 'JobAttempts', 'RejectedMessages'
)
$tableCount = [int](Invoke-LabSqlScalar -Database 'IntegrationLab' -Query (
    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME IN " +
    "('ExportRequests','OutboxMessages','InboxReceipts','IntegrationJobs','JobAttempts','RejectedMessages')"))
if ($tableCount -ne $expectedTables.Count) {
    throw "Expected $($expectedTables.Count) tables in $($script:LabIntegrationDatabase) but found $tableCount. The migration did not complete."
}

$erpTableCount = [int](Invoke-LabSqlScalar -Database 'FakeErpLab' `
    -Query "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'AppliedExports'")
if ($erpTableCount -ne 1) {
    throw "Expected the AppliedExports table in $($script:LabFakeErpDatabase) but found $erpTableCount. The migration did not complete."
}

Write-Host "    $($script:LabIntegrationDatabase): $tableCount tables"
Write-Host "    $($script:LabFakeErpDatabase): $erpTableCount table"

if ($SkipUserSecrets) {
    Write-LabStep 'Skipping user-secrets (requested)'
}
else {
    # Secrets go to the per-user secrets store, never into a file inside the repo.
    Write-LabStep 'Storing connection strings in user-secrets'

    $secretTargets = @(
        @{ Project = 'src/Integration.Api/Integration.Api.csproj'; Key = 'ConnectionStrings:IntegrationLab'; Value = $integrationConnectionString },
        @{ Project = 'src/Integration.Worker/Integration.Worker.csproj'; Key = 'ConnectionStrings:IntegrationLab'; Value = $integrationConnectionString },
        @{ Project = 'samples/FakeErp/FakeErp.csproj'; Key = 'ConnectionStrings:FakeErpLab'; Value = $fakeErpConnectionString }
    )
    foreach ($target in $secretTargets) {
        Invoke-LabNative -FilePath 'dotnet' -WorkingDirectory $root -Quiet -Arguments @(
            'user-secrets', 'set', $target.Key, $target.Value, '--project', $target.Project
        ) | Out-Null
    }

    $brokerSecrets = @(
        @{ Key = 'Lab:Rabbit:HostName'; Value = '127.0.0.1' },
        @{ Key = 'Lab:Rabbit:Port'; Value = "$rabbitPort" },
        @{ Key = 'Lab:Rabbit:UserName'; Value = $rabbitUser },
        @{ Key = 'Lab:Rabbit:Password'; Value = $rabbitPassword }
    )
    foreach ($secret in $brokerSecrets) {
        Invoke-LabNative -FilePath 'dotnet' -WorkingDirectory $root -Quiet -Arguments @(
            'user-secrets', 'set', $secret.Key, $secret.Value,
            '--project', 'src/Integration.Worker/Integration.Worker.csproj'
        ) | Out-Null
    }

    Write-Host '    API, worker and FakeErp can now be started without any command-line configuration.'
}

Write-Host ''
Write-LabStep 'Done. Start the three apps in separate terminals:'
Write-Host '    dotnet run --project samples/FakeErp/FakeErp.csproj'
Write-Host '    dotnet run --project src/Integration.Worker/Integration.Worker.csproj'
Write-Host '    dotnet run --project src/Integration.Api/Integration.Api.csproj'
