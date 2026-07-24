[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $CandidateSha,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $EvidenceSha,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $AttestationSha,
    [Parameter()][string] $RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
}
else {
    [IO.Path]::GetFullPath($RepositoryRoot)
}
$handoffPath = 'docs/specifications/contracts/cost-analytics/v1/issue-91-validation-handoff.json'
$matrixPath = 'docs/sprints/issue-95-cost-analytics/validation-matrix.json'
$checksumsPath = 'docs/sprints/issue-95-cost-analytics/artifact-checksums.json'
$attestationPath = 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json'
$evidencePaths = @($handoffPath, $matrixPath, $checksumsPath, 'docs/sprints/issue-95-cost-analytics/live-validation.md')

function Invoke-GitText([string[]] $Arguments) {
    $output = & git -C $repositoryRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ('git_command_failed={0}' -f ($Arguments -join ' '))
    }

    return ($output | Out-String).Trim()
}

function Assert-ExactCommit([string] $Sha, [string] $Name) {
    $resolved = Invoke-GitText @('rev-parse', '--verify', ($Sha + '^{commit}'))
    if ($resolved -ne $Sha) {
        throw ('{0}_not_exact_commit' -f $Name)
    }
}

function Get-GitBlobBytes([string] $Commit, [string] $Path) {
    # git show streams the exact evidence-commit blob, not a working-tree file.
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    [void]$startInfo.ArgumentList.Add('show')
    [void]$startInfo.ArgumentList.Add(($Commit + ':' + $Path))
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw 'git_show_start_failed' }
    $buffer = [IO.MemoryStream]::new()
    $process.StandardOutput.BaseStream.CopyTo($buffer)
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw ('git_show_failed={0}' -f $Path) }
    return $buffer.ToArray()
}

function Read-GitJson([string] $Commit, [string] $Path) {
    $bytes = Get-GitBlobBytes $Commit $Path
    try {
        return [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -Depth 50
    }
    catch {
        throw ('invalid_json={0}' -f $Path)
    }
}

function Assert-Tracked([string] $Commit, [string] $Path) {
    $null = Invoke-GitText @('cat-file', '-e', ($Commit + ':' + $Path))
}

function Assert-ExactPathSet([string] $Range, [string[]] $Expected, [string] $Name) {
    $diffOutput = Invoke-GitText @('diff', '--name-only', $Range)
    $actual = @($diffOutput -split "`r?`n" | Where-Object { $_ -ne '' } | Sort-Object -Unique)
    $expectedSorted = @($Expected | Sort-Object -Unique)
    if ((Compare-Object $actual $expectedSorted)) {
        throw ('{0}_diff_paths_invalid actual={1} expected={2}' -f $Name, ($actual -join ','), ($expectedSorted -join ','))
    }
}

Assert-ExactCommit $CandidateSha 'candidate'
Assert-ExactCommit $EvidenceSha 'evidence'
Assert-ExactCommit $AttestationSha 'attestation'

if ((Invoke-GitText @('rev-parse', 'HEAD')) -ne $AttestationSha) {
    throw 'current_head_not_attestation'
}
if ((Invoke-GitText @('rev-parse', ($EvidenceSha + '^'))) -ne $CandidateSha) {
    throw 'evidence_parent_not_candidate'
}
if ((Invoke-GitText @('rev-parse', ($AttestationSha + '^'))) -ne $EvidenceSha) {
    throw 'attestation_parent_not_evidence'
}

Assert-ExactPathSet ($CandidateSha + '..' + $EvidenceSha) $evidencePaths 'candidate_to_evidence'
Assert-ExactPathSet ($EvidenceSha + '..' + $AttestationSha) @($attestationPath) 'evidence_to_attestation'

foreach ($path in $evidencePaths) { Assert-Tracked $EvidenceSha $path }
Assert-Tracked $AttestationSha $attestationPath

$matrix = Read-GitJson $EvidenceSha $matrixPath
if ($matrix.final_validation_sha -ne $CandidateSha) { throw 'matrix_candidate_sha_mismatch' }
foreach ($row in @($matrix.active_rows)) {
    if ($row.validation_sha -ne $CandidateSha -or $row.versions.candidate -ne $CandidateSha) {
        throw 'matrix_row_candidate_sha_mismatch'
    }
}

$handoff = Read-GitJson $EvidenceSha $handoffPath
if ($handoff.evidence_binding.state -ne 'finalized' -or
    $handoff.evidence_binding.matrix_prep_sha -ne $matrix.matrix_prep_sha -or
    $handoff.evidence_binding.final_validation_sha -ne $CandidateSha) {
    throw 'handoff_evidence_binding_mismatch'
}

$checksums = Read-GitJson $EvidenceSha $checksumsPath
if ($checksums.candidate_base -ne $CandidateSha -or $checksums.algorithm -ne 'SHA-256') {
    throw 'checksum_manifest_binding_invalid'
}
$checksumArtifacts = @($checksums.artifacts)
if ($checksumArtifacts.Count -eq 0) { throw 'checksum_manifest_empty' }
foreach ($artifact in $checksumArtifacts) {
    $path = [string]$artifact.path
    $digest = [string]$artifact.sha256
    if ([string]::IsNullOrWhiteSpace($path) -or
        $path -eq $checksumsPath -or $path -eq $attestationPath -or
        $path -match '(?i)archive' -or $digest -notmatch '^[0-9a-f]{64}$') {
        throw 'checksum_manifest_artifact_invalid'
    }
    $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData((Get-GitBlobBytes $EvidenceSha $path))).ToLowerInvariant()
    if ($actual -ne $digest) { throw ('checksum_mismatch={0}' -f $path) }
}

$attestation = Read-GitJson $AttestationSha $attestationPath
if ($attestation.functional_candidate_sha -ne $CandidateSha -or
    $attestation.evidence_materialization_sha -ne $EvidenceSha -or
    $attestation.evidence_materialization_parent_sha -ne $CandidateSha -or
    $attestation.checksum_algorithm -ne 'SHA-256') {
    throw 'attestation_chain_binding_invalid'
}
$attested = @($attestation.artifacts_at_evidence_materialization)
if ($attested.Count -ne $checksumArtifacts.Count) { throw 'attestation_artifact_count_mismatch' }
foreach ($artifact in $attested) {
    $path = [string]$artifact.path
    $digest = [string]$artifact.sha256
    $matchingChecksum = @($checksumArtifacts | Where-Object { $_.path -eq $path -and $_.sha256 -eq $digest })
    if ($matchingChecksum.Count -ne 1) { throw 'attestation_checksum_binding_mismatch' }
    $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData((Get-GitBlobBytes $EvidenceSha $path))).ToLowerInvariant()
    if ($actual -ne $digest) { throw ('attestation_hash_mismatch={0}' -f $path) }
}

$validator = Join-Path $repositoryRoot 'scripts/validation/issue-91/validate-matrix.ps1'
& pwsh -NoLogo -NoProfile -File $validator -MatrixPath (Join-Path $repositoryRoot $matrixPath)
if ($LASTEXITCODE -ne 0) { throw 'issue_91_matrix_validator_failed' }

Write-Output 'evidence_chain=PASS'
