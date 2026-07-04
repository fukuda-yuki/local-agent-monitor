param(
    [string] $TaskName = 'CopilotAgentObservability LocalMonitor',
    [Parameter(Mandatory)]
    [ValidateSet('Enable', 'Disable')]
    [string] $Action
)

. "$PSScriptRoot\common.ps1"

$task = Get-LocalMonitorTask -TaskName $TaskName
if ($null -eq $task) {
    Write-Error 'task_not_registered'
    exit 1
}

if ($Action -eq 'Enable') {
    Enable-ScheduledTask -TaskName $TaskName | Out-Null
    Write-Output 'enabled'
    exit 0
}

Disable-ScheduledTask -TaskName $TaskName | Out-Null
Write-Output 'disabled'
exit 0
