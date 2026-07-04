param(
    [string] $TaskName = 'CopilotAgentObservability LocalMonitor',
    [string] $InstallRoot
)

. "$PSScriptRoot\common.ps1"

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Get-LocalMonitorDefaultInstallRoot
}

$state = Get-LocalMonitorState
$url = [string] (Get-LocalMonitorStateValue -State $state -Name 'url' -DefaultValue $script:DefaultUrl)
$dbPath = [string] (Get-LocalMonitorStateValue -State $state -Name 'db_path' -DefaultValue $script:DefaultDbPath)
$stateInstallRoot = [string] (Get-LocalMonitorStateValue -State $state -Name 'install_root' -DefaultValue $InstallRoot)
$task = Get-LocalMonitorTask -TaskName $TaskName
$taskInstalled = $null -ne $task
$taskEnabled = $taskInstalled -and $task.State -ne 'Disabled'
$processRunning = $false
$processId = Get-LocalMonitorStateValue -State $state -Name 'process_id'
if ($null -ne $processId) {
    $processRunning = Test-LocalMonitorProcess -ProcessId ([int] $processId)
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

$publishedExe = Get-LocalMonitorPublishedExePath -InstallRoot $stateInstallRoot
$appInstalled = Test-Path -LiteralPath $publishedExe
$appVersion = [string] (Get-LocalMonitorStateValue -State $state -Name 'app_version' -DefaultValue (Get-LocalMonitorAppVersion -InstallRoot $stateInstallRoot))
$sanitizedOnly = [bool] (Get-LocalMonitorStateValue -State $state -Name 'sanitized_only' -DefaultValue $false)

Write-Output ("installed: {0}" -f ($(if ($appInstalled) { 'yes' } else { 'no' })))
Write-Output ("running: {0}" -f ($(if ($processRunning) { 'yes' } else { 'no' })))
Write-Output ("startup registered: {0}" -f ($(if ($taskInstalled) { 'yes' } else { 'no' })))
Write-Output ("startup enabled: {0}" -f ($(if ($taskEnabled) { 'yes' } else { 'no' })))
Write-Output ("task name: {0}" -f $TaskName)
Write-Output ("URL: {0}" -f $url)
Write-Output ("DB path: {0}" -f $dbPath)
Write-Output ("log path: {0}" -f $script:LogDirectory)
Write-Output ("install root: {0}" -f $stateInstallRoot)
Write-Output ("app version: {0}" -f $appVersion)
Write-Output ("health/live HTTP status: {0}" -f ($(if ($null -ne $live) { [int] $live.StatusCode } else { 'unreachable' })))
Write-Output ("health/ready HTTP status: {0}" -f ($(if ($null -ne $ready) { [int] $ready.StatusCode } else { 'unreachable' })))
Write-Output ("readiness status: {0}" -f $readyStatus)
Write-Output ("degraded_reasons: {0}" -f ($degradedReasons -join ','))
Write-Output ("projection lag: {0}" -f $projectionLag)
Write-Output ("projection backlog: {0}" -f $projectionBacklog)
Write-Output ("last start time: {0}" -f (Get-LocalMonitorStateValue -State $state -Name 'started_at' -DefaultValue ''))
Write-Output ("last exit code: {0}" -f '')
Write-Output ("sanitized-only mode: {0}" -f ($(if ($sanitizedOnly) { 'yes' } else { 'no' })))
Write-Output ("mode: {0}" -f (Get-LocalMonitorStateValue -State $state -Name 'mode' -DefaultValue 'unknown'))

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
