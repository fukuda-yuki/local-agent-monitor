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
$repositoryPolicyGuard = Join-Path $repoRoot 'scripts\test\assert-repository-policy.ps1'
$validationContract = Join-Path $repoRoot 'scripts\test\assert-validation-contract.ps1'
$partitionToken = if ([string]::IsNullOrWhiteSpace($Partition)) { 'none' } else { $Partition.ToLowerInvariant() }
$runName = '{0}-{1}-{2}-{3}' -f $Lane.ToLowerInvariant(), $partitionToken, (Get-Date -Format 'yyyyMMddTHHmmssfff'), $PID
$resultsRoot = Join-Path $repoRoot (Join-Path 'artifacts\validation' $runName)
$completionStopwatch = if ($Lane -eq 'Completion') {
    [System.Diagnostics.Stopwatch]::StartNew()
}
else {
    $null
}

$operatorOnlyExclusion = 'Issue158Lane!=WindowsOwnedSession&Issue158Lane!=LinuxExt4CurrentFile'
$completionFastFilter = "ValidationLane!=Nightly&ValidationLane!=CriticalSmoke&$operatorOnlyExclusion"
$criticalSmokeFilter = 'ValidationLane=CriticalSmoke'
$nightlyFilter = $operatorOnlyExclusion
$criticalSmokeExpectedFqns = @(
    'CopilotAgentObservability.LocalMonitor.Tests.LocalMonitorV1RepositoryComparePlaywrightTests.ImmutableCompareRendersNineSectionsRowsEvidenceAndResponsiveTableWithoutRecompute'
    'CopilotAgentObservability.LocalMonitor.Tests.LocalMonitorV1SessionExplorerPlaywrightTests.ComparePreviewCreatesFromTransientOrderedCohortsAndNavigatesOnlyByServerLocation'
)

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

New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null
Invoke-NativeCommand -FilePath 'pwsh' -Arguments @(
    '-NoProfile',
    '-File',
    $repositoryPolicyGuard,
    '-RepositoryRoot',
    $repoRoot
)
Invoke-NativeCommand -FilePath 'dotnet' -Arguments @('build', $solution)

if ($Lane -eq 'Completion') {
    Invoke-TestPass -Filter $completionFastFilter -ResultsDirectory (Join-Path $resultsRoot 'fast')
    Install-PlaywrightChromium
    $criticalResults = Join-Path $resultsRoot 'critical-smoke'
    Invoke-TestPass -Filter $criticalSmokeFilter -ResultsDirectory $criticalResults
    Invoke-NativeCommand -FilePath 'pwsh' -Arguments @(
        '-NoProfile',
        '-File',
        $validationContract,
        '-Mode',
        'CriticalSmoke',
        '-ResultsDirectory',
        $criticalResults,
        '-ExpectedFqns',
        ($criticalSmokeExpectedFqns -join ';')
    )
    Invoke-NativeCommand -FilePath 'pwsh' -Arguments @(
        '-NoProfile',
        '-File',
        $validationContract,
        '-Mode',
        'CompletionBudget',
        '-ElapsedSeconds',
        $completionStopwatch.Elapsed.TotalSeconds.ToString(
            'R',
            [System.Globalization.CultureInfo]::InvariantCulture)
    )
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
