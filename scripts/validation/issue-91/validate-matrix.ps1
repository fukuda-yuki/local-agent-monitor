[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $MatrixPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$errors = [System.Collections.Generic.List[string]]::new()

function Add-ValidationError([string] $Code) {
    $errors.Add($Code)
}

function Test-RequiredProperties($Value, [string[]] $Names, [string] $Prefix) {
    $present = @($Value.PSObject.Properties.Name)
    foreach ($name in $Names) {
        if ($name -notin $present) { Add-ValidationError (('{0}_required_{1}' -f $Prefix, $name)) }
    }
}

if (-not (Test-Path -LiteralPath $MatrixPath -PathType Leaf)) {
    Write-Output 'matrix_validation=ERROR reason=matrix_missing'
    exit 2
}

try {
    $matrix = Get-Content -LiteralPath $MatrixPath -Raw | ConvertFrom-Json -Depth 50
}
catch {
    Write-Output 'matrix_validation=ERROR reason=matrix_invalid_json'
    exit 2
}

Test-RequiredProperties $matrix @(
    'schema_version', 'matrix_prep_sha', 'final_validation_sha', 'inventory_date', 'environment_boundary',
    'active_rows', 'future_registry_ref', 'evidence_ledger_refs', 'release_decision') 'matrix'
if ($errors.Count -gt 0) {
    foreach ($code in @($errors | Sort-Object -Unique)) { Write-Output ('validation_error={0}' -f $code) }
    Write-Output ('matrix_validation=FAIL errors={0}' -f $errors.Count)
    exit 1
}

if ($matrix.schema_version -ne 'validation-matrix.v1') { Add-ValidationError 'schema_version' }
if ([string]$matrix.matrix_prep_sha -notmatch '^[0-9a-f]{40}$') { Add-ValidationError 'matrix_prep_sha' }
if ([string]$matrix.final_validation_sha -notmatch '^[0-9a-f]{40}$') { Add-ValidationError 'final_validation_sha' }
if ([string]$matrix.inventory_date -notmatch '^\d{4}-\d{2}-\d{2}$') { Add-ValidationError 'inventory_date' }
if ([string]::IsNullOrWhiteSpace([string]$matrix.environment_boundary)) { Add-ValidationError 'environment_boundary' }
if ([string]::IsNullOrWhiteSpace([string]$matrix.future_registry_ref)) { Add-ValidationError 'future_registry_ref' }
if (@($matrix.evidence_ledger_refs).Count -eq 0) { Add-ValidationError 'evidence_ledger_refs' }
Test-RequiredProperties $matrix.release_decision @('decision', 'external_blockers') 'release_decision'
if ($errors.Count -gt 0) {
    foreach ($code in @($errors | Sort-Object -Unique)) { Write-Output ('validation_error={0}' -f $code) }
    Write-Output ('matrix_validation=FAIL errors={0}' -f $errors.Count)
    exit 1
}

$rows = @($matrix.active_rows)
if ($rows.Count -eq 0) { Add-ValidationError 'active_rows_empty' }
$rowIds = @($rows | ForEach-Object { [string]$_.row_id })
if (@($rowIds | Sort-Object -Unique).Count -ne $rowIds.Count) { Add-ValidationError 'duplicate_row_id' }

foreach ($row in $rows) {
    $rowRequired = @(
        'row_id', 'matrix_schema_version', 'surface', 'operation', 'profiles', 'requirement_level',
        'applicability', 'applicability_reason', 'versions', 'expected_invariant', 'evidence',
        'actual_result', 'classification', 'severity', 'blocker', 'retry_condition',
        'unverified_capability', 'owner', 'validation_sha', 'validation_date', 'environment_boundary')
    $beforeRequired = $errors.Count
    Test-RequiredProperties $row $rowRequired 'row'
    if ($errors.Count -gt $beforeRequired) { continue }

    $classification = [string]$row.classification
    $evidence = @($row.evidence)
    if ([string]$row.row_id -notmatch '^91-[A-Z]-[0-9]{3}$') { Add-ValidationError 'row_id' }
    if ([string]$row.matrix_schema_version -ne 'validation-matrix.v1') { Add-ValidationError 'row_schema_version' }
    if ([string]$row.surface -eq '' -or [string]$row.operation -eq '') { Add-ValidationError 'row_surface_operation' }
    if ([string]$row.requirement_level -notin @('required', 'optional')) { Add-ValidationError 'requirement_level' }
    if ([string]$row.applicability -notin @('applicable', 'not_applicable')) { Add-ValidationError 'applicability' }
    if ($classification -notin @('passed', 'failed', 'blocked_external', 'not_applicable', 'not_attempted')) { Add-ValidationError 'classification' }
    if ([string]$row.severity -notin @('none', 'low', 'medium', 'high', 'critical')) { Add-ValidationError 'severity' }
    if ([string]$row.validation_date -notmatch '^\d{4}-\d{2}-\d{2}$') { Add-ValidationError 'validation_date' }
    if ([string]::IsNullOrWhiteSpace([string]$row.expected_invariant) -or [string]::IsNullOrWhiteSpace([string]$row.owner) -or [string]::IsNullOrWhiteSpace([string]$row.environment_boundary)) { Add-ValidationError 'row_text_required' }
    Test-RequiredProperties $row.profiles @('collection', 'content_access', 'compatibility', 'hook', 'otel', 'binding', 'restart', 'retention') 'profiles'
    foreach ($item in $evidence) {
        Test-RequiredProperties $item @('kind', 'reference', 'compatibility_basis') 'evidence'
        if (@($item.PSObject.Properties.Name) -contains 'kind' -and [string]$item.kind -notin @('automated', 'live', 'historical')) { Add-ValidationError 'evidence_kind' }
    }
    if ([string]$row.validation_sha -ne [string]$matrix.final_validation_sha) { Add-ValidationError 'row_sha_mismatch' }
    if ([string]::IsNullOrWhiteSpace([string]$row.actual_result) -and $classification -ne 'not_attempted') { Add-ValidationError 'actual_result_missing' }
    if ($evidence.Count -eq 0 -and $classification -ne 'not_attempted') { Add-ValidationError 'evidence_missing' }

    if ($classification -eq 'not_applicable') {
        if ([string]$row.applicability -ne 'not_applicable' -or [string]::IsNullOrWhiteSpace([string]$row.applicability_reason)) {
            Add-ValidationError 'not_applicable_contract_missing'
        }
    }
    elseif ([string]$row.applicability -ne 'applicable') {
        Add-ValidationError 'applicability_classification_mismatch'
    }

    if ($classification -eq 'blocked_external') {
        if ([string]$row.severity -eq 'none' -or
            [string]::IsNullOrWhiteSpace([string]$row.blocker) -or
            [string]::IsNullOrWhiteSpace([string]$row.retry_condition) -or
            [string]::IsNullOrWhiteSpace([string]$row.unverified_capability) -or
            -not @($evidence | Where-Object { $_.kind -eq 'live' })) {
            Add-ValidationError 'external_blocker_incomplete'
        }
    }
    elseif ($classification -eq 'failed') {
        if ([string]$row.severity -eq 'none' -or [string]::IsNullOrWhiteSpace([string]$row.blocker)) {
            Add-ValidationError 'failure_detail_incomplete'
        }
    }
    elseif ($classification -in @('passed', 'not_applicable')) {
        if ([string]$row.severity -ne 'none' -or $null -ne $row.blocker -or $null -ne $row.retry_condition -or $null -ne $row.unverified_capability) {
            Add-ValidationError 'success_blocker_fields_present'
        }
    }
}

$validClassifications = @('passed', 'failed', 'blocked_external', 'not_applicable', 'not_attempted')
$hasUnknown = @($rows | Where-Object { [string]$_.classification -notin $validClassifications }).Count -gt 0
$hasBlocking = $hasUnknown -or @($rows | Where-Object { $_.classification -in @('failed', 'not_attempted') }).Count -gt 0
$externalRows = @($rows | Where-Object { $_.classification -eq 'blocked_external' })
$expectedDecision = if ($hasBlocking) { 'release_blocked' } elseif ($externalRows.Count -gt 0) { 'release_ready_with_external_blockers' } else { 'release_ready' }
if ([string]$matrix.release_decision.decision -notin @('release_ready', 'release_ready_with_external_blockers', 'release_blocked')) { Add-ValidationError 'release_decision' }
if ([string]$matrix.release_decision.decision -ne $expectedDecision) { Add-ValidationError 'release_aggregate_mismatch' }

$summaries = @($matrix.release_decision.external_blockers)
if ($summaries.Count -ne $externalRows.Count) { Add-ValidationError 'external_blocker_summary_count' }
foreach ($row in $externalRows) {
    $summary = @($summaries | Where-Object { $_.row_id -eq $row.row_id })
    if ($summary.Count -ne 1 -or
        [string]$summary[0].severity -ne [string]$row.severity -or
        [string]$summary[0].blocker -ne [string]$row.blocker -or
        [string]$summary[0].retry_condition -ne [string]$row.retry_condition -or
        [string]$summary[0].unverified_capability -ne [string]$row.unverified_capability) {
        Add-ValidationError 'external_blocker_summary_mismatch'
    }
}

if ($errors.Count -gt 0) {
    foreach ($code in @($errors | Sort-Object -Unique)) { Write-Output ('validation_error={0}' -f $code) }
    Write-Output ('matrix_validation=FAIL errors={0}' -f $errors.Count)
    exit 1
}

Write-Output ('matrix_validation=PASS rows={0} decision={1}' -f $rows.Count, $expectedDecision)
exit 0
