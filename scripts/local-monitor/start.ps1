param(
    [string] $Url = 'http://127.0.0.1:4320',
    [string] $DbPath,
    [ValidateSet('DotnetRun', 'Published')]
    [string] $Mode = 'DotnetRun',
    [switch] $SanitizedOnly,
    [switch] $NoBrowser = $true,
    [switch] $WaitReady = $true,
    [int] $TimeoutSeconds = 30
)

. "$PSScriptRoot\common.ps1"

if ([string]::IsNullOrWhiteSpace($DbPath)) {
    $DbPath = $script:DefaultDbPath
}

if (-not (Test-LocalMonitorLoopbackUrl -Url $Url)) {
    Write-Error 'non_loopback_url'
    exit 1
}

if ($Mode -eq 'Published') {
    Write-Error 'published_mode_not_implemented'
    exit 1
}

Initialize-LocalMonitorRuntime -DbPath $DbPath
$repoRoot = Get-LocalMonitorRepoRoot
$projectPath = Get-LocalMonitorProjectPath
if (-not (Test-Path -LiteralPath $projectPath)) {
    Write-Error 'monitor_project_not_found'
    exit 1
}

$live = Test-LocalMonitorHealth -Url $Url -Path '/health/live'
if ($null -ne $live -and [int] $live.StatusCode -eq 200) {
    Write-LocalMonitorLog "already_running url=$Url"
    Write-Output "already_running"
    exit 0
}

if (Test-LocalMonitorPortInUse -Url $Url) {
    Write-LocalMonitorLog "port_already_in_use url=$Url"
    Write-Error 'port_already_in_use'
    exit 1
}

$arguments = @(
    'run',
    '--project',
    $projectPath,
    '--',
    '--db',
    $DbPath,
    '--url',
    $Url
)
if ($SanitizedOnly) {
    $arguments += '--sanitized-only'
}

$stdoutPath = Join-Path $script:LogDirectory 'local-monitor.stdout.log'
$stderrPath = Join-Path $script:LogDirectory 'local-monitor.stderr.log'
$process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $repoRoot -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
Save-LocalMonitorState -ProcessId $process.Id -Url $Url -DbPath $DbPath -Mode 'dotnet-run' -RepoRoot $repoRoot -SanitizedOnly:$SanitizedOnly.IsPresent
Write-LocalMonitorLog "start process_id=$($process.Id) url=$Url sanitized_only=$($SanitizedOnly.IsPresent)"

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
    Start-Sleep -Milliseconds 500
    $live = Test-LocalMonitorHealth -Url $Url -Path '/health/live'
    if ($null -ne $live -and [int] $live.StatusCode -eq 200) {
        if (-not $WaitReady) {
            Write-Output "started"
            exit 0
        }

        $ready = Test-LocalMonitorHealth -Url $Url -Path '/health/ready'
        if ($null -eq $ready) {
            continue
        }

        $readyBody = $ready.Content | ConvertFrom-Json
        if ($readyBody.status -eq 'ready' -or $readyBody.status -eq 'degraded') {
            Write-Output ("started {0}" -f $readyBody.status)
            exit 0
        }

        Write-LocalMonitorLog "health_ready_not_ready status=$($readyBody.status)"
        Write-Error 'health_ready_not_ready'
        exit 2
    }
} while ((Get-Date) -lt $deadline)

Write-LocalMonitorLog "monitor_start_timeout url=$Url"
Write-Error 'monitor_start_timeout'
exit 1
