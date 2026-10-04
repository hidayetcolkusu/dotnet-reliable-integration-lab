#Requires -Version 7.0
<#
.SYNOPSIS
    Runs one demonstrable failure scenario end to end against resources this run creates for
    itself, and prints the SQL evidence, keyed by requestId.

.DESCRIPTION
    Every resource a run touches is created by that run and removed by it:

      * its OWN pair of databases (IntegrationLab_scnXXXXXXXX / FakeErpLab_scnXXXXXXXX) inside
        the shared SQL container, created and migrated through each application's
        `--initialize-db` entry point;
      * its OWN RabbitMQ container (the pinned image, run-scoped name, free loopback ports,
        no volume), so `broker-down` stops a broker nobody else is using;
      * its OWN FakeErp, worker and API child processes on free loopback ports, configured
        entirely from the command line rather than from user-secrets.

    That isolation is what makes the claim honest: a run never reads, writes, claims or
    publishes anything belonging to the applications you are running yourself, and the
    compose broker and the development databases are never stopped, emptied or modified.
    Cleanup removes ONLY the run-scoped database pair and container; every helper that can
    delete checks the run-scoped name pattern first.

    The shared SQL container (`lab-sql`) is still used - as a server, to host the run's own
    databases. It is never stopped or reconfigured.

    Scenarios:
      broker-down        Work is accepted while RabbitMQ is stopped, and published on return.
      duplicate-delivery The same transport message arrives twice; only one job exists.
      erp-timeout        The external effect commits but the response is lost; one apply.
      poison-message     A malformed delivery is quarantined and dead-lettered, then ACKed.
      worker-restart     A worker is killed mid-flight; a replacement finishes the work once.

    Requires `docker compose up -d` and `pwsh -File scripts/init-lab.ps1` to have been run.

.PARAMETER Scenario
    Which scenario to run.

.PARAMETER EnvFile
    Path to the environment file. Defaults to .env in the repository root.

.PARAMETER KeepLogs
    Keep the child-process logs after a successful run (they are always kept on failure).

.EXAMPLE
    pwsh -File scripts/run-scenario.ps1 -Scenario broker-down
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('broker-down', 'duplicate-delivery', 'erp-timeout', 'poison-message', 'worker-restart')]
    [string] $Scenario,

    [string] $EnvFile,
    [switch] $KeepLogs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'LabCommon.ps1')

$root = Get-LabRoot
if ([string]::IsNullOrWhiteSpace($EnvFile)) { $EnvFile = Join-Path $root '.env' }

$settings = Read-LabEnvFile -Path $EnvFile
$sqlPassword = Get-LabRequiredSetting -Settings $settings -Name 'LAB_SQL_PASSWORD'
$sqlPort = [int](Get-LabRequiredSetting -Settings $settings -Name 'LAB_SQL_PORT')
$rabbitUser = Get-LabRequiredSetting -Settings $settings -Name 'LAB_RABBIT_USER'
$rabbitPassword = Get-LabRequiredSetting -Settings $settings -Name 'LAB_RABBIT_PASSWORD'

# Identity of this run. Every resource it creates carries it, and every destructive helper
# refuses a name that does not.
$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$namePrefix = "scn$runId-"
$integrationDatabase = "$($script:LabIntegrationDatabase)_scn$runId"
$fakeErpDatabase = "$($script:LabFakeErpDatabase)_scn$runId"
$brokerContainer = "lab-scn$runId-rabbit"

# The run's own broker endpoints, never the compose ones.
$rabbitPort = Get-LabFreePort
$rabbitManagementPort = Get-LabFreePort

$integrationConnectionString = New-LabSqlConnectionString `
    -ServerHost '127.0.0.1' -Port $sqlPort -Password $sqlPassword -Database $integrationDatabase
$fakeErpConnectionString = New-LabSqlConnectionString `
    -ServerHost '127.0.0.1' -Port $sqlPort -Password $sqlPassword -Database $fakeErpDatabase

$logDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "lab-scenario-$runId"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

$apiPort = Get-LabFreePort
$erpPort = Get-LabFreePort
$apiBase = "http://127.0.0.1:$apiPort"
$erpBase = "http://127.0.0.1:$erpPort"

$commonWorkerArguments = @(
    '--environment=Development',
    "--ConnectionStrings:IntegrationLab=$integrationConnectionString",
    '--Lab:Rabbit:HostName=127.0.0.1',
    "--Lab:Rabbit:Port=$rabbitPort",
    "--Lab:Rabbit:UserName=$rabbitUser",
    "--Lab:Rabbit:Password=$rabbitPassword",
    "--Lab:Rabbit:NamePrefix=$namePrefix",
    "--Lab:ErpBaseAddress=$erpBase/",
    '--Lab:PollIntervalMilliseconds=200'
)

$apiArguments = @(
    '--environment=Development',
    "--urls=$apiBase",
    "--ConnectionStrings:IntegrationLab=$integrationConnectionString",
    "--Lab:Rabbit:NamePrefix=$namePrefix"
)

$erpArguments = @(
    '--environment=Development',
    "--urls=$erpBase",
    "--ConnectionStrings:FakeErpLab=$fakeErpConnectionString"
)

# --------------------------------------------------------------------------- helpers

$managementCredential = [System.Convert]::ToBase64String(
    [System.Text.Encoding]::UTF8.GetBytes("${rabbitUser}:${rabbitPassword}"))
$managementHeaders = @{ Authorization = "Basic $managementCredential" }
$managementBase = "http://127.0.0.1:$rabbitManagementPort/api"

function Publish-LabRawMessage {
    <#  Publishes a crafted message through the management API - no AMQP client needed. #>
    param(
        [Parameter(Mandatory)] [string] $Payload,
        [Parameter(Mandatory)] [AllowNull()] [string] $MessageId,
        [string] $Type = 'OrderExportRequested'
    )

    $properties = @{ delivery_mode = 2; content_type = 'application/json'; type = $Type }
    if (-not [string]::IsNullOrWhiteSpace($MessageId)) {
        $properties['message_id'] = $MessageId
        $properties['correlation_id'] = $MessageId
    }

    $body = @{
        properties       = $properties
        routing_key      = 'order.export.requested'
        payload          = $Payload
        payload_encoding = 'string'
    } | ConvertTo-Json -Depth 6

    $exchange = [uri]::EscapeDataString("${namePrefix}integration.events")
    $response = Invoke-RestMethod -Method Post -Headers $managementHeaders `
        -Uri "$managementBase/exchanges/%2F/$exchange/publish" `
        -ContentType 'application/json' -Body $body

    if (-not $response.routed) {
        throw 'The broker did not route the crafted message. Is the worker running (it declares the topology)?'
    }
}

