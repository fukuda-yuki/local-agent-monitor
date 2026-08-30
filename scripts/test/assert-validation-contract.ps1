[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('CriticalSmoke', 'CompletionBudget', 'Manifest', 'Shard', 'Aggregate', 'Workflow', 'TheoryExpansion')]
    [string]$Mode,
    [string]$ResultsDirectory,
    [string]$ExpectedFqns,
    [double]$ElapsedSeconds,
    [string]$ManifestPath,
    [string]$ExpectedShardIds,
    [string]$ExpectedPrerequisiteProjectsJson,
    [string]$ExpectedSkippedFqns,
    [string]$ShardId,
    [string]$ReceiptPath,
    [string]$ReceiptJson,
    [string]$ArtifactsDirectory,
    [string]$DependencyResultsPath,
    [string]$RunAttempt,
    [string]$WorkflowPath,
    [string]$RunnerPath,
    [string]$GuidePath,
    [string]$DiscoveryRowsPath,
    [string]$ExpansionResultsDirectory,
    [string]$ProjectPath,
    [string]$CollapsedFqns,
    [int]$ExpansionExitCode
)

$ErrorActionPreference = 'Stop'

function ConvertTo-StringArray {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return @() }
    return @($Value.Split(';', [StringSplitOptions]::RemoveEmptyEntries))
}

function ConvertTo-ExpectedPrerequisiteProjects {
    param([string]$Json)
    if ([string]::IsNullOrWhiteSpace($Json)) { return $null }
    $value = $Json | ConvertFrom-Json -Depth 100
    if ($null -eq $value) {
        throw 'Expected prerequisite authority JSON must contain an object.'
    }
    return $value
}

function Read-JsonFile {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required JSON file is missing; path=$Path."
    }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 100
}

function Get-ExecutionIdentity {
    param(
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$Fqn,
        [Parameter(Mandatory)][int]$Occurrence)
    return '{0}|{1}|{2}' -f $ProjectPath, $Fqn, $Occurrence
}

