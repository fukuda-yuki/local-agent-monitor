param(
    [string] $TaskName = 'CopilotAgentObservability LocalMonitor'
)

. "$PSScriptRoot\common.ps1"

$state = Get-LocalMonitorState
$url = if ($null -ne $state -and $state.url) { [string] $state.url } else { $script:DefaultUrl }
$dbPath = if ($null -ne $state -and $state.db_path) { [string] $state.db_path } else { $script:DefaultDbPath }
$task = Get-LocalMonitorTask -TaskName $TaskName
$taskInstalled = $null -ne $task
$taskEnabled = $taskInstalled -and $task.State -ne 'Disabled'
$processRunning = $false
if ($null -ne $state -and $state.process_id) {
    $processRunning = Test-LocalMonitorProcess -ProcessId ([int] $state.process_id)
}

$live = Test-LocalMonitorHealth -Url $url -Path '/health/live'
$ready = Test-LocalMonitorHealth -Url $url -Path '/health/ready'
$readyStatus = 'unknown'
$degradedReasons = @()
$projectionLag = $null
$projectionBacklog = $null
if ($null -ne $ready) {
    try {
        $readyBody = $ready.Content | ConvertFrom-Json
        $readyStatus = [string] $readyBody.status
        $degradedReasons = @($readyBody.degraded_reasons)
        $projectionLag = $readyBody.checks.projection_lag_seconds
        $projectionBacklog = $readyBody.checks.projection_backlog
    }
    catch {
        $readyStatus = 'unparseable'
    }
}

Write-Output ("installed task: {0}" -f ($(if ($taskInstalled) { 'yes' } else { 'no' })))
Write-Output ("task enabled: {0}" -f ($(if ($taskEnabled) { 'yes' } else { 'no' })))
Write-Output ("process running: {0}" -f ($(if ($processRunning) { 'yes' } else { 'no' })))
Write-Output ("URL: {0}" -f $url)
Write-Output ("DB path: {0}" -f $dbPath)
Write-Output ("health/live HTTP status: {0}" -f ($(if ($null -ne $live) { [int] $live.StatusCode } else { 'unreachable' })))
Write-Output ("health/ready HTTP status: {0}" -f ($(if ($null -ne $ready) { [int] $ready.StatusCode } else { 'unreachable' })))
Write-Output ("readiness status: {0}" -f $readyStatus)
Write-Output ("degraded_reasons: {0}" -f ($degradedReasons -join ','))
Write-Output ("projection lag: {0}" -f $projectionLag)
Write-Output ("projection backlog: {0}" -f $projectionBacklog)
Write-Output ("last start time: {0}" -f ($(if ($null -ne $state -and $state.started_at) { $state.started_at } else { '' })))
Write-Output ("last exit code: {0}" -f '')
Write-Output ("mode: {0}" -f ($(if ($null -ne $state -and $state.sanitized_only) { 'sanitized-only' } else { 'raw-default' })))

if (-not $taskInstalled -and -not $processRunning) {
    exit 1
}

if ($taskInstalled -and -not $taskEnabled) {
    exit 3
}

if ($readyStatus -eq 'not_ready') {
    exit 2
}

if ($processRunning -or ($null -ne $live -and [int] $live.StatusCode -eq 200)) {
    exit 0
}

exit 4