function Get-LabQueueDepth {
    <#
    .SYNOPSIS
        Ready/unacknowledged depths of a queue.
    .DESCRIPTION
        The management API omits the message counters until its statistics interval has
        produced them, and under Set-StrictMode reading a missing property is an error rather
        than zero - so each field is read defensively. An absent counter means "not reported
        yet", which for these scenarios is indistinguishable from empty.
    #>
    param([Parameter(Mandatory)] [string] $Queue)

    $escaped = [uri]::EscapeDataString($Queue)

    # $response, never $queue: PowerShell variable names are case-insensitive, and the [string]
    # type constraint on the $Queue parameter SURVIVES for the rest of the function - assigning
    # the REST object to $queue would silently coerce it to its string form, after which every
    # property read returns nothing and the depth reads as 0 forever.
    $response = Invoke-WebRequest -Method Get -Headers $managementHeaders `
        -Uri "$managementBase/queues/%2F/$escaped" -SkipHttpErrorCheck

    if ([int]$response.StatusCode -eq 404) {
        # Not declared yet: the applications own the topology, so an absent queue is empty.
        return [pscustomobject]@{ Ready = 0; Unacked = 0 }
    }
    if ([int]$response.StatusCode -ne 200) {
        throw "The management API answered $([int]$response.StatusCode) for queue '$Queue'."
    }

    # -AsHashtable so a missing counter is a missing KEY rather than a missing property, which
    # Set-StrictMode turns into an error instead of a zero.
    $queueState = $response.Content | ConvertFrom-Json -AsHashtable

    return [pscustomobject]@{
        Ready   = if ($queueState.ContainsKey('messages_ready')) { [int]$queueState['messages_ready'] } else { 0 }
        Unacked = if ($queueState.ContainsKey('messages_unacknowledged')) { [int]$queueState['messages_unacknowledged'] } else { 0 }
    }
}

function Submit-LabExport {
    param(
        [Parameter(Mandatory)] [string] $RequestId,
        [string] $ExternalReference = 'PO-DEMO',
        [decimal] $Amount = 160.00
    )

    $payload = @{
        requestId         = $RequestId
        externalReference = $ExternalReference
        amount            = $Amount
        currency          = 'TRY'
    } | ConvertTo-Json

    $response = Invoke-WebRequest -Method Post -Uri "$apiBase/api/exports" `
        -ContentType 'application/json' -Body $payload -SkipHttpErrorCheck
    if ([int]$response.StatusCode -ne 202) {
        throw "Expected 202 Accepted from the API but got $([int]$response.StatusCode): $($response.Content)"
    }

    return $RequestId
}

function Get-LabOutboxStatus {
    # The identifier is validated as a GUID before it is inlined, so the query text can never
    # carry anything a caller invented.
    param([Parameter(Mandatory)] [string] $RequestId, [ValidateSet('Export', 'DeadLetter')] [string] $Kind = 'Export')
    $id = Assert-LabGuid -Value $RequestId
    return Invoke-LabSqlScalar -Database $integrationDatabase `
        -Query "SELECT Status FROM OutboxMessages WHERE SourceRequestId = '$id' AND Kind = '$Kind'"
}

function Get-LabJobStatus {
    param([Parameter(Mandatory)] [string] $RequestId)
    $id = Assert-LabGuid -Value $RequestId
    return Invoke-LabSqlScalar -Database $integrationDatabase `
        -Query "SELECT Status FROM IntegrationJobs WHERE RequestId = '$id'"
}

function Get-LabJobAttempts {
    param([Parameter(Mandatory)] [string] $RequestId)
    $id = Assert-LabGuid -Value $RequestId
    $value = Invoke-LabSqlScalar -Database $integrationDatabase `
        -Query "SELECT AttemptsStarted FROM IntegrationJobs WHERE RequestId = '$id'"
    if ($null -eq $value) { return 0 }
    return [int]$value
}

function Get-LabReceiptCount {
    param([Parameter(Mandatory)] [string] $RequestId)
    $id = Assert-LabGuid -Value $RequestId
    return [int](Invoke-LabSqlScalar -Database $integrationDatabase `
        -Query "SELECT COUNT(*) FROM InboxReceipts WHERE EventId = '$id'")
}

function Get-LabJobCount {
    param([Parameter(Mandatory)] [string] $RequestId)
    $id = Assert-LabGuid -Value $RequestId
    return [int](Invoke-LabSqlScalar -Database $integrationDatabase `
        -Query "SELECT COUNT(*) FROM IntegrationJobs WHERE RequestId = '$id'")
}

function Get-LabAppliedCount {
    param([Parameter(Mandatory)] [string] $RequestId)
    $id = Assert-LabGuid -Value $RequestId
    return [int](Invoke-LabSqlScalar -Database $fakeErpDatabase `
        -Query "SELECT COUNT(*) FROM AppliedExports WHERE OperationKey = '$id'")
}

