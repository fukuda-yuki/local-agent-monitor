param(
    [string] $TaskName = 'CopilotAgentObservability LocalMonitor',
    [string] $Url = 'http://127.0.0.1:4320',
    [string] $DbPath,
    [string] $InstallRoot,
    [ValidateSet('DotnetRun', 'Published')]
    [string] $Mode = 'DotnetRun',
    [switch] $SanitizedOnly,
    [switch] $StartNow,
    [switch] $Force,
    [switch] $DryRun
)

. "$PSScriptRoot\common.ps1"

if ([string]::IsNullOrWhiteSpace($DbPath)) {
    $DbPath = $script:DefaultDbPath
}

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Get-LocalMonitorDefaultInstallRoot
}

if (-not (Test-LocalMonitorLoopbackUrl -Url $Url)) {
    Write-Error 'non_loopback_url'
    exit 1
}

$existing = Get-LocalMonitorTask -TaskName $TaskName
if ($null -ne $existing -and -not $Force) {
    Write-Error 'task_already_exists'
    exit 1
}

$repoRoot = Get-LocalMonitorRepoRoot
$startScript = Join-Path $PSScriptRoot 'start.ps1'
$psPath = Get-LocalMonitorPowerShellPath
$startArgs = @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    ('"{0}"' -f $startScript),
    '-Url',
    ('"{0}"' -f $Url),
    '-DbPath',
    ('"{0}"' -f $DbPath),
    '-Mode',
    $Mode,
    '-InstallRoot',
    ('"{0}"' -f $InstallRoot),
    '-NoBrowser',
    '-WaitReady'
)
if ($SanitizedOnly) {
    $startArgs += '-SanitizedOnly'
}

if ($DryRun) {
    Write-Output "task name: $TaskName"
    Write-Output "execute: $psPath"
    Write-Output "arguments: $($startArgs -join ' ')"
    Write-Output "working directory: $repoRoot"
    Write-Output "trigger: logon"
    Write-Output "multiple instances: IgnoreNew"
    exit 0
}

if ($null -ne $existing -and $Force) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

$action = New-ScheduledTaskAction -Execute $psPath -Argument ($startArgs -join ' ') -WorkingDirectory $repoRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null

$registered = Get-LocalMonitorTask -TaskName $TaskName
if ($null -eq $registered) {
    Write-Error 'task_registration_failed'
    exit 1
}

Write-Output "installed"
if ($StartNow) {
    & $startScript -Url $Url -DbPath $DbPath -InstallRoot $InstallRoot -Mode $Mode -SanitizedOnly:$SanitizedOnly.IsPresent -NoBrowser -WaitReady
    exit $LASTEXITCODE
}

exit 0
