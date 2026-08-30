[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$resultsRoot = $null
Push-Location $repositoryRoot
try {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-scan-outputs.ps1')
    $scannerExit = $LASTEXITCODE
    Write-Output ('matrix_suite=scanner-self-test exit={0}' -f $scannerExit)
    if ($scannerExit -ne 0) { exit $scannerExit }

    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-validation-contract.ps1')
    $contractExit = $LASTEXITCODE
    Write-Output ('matrix_suite=semantic-contract-self-test exit={0}' -f $contractExit)
    if ($contractExit -ne 0) { exit $contractExit }

    $manifestPath = Join-Path $PSScriptRoot 'automated-matrix.v1.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Write-Output 'automated_matrix_result=ERROR reason=manifest_missing'
        exit 2
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 20
    if ($manifest.schema_version -ne 'validation-automated-matrix.v1') {
        Write-Output 'automated_matrix_result=ERROR reason=manifest_version_unsupported'
        exit 2
    }

    $projectFilters = @{}
    foreach ($row in @($manifest.rows)) {
        foreach ($group in @($row.test_groups)) {
            $project = [string]$group.project
            if (-not $projectFilters.ContainsKey($project)) {
                $projectFilters[$project] = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            }
            foreach ($filter in @($group.filters)) {
                [void]$projectFilters[$project].Add([string]$filter)
            }
        }
    }

    $localMonitorProject = 'tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj'
    [void]$projectFilters[$localMonitorProject].Add('Issue91ValidationContractTests')

    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
    if (-not (Test-Path -LiteralPath $artifactsRoot -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $artifactsRoot)
    }
    $resultsRoot = Join-Path $artifactsRoot ('issue91-matrix-{0}' -f [Guid]::NewGuid().ToString('N'))
    if (-not $resultsRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Output 'automated_matrix_result=ERROR reason=results_path_outside_artifacts'
        exit 2
    }
    [void](New-Item -ItemType Directory -Path $resultsRoot)

    $projectIndex = 0
    foreach ($project in @($projectFilters.Keys | Sort-Object)) {
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
            Write-Output ('automated_matrix_result=ERROR reason=project_missing project={0}' -f $project)
            exit 2
        }

        $discoveryOutput = @(& dotnet test $project --list-tests 2>&1)
        $discoveryExit = $LASTEXITCODE
        Write-Output ('matrix_discovery={0} exit={1}' -f $project, $discoveryExit)
        if ($discoveryExit -ne 0) { exit $discoveryExit }
        $discoveryText = $discoveryOutput -join [Environment]::NewLine

        foreach ($filter in @($projectFilters[$project] | Sort-Object)) {
            if ($discoveryText.IndexOf($filter, [StringComparison]::Ordinal) -lt 0) {
                Write-Output ('automated_matrix_result=ERROR reason=filter_discovered_zero project={0} filter={1}' -f $project, $filter)
                exit 2
            }
        }

        $filterExpression = @($projectFilters[$project] | Sort-Object | ForEach-Object { 'FullyQualifiedName~{0}' -f $_ }) -join '|'
        $trxName = 'matrix-{0}.trx' -f $projectIndex
        & dotnet test $project --filter $filterExpression --logger ('trx;LogFileName={0}' -f $trxName) --results-directory $resultsRoot
        $suiteExit = $LASTEXITCODE
        Write-Output ('matrix_suite={0} exit={1}' -f $project, $suiteExit)
        if ($suiteExit -ne 0) { exit $suiteExit }

        $trxPath = Join-Path $resultsRoot $trxName
        if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
            Write-Output ('automated_matrix_result=ERROR reason=trx_missing project={0}' -f $project)
            exit 2
        }
        [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
        $counters = $trx.TestRun.ResultSummary.Counters
        $total = [int]$counters.total
        $executed = [int]$counters.executed
        $passed = [int]$counters.passed
        $failed = [int]$counters.failed
        $notExecuted = [int]$counters.notExecuted
        Write-Output ('matrix_counts={0} total={1} executed={2} passed={3} failed={4} not_executed={5}' -f $project, $total, $executed, $passed, $failed, $notExecuted)
        if ($total -le 0 -or $executed -ne $total -or $passed -ne $total -or $failed -ne 0 -or $notExecuted -ne 0) {
            Write-Output ('automated_matrix_result=ERROR reason=incomplete_or_skipped_tests project={0}' -f $project)
            exit 2
        }
        $projectIndex++
    }
}
finally {
    if ($null -ne $resultsRoot -and (Test-Path -LiteralPath $resultsRoot -PathType Container)) {
        Get-ChildItem -LiteralPath $resultsRoot -File | Remove-Item -Force
        Remove-Item -LiteralPath $resultsRoot -Force
    }
    Pop-Location
}

Write-Output 'automated_matrix_result=PASS'
exit 0
