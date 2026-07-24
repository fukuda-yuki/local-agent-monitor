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

function Write-PositiveEvidence([string] $Root, [string] $Candidate) {
    $matrix = Get-Content -LiteralPath $MatrixFixturePath -Raw | ConvertFrom-Json -Depth 50
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
    Write-FixtureFile $Root $evidencePaths[4] 'repository-safe fixture'
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

    Write-Output 'evidence_chain_self_test=PASS cases=6'
}
finally {
    foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
}
