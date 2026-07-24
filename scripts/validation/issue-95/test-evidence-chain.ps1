[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $VerifierPath,
    [Parameter(Mandatory)][string] $MatrixValidatorPath,
    [Parameter(Mandatory)][string] $MatrixFixturePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$evidencePaths = @(
    'docs/specifications/contracts/cost-analytics/v1/issue-91-validation-handoff.json',
    'docs/sprints/issue-95-cost-analytics/README.md',
    'docs/sprints/issue-95-cost-analytics/validation-matrix.json',
    'docs/sprints/issue-95-cost-analytics/artifact-checksums.json',
    'docs/sprints/issue-95-cost-analytics/live-validation.md')
$manifestPaths = @($evidencePaths[0], $evidencePaths[1], $evidencePaths[2], $evidencePaths[4])

function New-FixtureRoot {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('issue-95-evidence-chain-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    & git -C $root init -q
    & git -C $root config user.email 'fixture@example.invalid'
    & git -C $root config user.name 'Issue95Fixture'
    & git -C $root config core.autocrlf false
    Write-FixtureFile $root 'scripts/validation/issue-91/validate-matrix.ps1' ([IO.File]::ReadAllText($MatrixValidatorPath))
    & git -C $root add .
    & git -C $root commit -q -m matrix-prep
    & git -C $root commit --allow-empty -q -m candidate
    return $root
}

function Write-FixtureFile([string] $Root, [string] $RelativePath, [string] $Content) {
    $path = Join-Path $Root $RelativePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, $Content, [Text.UTF8Encoding]::new($false))
}

function Commit-Fixture([string] $Root, [string] $Message) {
    & git -C $Root add .
    & git -C $Root commit -q -m $Message
    return (& git -C $Root rev-parse HEAD).Trim()
}

function Add-EvidencePaths([string] $Root) {
    foreach ($path in $evidencePaths) { Write-FixtureFile $Root $path '{"fixture":true}' }
}

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-PositiveEvidence([string] $Root, [string] $Candidate) {
    $matrix = Get-Content -LiteralPath $MatrixFixturePath -Raw | ConvertFrom-Json -Depth 50
    $matrixPrep = (& git -C $Root rev-parse ($Candidate + '^')).Trim()
    $matrix.matrix_prep_sha = $matrixPrep
    $matrix.final_validation_sha = $Candidate
    foreach ($index in 0..2) {
        $matrix.active_rows[$index].row_id = @('91-A-095', '91-S-095', '91-L-095')[$index]
        $matrix.active_rows[$index].validation_sha = $Candidate
        $matrix.active_rows[$index].versions.candidate = $Candidate
    }
    $matrix.active_rows[0].classification = 'passed'
    $matrix.active_rows[1].classification = 'passed'
    $matrix.active_rows[2].classification = 'blocked_external'
    $matrix.active_rows[2].severity = 'high'
    $matrix.release_decision.external_blockers[0].row_id = '91-L-095'
    Write-FixtureFile $Root $evidencePaths[2] ($matrix | ConvertTo-Json -Depth 50)
    Write-FixtureFile $Root $evidencePaths[0] ('{"evidence_binding":{"state":"finalized","matrix_prep_sha":"' + $matrix.matrix_prep_sha + '","final_validation_sha":"' + $Candidate + '"}}')
    Write-FixtureFile $Root $evidencePaths[1] '# Issue #95 evidence fixture'
    $liveValidation = @"
# Issue #95 Live Validation

## Candidate binding
matrix_prep_sha: $matrixPrep
final_validation_sha: $Candidate

## Required commands and results
required_command_count: 4
required_command_failures: 0
command_result: dotnet build CopilotAgentObservability.slnx | exit=0 | result=passed
command_result: pwsh scripts\test\install-playwright-chromium.ps1 | exit=0 | result=passed
command_result: dotnet test CopilotAgentObservability.slnx | exit=0 | result=passed
command_result: pwsh scripts\validation\issue-95\test-evidence-chain.ps1 -VerifierPath scripts\validation\issue-95\verify-evidence-chain.ps1 -MatrixValidatorPath scripts\validation\issue-91\validate-matrix.ps1 -MatrixFixturePath docs\sprints\issue-75-historical-analysis\validation-matrix.json | exit=0 | result=passed

## RED and failure history
red_failure_count: 1
red_failure: matrix_prep_false_positive | command=pwsh scripts\validation\issue-95\test-evidence-chain.ps1 | observed=failed:unrelated prep SHA accepted | corrected_by=$Candidate

## OS-specific security coverage
validation_os: windows
applicable_os_security_tests: file_symlink=passed,directory_symlink=passed,windows_open_mutation=passed,windows_normalized_segment=passed
applicable_security_prerequisite_skips: 0
not_applicable_os_security_tests: linux_fifo=not_applicable

## Live validation
91-L-095: blocked_external
unverified_capability: genuine provider mapping and budget readback
"@
    Write-FixtureFile $Root $evidencePaths[4] $liveValidation
    $artifacts = foreach ($path in $manifestPaths) {
        [ordered]@{ path = $path; sha256 = Get-Sha256 (Join-Path $Root $path) }
    }
    $manifest = [ordered]@{
        schema_version = 'issue-95-artifact-checksums.v1'
        candidate_base = $Candidate
        algorithm = 'SHA-256'
        verification_date = '2026-07-24'
        artifacts = @($artifacts)
    }
    Write-FixtureFile $Root $evidencePaths[3] ($manifest | ConvertTo-Json -Depth 10)
}

function Write-Attestation([string] $Root, [string] $Candidate, [string] $Evidence) {
    $artifacts = foreach ($path in @($manifestPaths + $evidencePaths[3])) {
        [ordered]@{ path = $path; sha256 = Get-Sha256 (Join-Path $Root $path) }
    }
    $attestation = [ordered]@{
        schema_version = 'evidence-attestation.v1'
        issue = 95
        functional_candidate_sha = $Candidate
        evidence_materialization_sha = $Evidence
        evidence_materialization_parent_sha = $Candidate
        relationship = 'fixture'
        checksum_algorithm = 'SHA-256'
        artifacts_at_evidence_materialization = @($artifacts)
        verification = [ordered]@{ matrix_validator = 'passed' }
        publication = [ordered]@{ local_only = $true }
    }
    Write-FixtureFile $Root 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' ($attestation | ConvertTo-Json -Depth 10)
}

function Assert-Rejected([string] $Root, [string] $Candidate, [string] $Evidence, [string] $Attestation, [string] $ExpectedCode) {
    $failed = $false
    try {
        $output = & $VerifierPath -CandidateSha $Candidate -EvidenceSha $Evidence -AttestationSha $Attestation -RepositoryRoot $Root 2>&1 | Out-String
        $failed = $LASTEXITCODE -ne 0
    }
    catch {
        $failed = $true
        $output = $_.Exception.Message
    }
    if (-not $failed -or $output -notmatch [Regex]::Escape($ExpectedCode)) {
        throw ('expected_rejection_missing={0} output={1}' -f $ExpectedCode, $output)
    }
}

$roots = [Collections.Generic.List[string]]::new()
try {
    $wrongAncestryRoot = New-FixtureRoot
    $roots.Add($wrongAncestryRoot)
    $candidate = (& git -C $wrongAncestryRoot rev-parse HEAD).Trim()
    & git -C $wrongAncestryRoot commit --allow-empty -q -m evidence
    $evidence = (& git -C $wrongAncestryRoot rev-parse HEAD).Trim()
    & git -C $wrongAncestryRoot commit --allow-empty -q -m attestation
    $attestation = (& git -C $wrongAncestryRoot rev-parse HEAD).Trim()
    Assert-Rejected $wrongAncestryRoot $attestation $evidence $attestation 'evidence_parent_not_candidate'

    $unexpectedDiffRoot = New-FixtureRoot
    $roots.Add($unexpectedDiffRoot)
    $candidate = (& git -C $unexpectedDiffRoot rev-parse HEAD).Trim()
    Add-EvidencePaths $unexpectedDiffRoot
    Write-FixtureFile $unexpectedDiffRoot 'unexpected.txt' 'unexpected'
    $evidence = Commit-Fixture $unexpectedDiffRoot 'evidence'
    Write-FixtureFile $unexpectedDiffRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $unexpectedDiffRoot 'attestation'
    Assert-Rejected $unexpectedDiffRoot $candidate $evidence $attestation 'candidate_to_evidence_diff_paths_invalid'

    $attestationDiffRoot = New-FixtureRoot
    $roots.Add($attestationDiffRoot)
    $candidate = (& git -C $attestationDiffRoot rev-parse HEAD).Trim()
    Add-EvidencePaths $attestationDiffRoot
    $evidence = Commit-Fixture $attestationDiffRoot 'evidence'
    Write-FixtureFile $attestationDiffRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    Write-FixtureFile $attestationDiffRoot 'unexpected.txt' 'unexpected'
    $attestation = Commit-Fixture $attestationDiffRoot 'attestation'
    Assert-Rejected $attestationDiffRoot $candidate $evidence $attestation 'evidence_to_attestation_diff_paths_invalid'

    $hashMismatchRoot = New-FixtureRoot
    $roots.Add($hashMismatchRoot)
    $candidate = (& git -C $hashMismatchRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $hashMismatchRoot $candidate
    $manifestPath = Join-Path $hashMismatchRoot $evidencePaths[3]
    $manifestText = [IO.File]::ReadAllText($manifestPath)
    [IO.File]::WriteAllText($manifestPath, ($manifestText -replace '[0-9a-f]{64}', ('0' * 64)), [Text.UTF8Encoding]::new($false))
    $evidence = Commit-Fixture $hashMismatchRoot 'evidence'
    Write-FixtureFile $hashMismatchRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $hashMismatchRoot 'attestation'
    Assert-Rejected $hashMismatchRoot $candidate $evidence $attestation 'checksum_mismatch='

    $missingPrepRoot = New-FixtureRoot
    $roots.Add($missingPrepRoot)
    $candidate = (& git -C $missingPrepRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $missingPrepRoot $candidate
    $matrixPath = Join-Path $missingPrepRoot $evidencePaths[2]
    $matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json -Depth 50
    $matrix.matrix_prep_sha = 'f' * 40
    Write-FixtureFile $missingPrepRoot $evidencePaths[2] ($matrix | ConvertTo-Json -Depth 50)
    $handoffPath = Join-Path $missingPrepRoot $evidencePaths[0]
    $handoff = Get-Content -LiteralPath $handoffPath -Raw | ConvertFrom-Json
    $handoff.evidence_binding.matrix_prep_sha = $matrix.matrix_prep_sha
    Write-FixtureFile $missingPrepRoot $evidencePaths[0] ($handoff | ConvertTo-Json -Depth 10)
    $evidence = Commit-Fixture $missingPrepRoot 'evidence'
    Write-FixtureFile $missingPrepRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $missingPrepRoot 'attestation'
    Assert-Rejected $missingPrepRoot $candidate $evidence $attestation 'matrix_prep_not_exact_commit'

    $nonAncestorPrepRoot = New-FixtureRoot
    $roots.Add($nonAncestorPrepRoot)
    $candidate = (& git -C $nonAncestorPrepRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $nonAncestorPrepRoot $candidate
    $tree = (& git -C $nonAncestorPrepRoot rev-parse ($candidate + '^{tree}')).Trim()
    $nonAncestor = ('orphan matrix prep' | git -C $nonAncestorPrepRoot commit-tree $tree).Trim()
    $matrixPath = Join-Path $nonAncestorPrepRoot $evidencePaths[2]
    $matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json -Depth 50
    $matrix.matrix_prep_sha = $nonAncestor
    Write-FixtureFile $nonAncestorPrepRoot $evidencePaths[2] ($matrix | ConvertTo-Json -Depth 50)
    $handoffPath = Join-Path $nonAncestorPrepRoot $evidencePaths[0]
    $handoff = Get-Content -LiteralPath $handoffPath -Raw | ConvertFrom-Json
    $handoff.evidence_binding.matrix_prep_sha = $nonAncestor
    Write-FixtureFile $nonAncestorPrepRoot $evidencePaths[0] ($handoff | ConvertTo-Json -Depth 10)
    $evidence = Commit-Fixture $nonAncestorPrepRoot 'evidence'
    Write-FixtureFile $nonAncestorPrepRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $nonAncestorPrepRoot 'attestation'
    Assert-Rejected $nonAncestorPrepRoot $candidate $evidence $attestation 'matrix_prep_not_candidate_ancestor'

    $oneLineRoot = New-FixtureRoot
    $roots.Add($oneLineRoot)
    $candidate = (& git -C $oneLineRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $oneLineRoot $candidate
    Write-FixtureFile $oneLineRoot $evidencePaths[4] 'repository-safe fixture'
    $evidence = Commit-Fixture $oneLineRoot 'evidence'
    Write-FixtureFile $oneLineRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $oneLineRoot 'attestation'
    Assert-Rejected $oneLineRoot $candidate $evidence $attestation 'live_validation_contract_invalid'

    $securitySkipRoot = New-FixtureRoot
    $roots.Add($securitySkipRoot)
    $candidate = (& git -C $securitySkipRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $securitySkipRoot $candidate
    $livePath = Join-Path $securitySkipRoot $evidencePaths[4]
    $live = [IO.File]::ReadAllText($livePath).Replace(
        'applicable_security_prerequisite_skips: 0',
        'applicable_security_prerequisite_skips: 1',
        [StringComparison]::Ordinal)
    Write-FixtureFile $securitySkipRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $securitySkipRoot 'evidence'
    Write-FixtureFile $securitySkipRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $securitySkipRoot 'attestation'
    Assert-Rejected $securitySkipRoot $candidate $evidence $attestation 'live_validation_contract_invalid'

    $missingOsClassificationRoot = New-FixtureRoot
    Write-Verbose 'case=missing_os_classification'
    $roots.Add($missingOsClassificationRoot)
    $candidate = (& git -C $missingOsClassificationRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $missingOsClassificationRoot $candidate
    $livePath = Join-Path $missingOsClassificationRoot $evidencePaths[4]
    $live = [Regex]::Replace(
        [IO.File]::ReadAllText($livePath),
        '(?m)^not_applicable_os_security_tests: .+\r?\n?',
        '')
    Write-FixtureFile $missingOsClassificationRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $missingOsClassificationRoot 'evidence'
    Write-FixtureFile $missingOsClassificationRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $missingOsClassificationRoot 'attestation'
    Assert-Rejected $missingOsClassificationRoot $candidate $evidence $attestation 'live_validation_contract_invalid'

    $nonPassingApplicableSecurityRoot = New-FixtureRoot
    Write-Verbose 'case=nonpassing_applicable_security'
    $roots.Add($nonPassingApplicableSecurityRoot)
    $candidate = (& git -C $nonPassingApplicableSecurityRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $nonPassingApplicableSecurityRoot $candidate
    $livePath = Join-Path $nonPassingApplicableSecurityRoot $evidencePaths[4]
    $live = [IO.File]::ReadAllText($livePath).Replace(
        'file_symlink=passed',
        'file_symlink=skipped',
        [StringComparison]::Ordinal)
    Write-FixtureFile $nonPassingApplicableSecurityRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $nonPassingApplicableSecurityRoot 'evidence'
    Write-FixtureFile $nonPassingApplicableSecurityRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $nonPassingApplicableSecurityRoot 'attestation'
    Assert-Rejected $nonPassingApplicableSecurityRoot $candidate $evidence $attestation 'live_validation_contract_invalid'

    $unknownSecurityInventoryRoot = New-FixtureRoot
    Write-Verbose 'case=unknown_security_inventory'
    $roots.Add($unknownSecurityInventoryRoot)
    $candidate = (& git -C $unknownSecurityInventoryRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $unknownSecurityInventoryRoot $candidate
    $livePath = Join-Path $unknownSecurityInventoryRoot $evidencePaths[4]
    $live = [IO.File]::ReadAllText($livePath).Replace(
        'file_symlink=passed',
        'unknown_security_test=passed',
        [StringComparison]::Ordinal)
    Write-FixtureFile $unknownSecurityInventoryRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $unknownSecurityInventoryRoot 'evidence'
    Write-FixtureFile $unknownSecurityInventoryRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $unknownSecurityInventoryRoot 'attestation'
    Assert-Rejected $unknownSecurityInventoryRoot $candidate $evidence $attestation 'live_validation_contract_invalid'

    $extraSecurityInventoryRoot = New-FixtureRoot
    Write-Verbose 'case=extra_security_inventory'
    $roots.Add($extraSecurityInventoryRoot)
    $candidate = (& git -C $extraSecurityInventoryRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $extraSecurityInventoryRoot $candidate
    $livePath = Join-Path $extraSecurityInventoryRoot $evidencePaths[4]
    $live = [IO.File]::ReadAllText($livePath) +
        "`napplicable_os_security_tests: unknown_security_test=passed`n"
    Write-FixtureFile $extraSecurityInventoryRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $extraSecurityInventoryRoot 'evidence'
    Write-FixtureFile $extraSecurityInventoryRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $extraSecurityInventoryRoot 'attestation'
    Assert-Rejected $extraSecurityInventoryRoot $candidate $evidence $attestation 'live_validation_contract_invalid'

    $contradictoryScalarRoot = New-FixtureRoot
    Write-Verbose 'case=contradictory_scalar'
    $roots.Add($contradictoryScalarRoot)
    $candidate = (& git -C $contradictoryScalarRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $contradictoryScalarRoot $candidate
    $livePath = Join-Path $contradictoryScalarRoot $evidencePaths[4]
    $live = [IO.File]::ReadAllText($livePath) +
        "`nrequired_command_failures: 1`n"
    Write-FixtureFile $contradictoryScalarRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $contradictoryScalarRoot 'evidence'
    Write-FixtureFile $contradictoryScalarRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $contradictoryScalarRoot 'attestation'
    Assert-Rejected $contradictoryScalarRoot $candidate $evidence $attestation 'live_validation_contract_invalid'

    $falseRedRoot = New-FixtureRoot
    Write-Verbose 'case=false_red'
    $roots.Add($falseRedRoot)
    $candidate = (& git -C $falseRedRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $falseRedRoot $candidate
    $livePath = Join-Path $falseRedRoot $evidencePaths[4]
    $live = [IO.File]::ReadAllText($livePath).Replace(
        'observed=failed:unrelated prep SHA accepted',
        'observed=passed:unrelated prep SHA accepted',
        [StringComparison]::Ordinal)
    Write-FixtureFile $falseRedRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $falseRedRoot 'evidence'
    Write-FixtureFile $falseRedRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $falseRedRoot 'attestation'
    Assert-Rejected $falseRedRoot $candidate $evidence $attestation 'live_validation_contract_invalid'

    $contradictoryCommandRoot = New-FixtureRoot
    Write-Verbose 'case=contradictory_command'
    $roots.Add($contradictoryCommandRoot)
    $candidate = (& git -C $contradictoryCommandRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $contradictoryCommandRoot $candidate
    $livePath = Join-Path $contradictoryCommandRoot $evidencePaths[4]
    $live = [IO.File]::ReadAllText($livePath) +
        "`ncommand_result: dotnet test failed-filter | exit=1 | result=failed`n"
    Write-FixtureFile $contradictoryCommandRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $contradictoryCommandRoot 'evidence'
    Write-FixtureFile $contradictoryCommandRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $contradictoryCommandRoot 'attestation'
    Assert-Rejected $contradictoryCommandRoot $candidate $evidence $attestation 'live_validation_contract_invalid'

    $positiveRoot = New-FixtureRoot
    $roots.Add($positiveRoot)
    $candidate = (& git -C $positiveRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $positiveRoot $candidate
    $evidence = Commit-Fixture $positiveRoot 'evidence'
    Write-Attestation $positiveRoot $candidate $evidence
    $attestation = Commit-Fixture $positiveRoot 'attestation'
    $output = & $VerifierPath -CandidateSha $candidate -EvidenceSha $evidence -AttestationSha $attestation -RepositoryRoot $positiveRoot 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $output -notmatch 'evidence_chain=PASS') { throw ('positive_fixture_failed={0}' -f $output) }
    Add-Content -LiteralPath (Join-Path $positiveRoot $evidencePaths[2]) 'working-tree substitution'
    Assert-Rejected $positiveRoot $candidate $evidence $attestation 'working_tree_substitution_detected'

    Write-Output 'evidence_chain_self_test=PASS cases=17'
}
finally {
    foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
}
