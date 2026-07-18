[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('(?i)issue-106')]
    [string] $DisposableRoot,
    [int] $Port = 4323
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FullPath {
    param([Parameter(Mandatory)][string] $Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function Normalize-PathText {
    param([Parameter(Mandatory)][string] $Path)

    return ((Get-FullPath $Path).Replace('/', '\')).TrimEnd('\').ToLowerInvariant()
}

function Test-SamePath {
    param(
        [Parameter(Mandatory)][string] $Left,
        [Parameter(Mandatory)][string] $Right
    )

    return (Normalize-PathText $Left) -eq (Normalize-PathText $Right)
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory)][string] $Child,
        [Parameter(Mandatory)][string] $Parent
    )

    $childText = Normalize-PathText $Child
    $parentText = Normalize-PathText $Parent
    return $childText.StartsWith($parentText + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparsePointInPath {
    param([Parameter(Mandatory)][string] $Path)

    $current = Get-FullPath $Path
    while ($true) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "DisposableRoot or one of its existing parent paths is a reparse point: $current"
            }
        }

        $parent = [System.IO.Directory]::GetParent($current)
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $current) {
            break
        }

        $current = $parent
    }
}

function Assert-NoReparsePointInTree {
    param([Parameter(Mandatory)][string] $Path)

    Assert-NoReparsePointInPath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return
    }

    try {
        $entries = @(Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop)
    }
    catch {
        throw "Unable to enumerate disposable root before recursive deletion; refusing to delete anything: $($_.Exception.Message)"
    }

    foreach ($entry in $entries) {
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "DisposableRoot contains a descendant reparse point; refusing recursive deletion: $($entry.FullName)"
        }
    }
}

function Write-Action {
    param(
        [Parameter(Mandatory)][string] $Action,
        [Parameter(Mandatory)][string] $Status,
        [Parameter(Mandatory)][string] $Detail
    )

    Write-Output ('[{0}] {1}: {2}' -f $Status, $Action, $Detail)
}

function Get-ListeningProcessIds {
    if ($null -ne (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)) {
        try {
            return @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop |
                    Select-Object -ExpandProperty OwningProcess -Unique)
        }
        catch {
            # Fall through to netstat when the CIM-backed cmdlet is unavailable
            # to the current operator token.
        }
    }

    $netstat = Get-Command netstat.exe -ErrorAction SilentlyContinue
    if ($null -eq $netstat) {
        throw 'neither Get-NetTCPConnection nor netstat.exe is available'
    }

    $portText = [string]$Port
    $processIds = [System.Collections.Generic.List[int]]::new()
    $lines = @(& $netstat.Source -ano -p tcp 2>$null)
    foreach ($line in $lines) {
        if (($line -match '^\s*TCP\s+\S+:(?<port>\d+)\s+\S+\s+LISTENING\s+(?<pid>\d+)\s*$') -and $Matches.port -eq $portText) {
            $processIds.Add([int]$Matches.pid)
        }
    }

    return @($processIds | Select-Object -Unique)
}

$rootFull = Get-FullPath $DisposableRoot
$repoRoot = Get-FullPath (Join-Path $PSScriptRoot '..\..\..')
$tempRoot = Get-FullPath ([System.IO.Path]::GetTempPath())
$repoTmpRoot = Get-FullPath (Join-Path $repoRoot 'tmp')
if (-not (Test-PathWithin $rootFull $tempRoot) -and
    -not (Test-PathWithin $rootFull $repoTmpRoot)) {
    throw "DisposableRoot must be a strict child of the operating system temporary directory or $repoTmpRoot."
}
$protectedPaths = @(
    $repoRoot,
    (Get-Location).Path,
    $HOME,
    [System.IO.Path]::GetPathRoot($rootFull),
    $tempRoot,
    $repoTmpRoot
)
foreach ($protectedPath in $protectedPaths) {
    if (Test-SamePath $rootFull $protectedPath) {
        throw "DisposableRoot resolves to a protected path: $rootFull"
    }
}
$protectedRepositorySubdirectories = @('src', 'scripts', 'docs', 'tests', '.git', '.worktrees') |
    ForEach-Object { Join-Path $repoRoot $_ }
