param(
    [string] $Configuration = 'Release',
    [string] $RuntimeIdentifier = 'win-x64',
    [string] $OutputDirectory,
    [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\local-monitor-release'
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = '0.0.0-local'
}

$publishDirectory = Join-Path $OutputDirectory 'publish'
$stagingDirectory = Join-Path $OutputDirectory 'staging'
$zipPath = Join-Path $OutputDirectory 'local-monitor-win-x64.zip'
$projectPath = Join-Path $repoRoot 'src\CopilotAgentObservability.LocalMonitor\CopilotAgentObservability.LocalMonitor.csproj'

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $publishDirectory `
    -p:SelfContained=true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) {
    Write-Error 'dotnet_publish_failed'
    exit $LASTEXITCODE
}

$appDirectory = Join-Path $stagingDirectory 'app'
$scriptsDirectory = Join-Path $stagingDirectory 'scripts'
New-Item -ItemType Directory -Force -Path $appDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $scriptsDirectory | Out-Null

Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $appDirectory -Recurse -Force
$scriptNames = @(
    'common.ps1',
    'install.ps1',
    'start.ps1',
    'stop.ps1',
    'status.ps1',
    'set-startup-task.ps1',
    'install-startup-task.ps1',
    'uninstall-startup-task.ps1'
)
foreach ($scriptName in $scriptNames) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\local-monitor\$scriptName") -Destination $scriptsDirectory -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\local-monitor\README.md') -Destination (Join-Path $stagingDirectory 'README.md') -Force

$noticeSource = Join-Path $repoRoot 'src\CopilotAgentObservability.LocalMonitor\wwwroot\vendor\THIRD-PARTY-NOTICES.md'
if (Test-Path -LiteralPath $noticeSource) {
    Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $stagingDirectory 'THIRD-PARTY-NOTICES.md') -Force
}

$manifest = [ordered] @{
    name = 'CopilotAgentObservability LocalMonitor'
    artifact = 'local-monitor-win-x64.zip'
    runtime_identifier = $RuntimeIdentifier
    configuration = $Configuration
    version = $Version
    self_contained = $true
    single_file = $false
    app_directory = 'app'
    scripts_directory = 'scripts'
    default_url = 'http://127.0.0.1:4320'
    default_runtime_root = '%LOCALAPPDATA%\CopilotAgentObservability\LocalMonitor'
    raw_data_included = $false
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $stagingDirectory 'manifest.json') -Encoding UTF8

Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $zipPath -Force
Write-Output $zipPath
