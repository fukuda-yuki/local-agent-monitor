param(
    [string] $SourceRoot,
    [string] $InstallRoot,
    [switch] $Force
)

. "$PSScriptRoot\common.ps1"

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'app'
}

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Get-LocalMonitorDefaultInstallRoot
}

if (-not (Test-Path -LiteralPath $SourceRoot)) {
    Write-Error 'source_app_not_found'
    exit 1
}

$sourceExe = Join-Path $SourceRoot $script:PublishedExeName
if (-not (Test-Path -LiteralPath $sourceExe)) {
    Write-Error 'source_app_exe_not_found'
    exit 1
}

if ((Test-Path -LiteralPath $InstallRoot) -and -not $Force) {
    Write-Error 'install_root_already_exists'
    exit 1
}

Initialize-LocalMonitorRuntime
New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
Get-ChildItem -LiteralPath $InstallRoot -Force | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $SourceRoot '*') -Destination $InstallRoot -Recurse -Force

Write-LocalMonitorLog "install completed"
Write-Output "installed"
exit 0
