[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Completion', 'Nightly')]
    [string]$Lane,

    [ValidateSet('Windows', 'Linux')]
    [string]$Partition,

    [ValidateSet('Discovery', 'Shard', 'Aggregate')]
    [string]$Phase,

    [string]$ManifestPath,
    [string]$ShardId,
    [string]$OutputDirectory,
    [string]$ArtifactsDirectory,
    [string]$DependencyResultsPath,
    [string]$RunAttempt,
    [string]$DiscoveryResult,
    [string]$ShardDependencyResult
)

$ErrorActionPreference = 'Stop'

if ($Lane -eq 'Nightly' -and [string]::IsNullOrWhiteSpace($Partition)) {
    throw 'Nightly validation requires -Partition Windows or -Partition Linux.'
}
if ($Lane -eq 'Nightly' -and -not [string]::IsNullOrWhiteSpace($Phase)) {
    throw 'Nightly validation does not accept -Phase.'
}
if ($Lane -eq 'Completion' -and -not [string]::IsNullOrWhiteSpace($Partition)) {
    throw 'Completion validation does not accept -Partition.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$solution = Join-Path $repoRoot 'CopilotAgentObservability.slnx'
$playwrightInstaller = Join-Path $repoRoot 'scripts\test\install-playwright-chromium.ps1'
$repositoryPolicyTests = Join-Path $repoRoot 'scripts\test\test-repository-policy.ps1'
$repositoryPolicyGuard = Join-Path $repoRoot 'scripts\test\assert-repository-policy.ps1'
$validationContract = Join-Path $repoRoot 'scripts\test\assert-validation-contract.ps1'
$partitionToken = if ([string]::IsNullOrWhiteSpace($Partition)) { 'none' } else { $Partition.ToLowerInvariant() }
$runName = '{0}-{1}-{2}-{3}' -f $Lane.ToLowerInvariant(), $partitionToken, (Get-Date -Format 'yyyyMMddTHHmmssfff'), $PID
$defaultResultsRoot = Join-Path $repoRoot (Join-Path 'artifacts\validation' $runName)
$resultsRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $defaultResultsRoot
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
$phaseStopwatch = if ($Lane -eq 'Completion') { [System.Diagnostics.Stopwatch]::StartNew() } else { $null }
$phaseBudgetSeconds = 1800
$phaseFinalizationReserveSeconds = 5
$nightlyProjectTimeoutSeconds = 2700

$operatorOnlyExclusion = 'Issue158Lane!=WindowsOwnedSession&Issue158Lane!=LinuxExt4CurrentFile'
$completionFastFilter = "ValidationLane!=Nightly&ValidationLane!=CriticalSmoke&$operatorOnlyExclusion"
$criticalSmokeFilter = 'ValidationLane=CriticalSmoke'
$nightlyFilter = $operatorOnlyExclusion
$criticalSmokeExpectedFqns = @(
    'CopilotAgentObservability.LocalMonitor.Tests.LocalMonitorV1RepositoryComparePlaywrightTests.ImmutableCompareRendersNineSectionsRowsEvidenceAndResponsiveTableWithoutRecompute'
    'CopilotAgentObservability.LocalMonitor.Tests.LocalMonitorV1SessionExplorerPlaywrightTests.ComparePreviewCreatesFromTransientOrderedCohortsAndNavigatesOnlyByServerLocation'
)
$expectedWindowsSkippedFqns = @(
    'CopilotAgentObservability.LocalMonitor.Tests.SkillNativeClassifierTests.OpenAt2OpensAbsoluteAnchorAndSafeRelativeDescendant'
    'CopilotAgentObservability.LocalMonitor.Tests.PricingCatalogProviderTests.Create_RejectsALinuxFifoWithoutWaitingForAWriter'
)

$testProjects = [ordered]@{
    's01' = 'tests/CopilotAgentObservability.ConfigCli.Tests/CopilotAgentObservability.ConfigCli.Tests.csproj'
    's06' = 'tests/CopilotAgentObservability.Doctor.Tests/CopilotAgentObservability.Doctor.Tests.csproj'
    's07' = 'tests/CopilotAgentObservability.Alerts.Tests/CopilotAgentObservability.Alerts.Tests.csproj'
    's08' = 'tests/CopilotAgentObservability.Pricing.Tests/CopilotAgentObservability.Pricing.Tests.csproj'
    's09' = 'tests/CopilotAgentObservability.InstructionFindings.Tests/CopilotAgentObservability.InstructionFindings.Tests.csproj'
}
$localProject = 'tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj'
$nightlyExpectedProjects = @($testProjects.Values) + @($localProject)
$serializedNightlyExpectedProjects = ConvertTo-Json -InputObject $nightlyExpectedProjects -Compress
$localShardSelectors = [ordered]@{
    's02' = @(
        '.Tests.Local', '.Tests.Generic', '.Tests.Sanitized', '.Tests.Proposal', '.Tests.Playwright')
    's03' = @(
        '.Tests.Retention.', '.Tests.RuntimeBackup', '.Tests.Alert')
    's04' = @(
        '.Tests.Skill', '.Tests.Session', '.Tests.CurrentSkill', '.Tests.Source', '.Tests.Copilot',
        '.Tests.DotNet', '.Tests.Windows', '.Tests.Linux', '.Tests.Claude', '.Tests.Subagent',
        '.Tests.Monitor', 'CopilotAgentObservability.LocalMonitor.SkillRuntime.')
    's05' = @(
        '.Tests.Agent', '.Tests.Apply', '.Tests.Canvas', '.Tests.Cost', '.Tests.Discovery',
        '.Tests.Doctor', '.Tests.Effect', '.Tests.Fact', '.Tests.Historical', '.Tests.Hook',
        '.Tests.Ingestion', '.Tests.Instruction', '.Tests.Issue', '.Tests.Pricing', '.Tests.Projection',
        '.Tests.Raw', '.Tests.Repository', '.Tests.Settings', '.Tests.Sqlite', '.Tests.Trace')
}
$criticalShardId = 's10'
$requiredShardIds = @($testProjects.Keys[0]) + @($localShardSelectors.Keys) + @($testProjects.Keys | Select-Object -Skip 1) + @($criticalShardId)
$shardPrerequisiteProjects = [ordered]@{
    's01' = @('src/CopilotAgentObservability.LocalMonitor/CopilotAgentObservability.LocalMonitor.csproj')
    's02' = @()
    's03' = @()
    's04' = @()
    's05' = @()
    's06' = @()
    's07' = @()
    's08' = @()
    's09' = @()
    's10' = @()
}
$serializedShardPrerequisiteProjects = ConvertTo-Json -InputObject $shardPrerequisiteProjects -Depth 10 -Compress

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Invoke-BoundedCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds)
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Unable to start command '$FilePath'." }
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $processExited = $process.WaitForExit($TimeoutMilliseconds)
        $timedOut = -not $processExited
        if ($timedOut) {
            try { $process.Kill($true) }
            catch { Write-Warning "Unable to terminate timed-out process tree: $($_.Exception.Message)" }
            $processExited = $process.WaitForExit(10000)
        } else {
            $process.WaitForExit()
        }
        return [pscustomobject]@{
            ExitCode = if ($timedOut -or -not $processExited) { -1 } else { $process.ExitCode }
            TimedOut = $timedOut
            ProcessExited = $processExited
            ElapsedSeconds = $stopwatch.Elapsed.TotalSeconds
            Output = ''
        }
    }
    finally { $process.Dispose() }
}