foreach ($protectedDirectory in $protectedRepositorySubdirectories) {
    if (Test-PathWithin $rootFull $protectedDirectory) {
        throw "DisposableRoot is under a protected repository directory: $protectedDirectory"
    }
}
Assert-NoReparsePointInTree $rootFull

if (-not (Test-Path -LiteralPath $rootFull)) {
    Write-Action 'stop disposable monitor' 'SKIP' "disposable root does not exist: $rootFull"
    Write-Action 'delete disposable storage/database directory' 'SKIP' 'nothing exists to delete'
    Write-Action 'remove throwaway Hook configuration directory' 'SKIP' 'nothing exists to remove'
    Write-Output 'cleanup_result: PASS (idempotent no-op)'
    exit 0
}

$rootPathText = Normalize-PathText $rootFull
$listenerPids = @()
try {
    $listenerPids = @(Get-ListeningProcessIds)
}
catch {
    Write-Action 'inspect disposable monitor listener' 'FAIL' "unable to inspect port ${Port}: $($_.Exception.Message)"
    exit 1
}

$unsafeListener = $false
if ($listenerPids.Count -eq 0) {
    Write-Action 'stop disposable monitor' 'SKIP' "no listener was found on port $Port"
}
foreach ($listenerPid in $listenerPids) {
    $process = Get-Process -Id ([int]$listenerPid) -ErrorAction SilentlyContinue
    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $listenerPid" -ErrorAction SilentlyContinue
    $commandLine = if ($null -ne $cim) { [string]$cim.CommandLine } else { '' }
    $normalizedCommandLine = $commandLine.Replace('/', '\').ToLowerInvariant()
    $isMonitor = $normalizedCommandLine.Contains('copilotagentobservability.localmonitor')
    $ownsRoot = $normalizedCommandLine.Contains($rootPathText)
    $ownsPort = $normalizedCommandLine.Contains("--url http://127.0.0.1:$Port") -or
        $normalizedCommandLine.Contains("--port $Port")
    if ($isMonitor -and $ownsRoot -and $ownsPort) {
        if ($null -eq $process) {
            Write-Action "stop disposable monitor pid $listenerPid" 'SKIP' 'process no longer exists'
            continue
        }

        Stop-Process -Id ([int]$listenerPid) -Force
        Write-Action "stop disposable monitor pid $listenerPid" 'DONE' 'command line matched Local Monitor, disposable root, and requested port'
    }
    else {
        $unsafeListener = $true
        Write-Action "stop listener pid $listenerPid" 'SKIP' 'listener was not proven to be the disposable Local Monitor; operator-owned processes were left untouched'
    }
}

if ($unsafeListener) {
    Write-Action 'delete disposable storage/database directory' 'SKIP' 'a non-owned process is using the requested port'
    Write-Action 'remove throwaway Hook configuration directory' 'SKIP' 'a non-owned process is using the requested port'
    Write-Output 'cleanup_result: FAIL (ownership proof failed; no data was deleted)'
    exit 1
}

$storageDirectory = Join-Path $rootFull 'storage'
$hookProjectDirectory = Join-Path $rootFull 'hook-project'
if (Test-Path -LiteralPath $storageDirectory) {
    Assert-NoReparsePointInTree $rootFull
    Remove-Item -LiteralPath $storageDirectory -Recurse -Force
    Write-Action 'delete disposable storage/database directory' 'DONE' $storageDirectory
}
else {
    Write-Action 'delete disposable storage/database directory' 'SKIP' 'storage directory does not exist'
}

if (Test-Path -LiteralPath $hookProjectDirectory) {
    Assert-NoReparsePointInTree $rootFull
    Remove-Item -LiteralPath $hookProjectDirectory -Recurse -Force
    Write-Action 'remove throwaway Hook configuration directory' 'DONE' $hookProjectDirectory
}
else {
    Write-Action 'remove throwaway Hook configuration directory' 'SKIP' 'Hook project directory does not exist'
}

if (Test-Path -LiteralPath $rootFull) {
    Assert-NoReparsePointInTree $rootFull
    Remove-Item -LiteralPath $rootFull -Recurse -Force
    Write-Action 'delete remaining disposable root contents' 'DONE' $rootFull
}
else {
    Write-Action 'delete remaining disposable root contents' 'SKIP' 'disposable root is already absent'
}

Write-Output 'cleanup_result: PASS'
exit 0
