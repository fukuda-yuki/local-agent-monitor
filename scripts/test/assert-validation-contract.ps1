[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('CriticalSmoke', 'CompletionBudget')]
    [string]$Mode,

    [string]$ResultsDirectory,

    [double]$ElapsedSeconds
)

$ErrorActionPreference = 'Stop'

if ($Mode -eq 'CompletionBudget') {
    if ($ElapsedSeconds -gt 1800) {
        throw "Completion validation exceeded its 30 minute budget; elapsed_seconds=$ElapsedSeconds."
    }

    Write-Output "completion_elapsed_seconds=$ElapsedSeconds"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    throw 'Critical smoke validation requires a results directory.'
}

$expected = @(
    'CopilotAgentObservability.LocalMonitor.Tests.LocalMonitorV1RepositoryComparePlaywrightTests.ImmutableCompareRendersNineSectionsRowsEvidenceAndResponsiveTableWithoutRecompute'
    'CopilotAgentObservability.LocalMonitor.Tests.LocalMonitorV1SessionExplorerPlaywrightTests.ComparePreviewCreatesFromTransientOrderedCohortsAndNavigatesOnlyByServerLocation'
)
$trxFiles = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File -Recurse)
if ($trxFiles.Count -eq 0) {
    throw 'Critical smoke validation produced no TRX files.'
}

$rows = @(
    foreach ($trxFile in $trxFiles) {
        [xml]$document = Get-Content -LiteralPath $trxFile.FullName -Raw
        $definitions = @{}
        foreach ($unitTest in $document.SelectNodes("//*[local-name()='UnitTest']")) {
            $method = $unitTest.SelectSingleNode("./*[local-name()='TestMethod']")
            if ($null -eq $method) {
                continue
            }

            $definitions[$unitTest.GetAttribute('id')] =
                '{0}.{1}' -f $method.GetAttribute('className'), $method.GetAttribute('name')
        }

        foreach ($result in $document.SelectNodes("//*[local-name()='UnitTestResult']")) {
            $testId = $result.GetAttribute('testId')
            if (-not $definitions.ContainsKey($testId)) {
                throw "Critical smoke TRX result has no matching test definition; test_id=$testId."
            }

            [pscustomobject]@{
                Fqn = $definitions[$testId]
                Outcome = $result.GetAttribute('outcome')
            }
        }
    }
)

$passed = @($rows | Where-Object Outcome -eq 'Passed').Count
$failed = @($rows | Where-Object Outcome -eq 'Failed').Count
$skipped = @($rows | Where-Object Outcome -eq 'NotExecuted').Count
$executed = $rows.Count - $skipped
if ($executed -ne 2 -or $passed -ne 2 -or $failed -ne 0 -or $skipped -ne 0 -or $rows.Count -ne 2) {
    throw "Critical smoke validation requires exactly 2 executed, 2 passed, 0 failed, and 0 skipped tests; observed executed=$executed passed=$passed failed=$failed skipped=$skipped total=$($rows.Count)."
}

$identityCounts = @{}
foreach ($row in $rows) {
    $identityCounts[$row.Fqn] = 1 + ($identityCounts[$row.Fqn] ?? 0)
}
$missingOrDuplicated = @($expected | Where-Object { ($identityCounts[$_] ?? 0) -ne 1 })
$unexpected = @($identityCounts.Keys | Where-Object { $_ -notin $expected })
if ($missingOrDuplicated.Count -ne 0 -or $unexpected.Count -ne 0) {
    throw "Critical smoke validation requires the exact critical smoke identities once each; missing_or_duplicated=$($missingOrDuplicated -join ',') unexpected=$($unexpected -join ',')."
}

Write-Output 'critical_smoke_exact_identities=passed'
