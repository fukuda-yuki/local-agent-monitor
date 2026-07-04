param(
    [int] $TimeoutSeconds = 10,
    [switch] $Force
)

. "$PSScriptRoot\common.ps1"

$state = Get-LocalMonitorState
if ($null -eq $state) {
    Write-Output "not_running"
    exit 0
}

$processId = [int] $state.process_id
if (-not (Test-LocalMonitorProcess -ProcessId $processId)) {
    Remove-LocalMonitorState
    Write-Output "not_running"
    exit 0
}

$process = Get-Process -Id $processId -ErrorAction SilentlyContinue
if ($null -eq $process) {
    Remove-LocalMonitorState
    Write-Output "not_running"
    exit 0
}

$process.CloseMainWindow() | Out-Null
$exited = $process.WaitForExit($TimeoutSeconds * 1000)
if (-not $exited) {
    if (-not $Force) {
        Write-Error 'stop_timeout'
        exit 1
    }

    Stop-Process -Id $processId -Force
}

Remove-LocalMonitorState
Write-LocalMonitorLog "stop process_id=$processId"
Write-Output "stopped"
exit 0
