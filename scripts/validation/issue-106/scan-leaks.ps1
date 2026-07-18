[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Marker,
    [string] $RepositoryRoot,
    [string[]] $EvidencePath = @(),
    [string] $LogDirectory,
    [string[]] $SanitizedOutputPath = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Marker)) {
    throw 'Marker must be a non-empty runtime value.'
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
}

$markerReference = [Convert]::ToHexString(
    [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($Marker))).ToLowerInvariant().Substring(0, 12)
$script:failure = $false

function Protect-DisplayText {
    param([AllowEmptyString()][string] $Text)

    if ($null -eq $Text) {
        return ''
    }

    $builder = [System.Text.StringBuilder]::new()
    $position = 0
    while ($true) {
        $index = $Text.IndexOf($Marker, $position, [StringComparison]::OrdinalIgnoreCase)
        if ($index -lt 0) {
            if ($position -lt $Text.Length) {
                [void]$builder.Append($Text.Substring($position))
            }
            break
        }

        if ($index -gt $position) {
            [void]$builder.Append($Text.Substring($position, $index - $position))
        }
        [void]$builder.Append('[MARKER_REDACTED]')
        $position = $index + $Marker.Length
    }

    return $builder.ToString()
}

function Test-BytePrefix {
    param(
        [Parameter(Mandatory)][byte[]] $Bytes,
        [Parameter(Mandatory)][byte[]] $Prefix
    )

    if ($Bytes.Length -lt $Prefix.Length) {
        return $false
    }

    for ($index = 0; $index -lt $Prefix.Length; $index++) {
        if ($Bytes[$index] -ne $Prefix[$index]) {
            return $false
        }
    }

    return $true
}

function Get-TextDecodings {
    param([Parameter(Mandatory)][byte[]] $Bytes)

    $encoding = $null
    $encodingName = $null
    $payloadOffset = 0

    if (Test-BytePrefix $Bytes ([byte[]](0xFF, 0xFE, 0x00, 0x00))) {
        $encoding = [System.Text.UTF32Encoding]::new($false, $false, $true)
        $encodingName = 'utf-32-le-bom'
        $payloadOffset = 4
    }
    elseif (Test-BytePrefix $Bytes ([byte[]](0x00, 0x00, 0xFE, 0xFF))) {
        $encoding = [System.Text.UTF32Encoding]::new($true, $false, $true)
        $encodingName = 'utf-32-be-bom'
        $payloadOffset = 4
    }
    elseif (Test-BytePrefix $Bytes ([byte[]](0xEF, 0xBB, 0xBF))) {
        $encoding = [System.Text.UTF8Encoding]::new($false, $true)
        $encodingName = 'utf-8-bom'
        $payloadOffset = 3
    }
    elseif (Test-BytePrefix $Bytes ([byte[]](0xFF, 0xFE))) {
        $encoding = [System.Text.UnicodeEncoding]::new($false, $false, $true)
        $encodingName = 'utf-16-le-bom'
        $payloadOffset = 2
    }
    elseif (Test-BytePrefix $Bytes ([byte[]](0xFE, 0xFF))) {
        $encoding = [System.Text.UnicodeEncoding]::new($true, $false, $true)
        $encodingName = 'utf-16-be-bom'
        $payloadOffset = 2
    }

    if ($null -ne $encoding) {
        $payload = if ($payloadOffset -lt $Bytes.Length) {
            [byte[]]$Bytes[$payloadOffset..($Bytes.Length - 1)]
        }
        else {
            [byte[]]@()
        }

        return [pscustomobject]@{
            Name = $encodingName
            Text = $encoding.GetString($payload)
        }
    }

    $containsNul = $false
    foreach ($byte in $Bytes) {
        if ($byte -eq 0) {
            $containsNul = $true
            break
        }
    }

    $decodings = [System.Collections.Generic.List[object]]::new()
    if ($containsNul) {
        $decodings.Add([pscustomobject]@{
                Name = 'utf-16-le-no-bom'
                Text = ([System.Text.UnicodeEncoding]::new($false, $false, $true)).GetString($Bytes)
            })
        $decodings.Add([pscustomobject]@{
                Name = 'utf-16-be-no-bom'
                Text = ([System.Text.UnicodeEncoding]::new($true, $false, $true)).GetString($Bytes)
            })

        try {
            $decodings.Add([pscustomobject]@{
                    Name = 'utf-8-lenient'
                    Text = ([System.Text.UTF8Encoding]::new($false, $false)).GetString($Bytes)
                })
        }
        catch {
            # Keep the UTF-16 interpretations when lenient UTF-8 cannot decode.
        }
    }
    else {
        $decodings.Add([pscustomobject]@{
                Name = 'utf-8'
                Text = ([System.Text.UTF8Encoding]::new($false, $true)).GetString($Bytes)
            })
    }

    return $decodings.ToArray()
}

function Read-TextSafely {
    param([Parameter(Mandatory)][string] $Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -eq 0) {
        return @([pscustomobject]@{ Name = 'empty'; Text = '' })
    }

    return @(Get-TextDecodings $bytes)
}

$leakPatterns = [ordered]@{
    'credential-shaped string' = '(?i)\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{20,}|xox[baprs]-[A-Za-z0-9-]{16,})\b'
    'authorization header' = '(?i)(?:\bAuthorization\s*:\s*(?:Bearer|Basic)\s+\S+|["'']authorization["'']\s*:\s*["''][^"'']+["''])'
    'absolute user-profile path' = '(?i)(?:[A-Z]:\\Users\\[A-Za-z0-9._-]+|/home/[A-Za-z0-9._-]+|/Users/[A-Za-z0-9._-]+)'
}

