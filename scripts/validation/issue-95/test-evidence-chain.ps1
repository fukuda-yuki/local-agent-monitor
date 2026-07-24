[CmdletBinding()]
param(
    [Parameter()][string] $VerifierPath,
    [Parameter()][string] $MatrixValidatorPath,
    [Parameter()][string] $MatrixFixturePath,
    [Parameter()][string] $DescribeFixture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-AuthenticatedRedFixtures {
    return @(
        [ordered]@{
            name = 'wrong_surface'
            expected_code = 'row_contract_mismatch'
        })
}

if (-not [string]::IsNullOrWhiteSpace($DescribeFixture)) {
    $fixture = @(Get-AuthenticatedRedFixtures | Where-Object { $_.name -ceq $DescribeFixture })
    if ($fixture.Count -ne 1) { throw 'red_fixture_not_registered' }
    Write-Output ($fixture[0] | ConvertTo-Json -Compress)
    return
}
if ([string]::IsNullOrWhiteSpace($VerifierPath) -or
    [string]::IsNullOrWhiteSpace($MatrixValidatorPath) -or
    [string]::IsNullOrWhiteSpace($MatrixFixturePath)) {
    throw 'full_self_test_paths_required'
}

$evidencePaths = @(
    'docs/specifications/contracts/cost-analytics/v1/issue-91-validation-handoff.json',
    'docs/sprints/issue-95-cost-analytics/README.md',
    'docs/sprints/issue-95-cost-analytics/validation-matrix.json',
    'docs/sprints/issue-95-cost-analytics/artifact-checksums.json',
    'docs/sprints/issue-95-cost-analytics/live-validation.md')
$manifestPaths = @($evidencePaths[0], $evidencePaths[1], $evidencePaths[2], $evidencePaths[4])
$rowContractSourcePath = [IO.Path]::GetFullPath($MatrixFixturePath)
$handoffSourcePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\docs\specifications\contracts\cost-analytics\v1\issue-91-validation-handoff.json'))
$verifierSourcePath = [IO.Path]::GetFullPath($VerifierPath)
$selfTestSourcePath = [IO.Path]::GetFullPath($PSCommandPath)

function New-FixtureRoot {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('issue-95-evidence-chain-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    & git -C $root init -q
    & git -C $root config user.email 'fixture@example.invalid'
    & git -C $root config user.name 'Issue95Fixture'
    & git -C $root config core.autocrlf false
    Write-FixtureFile $root 'scripts/validation/issue-91/validate-matrix.ps1' ([IO.File]::ReadAllText($MatrixValidatorPath))
    Write-FixtureFile $root 'scripts/validation/issue-95/verify-evidence-chain.ps1' ([IO.File]::ReadAllText($verifierSourcePath))
    Write-FixtureFile $root 'docs/specifications/contracts/cost-analytics/v1/issue-91-validation-row-contract.json' ([IO.File]::ReadAllText($rowContractSourcePath))
    & git -C $root add .
    & git -C $root commit -q -m matrix-prep
    Write-FixtureFile $root 'scripts/validation/issue-95/test-evidence-chain.ps1' ([IO.File]::ReadAllText($selfTestSourcePath))
    & git -C $root add scripts/validation/issue-95/test-evidence-chain.ps1
    & git -C $root commit -q -m candidate
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

function Get-FixtureRow($Rows, [string] $RowId) {
    $match = @($Rows | Where-Object { $_.row_id -eq $RowId })
    if ($match.Count -ne 1) { throw ('fixture_row_invalid={0}' -f $RowId) }
    return $match[0]
}

function Write-PositiveEvidence([string] $Root, [string] $Candidate) {
    $contract = Get-Content -LiteralPath $rowContractSourcePath -Raw | ConvertFrom-Json -Depth 50
    $handoff = Get-Content -LiteralPath $handoffSourcePath -Raw | ConvertFrom-Json -Depth 50
    $matrixPrep = (& git -C $Root rev-parse ($Candidate + '^')).Trim()
    $rows = foreach ($contractRow in @($contract.active_rows)) {
        $versions = [ordered]@{}
        foreach ($property in @($contractRow.versions.PSObject.Properties)) {
            $versions[$property.Name] = if ([string]$property.Value -eq '$candidate') { $Candidate } else { [string]$property.Value }
        }
        $live = $contractRow.row_id -eq '91-L-095'
        $block = $contractRow.blocked_external_contract
        [ordered]@{
            row_id = $contractRow.row_id
            matrix_schema_version = 'validation-matrix.v1'
            surface = $contractRow.surface
            operation = $contractRow.operation
            profiles = [ordered]@{
                collection = @($contractRow.required_profiles.collection)
                content_access = @($contractRow.required_profiles.content_access)
                compatibility = @($contractRow.required_profiles.compatibility)
                hook = @($contractRow.required_profiles.hook)
                otel = @($contractRow.required_profiles.otel)
                binding = @($contractRow.required_profiles.binding)
                restart = @($contractRow.required_profiles.restart)
                retention = @($contractRow.required_profiles.retention)
            }
            requirement_level = 'required'
            applicability = 'applicable'
            applicability_reason = $null
            versions = $versions
            expected_invariant = if ($live) { $block.unverified_capability } else { 'The exact Issue #95 row contract is satisfied.' }
            evidence = @([ordered]@{
                kind = if ($live) { 'live' } else { 'automated' }
                reference = @($contractRow.evidence_references)[0]
                compatibility_basis = if ($live) { 'The live prerequisite stopped before provider execution.' } else { 'Candidate-pinned automated execution.' }
            })
            actual_result = if ($live) { 'The canonical external prerequisites remain unavailable.' } else { 'The candidate-pinned row passed.' }
            classification = if ($live) { 'blocked_external' } else { 'passed' }
            severity = if ($live) { $block.severity } else { 'none' }
            blocker = if ($live) { $block.blocker } else { $null }
            retry_condition = if ($live) { $block.retry_condition } else { $null }
            unverified_capability = if ($live) { $block.unverified_capability } else { $null }
            owner = 'Issue #95 cost analytics'
            validation_sha = $Candidate
            validation_date = '2026-07-24'
            environment_boundary = 'Synthetic repository-safe self-test fixture.'
        }
    }
    $liveBlock = (Get-FixtureRow $contract.active_rows '91-L-095').blocked_external_contract
    $matrix = [ordered]@{
        schema_version = 'validation-matrix.v1'
        matrix_prep_sha = $matrixPrep
        final_validation_sha = $Candidate
        inventory_date = '2026-07-24'
        environment_boundary = 'Synthetic repository-safe Issue #95 evidence-chain self-test.'
        active_rows = @($rows)
        future_registry_ref = 'docs/specifications/contracts/validation-matrix/v1/future-surface-registry.json'
        evidence_ledger_refs = @($evidencePaths[0], $evidencePaths[1], $evidencePaths[4])
        release_decision = [ordered]@{
            decision = 'release_ready_with_external_blockers'
            external_blockers = @([ordered]@{
                row_id = '91-L-095'
                severity = $liveBlock.severity
                blocker = $liveBlock.blocker
                retry_condition = $liveBlock.retry_condition
                unverified_capability = $liveBlock.unverified_capability
            })
        }
    }
    Write-FixtureFile $Root $evidencePaths[2] ($matrix | ConvertTo-Json -Depth 50)
    $handoff.evidence_binding.state = 'finalized'
    $handoff.evidence_binding.matrix_prep_sha = $matrixPrep
    $handoff.evidence_binding.final_validation_sha = $Candidate
    Write-FixtureFile $Root $evidencePaths[0] ($handoff | ConvertTo-Json -Depth 50)
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
command_result: pwsh scripts\validation\issue-95\test-evidence-chain.ps1 -VerifierPath scripts\validation\issue-95\verify-evidence-chain.ps1 -MatrixValidatorPath scripts\validation\issue-91\validate-matrix.ps1 -MatrixFixturePath docs\specifications\contracts\cost-analytics\v1\issue-91-validation-row-contract.json | exit=0 | result=passed

## RED and failure history
red_failure_count: 1
red_failure: wrong_surface | command=pwsh scripts\validation\issue-95\test-evidence-chain.ps1 | observed=failed:wrong surface accepted | expected_code=row_contract_mismatch | executable_fixture=wrong_surface | corrected_by=$Candidate

## OS-specific security coverage
validation_os: windows
applicable_os_security_tests: file_symlink=passed,directory_symlink=passed,windows_open_mutation=passed,windows_normalized_segment=passed
applicable_security_prerequisite_skips: 0
not_applicable_os_security_tests: linux_fifo=not_applicable

## Live validation
## 91-A-095
91-A-095: passed

## 91-S-095
91-S-095: passed

## 91-L-095
91-L-095: blocked_external
unverified_capability: $($liveBlock.unverified_capability)
"@
    foreach ($filter in @($handoff.automated_test_filters)) {
        $liveValidation += "`nautomated_filter: $filter"
    }
    $liveValidation += "`n"
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

function Assert-Rejected(
    [string] $Root,
    [string] $Candidate,
    [string] $Evidence,
    [string] $Attestation,
    [string] $ExpectedCode,
    [string] $Verifier = $VerifierPath) {
    $failed = $false
    try {
        $output = & $Verifier -CandidateSha $Candidate -EvidenceSha $Evidence -AttestationSha $Attestation -RepositoryRoot $Root 2>&1 | Out-String
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

    $missingAnchorRoot = New-FixtureRoot
    Write-Verbose 'case=missing_evidence_anchor'
    $roots.Add($missingAnchorRoot)
    $candidate = (& git -C $missingAnchorRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $missingAnchorRoot $candidate
    $livePath = Join-Path $missingAnchorRoot $evidencePaths[4]
    $live = [Regex]::Replace(
        [IO.File]::ReadAllText($livePath),
        '(?m)^## 91-A-095\r?\n',
        '')
    Write-FixtureFile $missingAnchorRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $missingAnchorRoot 'evidence'
    Write-FixtureFile $missingAnchorRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $missingAnchorRoot 'attestation'
    Assert-Rejected $missingAnchorRoot $candidate $evidence $attestation 'live_evidence_anchor_91-A-095_invalid'

    $duplicateAnchorRoot = New-FixtureRoot
    Write-Verbose 'case=duplicate_evidence_anchor'
    $roots.Add($duplicateAnchorRoot)
    $candidate = (& git -C $duplicateAnchorRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $duplicateAnchorRoot $candidate
    $livePath = Join-Path $duplicateAnchorRoot $evidencePaths[4]
    $live = [IO.File]::ReadAllText($livePath) + "`n## 91-S-095`n"
    Write-FixtureFile $duplicateAnchorRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $duplicateAnchorRoot 'evidence'
    Write-FixtureFile $duplicateAnchorRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $duplicateAnchorRoot 'attestation'
    Assert-Rejected $duplicateAnchorRoot $candidate $evidence $attestation 'live_evidence_anchor_91-S-095_invalid'

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
        'observed=failed:wrong surface accepted',
        'observed=passed:wrong surface accepted',
        [StringComparison]::Ordinal)
    Write-FixtureFile $falseRedRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $falseRedRoot 'evidence'
    Write-FixtureFile $falseRedRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $falseRedRoot 'attestation'
    Assert-Rejected $falseRedRoot $candidate $evidence $attestation 'live_validation_contract_invalid'

    $forgedRedRoot = New-FixtureRoot
    Write-Verbose 'case=forged_red_fixture'
    $roots.Add($forgedRedRoot)
    $candidate = (& git -C $forgedRedRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $forgedRedRoot $candidate
    $livePath = Join-Path $forgedRedRoot $evidencePaths[4]
    $live = [IO.File]::ReadAllText($livePath).Replace(
        'executable_fixture=wrong_surface',
        'executable_fixture=forged_missing_fixture',
        [StringComparison]::Ordinal)
    Write-FixtureFile $forgedRedRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $forgedRedRoot 'evidence'
    Write-FixtureFile $forgedRedRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $forgedRedRoot 'attestation'
    Assert-Rejected $forgedRedRoot $candidate $evidence $attestation 'red_failure_fixture_not_authenticated'

    $mismatchedRedRoot = New-FixtureRoot
    Write-Verbose 'case=mismatched_red_fixture_code'
    $roots.Add($mismatchedRedRoot)
    $candidate = (& git -C $mismatchedRedRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $mismatchedRedRoot $candidate
    $livePath = Join-Path $mismatchedRedRoot $evidencePaths[4]
    $live = [IO.File]::ReadAllText($livePath).Replace(
        'expected_code=row_contract_mismatch',
        'expected_code=live_blocker_providers_invalid',
        [StringComparison]::Ordinal)
    Write-FixtureFile $mismatchedRedRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $mismatchedRedRoot 'evidence'
    Write-FixtureFile $mismatchedRedRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $mismatchedRedRoot 'attestation'
    Assert-Rejected $mismatchedRedRoot $candidate $evidence $attestation 'red_failure_fixture_not_authenticated'

    $commentOnlyRedRoot = New-FixtureRoot
    Write-Verbose 'case=comment_only_red_fixture'
    $roots.Add($commentOnlyRedRoot)
    $selfTestPath = Join-Path $commentOnlyRedRoot 'scripts/validation/issue-95/test-evidence-chain.ps1'
    Add-Content -LiteralPath $selfTestPath "`n# name='comment_only' expected_code='row_contract_mismatch'`n"
    & git -C $commentOnlyRedRoot add scripts/validation/issue-95/test-evidence-chain.ps1
    & git -C $commentOnlyRedRoot commit --amend --no-edit -q
    $candidate = (& git -C $commentOnlyRedRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $commentOnlyRedRoot $candidate
    $livePath = Join-Path $commentOnlyRedRoot $evidencePaths[4]
    $live = [IO.File]::ReadAllText($livePath).Replace(
        'executable_fixture=wrong_surface',
        'executable_fixture=comment_only',
        [StringComparison]::Ordinal)
    Write-FixtureFile $commentOnlyRedRoot $evidencePaths[4] $live
    $evidence = Commit-Fixture $commentOnlyRedRoot 'evidence'
    Write-FixtureFile $commentOnlyRedRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $commentOnlyRedRoot 'attestation'
    Assert-Rejected $commentOnlyRedRoot $candidate $evidence $attestation 'red_failure_fixture_not_authenticated'

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

    $duplicateContractRoot = New-FixtureRoot
    Write-Verbose 'case=duplicate_contract_evidence'
    $roots.Add($duplicateContractRoot)
    $candidateContractPath = Join-Path $duplicateContractRoot 'docs/specifications/contracts/cost-analytics/v1/issue-91-validation-row-contract.json'
    $duplicateContract = Get-Content -LiteralPath $candidateContractPath -Raw | ConvertFrom-Json -Depth 50
    $duplicateRow = Get-FixtureRow $duplicateContract.active_rows '91-A-095'
    $duplicateRow.evidence_references = @($duplicateRow.evidence_references) + @($duplicateRow.evidence_references[0])
    Write-FixtureFile $duplicateContractRoot 'docs/specifications/contracts/cost-analytics/v1/issue-91-validation-row-contract.json' ($duplicateContract | ConvertTo-Json -Depth 50)
    & git -C $duplicateContractRoot add docs/specifications/contracts/cost-analytics/v1/issue-91-validation-row-contract.json
    & git -C $duplicateContractRoot commit --amend --no-edit -q
    $candidate = (& git -C $duplicateContractRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $duplicateContractRoot $candidate
    $evidence = Commit-Fixture $duplicateContractRoot 'evidence'
    Write-FixtureFile $duplicateContractRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $duplicateContractRoot 'attestation'
    Assert-Rejected $duplicateContractRoot $candidate $evidence $attestation 'row_contract_handoff_evidence_91-A-095_expected_duplicate'

    $staleProfileLedgerRoot = New-FixtureRoot
    Write-Verbose 'case=stale_profile_ledger'
    $roots.Add($staleProfileLedgerRoot)
    $candidate = (& git -C $staleProfileLedgerRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $staleProfileLedgerRoot $candidate
    $handoffPath = Join-Path $staleProfileLedgerRoot $evidencePaths[0]
    $handoff = Get-Content -LiteralPath $handoffPath -Raw | ConvertFrom-Json -Depth 50
    $handoff.required_profiles.collection = @($handoff.required_profiles.collection | Select-Object -Skip 1)
    Write-FixtureFile $staleProfileLedgerRoot $evidencePaths[0] ($handoff | ConvertTo-Json -Depth 50)
    $evidence = Commit-Fixture $staleProfileLedgerRoot 'evidence'
    Write-FixtureFile $staleProfileLedgerRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $staleProfileLedgerRoot 'attestation'
    Assert-Rejected $staleProfileLedgerRoot $candidate $evidence $attestation 'row_contract_profile_ledger_collection_invalid'

    $authenticatedRed = @(Get-AuthenticatedRedFixtures)[0]
    $semanticCases = @(
        @{
            name = $authenticatedRed.name
            expected = $authenticatedRed.expected_code
            mutate = {
                param($Root)
                $path = Join-Path $Root $evidencePaths[2]
                $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 50
                (Get-FixtureRow $value.active_rows '91-A-095').surface = 'another-surface'
                Write-FixtureFile $Root $evidencePaths[2] ($value | ConvertTo-Json -Depth 50)
            }
        },
        @{
            name = 'wrong_profile_axis'
            expected = 'row_contract_matrix_profiles_91-A-095_collection_invalid'
            mutate = {
                param($Root)
                $path = Join-Path $Root $evidencePaths[2]
                $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 50
                $row = Get-FixtureRow $value.active_rows '91-A-095'
                $moved = $row.profiles.collection[0]
                $row.profiles.collection = @($row.profiles.collection | Select-Object -Skip 1)
                $row.profiles.compatibility = @($row.profiles.compatibility) + $moved
                Write-FixtureFile $Root $evidencePaths[2] ($value | ConvertTo-Json -Depth 50)
            }
        },
        @{
            name = 'wrong_evidence_reference'
            expected = 'row_contract_matrix_evidence_91-A-095_invalid'
            mutate = {
                param($Root)
                $path = Join-Path $Root $evidencePaths[2]
                $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 50
                (Get-FixtureRow $value.active_rows '91-A-095').evidence[0].reference = 'docs/sprints/another-surface.md'
                Write-FixtureFile $Root $evidencePaths[2] ($value | ConvertTo-Json -Depth 50)
            }
        },
        @{
            name = 'wrong_provider'
            expected = 'live_blocker_providers_invalid'
            mutate = {
                param($Root)
                $path = Join-Path $Root $evidencePaths[0]
                $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 50
                (Get-FixtureRow $value.active_rows '91-L-095').blocked_external_contract.required_providers[0] = 'another-provider'
                Write-FixtureFile $Root $evidencePaths[0] ($value | ConvertTo-Json -Depth 50)
            }
        },
        @{
            name = 'wrong_capability'
            expected = 'live_blocker_capabilities_invalid'
            mutate = {
                param($Root)
                $path = Join-Path $Root $evidencePaths[0]
                $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 50
                (Get-FixtureRow $value.active_rows '91-L-095').blocked_external_contract.unverified_capabilities[0] = 'another-capability'
                Write-FixtureFile $Root $evidencePaths[0] ($value | ConvertTo-Json -Depth 50)
            }
        },
        @{
            name = 'wrong_filter'
            expected = 'row_contract_filters_91-A-095_invalid'
            mutate = {
                param($Root)
                $path = Join-Path $Root $evidencePaths[0]
                $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 50
                (Get-FixtureRow $value.active_rows '91-A-095').automated_test_filters[0] = 'FullyQualifiedName~AnotherSurfaceTests'
                Write-FixtureFile $Root $evidencePaths[0] ($value | ConvertTo-Json -Depth 50)
            }
        })
    foreach ($case in $semanticCases) {
        Write-Verbose ('case={0}' -f $case.name)
        $root = New-FixtureRoot
        $roots.Add($root)
        $candidate = (& git -C $root rev-parse HEAD).Trim()
        Write-PositiveEvidence $root $candidate
        & $case.mutate $root
        $evidence = Commit-Fixture $root 'evidence'
        Write-FixtureFile $root 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
        $attestation = Commit-Fixture $root 'attestation'
        Assert-Rejected $root $candidate $evidence $attestation $case.expected
    }

    $tamperedVerifierRoot = New-FixtureRoot
    Write-Verbose 'case=tampered_running_verifier'
    $roots.Add($tamperedVerifierRoot)
    $candidate = (& git -C $tamperedVerifierRoot rev-parse HEAD).Trim()
    Write-PositiveEvidence $tamperedVerifierRoot $candidate
    $evidence = Commit-Fixture $tamperedVerifierRoot 'evidence'
    Write-FixtureFile $tamperedVerifierRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $tamperedVerifierRoot 'attestation'
    $tamperedVerifier = Join-Path ([IO.Path]::GetTempPath()) (
        'issue-95-tampered-verifier-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    try {
        [IO.File]::WriteAllText(
            $tamperedVerifier,
            [IO.File]::ReadAllText($verifierSourcePath) + "`n# tampered working verifier`n",
            [Text.UTF8Encoding]::new($false))
        Assert-Rejected $tamperedVerifierRoot $candidate $evidence $attestation 'verifier_working_copy_mismatch' $tamperedVerifier
    }
    finally {
        Remove-Item -LiteralPath $tamperedVerifier -Force
    }

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

    Write-Output 'evidence_chain_self_test=PASS cases=31'
}
finally {
    foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
}