function Write-NightlyProjectReceipt {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][object]$Result)
    $identity = [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    Write-ImmutableJson -Path $Path -Value ([ordered]@{
        schemaVersion = 1
        projectPath = $ProjectPath
        projectIdentity = $identity
        status = if ($Result.TimedOut) { 'timeout' } elseif ($Result.ExitCode -eq 0) { 'success' } else { 'failure' }
        exitCode = $Result.ExitCode
        timedOut = $Result.TimedOut
        processExited = $Result.ProcessExited
        elapsedSeconds = $Result.ElapsedSeconds
    })
}

function Get-RemainingMilliseconds {
    $remaining = $phaseBudgetSeconds - $phaseFinalizationReserveSeconds - $phaseStopwatch.Elapsed.TotalSeconds
    if ($remaining -le 0) { return 0 }
    return [Math]::Max(1, [Math]::Floor($remaining * 1000))
}

function Assert-PhaseFinalizationReserve {
    param([Parameter(Mandatory)][double]$ElapsedSeconds)
    $latestFinalizationStart = $phaseBudgetSeconds - $phaseFinalizationReserveSeconds
    if ($ElapsedSeconds -gt $latestFinalizationStart) {
        throw "Completion phase did not preserve its fixed finalization reserve; elapsed_seconds=$ElapsedSeconds reserve_seconds=$phaseFinalizationReserveSeconds."
    }
}

function Assert-PhaseCompletedWithinBudget {
    param([Parameter(Mandatory)][double]$ElapsedSeconds)
    if ($ElapsedSeconds -gt $phaseBudgetSeconds) {
        throw "Completion phase exceeded its 30 minute budget after finalization; elapsed_seconds=$ElapsedSeconds."
    }
}

function Invoke-PhaseCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$SuppressOutput)
    $remainingMilliseconds = Get-RemainingMilliseconds
    if ($remainingMilliseconds -le 0) {
        return [pscustomobject]@{ ExitCode = -1; TimedOut = $true; Output = 'Phase deadline expired before process start.' }
    }
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Unable to start command '$FilePath'." }
    try {
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        $wait = $process.WaitForExitAsync()
        $deadline = [System.Threading.Tasks.Task]::Delay([int]$remainingMilliseconds)
        $completed = [System.Threading.Tasks.Task]::WhenAny($wait, $deadline).GetAwaiter().GetResult()
        $timedOut = $completed -ne $wait
        if ($timedOut) {
            try { $process.Kill($true) } catch { Write-Warning "Unable to terminate timed-out process tree: $($_.Exception.Message)" }
            try { $process.WaitForExit() } catch { }
        }
        $output = $standardOutput.GetAwaiter().GetResult() + $standardError.GetAwaiter().GetResult()
        if (-not $SuppressOutput -and -not [string]::IsNullOrWhiteSpace($output)) { Write-Host $output.TrimEnd() }
        return [pscustomobject]@{
            ExitCode = if ($timedOut) { -1 } else { $process.ExitCode }
            TimedOut = $timedOut
            Output = $output
        }
    }
    finally { $process.Dispose() }
}

function Assert-PhaseCommand {
    param([Parameter(Mandatory)][object]$Result, [Parameter(Mandatory)][string]$Description)
    if ($Result.TimedOut) { throw "$Description exceeded the remaining 30 minute phase budget." }
    if ($Result.ExitCode -ne 0) { throw "$Description failed with exit code $($Result.ExitCode)." }
}

function Assert-PhaseBudget {
    $result = Invoke-PhaseCommand -FilePath 'pwsh' -Arguments @(
        '-NoProfile', '-File', $validationContract,
        '-Mode', 'CompletionBudget',
        '-ElapsedSeconds', $phaseStopwatch.Elapsed.TotalSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture))
    Assert-PhaseCommand -Result $result -Description 'Completion phase budget assertion'
}

function Invoke-TestPass {
    param([string]$Target, [string]$Filter, [string]$ResultsDirectory, [string]$LogFilePrefix)
    New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
    Invoke-NativeCommand -FilePath 'dotnet' -Arguments @(
        'test', $Target, '--no-build', '--filter', $Filter,
        '--logger', ('trx;LogFilePrefix={0}' -f $LogFilePrefix), '--results-directory', $ResultsDirectory)
}