function Add-Failure {
    $script:failure = $true
}

function Scan-Text {
    param(
        [Parameter(Mandatory)][string] $Target,
        [Parameter(Mandatory)][string] $Scope,
        [Parameter(Mandatory)][AllowEmptyString()][string] $Text,
        [string] $EncodingName = 'text'
    )

    $safeTarget = Protect-DisplayText $Target
    $markerHit = $Text.IndexOf($Marker, [StringComparison]::OrdinalIgnoreCase) -ge 0
    if ($markerHit) {
        Write-Output ('[{0}] marker: MATCH ({1}; encoding={2})' -f $Scope, $safeTarget, $EncodingName)
        Add-Failure
    }
    else {
        Write-Output ('[{0}] marker: NO-MATCH ({1}; encoding={2})' -f $Scope, $safeTarget, $EncodingName)
    }

    foreach ($pattern in $leakPatterns.GetEnumerator()) {
        if ([regex]::IsMatch($Text, $pattern.Value, [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
            # Local runtime logs are uncommitted; the machine-boundary rule applies to repository and evidence content.
            if ($Scope -eq 'application-logs' -and $pattern.Key -eq 'absolute user-profile path') {
                Write-Output ('[{0}] generic-leak: WARN ({1}; encoding={2}) pattern={3}' -f $Scope, $safeTarget, $EncodingName, $pattern.Key)
            }
            else {
                Write-Output ('[{0}] generic-leak: MATCH ({1}; encoding={2}) pattern={3}' -f $Scope, $safeTarget, $EncodingName, $pattern.Key)
                Add-Failure
            }
        }
        else {
            Write-Output ('[{0}] generic-leak: NO-MATCH ({1}; encoding={2}) pattern={3}' -f $Scope, $safeTarget, $EncodingName, $pattern.Key)
        }
    }
}

function Scan-File {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Scope
    )

    try {
        $decodings = @(Read-TextSafely $Path)
        foreach ($decoding in $decodings) {
            Scan-Text (Protect-DisplayText $Path) $Scope $decoding.Text $decoding.Name
        }
    }
    catch {
        Write-Output ('[{0}] ERROR: unable to decode or read target {1}: {2}' -f $Scope, (Protect-DisplayText $Path), (Protect-DisplayText $_.Exception.Message))
        Add-Failure
    }
}

function Get-FilesUnder {
    param([Parameter(Mandatory)][string] $Path)

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        return @(Get-Item -LiteralPath $Path)
    }
    if (Test-Path -LiteralPath $Path -PathType Container) {
        return @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force)
    }
    return @()
}

function Scan-PathTarget {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Scope,
        [Parameter(Mandatory)][bool] $Required
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        $status = if ($Required) { 'ERROR' } else { 'SKIP' }
        Write-Output ('[{0}] {1}: target does not exist ({2})' -f $Scope, $status, (Protect-DisplayText $Path))
        if ($Required) { Add-Failure }
        return
    }

    $files = @(Get-FilesUnder $Path)
    if ($files.Count -eq 0) {
        Write-Output ('[{0}] marker: NO-MATCH (empty target {1})' -f $Scope, (Protect-DisplayText $Path))
        return
    }

    foreach ($file in $files) {
        Scan-File $file.FullName $Scope
    }
}

Write-Output 'Issue #106 leak scan'
Write-Output ('marker_reference=sha256:{0}' -f $markerReference)

$repoFull = (Resolve-Path $RepositoryRoot).Path
try {
    $diffOutput = (& git -C $repoFull diff --no-ext-diff --binary HEAD -- 2>$null | Out-String)
    $diffExit = $LASTEXITCODE
}
catch {
    $diffOutput = ''
    $diffExit = 1
}
if ($diffExit -ne 0) {
    Write-Output ('[repository-output] ERROR: git diff command exit_code={0}' -f $diffExit)
    Add-Failure
}
else {
    Scan-Text 'uncommitted diff' 'repository-output' $diffOutput
}

try {
    $untracked = @(& git -C $repoFull ls-files --others --exclude-standard 2>$null)
    $untrackedExit = $LASTEXITCODE
}
catch {
    $untracked = @()
    $untrackedExit = 1
}
if ($untrackedExit -ne 0) {
    Write-Output ('[repository-output] ERROR: git untracked-file command exit_code={0}' -f $untrackedExit)
    Add-Failure
}
else {
    foreach ($relativePath in $untracked) {
        $untrackedPath = Join-Path $repoFull ([string]$relativePath)
        Scan-File $untrackedPath 'repository-output'
    }
    if ($untracked.Count -eq 0) {
        Write-Output '[repository-output] untracked files: NONE'
    }
}

foreach ($path in $EvidencePath) {
    Scan-PathTarget $path 'evidence-files' $true
}

if (-not [string]::IsNullOrWhiteSpace($LogDirectory)) {
    Scan-PathTarget $LogDirectory 'application-logs' $false
}
else {
    Write-Output '[application-logs] SKIP: no log directory supplied'
}

foreach ($path in $SanitizedOutputPath) {
    Scan-PathTarget $path 'sanitized-api-ui-output' $false
}

Write-Output ('scan_result: {0}' -f $(if ($script:failure) { 'FAIL' } else { 'PASS' }))
if ($script:failure) {
    exit 1
}

exit 0
