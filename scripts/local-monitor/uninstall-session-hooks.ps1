param(
    [string] $HomeDirectory = $HOME
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$configPath = Join-Path $HomeDirectory '.copilot\hooks\local-agent-monitor.json'
$managedBy = 'CopilotAgentObservability.LocalMonitor'
if (-not (Test-Path -LiteralPath $configPath)) {
    return
}

try {
    $existing = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
}
catch {
    throw 'hook_config_exists_unmanaged'
}

$marker = $existing.PSObject.Properties['managed_by']
if ($null -eq $marker -or
    $marker.Value -isnot [string] -or
    -not [string]::Equals(
        $marker.Value,
        $managedBy,
        [System.StringComparison]::Ordinal)) {
    throw 'hook_config_exists_unmanaged'
}

Remove-Item -LiteralPath $configPath -Force
