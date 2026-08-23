[CmdletBinding()]
param([string]$CandidateSha,[string]$RuntimeDirectory,[string]$ResultDirectory,[string]$ResultFileName,[string]$Lane,[bool]$ExpectedCleanupComplete=$true)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop';$markerEnv='CAO_ISSUE158_RUNTIME_MARKER'
function Block([string]$Code){Write-Output "scan_result=BLOCKED check=$Code";exit 1}
try{. (Join-Path $PSScriptRoot 'common.ps1')
if($CandidateSha-cnotmatch'^[0-9a-f]{40}$'){Block 'candidate_sha'};if($Lane-cnotin@('windows_owned_session','linux_ext4_current_file')){Block 'lane'}
if([string]::IsNullOrWhiteSpace($ResultFileName)-or[IO.Path]::GetFileName($ResultFileName)-cne$ResultFileName-or$ResultFileName-cnotmatch'^[a-z0-9][a-z0-9._-]*\.json$'){Block 'result_name'}
$environment=[Environment]::GetEnvironmentVariables('Process');if(-not$environment.Contains($markerEnv)-or[string]::IsNullOrEmpty([string]$environment[$markerEnv])-or-not$environment.Contains('CAO_ISSUE158_RUN_ID')-or([string]$environment['CAO_ISSUE158_RUN_ID'])-cnotmatch'^[0-9a-f]{32}$'){Block 'runtime_marker'};$runtimeMarker=[string]$environment[$markerEnv];$runId=[string]$environment['CAO_ISSUE158_RUN_ID']
$repo=Get-Issue158PhysicalPath (Join-Path $PSScriptRoot '..\..\..');$runtime=Get-Issue158PhysicalPath $RuntimeDirectory $true;$result=Get-Issue158PhysicalPath $ResultDirectory;if((Get-Issue158LexicalPath $RuntimeDirectory)-cne$runtime-or(Get-Issue158LexicalPath $ResultDirectory)-cne$result){Block 'path_reparse'}
$head=(& git -C $repo rev-parse HEAD 2>$null|Out-String).Trim();if($LASTEXITCODE-ne0-or$head-cne$CandidateSha){Block 'candidate_mismatch'}
$branch=(& git -C $repo symbolic-ref -q HEAD 2>$null|Out-String).Trim();if($LASTEXITCODE-eq0-or$branch.Length-ne0){Block 'candidate_attached'}
$dirty=(& git -C $repo status --porcelain=v1 --untracked-files=normal 2>$null|Out-String).Trim();if($LASTEXITCODE-ne0-or$dirty.Length-ne0){Block 'candidate_dirty'}
if(Test-Path -LiteralPath $runtime){Block 'runtime_cleanup'};if(-not(Test-Path -LiteralPath $result -PathType Container)){Block 'result_owner'}
$entries=@(Get-ChildItem -LiteralPath $result -Force);if($entries.Count-ne2-or@($entries|Where-Object{$_.Name-cnotin@($script:Issue158OwnerName,$ResultFileName)}).Count-ne0){Block 'unexpected_artifact'}
if(@($entries|Where-Object{$_.PSIsContainer-or(($_.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0)-or($_.LinkType-and$_.Name-cne$script:Issue158OwnerName)}).Count-ne0){Block 'unexpected_artifact'}
try{[void](Get-Issue158Owner $result $runId $CandidateSha 'result' $runtime $result);$bytes=[IO.File]::ReadAllBytes((Join-Path $result $ResultFileName))}catch{Block 'result_owner'}
try{[void](Get-Issue158ValidatedResult $bytes $CandidateSha $Lane $runtimeMarker $ExpectedCleanupComplete)}catch{$code=$_.Exception.Message;if($code-cnotin@('result_size','runtime_marker_leak','prohibited_content','result_json','lane','result_schema','result_literal','result_count','result_check')){$code='internal'};Block $code}
Write-Output 'scan_result=PASSED';exit 0}catch{Block 'internal'}
