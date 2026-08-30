[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scanner = Join-Path $PSScriptRoot 'scan-outputs.ps1'
$corpusPath = Join-Path $PSScriptRoot 'fixtures\secret-corpus.v1.json'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('issue-91-scanner-{0}' -f [Guid]::NewGuid().ToString('N'))
$failure = $false

function Invoke-Scanner {
    param(
        [Parameter(Mandatory)][string] $Target,
        [ValidateSet('api', 'sse', 'ui', 'action', 'log', 'evidence')][string] $OutputType = 'evidence'
    )

    $output = @(& pwsh -NoProfile -File $scanner -InputPath $Target -OutputType $OutputType -CorpusPath $corpusPath 2>&1)
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine) }
}

try {
    [void](New-Item -ItemType Directory -Path $temporaryRoot)
    $cleanPath = Join-Path $temporaryRoot 'clean.txt'
    $leakPath = Join-Path $temporaryRoot 'leak.txt'
    $negativePath = Join-Path $temporaryRoot 'negative.txt'
    $emptyPath = Join-Path $temporaryRoot 'empty.txt'
    [System.IO.File]::WriteAllText($cleanPath, 'content_state=not_captured', [System.Text.UTF8Encoding]::new($false))

    $corpus = Get-Content -LiteralPath $corpusPath -Raw | ConvertFrom-Json -Depth 20
    $transformationCount = 0
    [System.IO.File]::WriteAllText(
        $negativePath,
        (@($corpus.negative_cases) -join [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))

    foreach ($outputType in @('api', 'sse', 'ui', 'action', 'log', 'evidence')) {
        $clean = Invoke-Scanner $cleanPath -OutputType $outputType
        Write-Output ('case=clean:{0} exit={1} scanner_result={2}' -f $outputType, $clean.ExitCode, $(if ($clean.Output -match 'scan_result=PASS') { 'PASS' } else { 'OTHER' }))
        if ($clean.Output -notmatch ('scan_result=PASS output_type={0}' -f $outputType)) { Write-Output ('clean_diagnostic={0}' -f $clean.Output) }
        if ($clean.ExitCode -ne 0 -or $clean.Output -notmatch ('scan_result=PASS output_type={0}' -f $outputType)) { $failure = $true }
    }

    foreach ($case in @($corpus.cases)) {
        $marker = [string]$case.marker
        $caseValues = [System.Collections.Generic.List[string]]::new()
        foreach ($transformation in @($case.expected_transformations)) {
            $digest = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($marker))
            $serialized = $marker | ConvertTo-Json -Compress
            $transformedValue = switch ([string]$transformation) {
                'plain' { $marker }
                'json_escape' { $serialized.Substring(1, $serialized.Length - 2) }
                'html_entity' { [System.Net.WebUtility]::HtmlEncode($marker) }
                'url_percent' { [System.Uri]::EscapeDataString($marker) }
                'base64_utf8' { [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($marker)) }
                'sha256_prefix_12' { [Convert]::ToHexString($digest).ToLowerInvariant().Substring(0, 12) }
                default { throw "Unsupported self-test transformation: $transformation" }
            }
            $caseValues.Add($transformedValue)
            $transformationCount++
        }
        [System.IO.File]::WriteAllText($leakPath, ($caseValues -join [Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))
        $result = Invoke-Scanner $leakPath
        Write-Output ('case=forbidden:{0} exit={1} scanner_result={2}' -f $case.case_id, $result.ExitCode, $(if ($result.Output -match 'scan_result=FAIL') { 'FAIL' } else { 'OTHER' }))
        if ($result.ExitCode -ne 1 -or $result.Output -notmatch 'scan_result=FAIL') { $failure = $true }
        foreach ($transformation in @($case.expected_transformations)) {
            if ($result.Output -notmatch [Regex]::Escape(('match={0}:{1}' -f $case.case_id, $transformation))) { $failure = $true }
        }
        if ($result.Output.Contains($marker, [StringComparison]::Ordinal)) { $failure = $true }
    }

    $negative = Invoke-Scanner $negativePath
    if ($negative.ExitCode -ne 0 -or $negative.Output -notmatch 'scan_result=PASS') { $failure = $true }

    $missing = Invoke-Scanner (Join-Path $temporaryRoot 'missing.txt')
    Write-Output ('case=missing exit={0} scanner_result={1}' -f $missing.ExitCode, $(if ($missing.Output -match 'required_target_missing') { 'ERROR' } else { 'OTHER' }))
    if ($missing.Output -notmatch 'required_target_missing') { Write-Output ('missing_diagnostic={0}' -f $missing.Output) }
    if ($missing.ExitCode -ne 2 -or $missing.Output -notmatch 'required_target_missing') { $failure = $true }

    [System.IO.File]::WriteAllBytes($emptyPath, [byte[]]::new(0))
    $empty = Invoke-Scanner $emptyPath
    Write-Output ('case=zero-bytes exit={0} scanner_result={1}' -f $empty.ExitCode, $(if ($empty.Output -match 'required_target_zero_bytes') { 'ERROR' } else { 'OTHER' }))
    if ($empty.ExitCode -ne 2 -or $empty.Output -notmatch 'required_target_zero_bytes') { $failure = $true }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

if ($failure) {
    Write-Output 'self_test_result=FAIL'
    exit 1
}

Write-Output ('transformation_cases={0}' -f $transformationCount)
Write-Output ('negative_cases={0}' -f @($corpus.negative_cases).Count)
Write-Output 'self_test_result=PASS'
exit 0
