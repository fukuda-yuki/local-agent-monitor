Set-StrictMode -Version Latest

$script:DefaultTaskName = 'CopilotAgentObservability LocalMonitor'
$script:DefaultUrl = 'http://127.0.0.1:4320'
$script:RuntimeRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CopilotAgentObservability\LocalMonitor'
$script:DefaultInstallRoot = Join-Path $script:RuntimeRoot 'app'
$script:DefaultDbPath = Join-Path $script:RuntimeRoot 'raw-store.db'
$script:LogDirectory = Join-Path $script:RuntimeRoot 'logs'
$script:StatePath = Join-Path $script:RuntimeRoot 'local-monitor.state.json'
$script:PidPath = Join-Path $script:RuntimeRoot 'local-monitor.pid'
$script:PublishedExeName = 'CopilotAgentObservability.LocalMonitor.exe'
$script:UserEnvironmentVariables = @(
    'CAO_COLLECTION_PROFILE',
    'COPILOT_OTEL_ENABLED',
    'COPILOT_OTEL_CAPTURE_CONTENT',
    'COPILOT_OTEL_ENDPOINT',
    'OTEL_EXPORTER_OTLP_ENDPOINT',
    'OTEL_EXPORTER_OTLP_PROTOCOL',
    'OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT',
    'OTEL_RESOURCE_ATTRIBUTES'
)

function Get-LocalMonitorDefaultInstallRoot {
    return $script:DefaultInstallRoot
}

function Get-LocalMonitorRepoRoot {
    $scriptDirectory = Split-Path -Parent $PSCommandPath
    return (Resolve-Path (Join-Path $scriptDirectory '..\..')).Path
}

function Get-LocalMonitorProjectPath {
    $repoRoot = Get-LocalMonitorRepoRoot
    return Join-Path $repoRoot 'src\CopilotAgentObservability.LocalMonitor\CopilotAgentObservability.LocalMonitor.csproj'
}

function Get-LocalMonitorPublishedExePath {
    param(
        [string] $InstallRoot = $script:DefaultInstallRoot
    )

    return Join-Path $InstallRoot $script:PublishedExeName
}

function Get-LocalMonitorAppVersion {
    param(
        [string] $InstallRoot = $script:DefaultInstallRoot
    )

    $exePath = Get-LocalMonitorPublishedExePath -InstallRoot $InstallRoot
    if (-not (Test-Path -LiteralPath $exePath)) {
        return ''
    }

    try {
        return [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion
    }
    catch {
        return ''
    }
}

function Initialize-LocalMonitorRuntime {
    param(
        [string] $DbPath = $script:DefaultDbPath
    )

    $dbDirectory = Split-Path -Parent $DbPath
    New-Item -ItemType Directory -Force -Path $dbDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $script:LogDirectory | Out-Null
}

function Write-LocalMonitorLog {
    param(
        [Parameter(Mandatory)]
        [string] $Message
    )

    Initialize-LocalMonitorRuntime
    $stamp = Get-Date -Format o
    $logPath = Join-Path $script:LogDirectory ('wrapper-{0}.log' -f (Get-Date -Format yyyyMMdd))
    Add-Content -Path $logPath -Value ("{0} {1}" -f $stamp, $Message)
}

function Test-LocalMonitorLoopbackUrl {
    param(
        [Parameter(Mandatory)]
        [string] $Url
    )

    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref] $uri)) {
        return $false
    }

    if ($uri.Scheme -ne 'http') {
        return $false
    }

    return @('127.0.0.1', 'localhost', '::1', '[::1]') -contains $uri.Host
}

function Get-LocalMonitorPort {
    param(
        [Parameter(Mandatory)]
        [string] $Url
    )

    return ([Uri] $Url).Port
}

function Test-LocalMonitorHealth {
    param(
        [Parameter(Mandatory)]
        [string] $Url,

        [string] $Path = '/health/live'
    )

    try {
        return Invoke-WebRequest -UseBasicParsing -Uri ($Url.TrimEnd('/') + $Path) -TimeoutSec 3
    }
    catch {
        return $null
    }
}

