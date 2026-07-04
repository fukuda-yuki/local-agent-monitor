param(
    [string] $Url = 'http://127.0.0.1:4320',
    [switch] $Force,
    [switch] $DryRun
)

. "$PSScriptRoot\common.ps1"

if (-not (Test-LocalMonitorLoopbackUrl -Url $Url)) {
    Write-Error 'non_loopback_url'
    exit 1
}

$managedValues = [ordered] @{
    CAO_COLLECTION_PROFILE = 'raw-local-receiver'
    COPILOT_OTEL_ENABLED = 'true'
    COPILOT_OTEL_CAPTURE_CONTENT = 'true'
    COPILOT_OTEL_ENDPOINT = $Url
    OTEL_EXPORTER_OTLP_ENDPOINT = $Url
    OTEL_EXPORTER_OTLP_PROTOCOL = 'http/protobuf'
    OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT = 'true'
    OTEL_RESOURCE_ATTRIBUTES = 'team.id=platform,department=engineering,experiment.id=baseline'
}

# Managed names include CAO_COLLECTION_PROFILE, COPILOT_OTEL_ENABLED,
# COPILOT_OTEL_CAPTURE_CONTENT, COPILOT_OTEL_ENDPOINT,
# OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL,
# OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT, and
# OTEL_RESOURCE_ATTRIBUTES.
foreach ($entry in $managedValues.GetEnumerator()) {
    $existing = Get-LocalMonitorUserEnvironmentVariable -Name $entry.Key
    if (-not [string]::IsNullOrEmpty($existing) -and $existing -ne $entry.Value -and -not $Force) {
        Write-Error ("user_env_value_differs:{0}" -f $entry.Key)
        exit 1
    }
}

foreach ($name in $script:UserEnvironmentVariables) {
    if ($DryRun) {
        Write-Output ("clear user env: {0}" -f $name)
        continue
    }

    Clear-LocalMonitorUserEnvironmentVariable -Name $name
}

if (-not $DryRun) {
    Send-LocalMonitorEnvironmentChanged
    Write-Output 'removed user environment'
    Write-Output 'restart VS Code, terminals, and Copilot CLI processes to drop the removed user environment.'
}

exit 0
