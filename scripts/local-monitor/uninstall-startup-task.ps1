param(
    [string] $TaskName = 'CopilotAgentObservability LocalMonitor',
    [switch] $StopRunning,
    [switch] $RemoveData,
    [switch] $Force
)

. "$PSScriptRoot\common.ps1"

if ($StopRunning) {
    & (Join-Path $PSScriptRoot 'stop.ps1') -Force:$Force.IsPresent
}

$task = Get-LocalMonitorTask -TaskName $TaskName
if ($null -ne $task) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

if ($RemoveData) {
    if (-not $Force) {
        $answer = Read-Host "Remove LocalMonitor runtime data under $script:RuntimeRoot ? Type YES to continue"
        if ($answer -ne 'YES') {
            Write-Output "data_not_removed"
            exit 0
        }
    }

    Remove-Item -LiteralPath $script:RuntimeRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "uninstalled"
exit 0