function Test-LocalMonitorPortInUse {
    param(
        [Parameter(Mandatory)]
        [string] $Url
    )

    $uri = [Uri] $Url
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.BeginConnect($uri.Host, $uri.Port, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(500)) {
            return $false
        }

        $client.EndConnect($connect)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Save-LocalMonitorState {
    param(
        [Parameter(Mandatory)]
        [int] $ProcessId,

        [Parameter(Mandatory)]
        [string] $Url,

        [Parameter(Mandatory)]
        [string] $DbPath,

        [Parameter(Mandatory)]
        [string] $Mode,

        [string] $RepoRoot = '',

        [string] $InstallRoot = $script:DefaultInstallRoot,

        [string] $ExecutablePath = '',

        [bool] $SanitizedOnly
    )

    Initialize-LocalMonitorRuntime -DbPath $DbPath
    $state = [ordered] @{
        process_id = $ProcessId
        started_at = (Get-Date).ToString('o')
        url = $Url
        db_path = $DbPath
        mode = $Mode
        repo_root = $RepoRoot
        install_root = $InstallRoot
        executable_path = $ExecutablePath
        app_version = Get-LocalMonitorAppVersion -InstallRoot $InstallRoot
        sanitized_only = $SanitizedOnly
    }
    $state | ConvertTo-Json -Depth 3 | Set-Content -Path $script:StatePath -Encoding UTF8
    Set-Content -Path $script:PidPath -Value $ProcessId -Encoding ASCII
}

function Get-LocalMonitorState {
    if (-not (Test-Path -LiteralPath $script:StatePath)) {
        return $null
    }

    try {
        return Get-Content -Raw -LiteralPath $script:StatePath | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Get-LocalMonitorStateValue {
    param(
        $State,

        [Parameter(Mandatory)]
        [string] $Name,

        $DefaultValue = $null
    )

    if ($null -eq $State) {
        return $DefaultValue
    }

    $property = $State.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return $DefaultValue
    }

    return $property.Value
}

function Remove-LocalMonitorState {
    Remove-Item -LiteralPath $script:StatePath -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $script:PidPath -ErrorAction SilentlyContinue
}

function Remove-LocalMonitorInstall {
    param(
        [string] $InstallRoot = $script:DefaultInstallRoot,

        [switch] $AllowExternal
    )

    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        throw 'install_root_required'
    }

    $resolvedRuntimeRoot = [System.IO.Path]::GetFullPath($script:RuntimeRoot)
    $resolvedInstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
    if (-not $AllowExternal -and -not $resolvedInstallRoot.StartsWith($resolvedRuntimeRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'install_root_outside_runtime_root'
    }

    Remove-Item -LiteralPath $resolvedInstallRoot -Recurse -Force -ErrorAction SilentlyContinue
}

function Test-LocalMonitorProcess {
    param(
        [Parameter(Mandatory)]
        [int] $ProcessId
    )

    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $false
    }

    try {
        $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
        return $cim.CommandLine -like '*CopilotAgentObservability.LocalMonitor*'
    }
    catch {
        return $process.ProcessName -in @('dotnet', 'pwsh', 'powershell')
    }
}

function Get-LocalMonitorPowerShellPath {
    $pwsh = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if ($null -ne $pwsh) {
        return $pwsh.Source
    }

    $powershell = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if ($null -ne $powershell) {
        return $powershell.Source
    }

    throw 'powershell_not_found'
}

function Get-LocalMonitorTask {
    param(
        [string] $TaskName = $script:DefaultTaskName
    )

    return Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
}

function Set-LocalMonitorUserEnvironmentVariable {
    param(
        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $Value
    )

    [Environment]::SetEnvironmentVariable($Name, $Value, 'User')
}

function Clear-LocalMonitorUserEnvironmentVariable {
    param(
        [Parameter(Mandatory)]
        [string] $Name
    )

    [Environment]::SetEnvironmentVariable($Name, $null, 'User')
}

function Get-LocalMonitorUserEnvironmentVariable {
    param(
        [Parameter(Mandatory)]
        [string] $Name
    )

    return [Environment]::GetEnvironmentVariable($Name, 'User')
}

function Send-LocalMonitorEnvironmentChanged {
    $signature = @'
using System;
using System.Runtime.InteropServices;

public static class LocalMonitorEnvironmentBroadcast
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        UIntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);
}
'@

    $isWindowsVariable = Get-Variable -Name IsWindows -ErrorAction SilentlyContinue
    $isWindowsPlatform = ($PSVersionTable.PSEdition -eq 'Desktop') -or ($null -ne $isWindowsVariable -and $isWindowsVariable.Value)
    if ($isWindowsPlatform) {
        Add-Type -TypeDefinition $signature -ErrorAction SilentlyContinue
        $wmSettingChange = 0x001A # WM_SETTINGCHANGE
        $result = [UIntPtr]::Zero
        [void] [LocalMonitorEnvironmentBroadcast]::SendMessageTimeout(
            [IntPtr] 0xffff,
            $wmSettingChange,
            [UIntPtr]::Zero,
            'Environment',
            0x0002,
            5000,
            [ref] $result)
    }
}
