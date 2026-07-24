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
} else {
    [IO.Path]::GetFullPath($RepositoryRoot)
}
$handoffPath = 'docs/specifications/contracts/cost-analytics/v1/issue-91-validation-handoff.json'
$readmePath = 'docs/sprints/issue-95-cost-analytics/README.md'
$matrixPath = 'docs/sprints/issue-95-cost-analytics/validation-matrix.json'
$checksumsPath = 'docs/sprints/issue-95-cost-analytics/artifact-checksums.json'
$attestationPath = 'docs/sprints/issue-95-cost-analytics/evidence-attestation.json'
$validatorPath = 'scripts/validation/issue-91/validate-matrix.ps1'
$evidencePaths = @($handoffPath, $readmePath, $matrixPath, $checksumsPath, 'docs/sprints/issue-95-cost-analytics/live-validation.md')
$manifestPaths = @($handoffPath, $readmePath, $matrixPath, 'docs/sprints/issue-95-cost-analytics/live-validation.md')
$attestationArtifactPaths = @($manifestPaths + $checksumsPath)

function Invoke-GitText([string[]] $Arguments) {
    $output = & git -C $repositoryRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('git_command_failed={0}' -f ($Arguments -join ' ')) }
    return ($output | Out-String).Trim()
}

function Assert-ExactCommit([string] $Sha, [string] $Name) {
    if ((Invoke-GitText @('rev-parse', '--verify', ($Sha + '^{commit}'))) -ne $Sha) {
        throw ('{0}_not_exact_commit' -f $Name)
    }
}

function Get-GitBlobBytes([string] $Commit, [string] $Path) {
    # git show streams the exact committed blob instead of a working-tree file.
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
    try { return [Text.Encoding]::UTF8.GetString((Get-GitBlobBytes $Commit $Path)) | ConvertFrom-Json -Depth 50 }
    catch { throw ('invalid_json={0}' -f $Path) }
}

function Assert-Tracked([string] $Commit, [string] $Path) {
    $null = Invoke-GitText @('cat-file', '-e', ($Commit + ':' + $Path))
}

function Assert-OrdinalSet([string[]] $Actual, [string[]] $Expected, [string] $Name) {
    $actualSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $expectedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Actual) { if (-not $actualSet.Add($item)) { throw ('{0}_duplicate' -f $Name) } }
    foreach ($item in $Expected) { [void]$expectedSet.Add($item) }
    if ($actualSet.Count -ne $expectedSet.Count) { throw ('{0}_invalid' -f $Name) }
    foreach ($item in $actualSet) { if (-not $expectedSet.Contains($item)) { throw ('{0}_invalid' -f $Name) } }
}

function Assert-ExactPathSet([string] $Range, [string[]] $Expected, [string] $Name) {
    $diffOutput = Invoke-GitText @('diff', '--no-renames', '--name-only', $Range)
    Assert-OrdinalSet @($diffOutput -split "`r?`n" | Where-Object { $_ -ne '' }) $Expected ($Name + '_diff_paths')
}

function Get-ArtifactPaths($Artifacts, [string] $Name) {
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($artifact in @($Artifacts)) {
        if ($null -eq $artifact -or [string]::IsNullOrWhiteSpace([string]$artifact.path) -or [string]$artifact.sha256 -notmatch '^[0-9a-f]{64}$') {
            throw ('{0}_artifact_invalid' -f $Name)
        }
        $paths.Add([string]$artifact.path)
    }
    return @($paths)
}

function Assert-CleanCommittedInputs {
    & git -C $repositoryRoot diff --quiet $AttestationSha -- $matrixPath $validatorPath
    if ($LASTEXITCODE -eq 1) { throw 'working_tree_substitution_detected' }
    if ($LASTEXITCODE -ne 0) { throw 'working_tree_substitution_check_failed' }
}

function Invoke-PinnedMatrixValidator {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('issue-95-evidence-chain-' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
        $temporaryMatrix = Join-Path $temporaryRoot 'validation-matrix.json'
        $temporaryValidator = Join-Path $temporaryRoot 'validate-matrix.ps1'
        [IO.File]::WriteAllBytes($temporaryMatrix, (Get-GitBlobBytes $EvidenceSha $matrixPath))
        [IO.File]::WriteAllBytes($temporaryValidator, (Get-GitBlobBytes $CandidateSha $validatorPath))
        & pwsh -NoLogo -NoProfile -File $temporaryValidator -MatrixPath $temporaryMatrix
        if ($LASTEXITCODE -ne 0) { throw 'issue_91_matrix_validator_failed' }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
    }
}

