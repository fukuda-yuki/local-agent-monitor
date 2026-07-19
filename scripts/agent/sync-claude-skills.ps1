[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$agentSkillsRoot = Join-Path $repositoryRoot '.agents\skills'
$claudeSkillsRoot = Join-Path $repositoryRoot '.claude\skills'
$sharedSkills = @(
    'commit'
    'seed-demo'
    'spec-update'
    'sprint-evidence'
    'validate'
)

function Get-MirroredFiles {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [switch]$ExcludeOpenAiMetadata
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return @{}
    }

    $files = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
        $relativePath = [System.IO.Path]::GetRelativePath($Root, $file.FullName)
        $relativePath = $relativePath.Replace('\', '/')

        if ($ExcludeOpenAiMetadata -and ($relativePath -eq 'agents/openai.yaml' -or $relativePath.StartsWith('agents/'))) {
            continue
        }

        $files[$relativePath] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }

    return $files
}

function Compare-Mirror {
    param(
        [Parameter(Mandatory)]
        [string]$SkillName
    )

    $sourceRoot = Join-Path $agentSkillsRoot $SkillName
    $destinationRoot = Join-Path $claudeSkillsRoot $SkillName

    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "Canonical skill '$SkillName' is missing at '$sourceRoot'."
    }

    $sourceFiles = Get-MirroredFiles -Root $sourceRoot -ExcludeOpenAiMetadata
    $destinationFiles = Get-MirroredFiles -Root $destinationRoot
    $differences = [System.Collections.Generic.List[string]]::new()

    foreach ($relativePath in ($sourceFiles.Keys | Sort-Object)) {
        if (-not $destinationFiles.ContainsKey($relativePath)) {
            $differences.Add("missing destination file: $SkillName/$relativePath")
            continue
        }

        if ($sourceFiles[$relativePath] -ne $destinationFiles[$relativePath]) {
            $differences.Add("content differs: $SkillName/$relativePath")
        }
    }

    foreach ($relativePath in ($destinationFiles.Keys | Sort-Object)) {
        if (-not $sourceFiles.ContainsKey($relativePath)) {
            $differences.Add("unexpected destination file: $SkillName/$relativePath")
        }
    }

    return $differences
}

if ($Check) {
    $allDifferences = [System.Collections.Generic.List[string]]::new()
    foreach ($skillName in $sharedSkills) {
        foreach ($difference in Compare-Mirror -SkillName $skillName) {
            $allDifferences.Add($difference)
        }
    }

    if ($allDifferences.Count -gt 0) {
        $message = "Claude skill mirror is out of date:`n- " + ($allDifferences -join "`n- ") +
            "`nRun: pwsh scripts/agent/sync-claude-skills.ps1"
        throw $message
    }

    Write-Output "Claude skill mirror is up to date ($($sharedSkills.Count) shared skills)."
    exit 0
}

New-Item -ItemType Directory -Path $claudeSkillsRoot -Force | Out-Null

foreach ($skillName in $sharedSkills) {
    $sourceRoot = Join-Path $agentSkillsRoot $skillName
    $destinationRoot = Join-Path $claudeSkillsRoot $skillName

    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "Canonical skill '$skillName' is missing at '$sourceRoot'."
    }

    if (Test-Path -LiteralPath $destinationRoot) {
        Remove-Item -LiteralPath $destinationRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null

    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Force) {
        $relativePath = [System.IO.Path]::GetRelativePath($sourceRoot, $file.FullName)
        $normalizedPath = $relativePath.Replace('\', '/')
        if ($normalizedPath -eq 'agents/openai.yaml' -or $normalizedPath.StartsWith('agents/')) {
            continue
        }

        $destinationPath = Join-Path $destinationRoot $relativePath
        $destinationDirectory = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force
    }
}

$remainingDifferences = [System.Collections.Generic.List[string]]::new()
foreach ($skillName in $sharedSkills) {
    foreach ($difference in Compare-Mirror -SkillName $skillName) {
        $remainingDifferences.Add($difference)
    }
}

if ($remainingDifferences.Count -gt 0) {
    throw "Claude skill synchronization failed:`n- $($remainingDifferences -join "`n- ")"
}

Write-Output "Synchronized $($sharedSkills.Count) shared skills from .agents/skills to .claude/skills."
