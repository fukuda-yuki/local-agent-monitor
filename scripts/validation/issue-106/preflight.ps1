[CmdletBinding()]
param(
    [int] $Port = 4323,
    [string] $DisposableRoot,
    [string] $StorageDirectory,
    [string] $HookProjectDirectory,
    [switch] $OperatorAuthorized
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if ([string]::IsNullOrWhiteSpace($DisposableRoot)) {
    $DisposableRoot = Join-Path $repoRoot 'tmp\issue-106-validation'
}

function Get-FullPath {
    param([Parameter(Mandatory)][string] $Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory)][string] $Child,
        [Parameter(Mandatory)][string] $Parent
    )

    $childFull = (Get-FullPath $Child).TrimEnd('\', '/')
    $parentFull = (Get-FullPath $Parent).TrimEnd('\', '/')
    return $childFull.Equals($parentFull, [StringComparison]::OrdinalIgnoreCase) -or
        $childFull.StartsWith($parentFull + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $childFull.StartsWith($parentFull + [System.IO.Path]::AltDirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

$disposableRootFull = Get-FullPath $DisposableRoot
if ([string]::IsNullOrWhiteSpace($StorageDirectory)) {
    $StorageDirectory = Join-Path $disposableRootFull 'storage'
}
if ([string]::IsNullOrWhiteSpace($HookProjectDirectory)) {
    $HookProjectDirectory = Join-Path $disposableRootFull 'hook-project'
}

$results = [System.Collections.Generic.List[object]]::new()

function Add-Check {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Command,
        [Parameter(Mandatory)][int] $ExitCode,
        [Parameter(Mandatory)][bool] $Passed,
        [Parameter(Mandatory)][string] $MissingPrerequisite,
        [Parameter(Mandatory)][string] $RetryRequires
    )

    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    $results.Add([pscustomobject]@{
            Name = $Name
            Command = $Command
            ExitCode = $ExitCode
            Status = $status
            MissingPrerequisite = $MissingPrerequisite
            RetryRequires = $RetryRequires
        })
    Write-Output ('[{0}] {1}' -f $status, $Name)
    Write-Output ('  command: {0}' -f $Command)
    Write-Output ('  exit_code: {0}' -f $ExitCode)
    Write-Output ('  missing_prerequisite: {0}' -f $MissingPrerequisite)
    Write-Output ('  retry_requires: {0}' -f $RetryRequires)
}

function Invoke-VersionCheck {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $CommandName
    )

    $command = Get-Command $CommandName -ErrorAction SilentlyContinue
    $commandText = '{0} --version' -f $CommandName
    if ($null -eq $command) {
        Add-Check $Name $commandText 127 $false "$CommandName is not on PATH." 'operator_action'
        return
    }

    $output = ''
    $exitCode = 1
    try {
        $output = (& $command.Source --version 2>&1 | Out-String).Trim()
        $exitCode = $LASTEXITCODE
    }
    catch {
        $output = ''
        $exitCode = 1
    }

    $passed = $exitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($output)
    $missing = if ($passed) { 'none' } else { "$CommandName --version did not return a version." }
    Add-Check $Name $commandText $exitCode $passed $missing 'operator_action'
    if ($passed) {
        Write-Output ('  observed_version: {0}' -f $output.Split([Environment]::NewLine)[0])
    }
}

function Test-PortFree {
    $listener = $null
    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $listener) {
            $listener.Stop()
        }
    }
}

function Test-WritableDirectory {
    param([Parameter(Mandatory)][string] $Directory)

    $probe = Join-Path $Directory ('.write-probe-{0}.tmp' -f ([Guid]::NewGuid().ToString('N')))
    try {
        New-Item -ItemType Directory -Force -Path $Directory | Out-Null
        [System.IO.File]::WriteAllText($probe, 'issue-106-preflight')
        Remove-Item -LiteralPath $probe -Force
        return $true
    }
    catch {
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        return $false
    }
}

function Test-DisposablePath {
    param(
        [Parameter(Mandatory)][string] $Candidate,
        [Parameter(Mandatory)][string] $Root
    )

    return (Test-PathWithin -Child $Candidate -Parent $Root) -and
        -not (Get-FullPath $Candidate).Equals((Get-FullPath $repoRoot), [StringComparison]::OrdinalIgnoreCase)
}

Write-Output 'Issue #106 preflight (fail-closed)'
Write-Output ('repository_root: {0}' -f $repoRoot)
Write-Output ('disposable_root: {0}' -f $disposableRootFull)

if ($Port -lt 1 -or $Port -gt 65535) {
    Add-Check 'loopback port range' "preflight -Port $Port" 2 $false 'Port must be between 1 and 65535.' 'operator_action'
}
else {
    $portFree = Test-PortFree
    Add-Check 'loopback port free' "bind 127.0.0.1:$Port and release" ($(if ($portFree) { 0 } else { 1 })) $portFree $(if ($portFree) { 'none' } else { "Port 127.0.0.1:$Port is already in use." }) 'operator_action'
}

Invoke-VersionCheck 'Claude Code CLI present and versioned' 'claude'
Invoke-VersionCheck '.NET SDK present and versioned' 'dotnet'