function Install-PlaywrightChromium {
    param([switch]$WithDeps)
    $arguments = @($playwrightInstaller)
    if ($WithDeps) { $arguments += '-WithDeps' }
    Invoke-NativeCommand -FilePath 'pwsh' -Arguments $arguments
}

function Invoke-NightlyValidation {
    param([Parameter(Mandatory)][string]$ResultsDirectory)
    New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
    foreach ($projectPath in $nightlyExpectedProjects) {
        $projectIdentity = [IO.Path]::GetFileNameWithoutExtension($projectPath)
        $receiptPath = Join-Path $ResultsDirectory "receipt-$projectIdentity.json"
        $result = $null
        try {
            $result = Invoke-BoundedCommand -FilePath 'dotnet' -Arguments @(
                'test', (Join-Path $repoRoot $projectPath), '--no-build', '--filter', $nightlyFilter,
                '--logger', ('trx;LogFilePrefix={0}' -f "nightly-$partitionToken-$projectIdentity"),
                '--results-directory', $ResultsDirectory) `
                -TimeoutMilliseconds ($nightlyProjectTimeoutSeconds * 1000)
        }
        catch {
            Write-Warning "Nightly project command failed before terminal evidence; project=$projectPath error=$($_.Exception.Message)"
            $result = [pscustomobject]@{
                ExitCode = -1; TimedOut = $false; ProcessExited = $false
                ElapsedSeconds = 0; Output = ''
            }
        }
        Write-NightlyProjectReceipt -Path $receiptPath -ProjectPath $projectPath -Result $result
    }
    Invoke-NativeCommand -FilePath 'pwsh' -Arguments @(
        '-NoProfile', '-File', $validationContract, '-Mode', 'NightlyEvidence',
        '-ResultsDirectory', $ResultsDirectory, '-ExpectedProjectsJson', $serializedNightlyExpectedProjects)
}

function Get-AuthorityDigest {
    $authority = [ordered]@{
        completionFastFilter = $completionFastFilter
        criticalSmokeFilter = $criticalSmokeFilter
        criticalSmokeExpectedFqns = $criticalSmokeExpectedFqns
        expectedWindowsSkippedFqns = $expectedWindowsSkippedFqns
        projectShards = $testProjects
        localProject = $localProject
        localShardSelectors = $localShardSelectors
        criticalShardId = $criticalShardId
        shardPrerequisiteProjects = $shardPrerequisiteProjects
    } | ConvertTo-Json -Depth 20 -Compress
    return Get-TextDigest -Value $authority
}

function Get-TextDigest {
    param([Parameter(Mandatory)][string]$Value)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Write-ImmutableJson {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Value)
    $json = $Value | ConvertTo-Json -Depth 100
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose() }
}

function Write-ShardFailureReceipt {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Value)
    Assert-PhaseCompletedWithinBudget -ElapsedSeconds $phaseStopwatch.Elapsed.TotalSeconds
    Write-ImmutableJson -Path $Path -Value $Value
    Assert-PhaseCompletedWithinBudget -ElapsedSeconds $phaseStopwatch.Elapsed.TotalSeconds
}

function Get-ManifestDigest {
    param([Parameter(Mandatory)][object]$Manifest)
    $payload = [ordered]@{
        schemaVersion = $Manifest.schemaVersion
        candidateSha = $Manifest.candidateSha
        authorityDigest = $Manifest.authorityDigest
        requiredShardIds = $Manifest.requiredShardIds
        expectedWindowsSkippedFqns = $Manifest.expectedWindowsSkippedFqns
        baselineCount = $Manifest.baselineCount
        baselineHash = $Manifest.baselineHash
        baselineRows = $Manifest.baselineRows
        shards = $Manifest.shards
    } | ConvertTo-Json -Depth 100 -Compress
    return Get-TextDigest -Value $payload
}

function Get-RowHash {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Rows)
    $identities = [Collections.Generic.List[string]]::new()
    foreach ($row in $Rows) { $identities.Add([string]$row.authorityIdentity) }
    $identities.Sort([StringComparer]::Ordinal)
    return Get-TextDigest -Value ([string]::Join("`n", $identities))
}

function Get-CanonicalRows {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Rows)
    $ordered = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($row in $Rows) {
        if ($ordered.ContainsKey([string]$row.authorityIdentity)) {
            throw "Duplicate row authority identity; identity=$($row.authorityIdentity)."
        }
        $ordered.Add([string]$row.authorityIdentity, $row)
    }
    return @($ordered.Values)
}

function Get-CandidateSha {
    $result = Invoke-PhaseCommand -FilePath 'git' -Arguments @('rev-parse', 'HEAD')
    Assert-PhaseCommand -Result $result -Description 'Candidate SHA resolution'
    return $result.Output.Trim()
}

function Resolve-RepositoryProjectPath {
    param([Parameter(Mandatory)][string]$RelativePath)
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains('\') -or
        -not $RelativePath.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Shard prerequisite must be a normalized repository-relative project path; path=$RelativePath."
    }
    $segments = @($RelativePath.Split('/'))
    if ($segments.Count -lt 2 -or @($segments | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
        throw "Shard prerequisite must remain within the repository; path=$RelativePath."
    }
    $repositoryPath = [IO.Path]::GetFullPath($repoRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $projectPath = [IO.Path]::GetFullPath((Join-Path $repositoryPath $RelativePath))
    $repositoryPrefix = $repositoryPath + [IO.Path]::DirectorySeparatorChar
    if (-not $projectPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.File]::Exists($projectPath)) {
        throw "Shard prerequisite project is missing or outside the repository; path=$RelativePath."
    }
    return $projectPath
}

function Assert-ShardPrerequisiteProjects {
    param(
        [Parameter(Mandatory)][object]$Shard,
        [Parameter(Mandatory)][string]$CurrentShardId)
    if (-not $shardPrerequisiteProjects.Contains($CurrentShardId) -or
        $null -eq $Shard.PSObject.Properties['prerequisiteProjects'] -or
        $Shard.prerequisiteProjects -isnot [System.Array]) {
        throw "Shard prerequisite authority is missing; shard_id=$CurrentShardId."
    }
    $expected = @($shardPrerequisiteProjects[$CurrentShardId] | ForEach-Object { [string]$_ })
    $actual = @($Shard.prerequisiteProjects | ForEach-Object { [string]$_ })
    if ((ConvertTo-Json -InputObject $actual -Compress) -ne
        (ConvertTo-Json -InputObject $expected -Compress)) {
        throw "Shard prerequisite projects do not match runner authority; shard_id=$CurrentShardId."
    }
    $distinct = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in $actual) {
        if (-not $distinct.Add($relativePath)) {
            throw "Shard prerequisite projects must be distinct; shard_id=$CurrentShardId path=$relativePath."
        }
        [void](Resolve-RepositoryProjectPath -RelativePath $relativePath)
    }
}

function ConvertTo-TestRows {
    param(
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string[]]$DisplayNames)
    $occurrences = @{}
    return @(
        foreach ($displayName in $DisplayNames) {
            $fqn = ($displayName -replace '\(.*$', '')
            $key = $fqn
            $occurrences[$key] = 1 + ($occurrences[$key] ?? 0)
            [ordered]@{
                projectPath = $ProjectPath
                fqn = $fqn
                testName = $displayName
                source = 'list'
                occurrence = $occurrences[$key]
                authorityIdentity = '{0}|{1}|{2}' -f $ProjectPath, $fqn, $occurrences[$key]
            }
        }
    )
}

