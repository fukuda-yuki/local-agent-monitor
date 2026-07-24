[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $VerifierPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$evidencePaths = @(
    'docs/specifications/contracts/cost-analytics/v1/issue-91-validation-handoff.json',
    'docs/sprints/issue-95-cost-analytics/validation-matrix.json',
    'docs/sprints/issue-95-cost-analytics/artifact-checksums.json',
    'docs/sprints/issue-95-cost-analytics/live-validation.md')

function New-FixtureRoot {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('issue-95-evidence-chain-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    & git -C $root init -q
    & git -C $root config user.email 'fixture@example.invalid'
    & git -C $root config user.name 'Issue95Fixture'
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
    Write-FixtureFile $hashMismatchRoot $evidencePaths[0] ('{"evidence_binding":{"state":"finalized","matrix_prep_sha":"' + ('a' * 40) + '","final_validation_sha":"' + $candidate + '"}}')
    Write-FixtureFile $hashMismatchRoot $evidencePaths[1] ('{"final_validation_sha":"' + $candidate + '","matrix_prep_sha":"' + ('a' * 40) + '","active_rows":[]}')
    Write-FixtureFile $hashMismatchRoot $evidencePaths[2] ('{"candidate_base":"' + $candidate + '","algorithm":"SHA-256","artifacts":[{"path":"' + $evidencePaths[3] + '","sha256":"' + ('0' * 64) + '"}]}')
    Write-FixtureFile $hashMismatchRoot $evidencePaths[3] 'repository-safe fixture'
    $evidence = Commit-Fixture $hashMismatchRoot 'evidence'
    Write-FixtureFile $hashMismatchRoot 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json' '{}'
    $attestation = Commit-Fixture $hashMismatchRoot 'attestation'
    Assert-Rejected $hashMismatchRoot $candidate $evidence $attestation 'checksum_mismatch='

    Write-Output 'evidence_chain_self_test=PASS cases=4'
}
finally {
    foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
}
