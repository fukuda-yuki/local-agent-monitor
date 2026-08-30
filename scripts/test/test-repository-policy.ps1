[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$guard = Join-Path $PSScriptRoot 'assert-repository-policy.ps1'
if (-not (Test-Path -LiteralPath $guard -PathType Leaf)) {
    throw "Repository policy guard was not found: $guard"
}

function Invoke-Git {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & git -C $RepositoryRoot @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function New-TestRepository {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("repository-policy-{0}" -f [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    Invoke-Git -RepositoryRoot $root -Arguments @('init', '--quiet')
    Invoke-Git -RepositoryRoot $root -Arguments @('config', 'user.email', 'repository-policy@example.invalid')
    Invoke-Git -RepositoryRoot $root -Arguments @('config', 'user.name', 'Repository Policy Test')
    Invoke-Git -RepositoryRoot $root -Arguments @('config', 'core.autocrlf', 'false')
    Set-Content -LiteralPath (Join-Path $root '.gitignore') -Value "/docs/sprints/`n/docs/task.md`n" -NoNewline
    Set-Content -LiteralPath (Join-Path $root 'README.md') -Value "# Synthetic repository`n" -NoNewline
    Invoke-Git -RepositoryRoot $root -Arguments @('add', '.gitignore', 'README.md')
    Invoke-Git -RepositoryRoot $root -Arguments @('commit', '--quiet', '-m', 'test fixture')
    return $root
}

function Invoke-Guard {
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $output = & pwsh -NoProfile -File $guard -RepositoryRoot $RepositoryRoot 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output -join "`n"
    }
}

$repositories = [System.Collections.Generic.List[string]]::new()
try {
    $clean = New-TestRepository
    $repositories.Add($clean)
    $cleanResult = Invoke-Guard -RepositoryRoot $clean
    if ($cleanResult.ExitCode -ne 0) {
        throw "Clean repository was rejected: $($cleanResult.Output)"
    }

    $trackedPath = New-TestRepository
    $repositories.Add($trackedPath)
    New-Item -ItemType Directory -Path (Join-Path $trackedPath 'docs') | Out-Null
    Set-Content -LiteralPath (Join-Path $trackedPath 'docs/task.md') -Value "temporary status`n" -NoNewline
    Invoke-Git -RepositoryRoot $trackedPath -Arguments @('add', '--force', 'docs/task.md')
    $trackedPathResult = Invoke-Guard -RepositoryRoot $trackedPath
    if ($trackedPathResult.ExitCode -eq 0 -or $trackedPathResult.Output -notmatch 'docs/task\.md') {
        throw "Tracked forbidden path was not rejected with its path: $($trackedPathResult.Output)"
    }

    $staleReference = New-TestRepository
    $repositories.Add($staleReference)
    Set-Content -LiteralPath (Join-Path $staleReference 'README.md') -Value "Read docs/sprints/example.md before release.`n" -NoNewline
    Invoke-Git -RepositoryRoot $staleReference -Arguments @('add', 'README.md')
    $staleReferenceResult = Invoke-Guard -RepositoryRoot $staleReference
    if ($staleReferenceResult.ExitCode -eq 0 -or $staleReferenceResult.Output -notmatch 'README\.md') {
        throw "Removed-path reference was not rejected with its source: $($staleReferenceResult.Output)"
    }

    $relativeReference = New-TestRepository
    $repositories.Add($relativeReference)
    Set-Content -LiteralPath (Join-Path $relativeReference 'README.md') -Value "Read ../../sprints/example.md before release.`n" -NoNewline
    Invoke-Git -RepositoryRoot $relativeReference -Arguments @('add', 'README.md')
    $relativeReferenceResult = Invoke-Guard -RepositoryRoot $relativeReference
    if ($relativeReferenceResult.ExitCode -eq 0 -or $relativeReferenceResult.Output -notmatch 'README\.md') {
        throw "Relative removed-path reference was not rejected with its source: $($relativeReferenceResult.Output)"
    }

    $segmentedReference = New-TestRepository
    $repositories.Add($segmentedReference)
    Set-Content -LiteralPath (Join-Path $segmentedReference 'README.md') -Value 'Path.Combine(root, "docs", "sprints", "evidence.md")' -NoNewline
    Invoke-Git -RepositoryRoot $segmentedReference -Arguments @('add', 'README.md')
    $segmentedReferenceResult = Invoke-Guard -RepositoryRoot $segmentedReference
    if ($segmentedReferenceResult.ExitCode -eq 0 -or $segmentedReferenceResult.Output -notmatch 'README\.md') {
        throw "Segmented removed-path reference was not rejected with its source: $($segmentedReferenceResult.Output)"
    }

    Write-Output 'repository_policy_tests=passed'
}
finally {
    foreach ($repository in $repositories) {
        if (Test-Path -LiteralPath $repository) {
            Remove-Item -LiteralPath $repository -Recurse -Force
        }
    }
}
