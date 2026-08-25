[CmdletBinding()]
param([string]$CandidateSha,[switch]$OperatorAuthorized)
try {
 Set-StrictMode -Version Latest
 $ErrorActionPreference='Stop'
 function Block([string]$Code){Write-Output "windows_result=BLOCKED check=$Code";exit 1}
 if(-not$OperatorAuthorized){Block 'operator_authorization'}
 if([string]::IsNullOrWhiteSpace($CandidateSha)-or$CandidateSha-cnotmatch'^[0-9a-f]{40}$'){Block 'candidate_sha'}
 $processEnvironment=[Environment]::GetEnvironmentVariables('Process')
 if(@($processEnvironment.Keys|Where-Object{[string]::Equals([string]$_,'COPILOT_CLI_PATH',[StringComparison]::OrdinalIgnoreCase)}).Count-ne0){Block 'copilot_cli_path'}
 . (Join-Path $PSScriptRoot 'common.ps1')
 $repo=Get-Issue158PhysicalPath (Join-Path $PSScriptRoot '..\..\..')
 $temp=Get-Issue158PhysicalPath ([IO.Path]::GetTempPath())
 $runId=([Guid]::NewGuid().ToString('N')).ToLowerInvariant()
 $runtime=Join-Path $temp ('cao-issue158-runtime-'+$runId)
 $result=Join-Path $temp ('cao-issue158-result-'+$runId)
 $resultFile='result.json'
 $runtimeMarker='issue158-'+[Guid]::NewGuid().ToString('N')
 $hadRunId=$processEnvironment.Contains('CAO_ISSUE158_RUN_ID');$priorRunId=if($hadRunId){[string]$processEnvironment['CAO_ISSUE158_RUN_ID']}else{$null}
 $hadRuntimeMarker=$processEnvironment.Contains('CAO_ISSUE158_RUNTIME_MARKER');$priorRuntimeMarker=if($hadRuntimeMarker){[string]$processEnvironment['CAO_ISSUE158_RUNTIME_MARKER']}else{$null}
 $identityEnvironmentBound=$false
 function Restore-IdentityEnvironment {
  if(-not$identityEnvironmentBound){return}
  [Environment]::SetEnvironmentVariable('CAO_ISSUE158_RUN_ID',$(if($hadRunId){$priorRunId}else{$null}),'Process')
  [Environment]::SetEnvironmentVariable('CAO_ISSUE158_RUNTIME_MARKER',$(if($hadRuntimeMarker){$priorRuntimeMarker}else{$null}),'Process')
  $script:identityEnvironmentBound=$false
 }
 if((Test-Path -LiteralPath $runtime)-or(Test-Path -LiteralPath $result)){Block 'path_collision'}
 [Environment]::SetEnvironmentVariable('CAO_ISSUE158_RUN_ID',$runId,'Process')
 [Environment]::SetEnvironmentVariable('CAO_ISSUE158_RUNTIME_MARKER',$runtimeMarker,'Process')
 $identityEnvironmentBound=$true
 $runtimeCreated=$false;$resultCreated=$false;$runtimeOwned=$false;$resultOwned=$false
 function Remove-Owned([string]$Path,[string]$Kind,[bool]$RuntimeMayBeAbsent=$false){
  $lexical=Get-Issue158LexicalPath $Path;$physical=Get-Issue158PhysicalPath $Path
  if($lexical-cne$physical-or-not(Test-Issue158Within $physical $temp)){throw 'cleanup'}
  $runtimeIdentity=Get-Issue158PhysicalPath $runtime $RuntimeMayBeAbsent
  $resultIdentity=Get-Issue158PhysicalPath $result
  [void](Get-Issue158Owner $physical $runId $CandidateSha $Kind $runtimeIdentity $resultIdentity)
  Remove-Item -LiteralPath $physical -Recurse -Force
  if(Test-Path -LiteralPath $physical){throw 'cleanup'}
 }
 function Remove-Partial([string]$Path,[string]$Kind){
  $lexical=Get-Issue158LexicalPath $Path;$physical=Get-Issue158PhysicalPath $Path
  if($lexical -cne $physical -or [IO.Path]::GetDirectoryName($physical) -cne $temp -or [IO.Path]::GetFileName($physical) -cne ("cao-issue158-$Kind-$runId")){throw 'cleanup'}
  $rootItem=Get-Item -LiteralPath $physical -Force
  if(-not$rootItem.PSIsContainer-or$rootItem.LinkType-or($rootItem.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw 'cleanup'}
  $children=@(Get-ChildItem -LiteralPath $physical -Force)
  if($children.Count-gt1){throw 'cleanup'}
  if($children.Count-eq1){$owner=$children[0];if($owner.Name-cne$script:Issue158OwnerName-or$owner.PSIsContainer-or$owner.LinkType-or($owner.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw 'cleanup'};Remove-Item -LiteralPath $owner.FullName -Force}
  Remove-Item -LiteralPath $physical -Force
  if(Test-Path -LiteralPath $physical){throw 'cleanup'}
 }
 function Read-BoundedChildOutput([Diagnostics.Process]$Process){
  $limit=1048576;$stdoutChars=0;$stdoutOverflow=$false;$stderrNonempty=$false
  $stdoutBuffer=[char[]]::new(8192);$stderrBuffer=[char[]]::new(8192)
  $stdoutTask=$Process.StandardOutput.ReadAsync($stdoutBuffer,0,$stdoutBuffer.Length)
  $stderrTask=$Process.StandardError.ReadAsync($stderrBuffer,0,$stderrBuffer.Length)
  $exitTask=$Process.WaitForExitAsync()
  while($null-ne$stdoutTask-or$null-ne$stderrTask-or-not$exitTask.IsCompleted){
   $pending=[Collections.Generic.List[Threading.Tasks.Task]]::new();if($null-ne$stdoutTask){[void]$pending.Add($stdoutTask)};if($null-ne$stderrTask){[void]$pending.Add($stderrTask)};if(-not$exitTask.IsCompleted){[void]$pending.Add($exitTask)}
   [void][Threading.Tasks.Task]::WhenAny($pending).GetAwaiter().GetResult()
   if($null-ne$stdoutTask-and$stdoutTask.IsCompleted){$read=$stdoutTask.GetAwaiter().GetResult();if($read-eq0){$stdoutTask=$null}else{if(-not$stdoutOverflow){$remaining=$limit-$stdoutChars;if($read-gt$remaining){$stdoutOverflow=$true}else{$stdoutChars+=$read}};$stdoutTask=$Process.StandardOutput.ReadAsync($stdoutBuffer,0,$stdoutBuffer.Length)}}
   if($null-ne$stderrTask-and$stderrTask.IsCompleted){$read=$stderrTask.GetAwaiter().GetResult();if($read-eq0){$stderrTask=$null}else{$stderrNonempty=$true;$stderrTask=$Process.StandardError.ReadAsync($stderrBuffer,0,$stderrBuffer.Length)}}
  }
  [void]$exitTask.GetAwaiter().GetResult()
  [pscustomobject]@{StdoutOverflow=$stdoutOverflow;StderrNonempty=$stderrNonempty}
 }
 function Stop-OwnedChildProcessBestEffort([Diagnostics.Process]$Process){
  if(-not$Process.HasExited){$Process.Kill($true)}
  if(-not$Process.WaitForExit(30000)){throw 'process wait'}
 }
 function Get-BlockerCode {
  $children=@(Get-ChildItem -LiteralPath $result -Force);if($children.Count-cne2-or@($children|Where-Object{$_.Name-cnotin@($script:Issue158OwnerName,'blocker.json')}).Count-ne0){throw 'blocker'}
  [void](Get-Issue158Owner $result $runId $CandidateSha result (Get-Issue158PhysicalPath $runtime) (Get-Issue158PhysicalPath $result))
  $path=Join-Path $result 'blocker.json';$item=Get-Item -LiteralPath $path -Force;if($item.PSIsContainer-or$item.LinkType-or($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0-or$item.Length-gt4096-or(Get-Issue158LexicalPath $path)-cne(Get-Issue158PhysicalPath $path)){throw 'blocker'}
  $bytes=[IO.File]::ReadAllBytes($path);[void][Text.UTF8Encoding]::new($false,$true).GetString($bytes);$document=$null
  try{$document=[Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($bytes));if($document.RootElement.ValueKind-ne[Text.Json.JsonValueKind]::Object){throw 'blocker'};$keys=@('schema_version','candidate_sha','terminal_status','last_checkpoint','driver_phase','poison_reason','post_freeze_failure','post_success_failure','execution_evidence_failure');$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);$value=@{};foreach($property in $document.RootElement.EnumerateObject()){if(-not$seen.Add($property.Name)-or$property.Name-cnotin$keys-or$property.Value.ValueKind-ne[Text.Json.JsonValueKind]::String){throw 'blocker'};$value[$property.Name]=$property.Value.GetString()};if($seen.Count-cne$keys.Count){throw 'blocker'}}finally{if($null-ne$document){$document.Dispose()}}
  if($value.schema_version-cne'issue-158-windows-blocker.v6'-or$value.candidate_sha-cne$CandidateSha-or$value.terminal_status-cnotin@('failed','canceled','timed_out','succeeded')){throw 'blocker'}
  $checkpoints=@('client_started','identity_certified','candidate_created','probe_certified','execution_inventory_certified','driver_completed','callbacks_frozen','import_completed','candidate_ready','candidate_published');if($value.last_checkpoint-cne'none'-and$value.last_checkpoint-cnotin$checkpoints){throw 'blocker'}
  if($value.driver_phase-cnotin@('none','command_pending','send_pending')){throw 'blocker'}
  if($value.poison_reason-cnotin@('none','work_token_pre_canceled','closed_relevant_event','session_start_contract','session_binding_contract','invocation_identity','invocation_description','invocation_content','invocation_native_reproof','invocation_preparation','invocation_buffer','terminal_contract','session_error','model_call_failure','abort','callback_exception')){throw 'blocker'}
  if($value.post_freeze_failure-cnotin@('none','candidate_not_admitted','prepared_body_rejected','candidate_lost_during_first_v2','first_v2_forward_unavailable','candidate_lost_during_later_v2','later_v2_forward_unavailable','start_envelope_rejected','terminal_envelope_rejected','start_validation_rejected','start_queue_refused','start_commit_busy','start_commit_failed','start_commit_timeout','start_commit_canceled','terminal_validation_rejected','terminal_queue_refused','terminal_commit_busy','terminal_commit_failed','terminal_commit_timeout','terminal_commit_canceled','unexpected_import_exception')){throw 'blocker'}
  $postSuccess=@('none','publication_barrier','execution_evidence','committed_identity_sequence','metadata_request','historical_request','current_file_request','route_matrix','metadata_document','historical_document','current_file_document','persistence_counts','aggregate_counts','observed_result','shutdown_drain','result_serialization','result_write','unexpected');if($value.post_success_failure-cnotin$postSuccess){throw 'blocker'}
  $evidenceFailures=@('none','observer_missing','observer_multiple','source_application_version','protocol_version','client_start_count','status_observation_count','probe_session_count','execution_session_count','retained_root_count','retained_skill_count','probe_inventory_count','execution_inventory_count','prepared_invocation_none','prepared_invocation_excess','same_client','exact_tool_union','retained_only_inventory','probe_native_reproof','execution_native_reproof','callback_native_reproof');if($value.execution_evidence_failure-cnotin$evidenceFailures){throw 'blocker'}
  if($value.terminal_status-ceq'succeeded'){$checkpointValid=if($value.post_success_failure-ceq'publication_barrier'){$value.last_checkpoint-cin@('candidate_ready','candidate_published')}else{$value.last_checkpoint-ceq'candidate_published'};$detailValid=if($value.post_success_failure-ceq'execution_evidence'){$value.execution_evidence_failure-cne'none'}else{$value.execution_evidence_failure-ceq'none'};if($value.post_success_failure-ceq'none'-or-not$checkpointValid-or-not$detailValid-or$value.driver_phase-cne'none'-or$value.poison_reason-cne'none'-or$value.post_freeze_failure-cne'none'){throw 'blocker'};$code='succeeded_post_success_'+$value.post_success_failure;if($value.post_success_failure-ceq'execution_evidence'){$code+='_'+$value.execution_evidence_failure};return $code}
  if($value.post_success_failure-cne'none'-or$value.execution_evidence_failure-cne'none'){throw 'blocker'}
  $code=if($value.last_checkpoint-ceq'none'){$value.terminal_status+'_before_checkpoint'}else{$value.terminal_status+'_after_'+$value.last_checkpoint}
  if($value.driver_phase-cne'none'){$code+='_at_'+$value.driver_phase};if($value.poison_reason-cne'none'){$code+='_due_'+$value.poison_reason};if($value.post_freeze_failure-cne'none'){$code+='_post_freeze_'+$value.post_freeze_failure};return $code
 }
 [void](New-Item -ItemType Directory -Path $runtime)
 $runtimeCreated=$true
 [void](New-Item -ItemType Directory -Path $result)
 $resultCreated=$true
 Write-Issue158Owner $runtime $runId $CandidateSha runtime (Get-Issue158PhysicalPath $runtime) (Get-Issue158PhysicalPath $result)
 $runtimeOwned=$true
 Write-Issue158Owner $result $runId $CandidateSha result (Get-Issue158PhysicalPath $runtime) (Get-Issue158PhysicalPath $result)
 $resultOwned=$true
 $preflightOutput=& (Join-Path $PSScriptRoot 'preflight.ps1') -CandidateSha $CandidateSha -RuntimeDirectory $runtime -ResultDirectory $result -OperatorAuthorized 2>$null
 if($LASTEXITCODE-ne0-or($preflightOutput-join'')-cne'preflight_result=PASSED'){throw 'preflight'}
 $testProject=Join-Path $repo 'tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj'
 $psi=[Diagnostics.ProcessStartInfo]::new('dotnet');$psi.UseShellExecute=$false;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
 foreach($argument in @('test',$testProject,'-p:CopilotCliVersion=1.0.75','--filter','FullyQualifiedName=CopilotAgentObservability.LocalMonitor.Tests.Issue158OwnedSessionLiveHarnessTests.WindowsOwnedSession_TraversesTheProductionHost&Issue158Lane=WindowsOwnedSession','--logger','console;verbosity=minimal')){$psi.ArgumentList.Add($argument)}
 $psi.Environment['CAO_ISSUE158_OPERATOR_AUTHORIZED']='issue-158-windows-owned-session-v1'
 $psi.Environment['CAO_ISSUE158_CANDIDATE_SHA']=$CandidateSha
 $psi.Environment['CAO_ISSUE158_RUN_ID']=$runId
 $psi.Environment['CAO_ISSUE158_RUNTIME_DIRECTORY']=$runtime
 $psi.Environment['CAO_ISSUE158_RESULT_DIRECTORY']=$result
 $psi.Environment['CAO_ISSUE158_RESULT_FILE']=$resultFile
 $psi.Environment['CAO_ISSUE158_RUNTIME_MARKER']=$runtimeMarker
 [void]$psi.Environment.Remove('COPILOT_CLI_PATH')
 $process=$null;$processCleanupAuthorized=$true;$childExitCode=$null
 $process=[Diagnostics.Process]::Start($psi);$processCleanupAuthorized=$false;$blockedCode='wrapper_test_output';$childOutput=Read-BoundedChildOutput $process
 if($process-is[Diagnostics.Process]){if(-not$process.HasExited){throw 'process exit'};$childExitCode=$process.ExitCode;$process.Dispose();$process=$null;$processCleanupAuthorized=$true}else{$childExitCode=$process.ExitCode;$process=$null;$processCleanupAuthorized=$true}
 if($childExitCode-ne0){$blockedCode='internal';try{$blockedCode=Get-BlockerCode}catch{try{$children=@(Get-ChildItem -LiteralPath $result -Force);if($children.Count-cne1-or$children[0].Name-cne$script:Issue158OwnerName){throw 'child'};[void](Get-Issue158Owner $result $runId $CandidateSha result (Get-Issue158PhysicalPath $runtime) (Get-Issue158PhysicalPath $result));$blockedCode='child_nonzero_unreported'}catch{$blockedCode='internal'}};throw 'test_process'}
  $blockedCode='wrapper_test_output_contract'
  if($null-eq$childOutput-or-not($childOutput-is[pscustomobject])-or@($childOutput.PSObject.Properties).Count-cne2-or(@($childOutput.PSObject.Properties.Name)-join',')-cne'StdoutOverflow,StderrNonempty'-or$childOutput.StdoutOverflow.GetType()-ne[bool]-or$childOutput.StderrNonempty.GetType()-ne[bool]){throw 'test_output'}
  $blockedCode='wrapper_test_output_stderr_nonempty';if($childOutput.StderrNonempty){throw 'test_process'}
  $blockedCode='wrapper_test_output_overflow';if($childOutput.StdoutOverflow){throw 'test_process'}
 $blockedCode='wrapper_runtime_cleanup'
 Remove-Owned $runtime runtime
 $blockedCode='wrapper_scan'
 $scanOutput=& (Join-Path $PSScriptRoot 'scan-leaks.ps1') -CandidateSha $CandidateSha -RuntimeDirectory $runtime -ResultDirectory $result -ResultFileName $resultFile -Lane windows_owned_session -ExpectedCleanupComplete:$false 2>$null
 if($LASTEXITCODE-ne0-or($scanOutput-join'')-cne'scan_result=PASSED'){throw 'scan'}
 $blockedCode='wrapper_result_validation';$preparedBytes=[IO.File]::ReadAllBytes((Join-Path $result $resultFile))
 $prepared=Get-Issue158ValidatedResult $preparedBytes $CandidateSha windows_owned_session $runtimeMarker $false
 $counts=$prepared.Json.counts;$invocations=$counts.user_invoked+$counts.agent_invoked;if($counts.user_invoked-cne1-or$counts.agent_invoked-lt0-or$counts.agent_invoked-gt63-or$invocations-lt1-or$invocations-gt64-or$counts.task_complete-cne1-or$counts.v1_imported-cne2-or$counts.v2_imported-cne$invocations-or$counts.snapshot_rows-cne$invocations){throw 'result_topology'}
 $retained=$prepared.Json
 $blockedCode='wrapper_result_cleanup';Remove-Owned $result result $true
 if((Test-Path -LiteralPath $runtime)-or(Test-Path -LiteralPath $result)){throw 'cleanup'}
 $retained.checks.cleanup_complete=$true
 $finalText=$retained|ConvertTo-Json -Compress -Depth 8
 $finalBytes=[Text.UTF8Encoding]::new($false).GetBytes($finalText)
 $blockedCode='wrapper_final_validation';[void](Get-Issue158ValidatedResult $finalBytes $CandidateSha windows_owned_session $runtimeMarker $true)
 $blockedCode='wrapper_identity_restore';Restore-IdentityEnvironment
 Write-Output $finalText
 exit 0
} catch {
 $cleanupComplete=$true
 $processFenceComplete=$true
 $processCleanupSafe=if($null-ne(Get-Variable processCleanupAuthorized -ErrorAction SilentlyContinue)){$processCleanupAuthorized}else{$true}
 if($null-ne(Get-Variable process -ErrorAction SilentlyContinue)-and$null-ne$process){
  if(-not$processCleanupSafe){try{Stop-OwnedChildProcessBestEffort $process}catch{$processFenceComplete=$false}}
  try{$process.Dispose();$process=$null}catch{$processFenceComplete=$false}
 }
 if(-not$processFenceComplete-or-not$processCleanupSafe){$cleanupComplete=$false}
 if($processFenceComplete-and$processCleanupSafe){
 try{if($null-ne(Get-Variable runtimeCreated -ErrorAction SilentlyContinue)-and$runtimeCreated-and(Test-Path -LiteralPath $runtime)){if($runtimeOwned){Remove-Owned $runtime runtime}else{Remove-Partial $runtime runtime}}}catch{$cleanupComplete=$false}
  try{if($null-ne(Get-Variable resultCreated -ErrorAction SilentlyContinue)-and$resultCreated-and(Test-Path -LiteralPath $result)){if($resultOwned){Remove-Owned $result result (-not(Test-Path -LiteralPath $runtime))}else{Remove-Partial $result result}}}catch{$cleanupComplete=$false}
 }
 try{if($null-ne(Get-Command Restore-IdentityEnvironment -ErrorAction SilentlyContinue)){Restore-IdentityEnvironment}}catch{$cleanupComplete=$false}
 if(($null-ne(Get-Variable runtime -ErrorAction SilentlyContinue)-and(Test-Path -LiteralPath $runtime))-or($null-ne(Get-Variable result -ErrorAction SilentlyContinue)-and(Test-Path -LiteralPath $result))){$cleanupComplete=$false}
 $code=if($cleanupComplete-and$null-ne(Get-Variable blockedCode -ErrorAction SilentlyContinue)){$blockedCode}else{'internal'};Write-Output "windows_result=BLOCKED check=$code"
 exit 1
}