function Get-ManifestRowIdentities {
    param([Parameter(Mandatory)][object[]]$Rows)
    return @(
        foreach ($row in $Rows) {
            $identity = Get-ExecutionIdentity `
                -ProjectPath ([string]$row.projectPath) `
                -Fqn ([string]$row.fqn) `
                -Occurrence ([int]$row.occurrence)
            if ([string]$row.authorityIdentity -ne $identity) {
                throw "Manifest row identity is not canonical; observed=$($row.authorityIdentity) expected=$identity."
            }
            $identity
        }
    )
}

function Get-TrxRows {
    param([Parameter(Mandatory)][string]$Directory)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "TRX results directory is missing; path=$Directory."
    }
    $trxFiles = @(Get-ChildItem -LiteralPath $Directory -Filter '*.trx' -File -Recurse)
    if ($trxFiles.Count -eq 0) {
        throw "Validation produced no TRX files; path=$Directory."
    }
    $rawRows = @(
        foreach ($trxFile in $trxFiles) {
            [xml]$document = Get-Content -LiteralPath $trxFile.FullName -Raw
            $definitions = @{}
            foreach ($unitTest in $document.SelectNodes("//*[local-name()='UnitTest']")) {
                $method = $unitTest.SelectSingleNode("./*[local-name()='TestMethod']")
                if ($null -eq $method) { continue }
                $definitions[$unitTest.GetAttribute('id')] =
                    '{0}.{1}' -f $method.GetAttribute('className'), $method.GetAttribute('name')
            }
            foreach ($result in $document.SelectNodes("//*[local-name()='UnitTestResult']")) {
                $testId = $result.GetAttribute('testId')
                if (-not $definitions.ContainsKey($testId)) {
                    throw "TRX result has no matching test definition in the same file; file=$($trxFile.FullName) test_id=$testId."
                }
                [pscustomobject]@{
                    Fqn = $definitions[$testId]
                    TestName = $result.GetAttribute('testName')
                    Outcome = $result.GetAttribute('outcome')
                    File = $trxFile.FullName
                }
            }
        }
    )
    $occurrences = @{}
    return @(
        foreach ($row in $rawRows) {
            $key = $row.Fqn
            $occurrences[$key] = 1 + ($occurrences[$key] ?? 0)
            [pscustomobject]@{
                Fqn = $row.Fqn
                TestName = $row.TestName
                Outcome = $row.Outcome
                Occurrence = $occurrences[$key]
                DiagnosticIdentity = '{0}|{1}' -f $row.Fqn, $row.TestName
            }
        }
    )
}

function Assert-EqualMultiset {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Expected,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Actual)
    $expectedList = [Collections.Generic.List[string]]::new()
    foreach ($value in $Expected) { $expectedList.Add([string]$value) }
    $actualList = [Collections.Generic.List[string]]::new()
    foreach ($value in $Actual) { $actualList.Add([string]$value) }
    $expectedList.Sort([StringComparer]::Ordinal)
    $actualList.Sort([StringComparer]::Ordinal)
    $expectedJson = ConvertTo-Json @($expectedList) -Compress
    $actualJson = ConvertTo-Json @($actualList) -Compress
    if ($expectedJson -ne $actualJson) {
        throw "$Name mismatch; expected=$expectedJson actual=$actualJson."
    }
}

function Get-IdentityHash {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Identities)
    $values = [Collections.Generic.List[string]]::new()
    foreach ($identity in $Identities) { $values.Add([string]$identity) }
    $values.Sort([StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $values))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Assert-Manifest {
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string[]]$RequiredShardIds,
        [object]$ExpectedPrerequisiteProjects)
    if ([int]$Manifest.schemaVersion -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.candidateSha) -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.authorityDigest) -or
        [string]::IsNullOrWhiteSpace([string]$Manifest.manifestDigest)) {
        throw 'Manifest schema, candidate SHA, authority digest, and manifest digest are required.'
    }
    $manifestRequired = @($Manifest.requiredShardIds | ForEach-Object { [string]$_ })
    Assert-EqualMultiset -Name 'Manifest required shard IDs' -Expected $RequiredShardIds -Actual $manifestRequired
    $shards = @($Manifest.shards)
    $shardIds = @($shards | ForEach-Object { [string]$_.id })
    Assert-EqualMultiset -Name 'Manifest shard IDs' -Expected $RequiredShardIds -Actual $shardIds
    if (@($shardIds | Select-Object -Unique).Count -ne $shardIds.Count) {
        throw 'Manifest contains duplicate shard IDs.'
    }
    if ($null -ne $ExpectedPrerequisiteProjects) {
        $authorityProperties = @($ExpectedPrerequisiteProjects.PSObject.Properties)
        Assert-EqualMultiset `
            -Name 'Expected prerequisite authority shard IDs' `
            -Expected $RequiredShardIds `
            -Actual @($authorityProperties | ForEach-Object Name)
        if (@($authorityProperties | Where-Object { $_.Value -isnot [System.Array] }).Count -ne 0) {
            throw 'Expected prerequisite authority values must be arrays.'
        }
    }
    $baselineIdentities = Get-ManifestRowIdentities -Rows @($Manifest.baselineRows)
    if ($baselineIdentities.Count -eq 0 -or
        @($baselineIdentities | Select-Object -Unique).Count -ne $baselineIdentities.Count) {
        throw 'Manifest baseline must contain distinct execution identities.'
    }
    if ([int]$Manifest.baselineCount -ne $baselineIdentities.Count -or
        [string]$Manifest.baselineHash -ne (Get-IdentityHash -Identities $baselineIdentities)) {
        throw 'Manifest baseline count/hash does not match its canonical identity multiset.'
    }
    $assigned = @(
        foreach ($shard in $shards) {
            $rows = @($shard.expectedRows)
            if ($rows.Count -eq 0) {
                throw "Manifest required shard is empty; shard_id=$($shard.id)."
            }
            if ([string]::IsNullOrWhiteSpace([string]$shard.projectPath) -or
                [string]::IsNullOrWhiteSpace([string]$shard.filter)) {
                throw "Manifest shard lacks runner-owned project/filter authority; shard_id=$($shard.id)."
            }
            if ($null -eq $shard.PSObject.Properties['prerequisiteProjects'] -or
                $shard.prerequisiteProjects -isnot [System.Array]) {
                throw "Manifest shard lacks runner-owned prerequisite authority; shard_id=$($shard.id)."
            }
            $prerequisites = @($shard.prerequisiteProjects | ForEach-Object { [string]$_ })
            if ($null -ne $ExpectedPrerequisiteProjects) {
                $authorityProperty = $ExpectedPrerequisiteProjects.PSObject.Properties[[string]$shard.id]
                if ($null -eq $authorityProperty) {
                    throw "Expected prerequisite authority is missing a shard; shard_id=$($shard.id)."
                }
                $expectedPrerequisites = @($authorityProperty.Value | ForEach-Object { [string]$_ })
                if ((ConvertTo-Json -InputObject $prerequisites -Compress) -ne
                    (ConvertTo-Json -InputObject $expectedPrerequisites -Compress)) {
                    throw "Manifest prerequisite projects do not match passed authority; shard_id=$($shard.id)."
                }
            }
            $distinctPrerequisites = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($prerequisite in $prerequisites) {
                $segments = @($prerequisite.Split('/'))
                if ([string]::IsNullOrWhiteSpace($prerequisite) -or
                    [IO.Path]::IsPathRooted($prerequisite) -or
                    $prerequisite.Contains('\') -or
                    -not $prerequisite.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase) -or
                    $segments.Count -lt 2 -or
                    @($segments | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
                    throw "Manifest prerequisite must be a normalized repository-contained project path; shard_id=$($shard.id) path=$prerequisite."
                }
                if (-not $distinctPrerequisites.Add($prerequisite)) {
                    throw "Manifest prerequisite project paths must be distinct; shard_id=$($shard.id) path=$prerequisite."
                }
            }
            if (@($rows | Where-Object { [string]$_.projectPath -ne [string]$shard.projectPath }).Count -ne 0) {
                throw "Manifest row project does not match its shard project; shard_id=$($shard.id)."
            }
            $rowIdentities = Get-ManifestRowIdentities -Rows $rows
            if ([int]$shard.expectedCount -ne $rowIdentities.Count -or
                [string]$shard.expectedHash -ne (Get-IdentityHash -Identities $rowIdentities)) {
                throw "Manifest shard count/hash is invalid; shard_id=$($shard.id)."
            }
            $rowIdentities
        }
    )
    Assert-EqualMultiset -Name 'Manifest partition' -Expected $baselineIdentities -Actual $assigned
}

function Assert-ShardEvidence {
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string]$CurrentShardId,
        [Parameter(Mandatory)][object]$Receipt,
        [Parameter(Mandatory)][object[]]$Rows,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$AllowedSkippedFqns)
    $matches = @($Manifest.shards | Where-Object { [string]$_.id -eq $CurrentShardId })
    if ($matches.Count -ne 1) {
        throw "Unknown or duplicated shard ID; shard_id=$CurrentShardId."
    }
    $shard = $matches[0]
    if ([string]$Receipt.shardId -ne $CurrentShardId -or
        [string]$Receipt.candidateSha -ne [string]$Manifest.candidateSha -or
        [string]$Receipt.authorityDigest -ne [string]$Manifest.authorityDigest -or
        [string]$Receipt.manifestDigest -ne [string]$Manifest.manifestDigest) {
        throw "Shard receipt identity does not match manifest; shard_id=$CurrentShardId."
    }
    if ([string]$Receipt.status -ne 'success' -or
        [int]$Receipt.exitCode -ne 0 -or
        [bool]$Receipt.timedOut) {
        throw "Shard receipt is not successful; shard_id=$CurrentShardId status=$($Receipt.status) exit_code=$($Receipt.exitCode) timed_out=$($Receipt.timedOut)."
    }
    $badOutcomes = @($Rows | Where-Object {
        $_.Outcome -ne 'Passed' -and
        -not ($_.Outcome -eq 'NotExecuted' -and $_.Fqn -in $AllowedSkippedFqns)
    })
    if ($badOutcomes.Count -ne 0) {
        throw "Shard contains failed or unexpected skipped rows; shard_id=$CurrentShardId count=$($badOutcomes.Count)."
    }
    $expected = Get-ManifestRowIdentities -Rows @($shard.expectedRows)
    $actual = @($Rows | ForEach-Object {
        Get-ExecutionIdentity `
            -ProjectPath ([string]$shard.projectPath) `
            -Fqn ([string]$_.Fqn) `
            -Occurrence ([int]$_.Occurrence)
    })
    $actualHash = Get-IdentityHash -Identities $actual
    if ([int]$Receipt.actualCount -ne $actual.Count -or
        [string]$Receipt.actualHash -ne $actualHash) {
        throw "Shard receipt actual count/hash does not match TRX evidence; shard_id=$CurrentShardId."
    }
    Assert-EqualMultiset -Name "Shard row identities ($CurrentShardId)" -Expected $expected -Actual $actual
}

if ($Mode -eq 'CompletionBudget') {
    if ($ElapsedSeconds -gt 1800) {
        throw "Completion validation exceeded its 30 minute budget; elapsed_seconds=$ElapsedSeconds."
    }
    Write-Output "completion_elapsed_seconds=$ElapsedSeconds"
    exit 0
}

if ($Mode -eq 'CriticalSmoke') {
    if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
        throw 'Critical smoke validation requires a results directory.'
    }
    $expected = ConvertTo-StringArray $ExpectedFqns
    if ($expected.Count -ne 2 -or @($expected | Select-Object -Unique).Count -ne 2) {
        throw 'Critical smoke validation requires exactly two distinct expected identities.'
    }
    $rows = Get-TrxRows -Directory $ResultsDirectory
    if ($rows.Count -ne 2 -or @($rows | Where-Object Outcome -eq 'Passed').Count -ne 2) {
        throw "Critical smoke validation requires exactly 2 executed, 2 passed, 0 failed, and 0 skipped tests; total=$($rows.Count)."
    }
    Assert-EqualMultiset -Name 'The exact critical smoke identities' -Expected $expected -Actual @($rows | ForEach-Object Fqn)
    Write-Output 'critical_smoke_exact_identities=passed'
    exit 0
}

if ($Mode -eq 'Manifest') {
    $manifest = Read-JsonFile -Path $ManifestPath
    $expectedPrerequisites = ConvertTo-ExpectedPrerequisiteProjects -Json $ExpectedPrerequisiteProjectsJson
    Assert-Manifest `
        -Manifest $manifest `
        -RequiredShardIds (ConvertTo-StringArray $ExpectedShardIds) `
        -ExpectedPrerequisiteProjects $expectedPrerequisites
    Write-Output 'manifest_exact_partition=passed'
    exit 0
}

if ($Mode -eq 'TheoryExpansion') {
    $discoveryRows = @(Read-JsonFile -Path $DiscoveryRowsPath)
    $collapsed = @(ConvertTo-StringArray $CollapsedFqns)
    if ($ExpansionExitCode -ne 0) {
        throw "Bounded Theory expansion command failed; exit_code=$ExpansionExitCode."
    }
    foreach ($fqn in $collapsed) {
        $placeholder = @($discoveryRows | Where-Object { [string]$_.fqn -eq $fqn })
        if ($placeholder.Count -ne 1 -or [string]$placeholder[0].testName -ne $fqn) {
            throw "Collapsed Theory must have exactly one residual-free list placeholder; fqn=$fqn count=$($placeholder.Count)."
        }
    }
    $rows = @(Get-TrxRows -Directory $ExpansionResultsDirectory)
    if ($rows.Count -eq 0) {
        throw 'Bounded Theory expansion produced zero rows.'
    }
    $unexpected = @($rows | Where-Object { $_.Fqn -notin $collapsed })
    if ($unexpected.Count -ne 0) {
        throw "Bounded Theory expansion produced an unexpected method; fqn=$($unexpected[0].Fqn)."
    }
    $badOutcomes = @($rows | Where-Object Outcome -ne 'Passed')
    if ($badOutcomes.Count -ne 0) {
        throw "Bounded Theory expansion contains failed or skipped rows; count=$($badOutcomes.Count)."
    }
    foreach ($fqn in $collapsed) {
        if (@($rows | Where-Object Fqn -eq $fqn).Count -eq 0) {
            throw "Bounded Theory expansion produced no rows for a collapsed method; fqn=$fqn."
        }
    }
    $preserved = @($discoveryRows | Where-Object { [string]$_.fqn -notin $collapsed })
    Write-Output "theory_expanded_rows=$($preserved.Count + $rows.Count)"
    Write-Output "theory_expansion_project=$ProjectPath"
    exit 0
}

if ($Mode -eq 'Shard') {
    $manifest = Read-JsonFile -Path $ManifestPath
    $receipt = if ([string]::IsNullOrWhiteSpace($ReceiptJson)) {
        Read-JsonFile -Path $ReceiptPath
    } else {
        $ReceiptJson | ConvertFrom-Json -Depth 100
    }
    $rows = Get-TrxRows -Directory $ResultsDirectory
    $allowedSkips = @(ConvertTo-StringArray $ExpectedSkippedFqns)
    Assert-ShardEvidence `
        -Manifest $manifest `
        -CurrentShardId $ShardId `
        -Receipt $receipt `
        -Rows $rows `
        -AllowedSkippedFqns $allowedSkips
    Write-Output 'shard_exact_rows=passed'
    exit 0
}

if ($Mode -eq 'Aggregate') {
    $manifest = Read-JsonFile -Path $ManifestPath
    $expectedIds = ConvertTo-StringArray $ExpectedShardIds
    $allowedSkips = @(ConvertTo-StringArray $ExpectedSkippedFqns)
    $expectedPrerequisites = ConvertTo-ExpectedPrerequisiteProjects -Json $ExpectedPrerequisiteProjectsJson
    Assert-Manifest `
        -Manifest $manifest `
        -RequiredShardIds $expectedIds `
        -ExpectedPrerequisiteProjects $expectedPrerequisites
    $dependencies = Read-JsonFile -Path $DependencyResultsPath
    $dependencyProperties = @($dependencies.PSObject.Properties)
    Assert-EqualMultiset `
        -Name 'Aggregate dependency shard IDs' `
        -Expected $expectedIds `
        -Actual @($dependencyProperties | ForEach-Object Name)
    $badDependencies = @($dependencyProperties | Where-Object { [string]$_.Value -ne 'success' })
    if ($badDependencies.Count -ne 0) {
        throw "Aggregate dependency did not succeed; shards=$($badDependencies.Name -join ',')."
    }
    if ([string]::IsNullOrWhiteSpace($RunAttempt) -or $RunAttempt -notmatch '^\d+$') {
        throw 'Aggregate requires a workflow run attempt for exact artifact boundaries.'
    }
    $artifactDirectories = @(Get-ChildItem -LiteralPath $ArtifactsDirectory -Directory)
    if (@(Get-ChildItem -LiteralPath $ArtifactsDirectory -File).Count -ne 0) {
        throw 'Aggregate artifact root contains files outside direct shard boundaries.'
    }
    $expectedArtifactNames = @($expectedIds | ForEach-Object {
        'completion-shard-{0}-{1}-{2}' -f $manifest.candidateSha, $RunAttempt, $_
    })
    Assert-EqualMultiset `
        -Name 'Aggregate direct shard artifact names' `
        -Expected $expectedArtifactNames `
        -Actual @($artifactDirectories | ForEach-Object Name)
    $allRows = @()
    foreach ($currentShardId in $expectedIds) {
        $artifactName = 'completion-shard-{0}-{1}-{2}' -f $manifest.candidateSha, $RunAttempt, $currentShardId
        $artifactMatches = @($artifactDirectories | Where-Object Name -ceq $artifactName)
        if ($artifactMatches.Count -ne 1) {
            throw "Aggregate requires exactly one direct artifact boundary per shard; shard_id=$currentShardId count=$($artifactMatches.Count)."
        }
        $artifactDirectory = $artifactMatches[0].FullName
        if (@(Get-ChildItem -LiteralPath $artifactDirectory -Directory).Count -ne 0) {
            throw "Aggregate shard artifact contains nested boundaries; shard_id=$currentShardId."
        }
        $receiptMatches = @(Get-ChildItem -LiteralPath $artifactDirectory -Filter 'receipt-*.json' -File)
        if ($receiptMatches.Count -ne 1 -or $receiptMatches[0].Name -cne "receipt-$currentShardId.json") {
            throw "Aggregate requires exactly one correctly named direct receipt per artifact; shard_id=$currentShardId count=$($receiptMatches.Count)."
        }
        $trxFiles = @(Get-ChildItem -LiteralPath $artifactDirectory -Filter '*.trx' -File)
        if ($trxFiles.Count -ne 1) {
            throw "Aggregate requires exactly one direct TRX per artifact; shard_id=$currentShardId count=$($trxFiles.Count)."
        }
        $rows = Get-TrxRows -Directory $artifactDirectory
        Assert-ShardEvidence `
            -Manifest $manifest `
            -CurrentShardId $currentShardId `
            -Receipt (Read-JsonFile -Path $receiptMatches[0].FullName) `
            -Rows $rows `
            -AllowedSkippedFqns $allowedSkips
        $allRows += $rows
        foreach ($row in $rows) {
            $row | Add-Member -NotePropertyName AuthorityIdentity -NotePropertyValue (
                Get-ExecutionIdentity `
                    -ProjectPath ([string]$manifest.shards.Where({ $_.id -eq $currentShardId })[0].projectPath) `
                    -Fqn ([string]$row.Fqn) `
                    -Occurrence ([int]$row.Occurrence)) -Force
        }
    }
    $baseline = Get-ManifestRowIdentities -Rows @($manifest.baselineRows)
    Assert-EqualMultiset `
        -Name 'Aggregate global row union' `
        -Expected $baseline `
        -Actual @($allRows | ForEach-Object AuthorityIdentity)
    $actualSkips = @($allRows | Where-Object Outcome -eq 'NotExecuted' | ForEach-Object Fqn)
    Assert-EqualMultiset -Name 'Aggregate exact Windows skips' -Expected $allowedSkips -Actual $actualSkips
    $critical = @($manifest.shards | Where-Object { [string]$_.kind -eq 'critical' })
    if ($critical.Count -ne 0) {
        if ($critical.Count -ne 1 -or @($critical[0].expectedRows).Count -ne 2) {
            throw 'Aggregate requires one critical shard with exactly two expected identities.'
        }
        $criticalIds = Get-ManifestRowIdentities -Rows @($critical[0].expectedRows)
        $criticalRows = @($allRows | Where-Object AuthorityIdentity -in $criticalIds)
        if ($criticalRows.Count -ne 2 -or @($criticalRows | Where-Object Outcome -eq 'Passed').Count -ne 2) {
            throw 'Aggregate requires the critical exact two identities to pass once each.'
        }
    }
    Write-Output 'completion_aggregate=passed'
    exit 0
}

if ($Mode -eq 'Workflow') {
    foreach ($path in @($WorkflowPath, $RunnerPath, $GuidePath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Workflow contract input is missing; path=$path."
        }
    }
    $workflow = Get-Content -LiteralPath $WorkflowPath -Raw
    $runner = Get-Content -LiteralPath $RunnerPath -Raw
    $guide = Get-Content -LiteralPath $GuidePath -Raw
    $requiredWorkflowPatterns = @(
        '(?m)^concurrency:',
        '(?m)^  completion-discovery:',
        '(?m)^  completion-shard:',
        '(?m)^  completion:',
        'fail-fast:\s*false',
        '-Phase Discovery',
        '-Phase Shard',
        '-Phase Aggregate',
        '-DiscoveryResult',
        'needs\.completion-discovery\.result',
        '-ShardDependencyResult',
        'needs\.completion-shard\.result',
        '-RunAttempt',
        'github\.run_attempt',
        'needs:\s*\[completion-discovery, completion-shard\]',
        'if:\s*always\(\)',
        'timeout-minutes:\s*45',
        'path:\s*~/\.nuget/packages',
        'validation-schedule-\{0\}.+github\.run_id',
        'validation-completion-\{0\}',
        'cancel-in-progress:\s*\$\{\{ github\.event_name != ''schedule'' \}\}'
    )
    foreach ($pattern in $requiredWorkflowPatterns) {
        if ($workflow -notmatch $pattern) {
            throw "Workflow lacks required Completion topology; pattern=$pattern."
        }
    }
    foreach ($forbidden in @('ValidationLane', 'CriticalSmoke', 'tests/.+\.csproj', 'dotnet\s+test\s+.*--filter')) {
        if ($workflow -match $forbidden) {
            throw "Workflow duplicates runner validation authority; pattern=$forbidden."
        }
    }
    if ($workflow -match 'env\.USERPROFILE' -or
        @([regex]::Matches($workflow, '(?m)^\s*concurrency:')).Count -ne 3) {
        throw 'Workflow cache or concurrency boundaries do not preserve sibling coexistence and old-candidate cancellation.'
    }
    if ($runner -notmatch '\[string\]\$Phase' -or
        $runner -notmatch 'if \(\$Lane -eq ''Completion'' -and \[string\]::IsNullOrWhiteSpace\(\$Phase\)\)' -or
        $runner -notmatch '\$completionFastFilter' -or
        $runner -notmatch '\$criticalSmokeExpectedFqns' -or
        $runner -notmatch 'WaitForExitAsync\(\)' -or
        $runner -notmatch '\.Kill\(\$true\)' -or
        $runner -notmatch '\$phaseFinalizationReserveSeconds\s*=\s*5') {
        throw 'Runner does not preserve the shared phased/unphased Completion authority.'
    }
    foreach ($peerPattern in @(
        "'.Tests.Local'", "'.Tests.Sanitized'", "'.Tests.Playwright'",
        "'.Tests.Retention.'", "'.Tests.Alert'",
        'ImmutableCompareRendersNineSectionsRowsEvidenceAndResponsiveTableWithoutRecompute',
        'ComparePreviewCreatesFromTransientOrderedCohortsAndNavigatesOnlyByServerLocation')) {
        if ($runner -notmatch $peerPattern) {
            throw "Runner does not preserve required Fast/Critical peer colocation; pattern=$peerPattern."
        }
    }
    $unphasedStart = $runner.IndexOf("if (`$Lane -eq 'Completion' -and [string]::IsNullOrWhiteSpace(`$Phase))")
    $unphased = if ($unphasedStart -ge 0) { $runner.Substring($unphasedStart) } else { '' }
    $unphasedMarkers = @(
        'Invoke-TestPass -Target $solution -Filter $completionFastFilter',
        'Install-PlaywrightChromium',
        'Invoke-TestPass -Target $solution -Filter $criticalSmokeFilter',
        "'-Mode', 'CriticalSmoke'",
        "'-Mode', 'CompletionBudget'")
    $lastMarker = -1
    foreach ($marker in $unphasedMarkers) {
        $markerIndex = $unphased.IndexOf($marker, $lastMarker + 1, [StringComparison]::Ordinal)
        if ($markerIndex -le $lastMarker) { throw "Unphased Completion sequence is incomplete or reordered; marker=$marker." }
        $lastMarker = $markerIndex
    }
    if ($guide -notmatch 'completion-discovery' -or
        $guide -notmatch 'stable aggregate' -or
        $guide -notmatch 'monolithic') {
        throw 'Repository workflow guide does not describe the hosted topology and compatibility entry.'
    }
    Write-Output 'workflow_completion_topology=passed'
    exit 0
}
