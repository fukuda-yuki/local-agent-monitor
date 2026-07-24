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
$rowContractPath = 'docs/specifications/contracts/cost-analytics/v1/issue-91-validation-row-contract.json'
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
    $output = & git -C $repositoryRoot rev-parse --verify ($Sha + '^{commit}') 2>&1
    if ($LASTEXITCODE -ne 0 -or ($output | Out-String).Trim() -ne $Sha) {
        throw ('{0}_not_exact_commit' -f $Name)
    }
}

function Assert-Ancestor([string] $Ancestor, [string] $Descendant, [string] $Name) {
    & git -C $repositoryRoot merge-base --is-ancestor $Ancestor $Descendant
    if ($LASTEXITCODE -ne 0) { throw ('{0}_not_candidate_ancestor' -f $Name) }
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

function Assert-RunningVerifierPinned {
    $committed = Get-GitBlobBytes $CandidateSha 'scripts/validation/issue-95/verify-evidence-chain.ps1'
    $running = [IO.File]::ReadAllBytes($PSCommandPath)
    $committedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($committed))
    $runningHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($running))
    if ($committedHash -cne $runningHash) {
        throw 'verifier_working_copy_mismatch'
    }
}

function Assert-Tracked([string] $Commit, [string] $Path) {
    $null = Invoke-GitText @('cat-file', '-e', ($Commit + ':' + $Path))
}

function Assert-OrdinalSet([string[]] $Actual, [string[]] $Expected, [string] $Name) {
    $actualSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $expectedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Actual) { if (-not $actualSet.Add($item)) { throw ('{0}_duplicate' -f $Name) } }
    foreach ($item in $Expected) { if (-not $expectedSet.Add($item)) { throw ('{0}_expected_duplicate' -f $Name) } }
    if ($actualSet.Count -ne $expectedSet.Count) { throw ('{0}_invalid' -f $Name) }
    foreach ($item in $actualSet) { if (-not $expectedSet.Contains($item)) { throw ('{0}_invalid' -f $Name) } }
}

function Assert-ExactObjectValues($Actual, $Expected, [string] $Name, [string] $Candidate) {
    Assert-OrdinalSet @($Actual.PSObject.Properties.Name) @($Expected.PSObject.Properties.Name) ($Name + '_properties')
    foreach ($property in @($Expected.PSObject.Properties)) {
        $expectedValue = if ([string]$property.Value -eq '$candidate') { $Candidate } else { [string]$property.Value }
        if ([string]$Actual.($property.Name) -cne $expectedValue) {
            throw ('{0}_invalid' -f $Name)
        }
    }
}

