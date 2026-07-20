[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]] $InputPath,
    [Parameter(Mandatory)]
    [ValidateSet('api', 'sse', 'ui', 'action', 'log', 'evidence')]
    [string] $OutputType,
    [string] $CorpusPath = (Join-Path $PSScriptRoot 'fixtures\secret-corpus.v1.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $CorpusPath -PathType Leaf)) {
    Write-Output 'scan_result=ERROR reason=corpus_missing'
    exit 2
}

try {
    $corpus = Get-Content -LiteralPath $CorpusPath -Raw | ConvertFrom-Json -Depth 20
}
catch {
    Write-Output 'scan_result=ERROR reason=corpus_invalid'
    exit 2
}

if ($corpus.schema_version -ne 'validation-secret-corpus.v1') {
    Write-Output 'scan_result=ERROR reason=corpus_version_unsupported'
    exit 2
}

$supported = @($corpus.supported_transformations)
$allowedTransforms = @('plain', 'json_escape', 'html_entity', 'url_percent', 'base64_utf8', 'sha256_prefix_12')
if ($supported.Count -ne $allowedTransforms.Count -or
    @(Compare-Object -ReferenceObject $allowedTransforms -DifferenceObject $supported).Count -ne 0) {
    Write-Output 'scan_result=ERROR reason=transformation_contract_invalid'
    exit 2
}

function Convert-Marker {
    param(
        [Parameter(Mandatory)][string] $Marker,
        [Parameter(Mandatory)][string] $Transformation
    )

    switch ($Transformation) {
        'plain' { return $Marker }
        'json_escape' {
            $serialized = $Marker | ConvertTo-Json -Compress
            return $serialized.Substring(1, $serialized.Length - 2)
        }
        'html_entity' { return [System.Net.WebUtility]::HtmlEncode($Marker) }
        'url_percent' { return [System.Uri]::EscapeDataString($Marker) }
        'base64_utf8' {
            return [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($Marker))
        }
        'sha256_prefix_12' {
            $digest = [System.Security.Cryptography.SHA256]::HashData(
                [System.Text.Encoding]::UTF8.GetBytes($Marker))
            return [Convert]::ToHexString($digest).ToLowerInvariant().Substring(0, 12)
        }
        default { throw 'Unsupported transformation.' }
    }
}

$targets = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
foreach ($path in $InputPath) {
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Output ('scan_result=ERROR output_type={0} reason=required_target_missing' -f $OutputType)
        exit 2
    }

    $item = Get-Item -LiteralPath $path
    if ($item -is [System.IO.DirectoryInfo]) {
        foreach ($file in Get-ChildItem -LiteralPath $item.FullName -File -Recurse -Force) {
            $targets.Add($file)
        }
    }
    else {
        $targets.Add($item)
    }
}

if ($targets.Count -eq 0) {
    Write-Output ('scan_result=ERROR output_type={0} reason=required_target_empty' -f $OutputType)
    exit 2
}

$matched = [System.Collections.Generic.List[string]]::new()
$scannedVariantCount = 0
foreach ($file in $targets) {
    if ($file.Length -eq 0) {
        Write-Output ('scan_result=ERROR output_type={0} reason=required_target_zero_bytes' -f $OutputType)
        exit 2
    }

    try {
        $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.UTF8Encoding]::new($false, $true))
    }
    catch {
        Write-Output ('scan_result=ERROR output_type={0} reason=target_not_utf8' -f $OutputType)
        exit 2
    }

    foreach ($case in $corpus.cases) {
        foreach ($transformation in $case.expected_transformations) {
            $scannedVariantCount++
            $needle = Convert-Marker -Marker ([string]$case.marker) -Transformation ([string]$transformation)
            if ($text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $matched.Add(('{0}:{1}' -f $case.case_id, $transformation))
            }
        }
    }
}

if ($matched.Count -gt 0) {
    foreach ($match in $matched | Sort-Object -Unique) {
        Write-Output ('match={0}' -f $match)
    }
    Write-Output ('scan_result=FAIL output_type={0} files={1} variants={2} matches={3}' -f
        $OutputType, $targets.Count, $scannedVariantCount, $matched.Count)
    exit 1
}

Write-Output ('scan_result=PASS output_type={0} files={1} variants={2} matches=0' -f
    $OutputType, $targets.Count, $scannedVariantCount)
exit 0
