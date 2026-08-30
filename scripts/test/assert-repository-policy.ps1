[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'

$resolvedRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$topLevel = & git -C $resolvedRoot rev-parse --show-toplevel 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Repository root is not a Git worktree: $resolvedRoot`n$($topLevel -join "`n")"
}

$trackedFiles = @(& git -C $resolvedRoot ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'git ls-files failed while checking repository policy.'
}

$forbiddenTrackedPatterns = @(
    '^\.superpowers/',
    '^docs/superpowers/',
    '^docs/sprints/',
    '^docs/BUG_ISSUE/',
    '^docs/task\.md$',
    '^docs/agent-guides/sprint-history\.md$'
)

$forbiddenTrackedFiles = @(
    $trackedFiles | Where-Object {
        $trackedFile = $_
        $forbiddenTrackedPatterns | Where-Object { $trackedFile -match $_ } | Select-Object -First 1
    }
)

$removedPathPattern = '(^|[./\\])sprints[\\/]|docs[\\/](BUG_ISSUE|superpowers)[\\/]|[.]superpowers[\\/]|docs[\\/]task[.]md|sprint-history[.]md|["'']docs["''][[:space:]]*,[[:space:]]*["''](sprints|BUG_ISSUE|superpowers|task[.]md)["'']'
$referenceOutput = @(& git -C $resolvedRoot grep -n -I -E $removedPathPattern -- . ':(exclude).gitignore' ':(exclude)scripts/test/assert-repository-policy.ps1' ':(exclude)scripts/test/test-repository-policy.ps1' 2>&1)
$grepExitCode = $LASTEXITCODE
if ($grepExitCode -gt 1) {
    throw "git grep failed while checking removed-path references.`n$($referenceOutput -join "`n")"
}

$violations = [System.Collections.Generic.List[string]]::new()
if ($forbiddenTrackedFiles.Count -gt 0) {
    $violations.Add("Forbidden tracked paths:`n$($forbiddenTrackedFiles -join "`n")")
}
if ($grepExitCode -eq 0) {
    $violations.Add("Removed-path references:`n$($referenceOutput -join "`n")")
}

if ($violations.Count -gt 0) {
    throw ($violations -join "`n`n")
}

Write-Output 'repository_policy=passed'
