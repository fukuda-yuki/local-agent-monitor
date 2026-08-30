[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# This read-only diagnostic emits fixed booleans only. Process IDs and
# executable/package paths are used only in memory. Command lines and private
# App state are never read.
$result = [ordered]@{
    schema_version = "issue-92-desktop-process-diagnostic.v1"
    classification = "non_authoritative_package_process_tree_observation"
    observation = "not_observed"
    package_root_codex_process_observed = $false
    package_root_parent_process_relationship_observed = $false
    app_server_identity_observed = $false
    desktop_otel_execution_observed = $false
    diagnostic_authority = $false
    merge_authority = $false
    pid_values_emitted = $false
    path_values_emitted = $false
    hash_values_emitted = $false
    command_line_read = $false
    private_state_read = $false
}

try {
    $package = Get-AppxPackage -Name "OpenAI.Codex" |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1

    if ($null -ne $package -and -not [string]::IsNullOrWhiteSpace($package.InstallLocation)) {
        $packageRoot = [IO.Path]::GetFullPath($package.InstallLocation).
            TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $pathPrefix = $packageRoot + [IO.Path]::DirectorySeparatorChar
        $processes = @(Get-CimInstance -ClassName Win32_Process `
            -Filter "Name = 'codex.exe'" `
            -Property ProcessId, ParentProcessId, ExecutablePath)
        $packageProcesses = @($processes | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(
                $pathPrefix,
                [StringComparison]::OrdinalIgnoreCase
            )
        })

        $result.package_root_codex_process_observed = $packageProcesses.Count -gt 0

        foreach ($packageProcess in $packageProcesses) {
            $parent = Get-CimInstance -ClassName Win32_Process `
                -Filter ("ProcessId = {0}" -f [uint32]$packageProcess.ParentProcessId) `
                -Property ProcessId, ParentProcessId, ExecutablePath
            if ($null -eq $parent -or [string]::IsNullOrWhiteSpace($parent.ExecutablePath)) {
                continue
            }

            $parentPath = [IO.Path]::GetFullPath($parent.ExecutablePath)
            if ($parentPath.StartsWith($pathPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                $result.package_root_parent_process_relationship_observed = $true
                break
            }
        }
    }

    if ($result.package_root_codex_process_observed -or
        $result.package_root_parent_process_relationship_observed) {
        $result.observation = "observed"
    }
}
catch {
    # Exception text can contain machine-local paths or command-line material.
    $result.observation = "unavailable"
}

$result | ConvertTo-Json -Compress
