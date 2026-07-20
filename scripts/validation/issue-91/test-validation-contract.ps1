[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot 'validate-matrix.ps1'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('issue-91-matrix-{0}' -f [Guid]::NewGuid().ToString('N'))
$failed = $false

function Copy-Value($Value) { ($Value | ConvertTo-Json -Depth 50) | ConvertFrom-Json -Depth 50 }
function Invoke-Case([string] $Name, $Value, [int] $ExpectedExit) {
    $path = Join-Path $temporaryRoot ($Name + '.json')
    $Value | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    $output = @(& pwsh -NoProfile -File $validator -MatrixPath $path 2>&1)
    Write-Output ('case={0} exit={1}' -f $Name, $LASTEXITCODE)
    if ($LASTEXITCODE -ne $ExpectedExit) { $script:failed = $true; Write-Output ($output -join [Environment]::NewLine) }
}

$sha = '1111111111111111111111111111111111111111'
$row = [ordered]@{
    row_id = '91-E-001'; matrix_schema_version = 'validation-matrix.v1'; surface = 'synthetic'; operation = 'validate'
    profiles = [ordered]@{ collection=@(); content_access=@(); compatibility=@(); hook=@(); otel=@(); binding=@(); restart=@(); retention=@() }
    requirement_level = 'required'; applicability = 'applicable'; applicability_reason = $null; versions = [ordered]@{}
    expected_invariant = 'Synthetic validation succeeds.'
    evidence = @([ordered]@{ kind='automated'; reference='synthetic:test'; compatibility_basis='Exact synthetic candidate.' })
    actual_result = 'Synthetic observations matched.'; classification = 'passed'; severity = 'none'; blocker = $null
    retry_condition = $null; unverified_capability = $null; owner = 'Issue #91'; validation_sha = $sha
    validation_date = '2026-07-21'; environment_boundary = 'synthetic'
}
$base = [ordered]@{
    schema_version='validation-matrix.v1'; matrix_prep_sha=$sha; final_validation_sha=$sha; inventory_date='2026-07-21'
    environment_boundary='synthetic'; active_rows=@($row); future_registry_ref='synthetic:registry'
    evidence_ledger_refs=@('synthetic:ledger'); release_decision=[ordered]@{ decision='release_ready'; external_blockers=@() }
}

try {
    [void](New-Item -ItemType Directory -Path $temporaryRoot)
    Invoke-Case 'valid-ready' (Copy-Value $base) 0

    $case = Copy-Value $base; $case.active_rows = @($case.active_rows[0], (Copy-Value $case.active_rows[0])); Invoke-Case 'duplicate-row' $case 1
    $case = Copy-Value $base; $case.active_rows[0].validation_sha = '2222222222222222222222222222222222222222'; Invoke-Case 'sha-mismatch' $case 1
    $case = Copy-Value $base; $case.active_rows[0].evidence = @(); Invoke-Case 'missing-evidence' $case 1
    $case = Copy-Value $base; $case.release_decision.decision = 'release_blocked'; Invoke-Case 'decision-mismatch' $case 1
    $case = Copy-Value $base; $case.active_rows = @(); Invoke-Case 'empty-inventory' $case 1
    $case = Copy-Value $base; $case.active_rows[0].row_id='invalid'; $case.active_rows[0].matrix_schema_version='unknown'; $case.active_rows[0].requirement_level='invalid'; $case.active_rows[0].classification='garbage'; $case.active_rows[0].severity='invalid'; $case.active_rows[0].evidence[0].kind='invalid'; $case.active_rows[0].validation_date='invalid'; Invoke-Case 'closed-schema' $case 1

    $external = Copy-Value $base
    $external.active_rows[0].classification='blocked_external'; $external.active_rows[0].severity='medium'
    $external.active_rows[0].blocker='Synthetic provider unavailable.'; $external.active_rows[0].retry_condition='Provider becomes available.'
    $external.active_rows[0].unverified_capability='Synthetic live transport.'; $external.active_rows[0].evidence[0].kind='live'
    $external.release_decision.decision='release_ready_with_external_blockers'
    $external.release_decision.external_blockers=@([ordered]@{ row_id='91-E-001'; severity='medium'; blocker='Synthetic provider unavailable.'; retry_condition='Provider becomes available.'; unverified_capability='Synthetic live transport.' })
    Invoke-Case 'valid-external' $external 0
    $case = Copy-Value $external; $case.active_rows[0].unverified_capability=$null; Invoke-Case 'external-incomplete' $case 1

    $case = Copy-Value $base; $case.active_rows[0].classification='failed'; $case.active_rows[0].severity='high'; $case.active_rows[0].blocker='Synthetic invariant failure.'; $case.release_decision.decision='release_blocked'; Invoke-Case 'valid-failed' $case 0
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}

if ($failed) { Write-Output 'contract_self_test=FAIL'; exit 1 }
Write-Output 'contract_self_test=PASS cases=10'
exit 0
