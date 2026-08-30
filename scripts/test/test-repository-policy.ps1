[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$guard = Join-Path $PSScriptRoot 'assert-repository-policy.ps1'
if (-not (Test-Path -LiteralPath $guard -PathType Leaf)) {
    throw "Repository policy guard was not found: $guard"
}

$validationRunner = Join-Path $PSScriptRoot 'run-validation.ps1'
if (-not (Test-Path -LiteralPath $validationRunner -PathType Leaf)) {
    throw "Validation runner was not found: $validationRunner"
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

function Assert-ValidationRunnerPreflightOrder {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("repository-policy-runner-{0}" -f [guid]::NewGuid().ToString('N'))
    $scriptRoot = Join-Path $root 'scripts\test'
    $shimRoot = Join-Path $root 'shims'
    $logPath = Join-Path $root 'commands.log'

    New-Item -ItemType Directory -Path $scriptRoot | Out-Null
    New-Item -ItemType Directory -Path $shimRoot | Out-Null
    Copy-Item -LiteralPath $validationRunner -Destination (Join-Path $scriptRoot 'run-validation.ps1')
    Set-Content -LiteralPath (Join-Path $root 'CopilotAgentObservability.slnx') -Value '' -NoNewline
    Set-Content -LiteralPath (Join-Path $scriptRoot 'test-repository-policy.ps1') -Value @'
[CmdletBinding()]
param()
Add-Content -LiteralPath $env:VALIDATION_PREFLIGHT_LOG -Value 'repository-policy-self-test'
'@ -NoNewline
    Set-Content -LiteralPath (Join-Path $scriptRoot 'assert-repository-policy.ps1') -Value @'
[CmdletBinding()]
param([string]$RepositoryRoot)
Add-Content -LiteralPath $env:VALIDATION_PREFLIGHT_LOG -Value 'repository-policy-guard'
'@ -NoNewline
    if ($IsWindows) {
        Set-Content -LiteralPath (Join-Path $shimRoot 'dotnet.cmd') -Value @'
@echo off
echo dotnet:%*>>"%VALIDATION_PREFLIGHT_LOG%"
exit /b 17
'@ -NoNewline
    }
    else {
        $dotnetShim = Join-Path $shimRoot 'dotnet'
        Set-Content -LiteralPath $dotnetShim -Value @'
#!/usr/bin/env sh
printf 'dotnet:%s\n' "$*" >> "$VALIDATION_PREFLIGHT_LOG"
exit 17
'@ -NoNewline
        & chmod +x $dotnetShim
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to make the dotnet validation shim executable."
        }
    }

    $previousPath = $env:PATH
    $previousLog = $env:VALIDATION_PREFLIGHT_LOG
    try {
        $env:PATH = "$shimRoot$([System.IO.Path]::PathSeparator)$previousPath"
        $env:VALIDATION_PREFLIGHT_LOG = $logPath
        $pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
        & $pwshExecutable -NoProfile -File (Join-Path $scriptRoot 'run-validation.ps1') -Lane Completion 2>&1 | Out-Null

        $commands = @(Get-Content -LiteralPath $logPath)
        $selfTestIndex = [Array]::IndexOf($commands, 'repository-policy-self-test')
        $guardIndex = [Array]::IndexOf($commands, 'repository-policy-guard')
        $buildIndex = [Array]::FindIndex($commands, [Predicate[string]] { param($line) $line -match '^dotnet:build ' })

        if ($selfTestIndex -lt 0 -or $guardIndex -lt 0 -or $buildIndex -lt 0) {
            throw "Validation runner did not execute repository policy self-test, guard, and build: $($commands -join ' | ')"
        }

        if (-not ($selfTestIndex -lt $guardIndex -and $guardIndex -lt $buildIndex)) {
            throw "Validation runner preflight order must be self-test, guard, then build: $($commands -join ' | ')"
        }
    }
    finally {
        $env:PATH = $previousPath
        $env:VALIDATION_PREFLIGHT_LOG = $previousLog
        if (Test-Path -LiteralPath $root) {
            Remove-Item -LiteralPath $root -Recurse -Force
        }
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

    Assert-ValidationRunnerPreflightOrder

    Write-Output 'repository_policy_tests=passed'
}
finally {
    foreach ($repository in $repositories) {
        if (Test-Path -LiteralPath $repository) {
            Remove-Item -LiteralPath $repository -Recurse -Force
        }
    }
}