function Get-CollapsedRuntimeTheoryFqns {
    param(
        [Parameter(Mandatory)][object[]]$Rows,
        [Parameter(Mandatory)][string]$AssemblyPath)
    $assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    return @(
        foreach ($row in $Rows | Where-Object { $_.testName -eq $_.fqn }) {
            $separator = $row.fqn.LastIndexOf('.')
            $className = $row.fqn.Substring(0, $separator)
            $methodName = $row.fqn.Substring($separator + 1)
            $type = $assembly.GetType($className, $false)
            if ($null -eq $type) { continue }
            $methods = @($type.GetMethods() | Where-Object Name -eq $methodName)
            if (@($methods | Where-Object {
                @($_.GetCustomAttributesData() | Where-Object {
                    $_.AttributeType.FullName -eq 'Xunit.TheoryAttribute'
                }).Count -ne 0
            }).Count -ne 0) {
                $row.fqn
            }
        }
    )
}

function Get-ExactFqnFilter {
    param([Parameter(Mandatory)][string[]]$Fqns)
    if ($Fqns.Count -eq 0) { throw 'Exact FQN filter requires at least one identity.' }
    return ($Fqns | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
}

function Merge-CollapsedTheoryRows {
    param(
        [Parameter(Mandatory)][object[]]$Rows,
        [Parameter(Mandatory)][string[]]$CollapsedFqns,
        [Parameter(Mandatory)][object[]]$ExpandedRows)
    foreach ($fqn in $CollapsedFqns) {
        $placeholders = @($Rows | Where-Object {
            $_.fqn -eq $fqn -and $_.testName -eq $fqn -and $_.source -eq 'list'
        })
        if ($placeholders.Count -ne 1) {
            throw "Collapsed Theory must have exactly one list placeholder; fqn=$fqn count=$($placeholders.Count)."
        }
        if (@($ExpandedRows | Where-Object fqn -eq $fqn).Count -eq 0) {
            throw "Runtime theory discovery returned no rows; fqn=$fqn."
        }
    }
    $unexpected = @($ExpandedRows | Where-Object { $_.fqn -notin $CollapsedFqns })
    if ($unexpected.Count -ne 0) {
        throw "Runtime theory discovery returned an unexpected identity; fqn=$($unexpected[0].fqn)."
    }
    $badOutcomes = @($ExpandedRows | Where-Object outcome -ne 'Passed')
    if ($badOutcomes.Count -ne 0) {
        throw "Runtime theory discovery contains failed or skipped rows; count=$($badOutcomes.Count)."
    }
    $merged = @($Rows | Where-Object fqn -notin $CollapsedFqns) + @($ExpandedRows)
    $residual = @($merged | Where-Object {
        $_.source -eq 'list' -and $_.fqn -in $CollapsedFqns
    })
    if ($residual.Count -ne 0) {
        throw "Collapsed Theory list placeholder remained after expansion; count=$($residual.Count)."
    }
    return $merged
}

function Get-DiscoveredTests {
    param([Parameter(Mandatory)][string]$ProjectPath, [Parameter(Mandatory)][string]$Filter)
    $result = Invoke-PhaseCommand -FilePath 'dotnet' -Arguments @(
        'test', (Join-Path $repoRoot $ProjectPath), '--no-build', '--no-restore',
        '--list-tests', '--filter', $Filter, '--logger', 'console;verbosity=minimal') -SuppressOutput
    Assert-PhaseCommand -Result $result -Description "Test discovery ($ProjectPath)"
    $names = @(
        foreach ($line in ($result.Output -split "`r?`n")) {
            if ($line -match '^\s{4,}(CopilotAgentObservability\..+)$') { $Matches[1].Trim() }
        }
    )
    if ($names.Count -eq 0) { throw "Test discovery returned no identities; project=$ProjectPath filter=$Filter." }
    $rows = @(ConvertTo-TestRows -ProjectPath $ProjectPath -DisplayNames $names)
    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    $assemblyPath = Join-Path $repoRoot "$(Split-Path -Parent $ProjectPath)/bin/Debug/net10.0/$assemblyName.dll"
    $runtimeTheoryFqns = @(
        Get-CollapsedRuntimeTheoryFqns -Rows $rows -AssemblyPath $assemblyPath)
    if ($runtimeTheoryFqns.Count -eq 0) { return $rows }

    $runtimeFilter = Get-ExactFqnFilter -Fqns $runtimeTheoryFqns
    $expansionDirectory = Join-Path $resultsRoot ("discovery-expansion-" + $assemblyName)
    New-Item -ItemType Directory -Force -Path $expansionDirectory | Out-Null
    $expansion = Invoke-PhaseCommand -FilePath 'dotnet' -Arguments @(
        'test', (Join-Path $repoRoot $ProjectPath), '--no-build', '--no-restore',
        '--filter', "($Filter)&($runtimeFilter)", '--logger', 'trx', '--results-directory', $expansionDirectory)
    Assert-PhaseCommand -Result $expansion -Description "Runtime theory discovery ($ProjectPath)"
    $expandedRows = @(Get-TrxExecutionRows -Directory $expansionDirectory -ProjectPath $ProjectPath)
    $merged = @(Merge-CollapsedTheoryRows `
        -Rows $rows `
        -CollapsedFqns $runtimeTheoryFqns `
        -ExpandedRows $expandedRows)
    return @(Get-CanonicalRows -Rows $merged)
}

function Get-TrxExecutionRows {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$ProjectPath)
    $rawRows = @(
        foreach ($trxFile in (Get-ChildItem -LiteralPath $Directory -Filter '*.trx' -File -Recurse)) {
            [xml]$document = Get-Content -LiteralPath $trxFile.FullName -Raw
            $definitions = @{}
            foreach ($unitTest in $document.SelectNodes("//*[local-name()='UnitTest']")) {
                $method = $unitTest.SelectSingleNode("./*[local-name()='TestMethod']")
                if ($null -ne $method) {
                    $definitions[$unitTest.GetAttribute('id')] =
                        '{0}.{1}' -f $method.GetAttribute('className'), $method.GetAttribute('name')
                }
            }
            foreach ($result in $document.SelectNodes("//*[local-name()='UnitTestResult']")) {
                $testId = $result.GetAttribute('testId')
                if (-not $definitions.ContainsKey($testId)) {
                    throw "Runtime theory TRX result has no same-file definition; test_id=$testId."
                }
                [pscustomobject]@{
                    fqn = $definitions[$testId]
                    testName = $result.GetAttribute('testName')
                    outcome = $result.GetAttribute('outcome')
                }
            }
        }
    )
    if ($rawRows.Count -eq 0) { throw "Runtime theory discovery produced no TRX rows; path=$Directory." }
    $occurrences = @{}
    return @(
        foreach ($row in $rawRows) {
            $key = $row.fqn
            $occurrences[$key] = 1 + ($occurrences[$key] ?? 0)
            [ordered]@{
                projectPath = $ProjectPath
                fqn = $row.fqn
                testName = $row.testName
                outcome = $row.outcome
                source = 'trx'
                occurrence = $occurrences[$key]
                authorityIdentity = '{0}|{1}|{2}' -f $ProjectPath, $row.fqn, $occurrences[$key]
            }
        }
    )
}

function Get-ShardActualEvidence {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$ProjectPath)
    $rows = @(Get-TrxExecutionRows -Directory $Directory -ProjectPath $ProjectPath)
    $outcomeCounts = [ordered]@{}
    foreach ($group in @($rows | Group-Object { $_.outcome })) {
        $outcomeCounts[[string]$group.Name] = [int]$group.Count
    }
    return [pscustomobject]@{
        Count = $rows.Count
        Hash = Get-RowHash -Rows $rows
        OutcomeCounts = $outcomeCounts
    }
}

function Get-LocalShardId {
    param([Parameter(Mandatory)][string]$Fqn)
    $matches = @(
        foreach ($entry in $localShardSelectors.GetEnumerator()) {
            if (@($entry.Value | Where-Object { $Fqn.Contains($_, [StringComparison]::Ordinal) }).Count -ne 0) {
                $entry.Key
            }
        }
    )
    if ($matches.Count -ne 1) {
        throw "LocalMonitor test must belong to exactly one positive family; fqn=$Fqn matches=$($matches -join ',')."
    }
    return $matches[0]
}

function Get-LocalFilter {
    param([Parameter(Mandatory)][string[]]$Selectors)
    $selectorFilter = ($Selectors | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
    return "($completionFastFilter)&($selectorFilter)"
}

function Resolve-ManifestPath {
    if ([string]::IsNullOrWhiteSpace($ManifestPath)) { return Join-Path $resultsRoot 'completion-manifest.json' }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $ManifestPath))
}

function Invoke-DiscoveryPhase {
    New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null
    $policyTests = Invoke-PhaseCommand -FilePath 'pwsh' -Arguments @(
        '-NoProfile', '-File', $repositoryPolicyTests)
    Assert-PhaseCommand -Result $policyTests -Description 'Repository policy tests'
    $policy = Invoke-PhaseCommand -FilePath 'pwsh' -Arguments @(
        '-NoProfile', '-File', $repositoryPolicyGuard, '-RepositoryRoot', $repoRoot)
    Assert-PhaseCommand -Result $policy -Description 'Repository policy validation'
    $build = Invoke-PhaseCommand -FilePath 'dotnet' -Arguments @('build', $solution)
    Assert-PhaseCommand -Result $build -Description 'Whole-solution build'

    [xml]$solutionDocument = Get-Content -LiteralPath $solution -Raw
    $discoveredProjects = @(
        $solutionDocument.SelectNodes("//*[local-name()='Project']") |
            ForEach-Object { $_.GetAttribute('Path').Replace('\', '/') } |
            Where-Object { $_ -like 'tests/*.Tests/*.Tests.csproj' }
    )
    $expectedProjects = @($testProjects.Values) + @($localProject)
    if ((ConvertTo-Json @($discoveredProjects | Sort-Object) -Compress) -ne
        (ConvertTo-Json @($expectedProjects | Sort-Object) -Compress)) {
        throw "Solution test project discovery differs from runner shard authority; discovered=$($discoveredProjects -join ',')."
    }

    $shardRows = @{}
    foreach ($id in $requiredShardIds) { $shardRows[$id] = @() }
    foreach ($entry in $testProjects.GetEnumerator()) {
        $shardRows[$entry.Key] = @(Get-DiscoveredTests -ProjectPath $entry.Value -Filter $completionFastFilter)
    }
    $localRows = @(Get-DiscoveredTests -ProjectPath $localProject -Filter $completionFastFilter)
    foreach ($row in $localRows) { $shardRows[(Get-LocalShardId -Fqn $row.fqn)] += $row }
    $criticalRows = @(Get-DiscoveredTests -ProjectPath $localProject -Filter $criticalSmokeFilter)
    $criticalFqns = @($criticalRows | ForEach-Object { $_.fqn })
    if ((ConvertTo-Json @($criticalFqns | Sort-Object) -Compress) -ne
        (ConvertTo-Json @($criticalSmokeExpectedFqns | Sort-Object) -Compress)) {
        throw "Critical Smoke discovery must equal the exact two runner-owned identities; observed=$($criticalFqns -join ',')."
    }
    $shardRows[$criticalShardId] = $criticalRows

    $shards = @(
        foreach ($id in $requiredShardIds) {
            $projectPath = if ($testProjects.Contains($id)) { $testProjects[$id] } else { $localProject }
            $filter = if ($id -eq $criticalShardId) {
                $criticalSmokeFilter
            } elseif ($localShardSelectors.Contains($id)) {
                Get-LocalFilter -Selectors $localShardSelectors[$id]
            } else {
                $completionFastFilter
            }
            [ordered]@{
                id = $id
                kind = if ($id -eq $criticalShardId) { 'critical' } else { 'fast' }
                projectPath = $projectPath
                filter = $filter
                prerequisiteProjects = @($shardPrerequisiteProjects[$id])
                expectedCount = @($shardRows[$id]).Count
                expectedHash = Get-RowHash -Rows @($shardRows[$id])
                expectedRows = @(Get-CanonicalRows -Rows @($shardRows[$id]))
            }
        }
    )
    $candidateSha = Get-CandidateSha
    $manifest = [ordered]@{
        schemaVersion = 1
        candidateSha = $candidateSha
        authorityDigest = Get-AuthorityDigest
        requiredShardIds = $requiredShardIds
        expectedWindowsSkippedFqns = $expectedWindowsSkippedFqns
        baselineCount = @($shards | ForEach-Object { $_.expectedRows }).Count
        baselineHash = Get-RowHash -Rows @($shards | ForEach-Object { $_.expectedRows })
        baselineRows = @(Get-CanonicalRows -Rows @($shards | ForEach-Object { $_.expectedRows }))
        shards = $shards
    }
    $manifest.manifestDigest = Get-ManifestDigest -Manifest $manifest
    $resolvedManifest = Resolve-ManifestPath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedManifest) | Out-Null
    Write-ImmutableJson -Path $resolvedManifest -Value $manifest
    $contract = Invoke-PhaseCommand -FilePath 'pwsh' -Arguments @(
        '-NoProfile', '-File', $validationContract, '-Mode', 'Manifest',
        '-ManifestPath', $resolvedManifest, '-ExpectedShardIds', ($requiredShardIds -join ';'),
        '-ExpectedPrerequisiteProjectsJson', $serializedShardPrerequisiteProjects)
    Assert-PhaseCommand -Result $contract -Description 'Completion manifest validation'
    $discoveryReceipt = [ordered]@{
        schemaVersion = 1
        candidateSha = $candidateSha
        authorityDigest = $manifest.authorityDigest
        manifestDigest = $manifest.manifestDigest
        status = 'success'
        elapsedSeconds = $phaseStopwatch.Elapsed.TotalSeconds
    }
    Assert-PhaseFinalizationReserve -ElapsedSeconds $phaseStopwatch.Elapsed.TotalSeconds
    Write-ImmutableJson `
        -Path (Join-Path $resultsRoot 'discovery-receipt.json') `
        -Value $discoveryReceipt
    $matrix = [ordered]@{ include = @($requiredShardIds | ForEach-Object { [ordered]@{ id = $_ } }) } | ConvertTo-Json -Depth 5 -Compress
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        "matrix=$matrix" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    }
    Write-Output "completion_matrix=$matrix"
    Write-Output "validation_manifest=$resolvedManifest"
    Assert-PhaseCompletedWithinBudget -ElapsedSeconds $phaseStopwatch.Elapsed.TotalSeconds
}

function Invoke-ShardPhase {
    New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null
    $receiptToken = if ([string]::IsNullOrWhiteSpace($ShardId) -or $ShardId -notmatch '^[A-Za-z0-9_-]+$') {
        'invalid'
    } else {
        $ShardId
    }
    $receiptPath = Join-Path $resultsRoot "receipt-$receiptToken.json"
    $resolvedManifest = Resolve-ManifestPath
    $manifest = $null
    $shard = $null
    $lastExitCode = 1
    $timedOut = $false
    $actualEvidence = $null
    try {
        if ([string]::IsNullOrWhiteSpace($ShardId)) { throw 'Completion Shard phase requires -ShardId.' }
        $manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json -Depth 100
        if ([string]$manifest.candidateSha -ne (Get-CandidateSha)) { throw 'Completion manifest candidate SHA does not match HEAD.' }
        if ([string]$manifest.authorityDigest -ne (Get-AuthorityDigest)) { throw 'Completion manifest authority digest does not match the runner.' }
        if ([string]$manifest.manifestDigest -ne (Get-ManifestDigest -Manifest $manifest)) { throw 'Completion manifest digest is invalid.' }
        $shardMatches = @($manifest.shards | Where-Object { [string]$_.id -eq $ShardId })
        if ($shardMatches.Count -ne 1) { throw "Unknown Completion shard ID; shard_id=$ShardId." }
        $shard = $shardMatches[0]
        Assert-ShardPrerequisiteProjects -Shard $shard -CurrentShardId $ShardId
        foreach ($prerequisiteProject in @($shard.prerequisiteProjects)) {
            $prerequisiteProjectPath = Resolve-RepositoryProjectPath -RelativePath ([string]$prerequisiteProject)
            $prerequisiteRestore = Invoke-PhaseCommand -FilePath 'dotnet' -Arguments @('restore', $prerequisiteProjectPath)
            $lastExitCode = $prerequisiteRestore.ExitCode; $timedOut = $prerequisiteRestore.TimedOut
            Assert-PhaseCommand -Result $prerequisiteRestore -Description "Shard prerequisite restore ($ShardId)"
            $prerequisiteBuild = Invoke-PhaseCommand -FilePath 'dotnet' -Arguments @('build', $prerequisiteProjectPath, '--no-restore')
            $lastExitCode = $prerequisiteBuild.ExitCode; $timedOut = $prerequisiteBuild.TimedOut
            Assert-PhaseCommand -Result $prerequisiteBuild -Description "Shard prerequisite build ($ShardId)"
        }
        $projectPath = Join-Path $repoRoot ([string]$shard.projectPath)
        $restore = Invoke-PhaseCommand -FilePath 'dotnet' -Arguments @('restore', $projectPath)
        $lastExitCode = $restore.ExitCode; $timedOut = $restore.TimedOut
        Assert-PhaseCommand -Result $restore -Description "Shard restore ($ShardId)"
        $build = Invoke-PhaseCommand -FilePath 'dotnet' -Arguments @('build', $projectPath, '--no-restore')
        $lastExitCode = $build.ExitCode; $timedOut = $build.TimedOut
        Assert-PhaseCommand -Result $build -Description "Shard build ($ShardId)"
        if ([string]$shard.kind -eq 'critical') {
            $install = Invoke-PhaseCommand -FilePath 'pwsh' -Arguments @($playwrightInstaller)
            $lastExitCode = $install.ExitCode; $timedOut = $install.TimedOut
            Assert-PhaseCommand -Result $install -Description 'Critical shard Chromium install'
        }
        $test = Invoke-PhaseCommand -FilePath 'dotnet' -Arguments @(
            'test', $projectPath, '--no-build', '--no-restore', '--filter', [string]$shard.filter,
            '--logger', 'trx', '--results-directory', $resultsRoot)
        $lastExitCode = $test.ExitCode; $timedOut = $test.TimedOut
        if (@(Get-ChildItem -LiteralPath $resultsRoot -Filter '*.trx' -File -Recurse).Count -ne 0) {
            $actualEvidence = Get-ShardActualEvidence `
                -Directory $resultsRoot `
                -ProjectPath ([string]$shard.projectPath)
        }
        Assert-PhaseCommand -Result $test -Description "Shard test ($ShardId)"
        $successReceipt = [ordered]@{
            schemaVersion = 1; shardId = $ShardId; candidateSha = [string]$manifest.candidateSha
            authorityDigest = [string]$manifest.authorityDigest; status = 'success'; exitCode = 0
            manifestDigest = [string]$manifest.manifestDigest
            timedOut = $false; elapsedSeconds = $phaseStopwatch.Elapsed.TotalSeconds
            actualCount = $actualEvidence.Count; actualHash = $actualEvidence.Hash
            outcomeCounts = $actualEvidence.OutcomeCounts
        }
        $contractArguments = @(
            '-NoProfile', '-File', $validationContract, '-Mode', 'Shard',
            '-ManifestPath', $resolvedManifest, '-ShardId', $ShardId,
            '-ReceiptJson', ($successReceipt | ConvertTo-Json -Depth 100 -Compress),
            '-ResultsDirectory', $resultsRoot)
        if ([string]$shard.kind -ne 'critical') {
            $contractArguments += @('-ExpectedSkippedFqns', ($expectedWindowsSkippedFqns -join ';'))
        }
        $contract = Invoke-PhaseCommand -FilePath 'pwsh' -Arguments $contractArguments
        $lastExitCode = $contract.ExitCode; $timedOut = $contract.TimedOut
        Assert-PhaseCommand -Result $contract -Description "Shard evidence validation ($ShardId)"
        $successReceipt.elapsedSeconds = $phaseStopwatch.Elapsed.TotalSeconds
        Assert-PhaseFinalizationReserve -ElapsedSeconds $phaseStopwatch.Elapsed.TotalSeconds
        Write-ImmutableJson -Path $receiptPath -Value $successReceipt
        Write-Output "validation_shard=$ShardId"
        Write-Output "validation_results=$resultsRoot"
        Assert-PhaseCompletedWithinBudget -ElapsedSeconds $phaseStopwatch.Elapsed.TotalSeconds
    }
    catch {
        $failure = $_
        $failureReceipt = [ordered]@{
            schemaVersion = 1; shardId = $ShardId; candidateSha = [string]$manifest.candidateSha
            authorityDigest = [string]$manifest.authorityDigest; status = if ($timedOut) { 'timeout' } else { 'failure' }
            manifestDigest = [string]$manifest.manifestDigest
            exitCode = $lastExitCode; timedOut = $timedOut; elapsedSeconds = $phaseStopwatch.Elapsed.TotalSeconds
            actualCount = if ($null -eq $actualEvidence) { $null } else { $actualEvidence.Count }
            actualHash = if ($null -eq $actualEvidence) { $null } else { $actualEvidence.Hash }
            outcomeCounts = if ($null -eq $actualEvidence) { $null } else { $actualEvidence.OutcomeCounts }
            error = $failure.Exception.Message
        }
        try {
            Write-ShardFailureReceipt -Path $receiptPath -Value $failureReceipt
        }
        catch { Write-Warning "Unable to write immutable shard failure receipt: $($_.Exception.Message)" }
        throw $failure
    }
}

function Invoke-AggregatePhase {
    $resolvedManifest = Resolve-ManifestPath
    $manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json -Depth 100
    if ([string]$manifest.candidateSha -ne (Get-CandidateSha)) { throw 'Completion manifest candidate SHA does not match HEAD.' }
    if ([string]$manifest.authorityDigest -ne (Get-AuthorityDigest)) { throw 'Completion manifest authority digest does not match the runner.' }
    if ([string]$manifest.manifestDigest -ne (Get-ManifestDigest -Manifest $manifest)) { throw 'Completion manifest digest is invalid.' }
    if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) { throw 'Completion Aggregate phase requires -ArtifactsDirectory.' }
    if ([string]::IsNullOrWhiteSpace($RunAttempt)) { throw 'Completion Aggregate phase requires -RunAttempt.' }
    $resolvedArtifacts = [IO.Path]::GetFullPath((Join-Path $repoRoot $ArtifactsDirectory))
    New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null
    if ([string]::IsNullOrWhiteSpace($DependencyResultsPath)) {
        if ([string]::IsNullOrWhiteSpace($ShardDependencyResult)) { throw 'Completion Aggregate phase requires shard dependency evidence.' }
        $resolvedDependencies = Join-Path $resultsRoot 'dependency-results.json'
        $dependencies = [ordered]@{}
        foreach ($id in $requiredShardIds) { $dependencies[$id] = $ShardDependencyResult }
        $dependencies | ConvertTo-Json | Set-Content -LiteralPath $resolvedDependencies -Encoding utf8NoBOM
    } else {
        $resolvedDependencies = [IO.Path]::GetFullPath((Join-Path $repoRoot $DependencyResultsPath))
    }
    if (-not [string]::IsNullOrWhiteSpace($DiscoveryResult) -and $DiscoveryResult -ne 'success') {
        throw "Completion discovery dependency did not succeed; result=$DiscoveryResult."
    }
    $contract = Invoke-PhaseCommand -FilePath 'pwsh' -Arguments @(
        '-NoProfile', '-File', $validationContract, '-Mode', 'Aggregate',
        '-ManifestPath', $resolvedManifest, '-ArtifactsDirectory', $resolvedArtifacts,
        '-DependencyResultsPath', $resolvedDependencies, '-ExpectedShardIds', ($requiredShardIds -join ';'),
        '-ExpectedPrerequisiteProjectsJson', $serializedShardPrerequisiteProjects,
        '-RunAttempt', $RunAttempt,
        '-ExpectedSkippedFqns', ($expectedWindowsSkippedFqns -join ';'))
    Assert-PhaseCommand -Result $contract -Description 'Completion aggregate validation'
    $aggregateReceipt = [ordered]@{
        schemaVersion = 1; candidateSha = [string]$manifest.candidateSha
        authorityDigest = [string]$manifest.authorityDigest; status = 'success'
        manifestDigest = [string]$manifest.manifestDigest
        elapsedSeconds = $phaseStopwatch.Elapsed.TotalSeconds
    }
    Assert-PhaseFinalizationReserve -ElapsedSeconds $phaseStopwatch.Elapsed.TotalSeconds
    Write-ImmutableJson `
        -Path (Join-Path $resultsRoot 'aggregate-receipt.json') `
        -Value $aggregateReceipt
    Write-Output 'validation_aggregate=passed'
    Write-Output "validation_results=$resultsRoot"
    Assert-PhaseCompletedWithinBudget -ElapsedSeconds $phaseStopwatch.Elapsed.TotalSeconds
}

if ($Lane -eq 'Completion' -and -not [string]::IsNullOrWhiteSpace($Phase)) {
    switch ($Phase) {
        'Discovery' { Invoke-DiscoveryPhase }
        'Shard' { Invoke-ShardPhase }
        'Aggregate' { Invoke-AggregatePhase }
    }
    exit 0
}

New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null
Invoke-NativeCommand -FilePath 'pwsh' -Arguments @(
    '-NoProfile',
    '-File',
    $repositoryPolicyTests
)
Invoke-NativeCommand -FilePath 'pwsh' -Arguments @(
    '-NoProfile',
    '-File',
    $repositoryPolicyGuard,
    '-RepositoryRoot',
    $repoRoot
)
Invoke-NativeCommand -FilePath 'dotnet' -Arguments @('build', $solution)

if ($Lane -eq 'Completion' -and [string]::IsNullOrWhiteSpace($Phase)) {
    Invoke-TestPass -Target $solution -Filter $completionFastFilter -ResultsDirectory (Join-Path $resultsRoot 'fast') -LogFilePrefix 'completion-fast'
    Install-PlaywrightChromium
    $criticalResults = Join-Path $resultsRoot 'critical-smoke'
    Invoke-TestPass -Target $solution -Filter $criticalSmokeFilter -ResultsDirectory $criticalResults -LogFilePrefix 'completion-critical-smoke'
    Invoke-NativeCommand -FilePath 'pwsh' -Arguments @(
        '-NoProfile', '-File', $validationContract, '-Mode', 'CriticalSmoke',
        '-ResultsDirectory', $criticalResults, '-ExpectedFqns', ($criticalSmokeExpectedFqns -join ';'))
    Invoke-NativeCommand -FilePath 'pwsh' -Arguments @(
        '-NoProfile', '-File', $validationContract, '-Mode', 'CompletionBudget',
        '-ElapsedSeconds', $phaseStopwatch.Elapsed.TotalSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture))
} else {
    Install-PlaywrightChromium -WithDeps:($Partition -eq 'Linux')
    $nightlyResults = Join-Path $resultsRoot 'nightly'
    Invoke-NightlyValidation -ResultsDirectory $nightlyResults
}

Write-Output "validation_lane=$Lane"
if ($Lane -eq 'Nightly') { Write-Output "validation_partition=$Partition" }
Write-Output "validation_results=$resultsRoot"
