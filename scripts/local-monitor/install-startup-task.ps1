param(
    [string] $TaskName = 'CopilotAgentObservability LocalMonitor',
    [string] $Url = 'http://127.0.0.1:4320',
    [string] $DbPath,
    [string] $InstallRoot,
    [ValidateSet('DotnetRun', 'Published')]
    [string] $Mode = 'DotnetRun',
    [switch] $SanitizedOnly,
    [string[]] $PricingRegistryOverride = @(),
    [string[]] $SkillDiscoveryProjectPath = @(),
    [string[]] $SkillDiscoveryDirectory = @(),
    [switch] $StartNow,
    [switch] $Force,
    [switch] $DryRun
)

. "$PSScriptRoot\common.ps1"

if (-not (Test-LocalMonitorPricingRegistryOverrideCount -PricingRegistryOverride $PricingRegistryOverride)) {
    Write-Error 'pricing_registry_override_count_invalid'
    exit 1
}

$skillDiscoveryValidationError = Test-LocalMonitorSkillDiscoveryArguments `
    -SkillDiscoveryProjectPath $SkillDiscoveryProjectPath `
    -SkillDiscoveryDirectory $SkillDiscoveryDirectory `
    -SanitizedOnly:$SanitizedOnly.IsPresent
if ($null -ne $skillDiscoveryValidationError) {
    Write-Error $skillDiscoveryValidationError
    exit 1
}

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
$taskArgument = New-LocalMonitorStartupTaskArgument `
    -StartScript $startScript `
    -Url $Url `
    -DbPath $DbPath `
    -Mode $Mode `
    -InstallRoot $InstallRoot `
    -SanitizedOnly:$SanitizedOnly.IsPresent `
    -PricingRegistryOverride $PricingRegistryOverride `
    -SkillDiscoveryProjectPath $SkillDiscoveryProjectPath `
    -SkillDiscoveryDirectory $SkillDiscoveryDirectory

if ($DryRun) {
    Write-Output "task name: $TaskName"
    Write-Output "execute: $psPath"
    Write-Output 'arguments: encoded'
    Write-Output ("pricing registry overrides: {0} (count: {1})" -f $(if (@($PricingRegistryOverride).Count -gt 0) { 'present' } else { 'absent' }), @($PricingRegistryOverride).Count)
    Write-Output ("skill discovery project paths: {0} (count: {1})" -f $(if (@($SkillDiscoveryProjectPath).Count -gt 0) { 'present' } else { 'absent' }), @($SkillDiscoveryProjectPath).Count)
    Write-Output ("skill discovery directories: {0} (count: {1})" -f $(if (@($SkillDiscoveryDirectory).Count -gt 0) { 'present' } else { 'absent' }), @($SkillDiscoveryDirectory).Count)
    Write-Output "working directory: $repoRoot"
    Write-Output "trigger: logon"
    Write-Output "multiple instances: IgnoreNew"
    exit 0
}

if ($null -ne $existing -and $Force) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

$action = New-ScheduledTaskAction -Execute $psPath -Argument $taskArgument -WorkingDirectory $repoRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null

$registered = Get-LocalMonitorTask -TaskName $TaskName
if ($null -eq $registered) {
    Write-Error 'task_registration_failed'
    exit 1
}

Write-Output ("installed (pricing registry overrides: {0} (count: {1}))" -f $(if (@($PricingRegistryOverride).Count -gt 0) { 'present' } else { 'absent' }), @($PricingRegistryOverride).Count)
Write-Output ("skill discovery project paths: {0} (count: {1})" -f $(if (@($SkillDiscoveryProjectPath).Count -gt 0) { 'present' } else { 'absent' }), @($SkillDiscoveryProjectPath).Count)
Write-Output ("skill discovery directories: {0} (count: {1})" -f $(if (@($SkillDiscoveryDirectory).Count -gt 0) { 'present' } else { 'absent' }), @($SkillDiscoveryDirectory).Count)
if ($StartNow) {
    $startParameters = @{
        Url = $Url
        DbPath = $DbPath
        InstallRoot = $InstallRoot
        Mode = $Mode
        SanitizedOnly = $SanitizedOnly.IsPresent
        NoBrowser = $true
        WaitReady = $true
    }
    if (@($PricingRegistryOverride).Count -gt 0) {
        $startParameters.PricingRegistryOverride = $PricingRegistryOverride
    }
    if (@($SkillDiscoveryProjectPath).Count -gt 0) {
        $startParameters.SkillDiscoveryProjectPath = $SkillDiscoveryProjectPath
    }
    if (@($SkillDiscoveryDirectory).Count -gt 0) {
        $startParameters.SkillDiscoveryDirectory = $SkillDiscoveryDirectory
    }
    & $startScript @startParameters
    exit $LASTEXITCODE
}

exit 0