function Assert-RowContract($Contract, $Handoff, $Matrix) {
    if ($Contract.schema_version -ne 'cost-analytics-validation-row-contract.v1' -or
        $Contract.surface_id -ne 'cost-analytics' -or
        $Handoff.surface_id -cne $Contract.surface_id -or
        $Handoff.row_contract_path -cne $rowContractPath) {
        throw 'row_contract_mismatch'
    }
    $contractRows = @($Contract.active_rows)
    $handoffRows = @($Handoff.active_rows)
    $matrixRows = @($Matrix.active_rows)
    Assert-OrdinalSet @($Handoff.active_row_ids) @($contractRows | ForEach-Object { [string]$_.row_id }) 'row_contract_ids'
    Assert-OrdinalSet @($handoffRows | ForEach-Object { [string]$_.row_id }) @($contractRows | ForEach-Object { [string]$_.row_id }) 'row_contract_handoff_ids'
    Assert-OrdinalSet @($matrixRows | ForEach-Object { [string]$_.row_id }) @($contractRows | ForEach-Object { [string]$_.row_id }) 'row_contract_matrix_ids'
    $contractFilters = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $profileAxes = @('collection', 'content_access', 'compatibility', 'hook', 'otel', 'binding', 'restart', 'retention')
    $contractProfileUnion = [ordered]@{}
    foreach ($axis in $profileAxes) {
        $contractProfileUnion[$axis] = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    }
    foreach ($contractRow in $contractRows) {
        $rowId = [string]$contractRow.row_id
        $handoffRow = @($handoffRows | Where-Object { $_.row_id -ceq $rowId })
        $matrixRow = @($matrixRows | Where-Object { $_.row_id -ceq $rowId })
        if ($handoffRow.Count -ne 1 -or $matrixRow.Count -ne 1 -or
            [string]$handoffRow[0].surface -cne [string]$contractRow.surface -or
            [string]$matrixRow[0].surface -cne [string]$contractRow.surface -or
            [string]$handoffRow[0].operation -cne [string]$contractRow.operation -or
            [string]$matrixRow[0].operation -cne [string]$contractRow.operation) {
            throw 'row_contract_mismatch'
        }
        Assert-OrdinalSet @($handoffRow[0].required_profiles.PSObject.Properties.Name) $profileAxes ('row_contract_handoff_profile_axes_' + $rowId)
        Assert-OrdinalSet @($contractRow.required_profiles.PSObject.Properties.Name) $profileAxes ('row_contract_profile_axes_' + $rowId)
        Assert-OrdinalSet @($matrixRow[0].profiles.PSObject.Properties.Name) $profileAxes ('row_contract_matrix_profile_axes_' + $rowId)
        foreach ($axis in $profileAxes) {
            Assert-OrdinalSet @($handoffRow[0].required_profiles.$axis) @($contractRow.required_profiles.$axis) ('row_contract_profiles_' + $rowId + '_' + $axis)
            Assert-OrdinalSet @($matrixRow[0].profiles.$axis) @($contractRow.required_profiles.$axis) ('row_contract_matrix_profiles_' + $rowId + '_' + $axis)
            foreach ($profile in @($contractRow.required_profiles.$axis)) {
                [void]$contractProfileUnion[$axis].Add([string]$profile)
            }
        }
        Assert-ExactObjectValues $handoffRow[0].versions $contractRow.versions ('row_contract_handoff_versions_' + $rowId) '$candidate'
        Assert-ExactObjectValues $matrixRow[0].versions $contractRow.versions ('row_contract_matrix_versions_' + $rowId) $CandidateSha
        Assert-OrdinalSet @($handoffRow[0].evidence_references) @($contractRow.evidence_references) ('row_contract_handoff_evidence_' + $rowId)
        Assert-OrdinalSet @($matrixRow[0].evidence | ForEach-Object { [string]$_.reference }) @($contractRow.evidence_references) ('row_contract_matrix_evidence_' + $rowId)
        Assert-OrdinalSet @($handoffRow[0].automated_test_filters) @($contractRow.automated_test_filters) ('row_contract_filters_' + $rowId)
        foreach ($filter in @($contractRow.automated_test_filters)) { [void]$contractFilters.Add([string]$filter) }
        if ($rowId -eq '91-L-095') {
            $expectedBlock = $contractRow.blocked_external_contract
            $actualBlock = $handoffRow[0].blocked_external_contract
            if ($null -eq $actualBlock -or
                [string]$actualBlock.severity -cne [string]$expectedBlock.severity -or
                [string]$actualBlock.blocker -cne [string]$expectedBlock.blocker -or
                [string]$actualBlock.retry_condition -cne [string]$expectedBlock.retry_condition -or
                [string]$actualBlock.unverified_capability -cne [string]$expectedBlock.unverified_capability) {
                throw 'live_blocker_contract_mismatch'
            }
            Assert-OrdinalSet @($actualBlock.required_providers) @($expectedBlock.required_providers) 'live_blocker_providers'
            Assert-OrdinalSet @($actualBlock.unverified_capabilities) @($expectedBlock.unverified_capabilities) 'live_blocker_capabilities'
            if ([string]$matrixRow[0].severity -cne [string]$expectedBlock.severity -or
                [string]$matrixRow[0].blocker -cne [string]$expectedBlock.blocker -or
                [string]$matrixRow[0].retry_condition -cne [string]$expectedBlock.retry_condition -or
                [string]$matrixRow[0].unverified_capability -cne [string]$expectedBlock.unverified_capability) {
                throw 'live_blocker_contract_mismatch'
            }
        }
        elseif ($null -ne $contractRow.blocked_external_contract -or $null -ne $handoffRow[0].blocked_external_contract) {
            throw 'row_contract_mismatch'
        }
    }
    Assert-OrdinalSet @($Handoff.required_profiles.PSObject.Properties.Name) $profileAxes 'row_contract_profile_ledger_axes'
    foreach ($axis in $profileAxes) {
        Assert-OrdinalSet @($Handoff.required_profiles.$axis) @($contractProfileUnion[$axis]) ('row_contract_profile_ledger_' + $axis)
    }
    Assert-OrdinalSet @($Handoff.automated_test_filters) @($contractFilters) 'row_contract_filter_ledger'
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

function Read-AuthenticatedRedFixture(
    [string] $CorrectionSha,
    [string] $FixtureName) {
    $temporaryScript = Join-Path ([IO.Path]::GetTempPath()) (
        'issue-95-red-fixture-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    try {
        [IO.File]::WriteAllBytes(
            $temporaryScript,
            (Get-GitBlobBytes $CorrectionSha 'scripts/validation/issue-95/test-evidence-chain.ps1'))
        $output = & pwsh -NoLogo -NoProfile -File $temporaryScript -DescribeFixture $FixtureName 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'red_failure_fixture_not_authenticated' }
        try {
            $fixture = (($output | Out-String).Trim() | ConvertFrom-Json)
        }
        catch {
            throw 'red_failure_fixture_not_authenticated'
        }
        Assert-OrdinalSet @($fixture.PSObject.Properties.Name) @('name', 'expected_code') 'red_failure_fixture_registry_fields'
        return $fixture
    }
    finally {
        if (Test-Path -LiteralPath $temporaryScript) {
            Remove-Item -LiteralPath $temporaryScript -Force
        }
    }
}