Assert-ExactCommit $CandidateSha 'candidate'
Assert-ExactCommit $EvidenceSha 'evidence'
Assert-ExactCommit $AttestationSha 'attestation'
if ((Invoke-GitText @('rev-parse', 'HEAD')) -ne $AttestationSha) { throw 'current_head_not_attestation' }
if ((Invoke-GitText @('rev-parse', ($EvidenceSha + '^'))) -ne $CandidateSha) { throw 'evidence_parent_not_candidate' }
if ((Invoke-GitText @('rev-parse', ($AttestationSha + '^'))) -ne $EvidenceSha) { throw 'attestation_parent_not_evidence' }
Assert-ExactPathSet ($CandidateSha + '..' + $EvidenceSha) $evidencePaths 'candidate_to_evidence'
Assert-ExactPathSet ($EvidenceSha + '..' + $AttestationSha) @($attestationPath) 'evidence_to_attestation'
Assert-CleanCommittedInputs
foreach ($path in $evidencePaths) { Assert-Tracked $EvidenceSha $path }
Assert-Tracked $AttestationSha $attestationPath

$matrix = Read-GitJson $EvidenceSha $matrixPath
if ($matrix.final_validation_sha -ne $CandidateSha) { throw 'matrix_candidate_sha_mismatch' }
$rows = @($matrix.active_rows)
Assert-OrdinalSet @($rows | ForEach-Object { [string]$_.row_id }) @('91-A-095', '91-S-095', '91-L-095') 'matrix_rows'
foreach ($row in $rows) {
    if ($row.validation_sha -ne $CandidateSha -or $row.versions.candidate -ne $CandidateSha) { throw 'matrix_row_candidate_sha_mismatch' }
}
if ((@($rows | Where-Object { $_.row_id -eq '91-A-095' }).classification) -ne 'passed' -or
    (@($rows | Where-Object { $_.row_id -eq '91-S-095' }).classification) -ne 'passed' -or
    (@($rows | Where-Object { $_.row_id -eq '91-L-095' }).classification) -ne 'blocked_external' -or
    (@($rows | Where-Object { $_.row_id -eq '91-L-095' }).severity) -ne 'high') { throw 'matrix_classification_invalid' }

$handoff = Read-GitJson $EvidenceSha $handoffPath
if ($handoff.evidence_binding.state -ne 'finalized' -or $handoff.evidence_binding.matrix_prep_sha -ne $matrix.matrix_prep_sha -or $handoff.evidence_binding.final_validation_sha -ne $CandidateSha) {
    throw 'handoff_evidence_binding_mismatch'
}

$checksums = Read-GitJson $EvidenceSha $checksumsPath
$manifestProperties = @($checksums.PSObject.Properties.Name)
Assert-OrdinalSet $manifestProperties @('schema_version', 'candidate_base', 'algorithm', 'verification_date', 'artifacts') 'checksum_manifest_properties'
if ($checksums.schema_version -ne 'issue-95-artifact-checksums.v1' -or $checksums.candidate_base -ne $CandidateSha -or $checksums.algorithm -ne 'SHA-256' -or [string]$checksums.verification_date -notmatch '^\d{4}-\d{2}-\d{2}$') { throw 'checksum_manifest_metadata_invalid' }
$checksumArtifacts = @($checksums.artifacts)
if ($checksumArtifacts.Count -eq 0) { throw 'checksum_manifest_empty' }
$checksumPaths = Get-ArtifactPaths $checksumArtifacts 'checksum_manifest'
Assert-OrdinalSet $checksumPaths $manifestPaths 'checksum_manifest_paths'
foreach ($artifact in $checksumArtifacts) {
    $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData((Get-GitBlobBytes $EvidenceSha $artifact.path))).ToLowerInvariant()
    if ($actual -ne $artifact.sha256) { throw ('checksum_mismatch={0}' -f $artifact.path) }
}

$attestation = Read-GitJson $AttestationSha $attestationPath
Assert-OrdinalSet @($attestation.PSObject.Properties.Name) @('schema_version', 'issue', 'functional_candidate_sha', 'evidence_materialization_sha', 'evidence_materialization_parent_sha', 'relationship', 'checksum_algorithm', 'artifacts_at_evidence_materialization', 'verification', 'publication') 'attestation_properties'
if ($attestation.schema_version -ne 'evidence-attestation.v1' -or $attestation.issue -ne 95 -or $attestation.functional_candidate_sha -ne $CandidateSha -or $attestation.evidence_materialization_sha -ne $EvidenceSha -or $attestation.evidence_materialization_parent_sha -ne $CandidateSha -or $attestation.checksum_algorithm -ne 'SHA-256' -or [string]::IsNullOrWhiteSpace([string]$attestation.relationship) -or @($attestation.verification.PSObject.Properties).Count -eq 0 -or @($attestation.publication.PSObject.Properties).Count -eq 0) { throw 'attestation_chain_binding_invalid' }
$attested = @($attestation.artifacts_at_evidence_materialization)
$actualAttestedPaths = Get-ArtifactPaths $attested 'attestation'
Assert-OrdinalSet $actualAttestedPaths $attestationArtifactPaths 'attestation_paths'
foreach ($artifact in $attested) {
    $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData((Get-GitBlobBytes $EvidenceSha $artifact.path))).ToLowerInvariant()
    if ($actual -ne $artifact.sha256) { throw ('attestation_hash_mismatch={0}' -f $artifact.path) }
}

Invoke-PinnedMatrixValidator
Write-Output 'evidence_chain=PASS'
