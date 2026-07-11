param(
    [string] $HomeDirectory = $HOME,
    [string] $InstallRoot,
    [string] $Endpoint = 'http://127.0.0.1:4320',
    [int] $TimeoutMs = 250
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Get-LocalMonitorDefaultInstallRoot
}
if (-not (Test-LocalMonitorLoopbackUrl -Url $Endpoint)) {
    throw 'non_loopback_url'
}
$endpointUri = [Uri] $Endpoint
$endpointPath = $endpointUri.AbsolutePath.TrimEnd('/')
if (-not [string]::IsNullOrEmpty($endpointUri.UserInfo) -or
    -not [string]::IsNullOrEmpty($endpointUri.Query) -or
    -not [string]::IsNullOrEmpty($endpointUri.Fragment) -or
    ($endpointPath.Length -gt 0 -and $endpointPath -cne '/api/session-ingest/v1/events')) {
    throw 'invalid_session_ingest_url'
}
if ($TimeoutMs -le 0) {
    throw 'invalid_timeout'
}

$executablePath = Get-LocalMonitorPublishedExePath -InstallRoot $InstallRoot
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw 'published_app_not_installed'
}

$hooksDirectory = Join-Path $HomeDirectory '.copilot\hooks'
$configPath = Join-Path $hooksDirectory 'local-agent-monitor.json'
$managedBy = 'CopilotAgentObservability.LocalMonitor'
if (Test-Path -LiteralPath $configPath) {
    try {
        $existing = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    }
    catch {
        throw 'hook_config_exists_unmanaged'
    }

    $marker = $existing.PSObject.Properties['managed_by']
    if ($null -eq $marker -or $marker.Value -ne $managedBy) {
        throw 'hook_config_exists_unmanaged'
    }
}

New-Item -ItemType Directory -Path $hooksDirectory -Force | Out-Null
$escapedExecutable = $executablePath.Replace("'", "''")
$escapedEndpoint = $Endpoint.Replace("'", "''")
$command = "& '$escapedExecutable' hook-forward --endpoint '$escapedEndpoint' --timeout-ms $TimeoutMs"
$hooks = [ordered] @{}
foreach ($eventName in @('SessionStart', 'UserPromptSubmit', 'PreToolUse', 'PostToolUse', 'SubagentStart', 'SubagentStop', 'Stop')) {
    $hooks[$eventName] = @(
        [ordered] @{
            type = 'command'
            command = $command
            timeoutSec = 1
        }
    )
}

$config = [ordered] @{
    version = 1
    managed_by = $managedBy
    hooks = $hooks
}
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding utf8
