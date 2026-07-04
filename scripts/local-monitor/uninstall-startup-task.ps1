param(
    [string] $TaskName = 'CopilotAgentObservability LocalMonitor',
    [string] $InstallRoot,
    [switch] $StopRunning,
    [switch] $RemoveData,
    [switch] $Force
)

. "$PSScriptRoot\common.ps1"

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Get-LocalMonitorDefaultInstallRoot
}

if ($StopRunning) {
    & (Join-Path $PSScriptRoot 'stop.ps1') -Force:$Force.IsPresent
}

$task = Get-LocalMonitorTask -TaskName $TaskName
if ($null -ne $task) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Remove-LocalMonitorState
Remove-LocalMonitorInstall -InstallRoot $InstallRoot -AllowExternal:$Force.IsPresent

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
