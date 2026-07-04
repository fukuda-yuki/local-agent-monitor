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

$values = [ordered] @{
    CAO_COLLECTION_PROFILE = 'raw-local-receiver'
    COPILOT_OTEL_ENABLED = 'true'
    COPILOT_OTEL_CAPTURE_CONTENT = 'true'
    COPILOT_OTEL_ENDPOINT = $Url
    OTEL_EXPORTER_OTLP_ENDPOINT = $Url
    OTEL_EXPORTER_OTLP_PROTOCOL = 'http/protobuf'
    OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT = 'true'
    OTEL_RESOURCE_ATTRIBUTES = 'team.id=platform,department=engineering,experiment.id=baseline'
}

foreach ($entry in $values.GetEnumerator()) {
    $existing = Get-LocalMonitorUserEnvironmentVariable -Name $entry.Key
    if (-not [string]::IsNullOrEmpty($existing) -and $existing -ne $entry.Value -and -not $Force) {
        Write-Error ("user_env_already_set:{0}" -f $entry.Key)
        exit 1
    }
}

foreach ($entry in $values.GetEnumerator()) {
    if ($DryRun) {
        Write-Output ("set user env: {0}={1}" -f $entry.Key, $entry.Value)
        continue
    }

    Set-LocalMonitorUserEnvironmentVariable -Name $entry.Key -Value $entry.Value
}

if (-not $DryRun) {
    Send-LocalMonitorEnvironmentChanged
    Write-Output 'installed user environment'
    Write-Output 'restart VS Code, terminals, and Copilot CLI processes to inherit the updated user environment.'
}

exit 0