$monitorProject = Join-Path $repoRoot 'src\CopilotAgentObservability.LocalMonitor\CopilotAgentObservability.LocalMonitor.csproj'
$buildCommand = "dotnet build $monitorProject --no-restore --nologo"
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$monitorProjectExists = Test-Path -LiteralPath $monitorProject
$monitorBuilt = $false
$buildExitCode = 127
if ($null -ne $dotnetCommand -and $monitorProjectExists) {
    try {
        & $dotnetCommand.Source build $monitorProject --no-restore --nologo 2>&1 | Out-Host
        $buildExitCode = $LASTEXITCODE
        $monitorBuilt = $buildExitCode -eq 0
    }
    catch {
        $buildExitCode = 1
    }
}

$binaryCandidates = @(
    (Join-Path $repoRoot 'src\CopilotAgentObservability.LocalMonitor\bin\Debug\net10.0\CopilotAgentObservability.LocalMonitor.dll'),
    (Join-Path $repoRoot 'src\CopilotAgentObservability.LocalMonitor\bin\Debug\net10.0\CopilotAgentObservability.LocalMonitor.exe'),
    (Join-Path $repoRoot 'src\CopilotAgentObservability.LocalMonitor\bin\Release\net10.0\CopilotAgentObservability.LocalMonitor.dll'),
    (Join-Path $repoRoot 'src\CopilotAgentObservability.LocalMonitor\bin\Release\net10.0\CopilotAgentObservability.LocalMonitor.exe')
)
$builtBinary = $binaryCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$monitorCheckPassed = $null -ne $dotnetCommand -and $monitorProjectExists -and $monitorBuilt
$monitorMissing = if ($monitorCheckPassed) {
    'none'
}
elseif ($null -eq $dotnetCommand) {
    'dotnet is not on PATH; a pre-existing binary cannot satisfy this check.'
}
elseif (-not $monitorProjectExists) {
    "Local Monitor project file is missing: $monitorProject"
}
else {
    'Local Monitor build failed; a pre-existing binary cannot satisfy this check.'
}
$monitorRetry = if ($monitorCheckPassed) { 'none' } else { 'operator_action or product_candidate_change' }
Add-Check 'Local Monitor build or built binary' $buildCommand $buildExitCode $monitorCheckPassed $monitorMissing $monitorRetry
if ($monitorBuilt) {
    Write-Output '  build_evidence: build succeeded'
}
elseif ($null -ne $builtBinary) {
    Write-Output '  supplementary_evidence: pre-existing binary present (not a pass condition)'
}

$storageSafe = Test-DisposablePath $StorageDirectory $disposableRootFull
$storageWritable = $storageSafe -and (Test-WritableDirectory $StorageDirectory)
$storageMissing = if (-not $storageSafe) { 'StorageDirectory must be inside DisposableRoot.' } elseif ($storageWritable) { 'none' } else { 'Disposable storage directory is not writable.' }
Add-Check 'writable disposable storage directory' "write and remove a probe under $StorageDirectory" ($(if ($storageWritable) { 0 } else { 1 })) $storageWritable $storageMissing 'operator_action'

$hookSafe = Test-DisposablePath $HookProjectDirectory $disposableRootFull
$hookConfigDirectory = Join-Path $HookProjectDirectory '.claude'
$hookWritable = $hookSafe -and (Test-WritableDirectory $hookConfigDirectory)
$hookSettingsPath = Join-Path $hookConfigDirectory 'settings.json'
if ($hookWritable) {
    try {
        [System.IO.File]::WriteAllText($hookSettingsPath, '{"issue_106_preflight":true}')
        Remove-Item -LiteralPath $hookSettingsPath -Force
    }
    catch {
        $hookWritable = $false
        Remove-Item -LiteralPath $hookSettingsPath -Force -ErrorAction SilentlyContinue
    }
}
$hookMissing = if (-not $hookSafe) { 'HookProjectDirectory must be inside DisposableRoot.' } elseif ($hookWritable) { 'none' } else { 'Throwaway Hook configuration directory is not writable.' }
Add-Check 'writable throwaway Hook configuration path' "write and remove $hookSettingsPath" ($(if ($hookWritable) { 0 } else { 1 })) $hookWritable $hookMissing 'operator_action'

$authorizationMessage = 'live content-enabled runs require distinct operator authorization; rerun with -OperatorAuthorized only after explicit human approval.'
if ($OperatorAuthorized) {
    Add-Check 'explicit live-content operator authorization' 'preflight -OperatorAuthorized' 0 $true 'none' 'none'
}
else {
    Write-Output "AUTHORIZATION REQUIRED: $authorizationMessage"
    Add-Check 'explicit live-content operator authorization' 'preflight -OperatorAuthorized' 2 $false $authorizationMessage 'operator_action'
}

$failed = @($results | Where-Object { $_.Status -ne 'PASS' }).Count -gt 0
Write-Output ('preflight_result: {0}' -f $(if ($failed) { 'FAIL' } else { 'PASS' }))
if ($failed) {
    exit 1
}

exit 0