function Get-LabRejectedReason {
    param([Parameter(Mandatory)] [string] $BodySha256)
    if ($BodySha256 -notmatch '^[0-9a-f]{64}$') { throw 'Expected a lowercase hex SHA-256.' }
    return Invoke-LabSqlScalar -Database $integrationDatabase `
        -Query "SELECT ReasonCode FROM RejectedMessages WHERE BodySha256 = '$BodySha256'"
}

function Get-LabState {
    param([Parameter(Mandatory)] [string] $RequestId)
    $outbox = Get-LabOutboxStatus -RequestId $RequestId
    $job = Get-LabJobStatus -RequestId $RequestId
    $outboxText = if ($null -eq $outbox) { 'none' } else { $outbox }
    $jobText = if ($null -eq $job) { 'none' } else { $job }
    return "outbox=$outboxText, job=$jobText/attempts=$(Get-LabJobAttempts -RequestId $RequestId), " +
        "receipts=$(Get-LabReceiptCount -RequestId $RequestId), applied=$(Get-LabAppliedCount -RequestId $RequestId)"
}

function New-LabEnvelopeJson {
    param([Parameter(Mandatory)] [string] $RequestId, [decimal] $Amount = 160.00)
    $amountText = $Amount.ToString('0.00', [System.Globalization.CultureInfo]::InvariantCulture)
    $occurredAt = [DateTimeOffset]::UtcNow.ToString('o')
    return '{"eventId":"' + $RequestId + '","type":"OrderExportRequested","schemaVersion":1,"occurredAtUtc":"' +
        $occurredAt + '","data":{"requestId":"' + $RequestId +
        '","externalReference":"PO-DEMO","amount":' + $amountText + ',"currency":"TRY"}}'
}

function Set-LabErpScenario {
    param(
        [Parameter(Mandatory)] [string] $RequestId,
        [Parameter(Mandatory)] [string] $Type,
        [int] $N = 0,
        [int] $DelayMs = 8000
    )

    $body = @{ requestId = $RequestId; type = $Type; n = $N; delayMs = $DelayMs } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Uri "$erpBase/erp/scenarios" -ContentType 'application/json' -Body $body | Out-Null
}

# --------------------------------------------------------------------------- run

$erp = $null
$worker = $null
$api = $null
$brokerStarted = $false
$databasesCreated = $false
$failed = $false

try {
    Write-LabStep "Scenario '$Scenario' (run $runId, topology prefix '$namePrefix')"
    Write-Host "    databases: $integrationDatabase, $fakeErpDatabase"
    Write-Host "    broker container: $brokerContainer (amqp $rabbitPort, management $rabbitManagementPort)"
    Write-Host "    logs: $logDirectory"

    Invoke-LabNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'build', '-v', 'quiet', '--nologo'
    ) | Out-Null

    # ---- the run's own dependencies ---------------------------------------------------
    # Created here, removed in the finally block, and never shared with anything else: this
    # is what lets the demo claim and publish freely without seeing another process's work.

    Write-LabStep 'Creating this run''s own databases'
    New-LabScenarioDatabase -Name $integrationDatabase
    New-LabScenarioDatabase -Name $fakeErpDatabase
    $databasesCreated = $true

    # The same --initialize-db entry point init-lab.ps1 uses, so the demo cannot drift away
    # from the migration code that ships.
    Invoke-LabNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'run', '--project', 'src/Integration.Worker/Integration.Worker.csproj', '--no-build', '--',
        '--initialize-db', '--environment=Development',
        "--ConnectionStrings:IntegrationLab=$integrationConnectionString"
    ) -Quiet | Out-Null
    Invoke-LabNative -FilePath 'dotnet' -WorkingDirectory $root -Arguments @(
        'run', '--project', 'samples/FakeErp/FakeErp.csproj', '--no-build', '--',
        '--initialize-db', '--environment=Development',
        "--ConnectionStrings:FakeErpLab=$fakeErpConnectionString"
    ) -Quiet | Out-Null

    Write-LabStep 'Starting this run''s own RabbitMQ container'
    Start-LabScenarioBroker -ContainerName $brokerContainer -AmqpPort $rabbitPort `
        -ManagementPort $rabbitManagementPort -UserName $rabbitUser -Secret $rabbitPassword -Root $root
    $brokerStarted = $true
    if (-not (Wait-LabBrokerReady -ManagementBase $managementBase -Headers $managementHeaders)) {
        throw "The scenario broker '$brokerContainer' did not become ready on port $rabbitManagementPort."
    }

    Write-LabStep 'Starting FakeErp'
    $erp = Start-LabProcess -Root $root -Project 'samples/FakeErp/FakeErp.csproj' `
        -Arguments $erpArguments -LogPath (Join-Path $logDirectory 'fake-erp.log')
    if (-not (Wait-LabHttp -Url "$erpBase/health/live")) {
        throw "FakeErp did not become ready at $erpBase. See $logDirectory/fake-erp.log"
    }

    Write-LabStep 'Starting the API'
    $api = Start-LabProcess -Root $root -Project 'src/Integration.Api/Integration.Api.csproj' `
        -Arguments $apiArguments -LogPath (Join-Path $logDirectory 'api.log')
    if (-not (Wait-LabHttp -Url "$apiBase/health/live")) {
        throw "The API did not become ready at $apiBase. See $logDirectory/api.log"
    }

    switch ($Scenario) {

        'broker-down' {
            # The API writes request + outbox row in one SQL transaction and never touches the
            # broker, so acceptance must keep working while RabbitMQ is gone.
            # This run's own broker, not the shared compose one: stopping it proves the
            # guarantee without taking the broker away from anything else on the machine.
            Write-LabStep "Stopping this run's RabbitMQ container"
            Invoke-LabScenarioBrokerCommand -Verb 'stop' -ContainerName $brokerContainer

            $requestId = Submit-LabExport -RequestId ([guid]::NewGuid().ToString())
            Write-LabEvidence -RequestId $requestId -Message 'API answered 202 with the broker DOWN'

            $status = Get-LabOutboxStatus -RequestId $requestId
            if ($status -ne 'Pending') { throw "Expected the outbox row to be Pending during the outage but it was '$status'." }
            Write-LabEvidence -RequestId $requestId -Message "outbox=$status (durable, waiting for the broker)"

            Write-LabStep 'Starting the worker while the broker is still down'
            $worker = Start-LabProcess -Root $root -Project 'src/Integration.Worker/Integration.Worker.csproj' `
                -Arguments $commonWorkerArguments -LogPath (Join-Path $logDirectory 'worker.log')
            Wait-LabLogMarker -Handle $worker -Marker 'integration-worker-host-ready' | Out-Null
            Start-Sleep -Seconds 3

            $duringOutage = Get-LabOutboxStatus -RequestId $requestId
            if ($duringOutage -eq 'Published') { throw 'The row was marked Published while the broker was down.' }
            Write-LabEvidence -RequestId $requestId -Message "outbox=$duringOutage while the broker is down (never Published)"

            Write-LabStep "Starting this run's RabbitMQ container again"
            Invoke-LabScenarioBrokerCommand -Verb 'start' -ContainerName $brokerContainer
            if (-not (Wait-LabBrokerReady -ManagementBase $managementBase -Headers $managementHeaders)) {
                throw "The scenario broker '$brokerContainer' did not come back on port $rabbitManagementPort."
            }

            Wait-LabCondition -Description 'the outbox row to be published after the broker returns' -TimeoutSeconds 120 `
                -Condition { (Get-LabOutboxStatus -RequestId $requestId) -eq 'Published' } `
                -Diagnostics { Get-LabState -RequestId $requestId }
            Write-LabEvidence -RequestId $requestId -Message 'outbox=Published after the broker returned'

            Wait-LabCondition -Description 'the job to complete' -TimeoutSeconds 120 `
                -Condition { (Get-LabJobStatus -RequestId $requestId) -eq 'Completed' } `
                -Diagnostics { Get-LabState -RequestId $requestId }
            Write-LabEvidence -RequestId $requestId -Message "final: $(Get-LabState -RequestId $requestId)"
        }

        'duplicate-delivery' {
            Write-LabStep 'Starting the worker'
            $worker = Start-LabProcess -Root $root -Project 'src/Integration.Worker/Integration.Worker.csproj' `
                -Arguments $commonWorkerArguments -LogPath (Join-Path $logDirectory 'worker.log')
            Wait-LabLogMarker -Handle $worker -Marker 'integration-worker-host-ready' | Out-Null

            # A request the API accepted, so the inbox's source check recognises the event.
            $requestId = Submit-LabExport -RequestId ([guid]::NewGuid().ToString())
            Wait-LabCondition -Description 'the first delivery to be accepted' -TimeoutSeconds 120 `
                -Condition { (Get-LabReceiptCount -RequestId $requestId) -ge 1 } `
                -Diagnostics { Get-LabState -RequestId $requestId }
            Write-LabEvidence -RequestId $requestId -Message "after the normal delivery: $(Get-LabState -RequestId $requestId)"

            # The same business event delivered again under a NEW transport identity: what a
            # republish after a lost confirm looks like.
            Publish-LabRawMessage -Payload (New-LabEnvelopeJson -RequestId $requestId) -MessageId ([guid]::NewGuid().ToString())
            Wait-LabCondition -Description 'the duplicate delivery to be absorbed' -TimeoutSeconds 120 `
                -Condition { (Get-LabReceiptCount -RequestId $requestId) -ge 2 } `
                -Diagnostics { Get-LabState -RequestId $requestId }

            $jobs = Get-LabJobCount -RequestId $requestId
            if ($jobs -ne 1) { throw "Expected exactly one job for $requestId but found $jobs." }

            Write-LabEvidence -RequestId $requestId -Message "two receipts, ONE job: $(Get-LabState -RequestId $requestId)"

            Wait-LabCondition -Description 'the job to complete' -TimeoutSeconds 120 `
                -Condition { (Get-LabJobStatus -RequestId $requestId) -eq 'Completed' } `
                -Diagnostics { Get-LabState -RequestId $requestId }
            $applied = Get-LabAppliedCount -RequestId $requestId
            if ($applied -ne 1) { throw "Expected exactly one external effect but found $applied." }
            Write-LabEvidence -RequestId $requestId -Message "final: $(Get-LabState -RequestId $requestId)"
        }

        'erp-timeout' {
            $requestId = [guid]::NewGuid().ToString()

            # The effect commits, then the response is held past the client timeout: the worker
            # learns nothing, which is "unknown", never "failed".
            Set-LabErpScenario -RequestId $requestId -Type 'apply-then-delay-response' -DelayMs 8000
            Write-LabEvidence -RequestId $requestId -Message 'FakeErp will commit the effect and then lose the response'

            Write-LabStep 'Starting the worker with a 2s HTTP timeout'
            $worker = Start-LabProcess -Root $root -Project 'src/Integration.Worker/Integration.Worker.csproj' `
                -Arguments ($commonWorkerArguments + @('--Lab:HttpTimeoutSeconds=2', '--Lab:JobRetryDelaysSeconds:0=2')) `
                -LogPath (Join-Path $logDirectory 'worker.log')
            Wait-LabLogMarker -Handle $worker -Marker 'integration-worker-host-ready' | Out-Null

            Submit-LabExport -RequestId $requestId | Out-Null

            Wait-LabCondition -Description 'the job to complete after the lost response' -TimeoutSeconds 180 `
                -Condition { (Get-LabJobStatus -RequestId $requestId) -eq 'Completed' } `
                -Diagnostics { Get-LabState -RequestId $requestId }

            $attempts = Get-LabJobAttempts -RequestId $requestId
            if ($attempts -lt 2) { throw "Expected a retry after the lost response but attempts were $attempts." }

            $applied = Get-LabAppliedCount -RequestId $requestId
            if ($applied -ne 1) { throw "Expected exactly ONE applied export but found $applied." }

            Write-LabEvidence -RequestId $requestId -Message "retried $attempts times, applied once: $(Get-LabState -RequestId $requestId)"
        }

        'poison-message' {
            Write-LabStep 'Starting the worker'
            $worker = Start-LabProcess -Root $root -Project 'src/Integration.Worker/Integration.Worker.csproj' `
                -Arguments $commonWorkerArguments -LogPath (Join-Path $logDirectory 'worker.log')
            Wait-LabLogMarker -Handle $worker -Marker 'integration-worker-host-ready' | Out-Null

            $marker = [guid]::NewGuid().ToString('N')
            $payload = '{"eventId":"not-a-guid","type":"OrderExportRequested","marker":"' + $marker + '"}'
            $bodySha = [System.BitConverter]::ToString(
                [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($payload))
            ).Replace('-', '').ToLowerInvariant()

            Publish-LabRawMessage -Payload $payload -MessageId ([guid]::NewGuid().ToString())
            Write-LabEvidence -RequestId $marker -Message 'published a structurally invalid delivery'

            Wait-LabCondition -Description 'the delivery to be quarantined' -TimeoutSeconds 120 `
                -Condition { $null -ne (Get-LabRejectedReason -BodySha256 $bodySha) } `
                -Diagnostics { "no RejectedMessages row for body sha256 $bodySha yet" }

            $reason = Get-LabRejectedReason -BodySha256 $bodySha
            Write-LabEvidence -RequestId $marker -Message "quarantined durably with reason '$reason' (body sha256 $bodySha)"

            # Quarantined and THEN acked: the queue drains instead of cycling the poison message.
            Wait-LabCondition -Description 'the export queue to drain' -TimeoutSeconds 60 `
                -Condition {
                    $depth = Get-LabQueueDepth -Queue "${namePrefix}integration.exports"
                    $depth.Ready -eq 0 -and $depth.Unacked -eq 0
                } `
                -Diagnostics {
                    $depth = Get-LabQueueDepth -Queue "${namePrefix}integration.exports"
                    "export queue ready=$($depth.Ready) unacked=$($depth.Unacked)"
                }
            Write-LabEvidence -RequestId $marker -Message 'export queue drained: acked after the quarantine committed, not requeued'

            Wait-LabCondition -Description 'the dead-letter event to be published' -TimeoutSeconds 120 `
                -Condition { (Get-LabQueueDepth -Queue "${namePrefix}integration.exports.dead").Ready -ge 1 } `
                -Diagnostics {
                    $depth = Get-LabQueueDepth -Queue "${namePrefix}integration.exports.dead"
                    "dead-letter queue ready=$($depth.Ready)"
                }
            $dead = Get-LabQueueDepth -Queue "${namePrefix}integration.exports.dead"
            Write-LabEvidence -RequestId $marker -Message "dead-letter queue holds $($dead.Ready) message(s) carrying hashes and reason codes, not the raw body"
        }

        'worker-restart' {
            $requestId = [guid]::NewGuid().ToString()

            # Two 503s first, so there is real durable retry state to inherit across the restart.
            Set-LabErpScenario -RequestId $requestId -Type 'first-n-503' -N 2
            Submit-LabExport -RequestId $requestId | Out-Null
            Write-LabEvidence -RequestId $requestId -Message 'accepted; the external system will answer 503 twice'

            Write-LabStep 'Starting the first worker'
            $worker = Start-LabProcess -Root $root -Project 'src/Integration.Worker/Integration.Worker.csproj' `
                -Arguments ($commonWorkerArguments + @('--Lab:JobRetryDelaysSeconds:0=30', '--Lab:JobLeaseSeconds=5')) `
                -LogPath (Join-Path $logDirectory 'worker-1.log')
            Wait-LabLogMarker -Handle $worker -Marker 'integration-worker-host-ready' | Out-Null

            Wait-LabCondition -Description 'the first attempt to be spent' -TimeoutSeconds 180 `
                -Condition { (Get-LabJobAttempts -RequestId $requestId) -ge 1 } `
                -Diagnostics { Get-LabState -RequestId $requestId }
            $beforeRestart = Get-LabJobAttempts -RequestId $requestId
            Write-LabEvidence -RequestId $requestId -Message "before the kill: $(Get-LabState -RequestId $requestId)"

            Write-LabStep 'Killing the worker hard (no graceful shutdown)'
            Stop-LabProcess -Handle $worker
            $worker = $null

            $afterKill = Get-LabJobAttempts -RequestId $requestId
            if ($afterKill -ne $beforeRestart) { throw "The attempt counter changed during the kill ($beforeRestart -> $afterKill)." }
            Write-LabEvidence -RequestId $requestId -Message "the durable counter survived the kill: attempts=$afterKill"

            Write-LabStep 'Starting a replacement worker'
            $worker = Start-LabProcess -Root $root -Project 'src/Integration.Worker/Integration.Worker.csproj' `
                -Arguments ($commonWorkerArguments + @('--Lab:JobRetryDelaysSeconds:0=1', '--Lab:JobLeaseSeconds=5')) `
                -LogPath (Join-Path $logDirectory 'worker-2.log')
            Wait-LabLogMarker -Handle $worker -Marker 'integration-worker-host-ready' | Out-Null

            Wait-LabCondition -Description 'the replacement worker to finish the job' -TimeoutSeconds 180 `
                -Condition { (Get-LabJobStatus -RequestId $requestId) -eq 'Completed' } `
                -Diagnostics { Get-LabState -RequestId $requestId }

            $applied = Get-LabAppliedCount -RequestId $requestId
            if ($applied -ne 1) { throw "Expected exactly ONE applied export after the restart but found $applied." }

            $total = Get-LabJobAttempts -RequestId $requestId
            Write-LabEvidence -RequestId $requestId -Message "continued from attempt $afterKill to $total and applied once: $(Get-LabState -RequestId $requestId)"
        }
    }

    Write-Host ''
    Write-LabStep "Scenario '$Scenario' completed as expected."
}
catch {
    $failed = $true
    Write-Host ''
    Write-Error "Scenario '$Scenario' failed: $($_.Exception.Message)"
    throw
}
finally {
    # Remove exactly what this run created, and nothing else. Every helper below re-checks the
    # run-scoped name, so a bug here can still only reach this run's own resources. The
    # developer's compose containers and databases are never named, stopped or dropped.
    Stop-LabProcess -Handle $worker
    Stop-LabProcess -Handle $api
    Stop-LabProcess -Handle $erp

    if ($brokerStarted) {
        Write-LabStep "Removing this run's RabbitMQ container"
        # `rm --force` covers both the running and the stopped case, so the broker-down
        # scenario needs no separate restore step.
        Invoke-LabScenarioBrokerCommand -Verb 'rm' -ContainerName $brokerContainer -IgnoreFailure
    }

    if ($databasesCreated) {
        Write-LabStep "Removing this run's databases"
        Remove-LabScenarioDatabase -Name $integrationDatabase
        Remove-LabScenarioDatabase -Name $fakeErpDatabase
    }

    if ($failed -or $KeepLogs) {
        Write-Host "    child-process logs kept in $logDirectory"
    }
    else {
        Remove-Item -LiteralPath $logDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
