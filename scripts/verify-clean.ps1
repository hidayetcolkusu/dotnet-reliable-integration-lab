#Requires -Version 7.0
<#
.SYNOPSIS
    The acceptance chain a clean checkout must pass: exact tool restore, locked package
    restore, Release build, the whole test suite and a formatting check.

.DESCRIPTION
    Every step's exit code is checked and the script stops at the first failure. The test
    step additionally refuses to pass when a filter matched zero tests - "no tests ran" is
    not a green build.

    The tests own their SQL Server and RabbitMQ through Testcontainers, so Docker must be
    running, but `docker compose up` is NOT required for this script.

.PARAMETER Filter
    Optional test filter, passed to `dotnet test --filter`. A filter that selects no tests
    fails the run.

.PARAMETER SkipTests
    Runs restore, build and format only. Use when Docker is unavailable - the output then
    says so explicitly instead of implying the suite passed.

.EXAMPLE
    pwsh -File scripts/verify-clean.ps1

.EXAMPLE
    pwsh -File scripts/verify-clean.ps1 -Filter 'FullyQualifiedName~OutboxTests'
#>
[CmdletBinding()]
param(
    [string] $Filter,
    [switch] $SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'LabCommon.ps1')

$root = Get-LabRoot
$resultsDirectory = Join-Path $root 'TestResults'
$summary = [System.Collections.Generic.List[string]]::new()

Write-LabStep 'dotnet --version'
$sdk = Invoke-LabNative -FilePath 'dotnet' -Arguments @('--version') -WorkingDirectory $root -Quiet
$sdkVersion = ($sdk.Output | Select-Object -Last 1).ToString().Trim()
Write-Host "    SDK $sdkVersion"
$summary.Add("SDK: $sdkVersion")

Write-LabStep 'dotnet tool restore'
Invoke-LabNative -FilePath 'dotnet' -Arguments @('tool', 'restore') -WorkingDirectory $root | Out-Null
$summary.Add('tool restore: ok')

Write-LabStep 'dotnet restore --locked-mode'
Invoke-LabNative -FilePath 'dotnet' -Arguments @('restore', '--locked-mode') -WorkingDirectory $root | Out-Null
$summary.Add('locked restore: ok')

Write-LabStep 'dotnet build -c Release'
Invoke-LabNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
    'build', '-c', 'Release', '--no-restore', '--nologo'
) | Out-Null
$summary.Add('Release build: ok')

if ($SkipTests) {
    Write-LabStep 'Skipping tests (requested). This run does NOT prove the suite passes.'
    $summary.Add('tests: SKIPPED (not verified)')
}
else {
    Write-LabStep 'dotnet test -c Release'

    $testArguments = @(
        'test', '-c', 'Release', '--no-build', '--nologo',
        '--logger', 'trx',
        '--results-directory', $resultsDirectory
    )
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $testArguments += @('--filter', $Filter)
    }

    # A failing suite must be reported, not swallowed, so the exit code is inspected below
    # rather than thrown on immediately - the TRX still gets parsed for the counts.
    $test = Invoke-LabNative -FilePath 'dotnet' -Arguments $testArguments -WorkingDirectory $root -AllowExitCodes @(0, 1)

    $trx = Get-ChildItem -LiteralPath $resultsDirectory -Filter '*.trx' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $trx) {
        throw 'No TRX file was produced; the test run cannot be verified.'
    }

    [xml]$trxXml = Get-Content -LiteralPath $trx.FullName
    $counters = $trxXml.TestRun.ResultSummary.Counters
    $total = [int]$counters.total
    $passed = [int]$counters.passed
    $failed = [int]$counters.failed

    Write-Host "    total=$total passed=$passed failed=$failed"
    Write-Host "    TRX: $($trx.FullName)"

    # "Zero tests ran" is a broken filter, not a pass.
    if ($total -eq 0) {
        throw 'The test run executed 0 tests. A filter that matches nothing is a failure, not a pass.'
    }
    if ($failed -gt 0 -or $test.ExitCode -ne 0) {
        $summary.Add("tests: FAILED (total=$total passed=$passed failed=$failed)")
        Write-Host ''
        $summary | ForEach-Object { Write-Host "  $_" }
        throw "$failed test(s) failed. See $($trx.FullName)."
    }

    $summary.Add("tests: ok (total=$total passed=$passed)")
}

Write-LabStep 'dotnet format --verify-no-changes'
Invoke-LabNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
    'format', '--verify-no-changes', '--no-restore', '--verbosity', 'minimal'
) | Out-Null
$summary.Add('format: ok')

Write-Host ''
Write-LabStep 'Verification summary'
$summary | ForEach-Object { Write-Host "    $_" }
