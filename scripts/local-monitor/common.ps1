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

function Test-LocalMonitorPricingRegistryOverrideCount {
    param(
        [string[]] $PricingRegistryOverride = @()
    )

    return @($PricingRegistryOverride).Count -le 8
}

function ConvertTo-LocalMonitorPowerShellSingleQuotedLiteral {
    param(
        [Parameter(Mandatory)]
        [string] $Value
    )

    return "'{0}'" -f $Value.Replace("'", "''")
}

function New-LocalMonitorStartupTaskArgument {
    param(
        [Parameter(Mandatory)]
        [string] $StartScript,

        [Parameter(Mandatory)]
        [string] $Url,

        [Parameter(Mandatory)]
        [string] $DbPath,

        [Parameter(Mandatory)]
        [string] $Mode,

        [Parameter(Mandatory)]
        [string] $InstallRoot,

        [switch] $SanitizedOnly,

        [string[]] $PricingRegistryOverride = @()
    )

    $command = "& {0} -Url {1} -DbPath {2} -Mode {3} -InstallRoot {4} -NoBrowser -WaitReady" -f `
        (ConvertTo-LocalMonitorPowerShellSingleQuotedLiteral -Value $StartScript), `
        (ConvertTo-LocalMonitorPowerShellSingleQuotedLiteral -Value $Url), `
        (ConvertTo-LocalMonitorPowerShellSingleQuotedLiteral -Value $DbPath), `
        (ConvertTo-LocalMonitorPowerShellSingleQuotedLiteral -Value $Mode), `
        (ConvertTo-LocalMonitorPowerShellSingleQuotedLiteral -Value $InstallRoot)
    if ($SanitizedOnly) {
        $command += ' -SanitizedOnly'
    }

    $literals = @()
    foreach ($override in @($PricingRegistryOverride)) {
        $literals += ConvertTo-LocalMonitorPowerShellSingleQuotedLiteral -Value $override
    }
    if ($literals.Count -gt 0) {
        $command += ' -PricingRegistryOverride @(' + ($literals -join ',') + ')'
    }

    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    return '-NoProfile -ExecutionPolicy Bypass -EncodedCommand {0}' -f $encoded
}

function Start-LocalMonitorProcess {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,

        [Parameter(Mandatory)]
        [string] $WorkingDirectory,

        [Parameter(Mandatory)]
        [string[]] $ArgumentList,

        [Parameter(Mandatory)]
        [string] $StandardOutputPath,

        [Parameter(Mandatory)]
        [string] $StandardErrorPath
    )

    $serializedArgumentList = ConvertTo-LocalMonitorWindowsCommandLine -ArgumentList $ArgumentList
    return Start-Process `
        -FilePath $FilePath `
        -ArgumentList $serializedArgumentList `
        -WorkingDirectory $WorkingDirectory `
        -WindowStyle Hidden `
        -RedirectStandardOutput $StandardOutputPath `
        -RedirectStandardError $StandardErrorPath `
        -PassThru
}

function ConvertTo-LocalMonitorWindowsCommandLine {
    param(
        [Parameter(Mandatory)]
        [string[]] $ArgumentList
    )

    $serialized = @()
    foreach ($argument in $ArgumentList) {
        $value = [string] $argument
        if ($value.Length -eq 0) {
            $serialized += '""'
            continue
        }
        if ($value -notmatch '[\s"]') {
            $serialized += $value
            continue
        }

        $builder = New-Object System.Text.StringBuilder
        [void] $builder.Append([char] 34)
        $backslashCount = 0
        foreach ($character in $value.ToCharArray()) {
            if ($character -eq [char] 92) {
                $backslashCount++
                continue
            }
            if ($character -eq [char] 34) {
                if ($backslashCount -gt 0) {
                    [void] $builder.Append([char] 92, $backslashCount * 2)
                }
                [void] $builder.Append([char] 92)
                [void] $builder.Append([char] 34)
                $backslashCount = 0
                continue
            }
            if ($backslashCount -gt 0) {
                [void] $builder.Append([char] 92, $backslashCount)
                $backslashCount = 0
            }
            [void] $builder.Append($character)
        }
        if ($backslashCount -gt 0) {
            [void] $builder.Append([char] 92, $backslashCount * 2)
        }
        [void] $builder.Append([char] 34)
        $serialized += $builder.ToString()
    }

    return $serialized -join ' '
}

function Get-LocalMonitorTaskPricingRegistryOverrideState {
    param(
        $Task
    )

    if ($null -eq $Task) {
        return [pscustomobject] @{ State = 'absent'; Count = $null }
    }

    $actions = @($Task.Actions)
    if ($actions.Count -ne 1 -or $null -eq $actions[0]) {
        return [pscustomobject] @{ State = 'unknown'; Count = $null }
    }

    $argumentsProperty = $actions[0].PSObject.Properties['Arguments']
    if ($null -eq $argumentsProperty -or [string]::IsNullOrWhiteSpace([string] $argumentsProperty.Value)) {
        return [pscustomobject] @{ State = 'unknown'; Count = $null }
    }

    $match = [regex]::Match([string] $argumentsProperty.Value, '\A-NoProfile -ExecutionPolicy Bypass -EncodedCommand ([A-Za-z0-9+/]+={0,2})\z')
    if (-not $match.Success) {
        return [pscustomobject] @{ State = 'unknown'; Count = $null }
    }

    try {
        $command = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($match.Groups[1].Value))
    }
    catch {
        return [pscustomobject] @{ State = 'unknown'; Count = $null }
    }

    $prefix = [regex]::Match($command, "\A& '(?:[^']|'')*' -Url '(?:[^']|'')*' -DbPath '(?:[^']|'')*' -Mode '(?:[^']|'')*' -InstallRoot '(?:[^']|'')*' -NoBrowser -WaitReady")
    if (-not $prefix.Success) {
        return [pscustomobject] @{ State = 'unknown'; Count = $null }
    }

    $remaining = $command.Substring($prefix.Length)
    if ($remaining.StartsWith(' -SanitizedOnly')) {
        $remaining = $remaining.Substring(' -SanitizedOnly'.Length)
    }
    if ($remaining.Length -eq 0) {
        return [pscustomobject] @{ State = 'absent'; Count = $null }
    }

    $marker = ' -PricingRegistryOverride @('
    if (-not $remaining.StartsWith($marker) -or -not $remaining.EndsWith(')')) {
        return [pscustomobject] @{ State = 'unknown'; Count = $null }
    }

    $members = $remaining.Substring($marker.Length, $remaining.Length - $marker.Length - 1)
    $position = 0
    $count = 0
    while ($position -lt $members.Length) {
        if ($members[$position] -ne [char] 39) {
            return [pscustomobject] @{ State = 'unknown'; Count = $null }
        }
        $count++
        $position++
        while ($position -lt $members.Length) {
            if ($members[$position] -ne [char] 39) {
                $position++
                continue
            }
            if ($position + 1 -lt $members.Length -and $members[$position + 1] -eq [char] 39) {
                $position += 2
                continue
            }
            $position++
            break
        }
        if ($position -eq $members.Length) {
            break
        }
        if ($members[$position] -ne [char] 44) {
            return [pscustomobject] @{ State = 'unknown'; Count = $null }
        }
        $position++
    }

    if ($count -eq 0 -or $count -gt 8) {
        return [pscustomobject] @{ State = 'unknown'; Count = $null }
    }

    return [pscustomobject] @{ State = 'present'; Count = $count }
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