function Assert-LiveValidationContract([string] $MatrixPrepSha) {
    $path = 'docs/sprints/issue-95-cost-analytics/live-validation.md'
    $text = [Text.Encoding]::UTF8.GetString((Get-GitBlobBytes $EvidenceSha $path))
    function Get-SingleLineMatch([string] $Pattern) {
        $matches = @([Regex]::Matches($text, $Pattern))
        if ($matches.Count -ne 1) { throw 'live_validation_contract_invalid' }
        return $matches[0]
    }
    $requiredSections = @(
        '# Issue #95 Live Validation',
        '## Candidate binding',
        '## Required commands and results',
        '## RED and failure history',
        '## OS-specific security coverage',
        '## Live validation')
    foreach ($section in $requiredSections) {
        $null = Get-SingleLineMatch (
            '(?m)^' + [Regex]::Escape($section) + '$')
    }
    $matrixPrepLine = Get-SingleLineMatch '(?m)^matrix_prep_sha: (?<value>\S+)$'
    $candidateLine = Get-SingleLineMatch '(?m)^final_validation_sha: (?<value>\S+)$'
    $commandCount = Get-SingleLineMatch '(?m)^required_command_count: (?<count>\S+)$'
    $commandFailures = Get-SingleLineMatch '(?m)^required_command_failures: (?<count>\S+)$'
    $redFailureCount = Get-SingleLineMatch '(?m)^red_failure_count: (?<count>\S+)$'
    $validationOs = Get-SingleLineMatch '(?m)^validation_os: (?<os>\S+)$'
    $applicableOs = Get-SingleLineMatch '(?m)^applicable_os_security_tests: (?<value>.+)$'
    $notApplicableOs = Get-SingleLineMatch '(?m)^not_applicable_os_security_tests: (?<value>.+)$'
    $prerequisiteSkips = Get-SingleLineMatch '(?m)^applicable_security_prerequisite_skips: (?<count>\S+)$'
    $liveClassification = Get-SingleLineMatch '(?m)^91-L-095: (?<value>\S+)$'
    $unverifiedCapability = Get-SingleLineMatch '(?m)^unverified_capability: (?<value>\S.+)$'
    $osCoverageValid = $false
    if ($prerequisiteSkips.Groups['count'].Value -eq '0') {
        if ($validationOs.Groups['os'].Value -eq 'windows') {
            $osCoverageValid =
                $applicableOs.Groups['value'].Value -eq 'file_symlink=passed,directory_symlink=passed,windows_open_mutation=passed,windows_normalized_segment=passed' -and
                $notApplicableOs.Groups['value'].Value -eq 'linux_fifo=not_applicable'
        }
        elseif ($validationOs.Groups['os'].Value -eq 'linux') {
            $osCoverageValid =
                $applicableOs.Groups['value'].Value -eq 'file_symlink=passed,directory_symlink=passed,linux_fifo=passed' -and
                $notApplicableOs.Groups['value'].Value -eq 'windows_open_mutation=not_applicable,windows_normalized_segment=not_applicable'
        }
    }
    $checks = @(
        $matrixPrepLine.Groups['value'].Value -eq $MatrixPrepSha
        $candidateLine.Groups['value'].Value -eq $CandidateSha
        $commandCount.Groups['count'].Value -match '^[1-9][0-9]*$'
        $commandFailures.Groups['count'].Value -eq '0'
        $osCoverageValid
        $redFailureCount.Groups['count'].Value -match '^[1-9][0-9]*$'
        $liveClassification.Groups['value'].Value -eq 'blocked_external'
        -not [string]::IsNullOrWhiteSpace($unverifiedCapability.Groups['value'].Value))
    if ($checks -contains $false) {
        throw 'live_validation_contract_invalid'
    }
    $filterLines = @([Regex]::Matches(
        $text,
        '(?m)^automated_filter: (?<value>FullyQualifiedName~\S+)$') |
        ForEach-Object { $_.Groups['value'].Value })
    $contract = Read-GitJson $CandidateSha $rowContractPath
    foreach ($contractRow in @($contract.active_rows)) {
        $rowId = [string]$contractRow.row_id
        $expectedReference = $path + '#' + $rowId.ToLowerInvariant()
        Assert-OrdinalSet @($contractRow.evidence_references) @($expectedReference) ('live_evidence_reference_' + $rowId)
        $anchorMatches = @([Regex]::Matches(
            $text,
            '(?m)^## ' + [Regex]::Escape($rowId) + '$'))
        if ($anchorMatches.Count -ne 1) {
            throw ('live_evidence_anchor_{0}_invalid' -f $rowId)
        }
    }
    $expectedFilters = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($filter in @($contract.active_rows | ForEach-Object { @($_.automated_test_filters) })) {
        [void]$expectedFilters.Add([string]$filter)
    }
    Assert-OrdinalSet $filterLines @($expectedFilters) 'live_automated_filters'
    $commandResults = @([Regex]::Matches(
        $text,
        '(?m)^command_result: \S.+ \| exit=0 \| result=passed$'))
    $allCommandResults = @([Regex]::Matches(
        $text,
        '(?m)^command_result: .+$'))
    if ($commandResults.Count -lt 4) { throw 'live_validation_contract_invalid' }
    if ($commandResults.Count -ne $allCommandResults.Count) {
        throw 'live_validation_contract_invalid'
    }
    if ($commandResults.Count -ne [int]$commandCount.Groups['count'].Value) {
        throw 'live_validation_contract_invalid'
    }
    foreach ($commandPattern in @(
        'dotnet build CopilotAgentObservability\.slnx',
        'pwsh scripts\\test\\install-playwright-chromium\.ps1',
        'dotnet test CopilotAgentObservability\.slnx',
        'pwsh scripts\\validation\\issue-95\\test-evidence-chain\.ps1 -VerifierPath \S.+ -MatrixValidatorPath \S.+ -MatrixFixturePath \S+')) {
        if ($text -notmatch ('(?m)^command_result: ' + $commandPattern + ' \| exit=0 \| result=passed$')) {
            throw 'live_validation_contract_invalid'
        }
    }
    $redFailures = @([Regex]::Matches(
        $text,
        '(?m)^red_failure: (?<code>[a-z0-9._-]+) \| command=(?<command>\S.+) \| observed=failed:(?<observed>[^|]+) \| expected_code=(?<expected>[a-z0-9._=-]+) \| executable_fixture=(?<fixture>[a-z0-9._-]+) \| corrected_by=(?<sha>[0-9a-f]{40})$'))
    if ($redFailures.Count -ne [int]$redFailureCount.Groups['count'].Value) {
        throw 'live_validation_contract_invalid'
    }
    foreach ($failure in $redFailures) {
        $correctionSha = $failure.Groups['sha'].Value
        $fixtureName = $failure.Groups['fixture'].Value
        $expectedCode = $failure.Groups['expected'].Value
        Assert-ExactCommit $correctionSha 'red_failure_correction'
        Assert-Ancestor $MatrixPrepSha $correctionSha 'red_failure_correction'
        Assert-Ancestor $correctionSha $CandidateSha 'red_failure_correction'
        $correctionPaths = @(Invoke-GitText @('diff-tree', '--no-commit-id', '--name-only', '-r', $correctionSha) -split "`r?`n")
        if ($correctionPaths -notcontains 'scripts/validation/issue-95/test-evidence-chain.ps1') {
            throw 'red_failure_correction_not_executable'
        }
        $registeredFixture = Read-AuthenticatedRedFixture $correctionSha $fixtureName
        if ([string]$registeredFixture.name -cne $fixtureName -or
            [string]$registeredFixture.expected_code -cne $expectedCode) {
            throw 'red_failure_fixture_not_authenticated'
        }
    }
}

Assert-ExactCommit $CandidateSha 'candidate'
Assert-ExactCommit $EvidenceSha 'evidence'
Assert-ExactCommit $AttestationSha 'attestation'
Assert-RunningVerifierPinned
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
$matrixPrepSha = [string]$matrix.matrix_prep_sha
if ($matrixPrepSha -notmatch '^[0-9a-f]{40}$') { throw 'matrix_prep_not_exact_commit' }
Assert-ExactCommit $matrixPrepSha 'matrix_prep'
Assert-Ancestor $matrixPrepSha $CandidateSha 'matrix_prep'
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
$rowContract = Read-GitJson $CandidateSha $rowContractPath
Assert-RowContract $rowContract $handoff $matrix
Assert-LiveValidationContract $matrixPrepSha

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
