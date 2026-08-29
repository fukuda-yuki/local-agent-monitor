[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Completion', 'Nightly')]
    [string]$Lane,

    [ValidateSet('Windows', 'Linux')]
    [string]$Partition
)

$ErrorActionPreference = 'Stop'

if ($Lane -eq 'Nightly' -and [string]::IsNullOrWhiteSpace($Partition)) {
    throw 'Nightly validation requires -Partition Windows or -Partition Linux.'
}

if ($Lane -eq 'Completion' -and -not [string]::IsNullOrWhiteSpace($Partition)) {
    throw 'Completion validation does not accept -Partition.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$solution = Join-Path $repoRoot 'CopilotAgentObservability.slnx'
$playwrightInstaller = Join-Path $repoRoot 'scripts\test\install-playwright-chromium.ps1'
$partitionToken = if ([string]::IsNullOrWhiteSpace($Partition)) { 'none' } else { $Partition.ToLowerInvariant() }
$runName = '{0}-{1}-{2}-{3}' -f $Lane.ToLowerInvariant(), $partitionToken, (Get-Date -Format 'yyyyMMddTHHmmssfff'), $PID
$resultsRoot = Join-Path $repoRoot (Join-Path 'artifacts\validation' $runName)

$operatorOnlyExclusion = 'Issue158Lane!=WindowsOwnedSession&Issue158Lane!=LinuxExt4CurrentFile'
$completionFastFilter = "ValidationLane!=Nightly&ValidationLane!=CriticalSmoke&$operatorOnlyExclusion"
$criticalSmokeFilter = 'ValidationLane=CriticalSmoke'
$nightlyFilter = $operatorOnlyExclusion

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Invoke-TestPass {
    param(
        [Parameter(Mandatory)]
        [string]$Filter,

        [Parameter(Mandatory)]
        [string]$ResultsDirectory
    )

    New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
    Invoke-NativeCommand -FilePath 'dotnet' -Arguments @(
        'test',
        $solution,
        '--no-build',
        '--filter',
        $Filter,
        '--logger',
        'trx',
        '--results-directory',
        $ResultsDirectory
    )
}

function Install-PlaywrightChromium {
    param([switch]$WithDeps)

    $arguments = @($playwrightInstaller)
    if ($WithDeps) {
        $arguments += '-WithDeps'
    }

    Invoke-NativeCommand -FilePath 'pwsh' -Arguments $arguments
}

function Assert-CriticalSmokeResults {
    param(
        [Parameter(Mandatory)]
        [string]$ResultsDirectory
    )

    $trxFiles = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File -Recurse)
    if ($trxFiles.Count -eq 0) {
        throw 'Critical smoke validation produced no TRX files.'
    }

    $results = @(
        foreach ($trxFile in $trxFiles) {
            [xml]$document = Get-Content -LiteralPath $trxFile.FullName -Raw
            $document.SelectNodes("//*[local-name()='UnitTestResult']")
        }
    )

    $passed = @($results | Where-Object { $_.GetAttribute('outcome') -eq 'Passed' }).Count
    $failed = @($results | Where-Object { $_.GetAttribute('outcome') -eq 'Failed' }).Count
    $skipped = @($results | Where-Object { $_.GetAttribute('outcome') -eq 'NotExecuted' }).Count
    $executed = $results.Count - $skipped

    if ($executed -ne 2 -or $passed -ne 2 -or $failed -ne 0 -or $skipped -ne 0 -or $results.Count -ne 2) {
        throw "Critical smoke validation requires exactly 2 executed, 2 passed, 0 failed, and 0 skipped tests; observed executed=$executed passed=$passed failed=$failed skipped=$skipped total=$($results.Count)."
    }
}

New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null
Invoke-NativeCommand -FilePath 'dotnet' -Arguments @('build', $solution)

if ($Lane -eq 'Completion') {
    Invoke-TestPass -Filter $completionFastFilter -ResultsDirectory (Join-Path $resultsRoot 'fast')
    Install-PlaywrightChromium
    $criticalResults = Join-Path $resultsRoot 'critical-smoke'
    Invoke-TestPass -Filter $criticalSmokeFilter -ResultsDirectory $criticalResults
    Assert-CriticalSmokeResults -ResultsDirectory $criticalResults
}
else {
    Install-PlaywrightChromium -WithDeps:($Partition -eq 'Linux')
    Invoke-TestPass -Filter $nightlyFilter -ResultsDirectory (Join-Path $resultsRoot 'nightly')
}

Write-Output "validation_lane=$Lane"
if ($Lane -eq 'Nightly') {
    Write-Output "validation_partition=$Partition"
}
Write-Output "validation_results=$resultsRoot"
