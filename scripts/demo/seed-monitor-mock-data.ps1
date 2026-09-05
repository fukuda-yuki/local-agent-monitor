#Requires -Version 7.0
<#
.SYNOPSIS
    Seeds the Local Ingestion Monitor with synthetic demo traces.

.DESCRIPTION
    Posts every OTLP JSON payload under scripts\demo\payloads\ to the
    monitor's POST /v1/traces endpoint. Synthetic data still mutates the
    destination DB. Use only a task-owned Monitor started with a fresh,
    unused disposable DB under tmp and an isolated -RuntimeRoot. Never
    target the normal capture DB or stop its Monitor to prepare a demo.
    This script checks loopback/reachability, not DB disposability.
    Follow .agents/skills/seed-demo/SKILL.md for instance verification.

    Run this once per monitor database. Re-running against the same
    database re-ingests the same trace ids as new raw records, which
    duplicates their spans in the span projection. For a clean demo,
    start a new isolated demo runtime with a fresh explicit -DbPath.
    Do not reuse or automatically delete an existing DB, even after a
    partial seed failure.

    Successful POSTs and the fixed wait do not prove projection completion
    or UI coverage. The current / page is Repository selection; OTLP alone
    does not prove Session lifecycle, Skill invocation, or Sub-agent data.

.EXAMPLE
    # After verifying the task-owned demo instance and its fresh DB:
    pwsh scripts\demo\seed-monitor-mock-data.ps1 -MonitorUrl http://127.0.0.1:4321
#>
param(
    [string] $MonitorUrl = 'http://127.0.0.1:4320'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $PSCommandPath
. (Join-Path $scriptDirectory '..\local-monitor\common.ps1')

if (-not (Test-LocalMonitorLoopbackUrl -Url $MonitorUrl)) {
    Write-Error "MonitorUrl must be a loopback http URL (e.g. http://127.0.0.1:4320). Got: $MonitorUrl"
    exit 1
}

$MonitorUrl = $MonitorUrl.TrimEnd('/')

$reachable = $false
for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
        # Any HTTP response (including 503 on an empty store) proves the monitor is up.
        Invoke-WebRequest -UseBasicParsing -Uri "$MonitorUrl/health/ready" -TimeoutSec 3 -SkipHttpErrorCheck | Out-Null
        $reachable = $true
        break
    }
    catch {
        Start-Sleep -Seconds 1
    }
}

if (-not $reachable) {
    Write-Error "Local Monitor is not reachable at $MonitorUrl. Start it first, e.g.: dotnet run --project src\CopilotAgentObservability.LocalMonitor -- --db tmp\monitor-demo\monitor.db --url $MonitorUrl"
    exit 1
}

$payloadDirectory = Join-Path $scriptDirectory 'payloads'
$payloadFiles = @(
    'trace-rich.json'
    'trace-rich-ok.json'
    'trace-rich-unrecovered.json'
    'trace-error-recovered.json'
    'trace-list-varied-1.json'
    'trace-list-varied-2.json'
    'trace-list-varied-3.json'
    'trace-list-varied-4.json'
    'trace-overview-recent.json'
)

$seeded = 0
foreach ($fileName in $payloadFiles) {
    $path = Join-Path $payloadDirectory $fileName
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Error "Payload file not found: $path"
        exit 1
    }

    $body = Get-Content -Raw -LiteralPath $path
    $response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$MonitorUrl/v1/traces" -ContentType 'application/json' -Body $body -SkipHttpErrorCheck
    if ($response.StatusCode -ne 200) {
        Write-Error "POST /v1/traces failed for $fileName (HTTP $($response.StatusCode)): $($response.Content)"
        exit 1
    }

    Write-Host ("ok    {0}" -f $fileName)
    $seeded++
}

# Give the projection worker (~1s poll) time to project before the user looks.
Start-Sleep -Seconds 2
Write-Host ""
Write-Host "Seeded $seeded demo traces. Open $MonitorUrl/ to explore the monitor UI."
Write-Host "Note: seed each monitor database only once. Re-seeding the same DB duplicates spans."
