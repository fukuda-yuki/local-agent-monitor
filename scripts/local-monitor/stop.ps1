param(
    [int] $TimeoutSeconds = 10,
    [switch] $Force,
    [AllowEmptyString()]
    [string] $RuntimeRoot
)

$runtimeRootSupplied = $PSBoundParameters.ContainsKey('RuntimeRoot')
$runtimeRootOverride = $RuntimeRoot
. "$PSScriptRoot\common.ps1"

if ($runtimeRootSupplied) {
    try {
        Set-LocalMonitorRuntimeRoot -RuntimeRoot $runtimeRootOverride
    }
    catch {
        Write-Error 'runtime_root_invalid'
        exit 1
    }
}

$stateFileExists = Test-Path -LiteralPath $script:StatePath -PathType Leaf
$pidFileExists = Test-Path -LiteralPath $script:PidPath -PathType Leaf
$presenceMismatch = ($stateFileExists -and -not $pidFileExists) -or ($pidFileExists -and -not $stateFileExists)
if ($runtimeRootSupplied -and $presenceMismatch) {
    Write-Error 'runtime_state_mismatch'
    exit 1
}
$state = Get-LocalMonitorState
if ($null -eq $state) {
    if ($runtimeRootSupplied -and $stateFileExists) {
        Write-Error 'runtime_state_mismatch'
        exit 1
    }
    Write-Output "not_running"
    exit 0
}

if ($runtimeRootSupplied -and -not (Test-LocalMonitorExplicitRuntimeState -State $state)) {
    Write-Error 'runtime_state_mismatch'
    exit 1
}
$processId = [int] $state.process_id
$process = Get-Process -Id $processId -ErrorAction SilentlyContinue
if ($runtimeRootSupplied -and $null -ne $process -and -not (Test-LocalMonitorExplicitRuntimeProcessOwnership -State $state)) {
    Write-Error 'runtime_state_mismatch'
    exit 1
}
if ($runtimeRootSupplied -and $null -eq $process) {
    Remove-LocalMonitorState
    Write-Output "not_running"
    exit 0
}
if (-not $runtimeRootSupplied -and -not (Test-LocalMonitorProcess -ProcessId $processId)) {
    Remove-LocalMonitorState
    Write-Output "not_running"
    exit 0
}

if ($runtimeRootSupplied) {
    try {
        $process.Kill($true)
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Write-Error 'stop_timeout'
            exit 1
        }
    }
    catch {
        Write-Error 'stop_failed'
        exit 1
    }

    Remove-LocalMonitorState
    Write-LocalMonitorLog "stop process_id=$processId"
    Write-Output "stopped"
    exit 0
}

$closeRequested = $process.CloseMainWindow()
if (-not $closeRequested) {
    try {
        Stop-Process -Id $processId -Force:$Force.IsPresent -ErrorAction Stop
    }
    catch {
        Write-Error 'stop_failed'
        exit 1
    }
}

$exited = $process.WaitForExit($TimeoutSeconds * 1000)
if (-not $exited) {
    if (-not $Force) {
        Write-Error 'stop_timeout'
        exit 1
    }

    try {
        Stop-Process -Id $processId -Force -ErrorAction Stop
        $exited = $process.WaitForExit($TimeoutSeconds * 1000)
    }
    catch {
        Write-Error 'stop_failed'
        exit 1
    }

    if (-not $exited) {
        Write-Error 'stop_timeout'
        exit 1
    }
}

Remove-LocalMonitorState
Write-LocalMonitorLog "stop process_id=$processId"
Write-Output "stopped"
exit 0
