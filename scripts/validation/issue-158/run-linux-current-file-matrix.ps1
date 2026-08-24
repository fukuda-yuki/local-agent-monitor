[CmdletBinding()]
param([string]$CandidateSha,[switch]$OperatorAuthorized)
try {
 Set-StrictMode -Version Latest
 $ErrorActionPreference='Stop'
 function Block([string]$Code){Write-Output "linux_result=BLOCKED check=$Code";exit 1}
 function Read-LinuxChildOutput([Diagnostics.Process]$Process,[int]$TimeoutMilliseconds=780000){
  $limit=1048576;$retained=[byte[]]::new($limit);$length=0;$stdoutOverflow=$false;$stdoutNonAscii=$false;$stderrNonempty=$false;$stdoutBuffer=[byte[]]::new(8192);$stderrBuffer=[byte[]]::new(8192);$cts=[Threading.CancellationTokenSource]::new($TimeoutMilliseconds)
  try{
   $stdoutMemory=[Memory[byte]]::new($stdoutBuffer);$stderrMemory=[Memory[byte]]::new($stderrBuffer);$stdoutTask=$Process.StandardOutput.BaseStream.ReadAsync($stdoutMemory,$cts.Token).AsTask();$stderrTask=$Process.StandardError.BaseStream.ReadAsync($stderrMemory,$cts.Token).AsTask();$exitTask=$Process.WaitForExitAsync($cts.Token)
   while($null-ne$stdoutTask-or$null-ne$stderrTask-or-not$exitTask.IsCompleted){
    $pending=[Collections.Generic.List[Threading.Tasks.Task]]::new();if($null-ne$stdoutTask){[void]$pending.Add($stdoutTask)};if($null-ne$stderrTask){[void]$pending.Add($stderrTask)};if(-not$exitTask.IsCompleted){[void]$pending.Add($exitTask)};[void][Threading.Tasks.Task]::WhenAny($pending).GetAwaiter().GetResult()
    if($null-ne$stdoutTask-and$stdoutTask.IsCompleted){$read=$stdoutTask.GetAwaiter().GetResult();if($read-eq0){$stdoutTask=$null}else{for($i=0;$i-lt$read;$i++){if($stdoutBuffer[$i]-gt127){$stdoutNonAscii=$true}};$remaining=$limit-$length;if($remaining-gt0){$copy=[Math]::Min($read,$remaining);[Array]::Copy($stdoutBuffer,0,$retained,$length,$copy);$length+=$copy};if($read-gt$remaining){$stdoutOverflow=$true};$stdoutTask=$Process.StandardOutput.BaseStream.ReadAsync($stdoutMemory,$cts.Token).AsTask()}}
    if($null-ne$stderrTask-and$stderrTask.IsCompleted){$read=$stderrTask.GetAwaiter().GetResult();if($read-eq0){$stderrTask=$null}else{$stderrNonempty=$true;$stderrTask=$Process.StandardError.BaseStream.ReadAsync($stderrMemory,$cts.Token).AsTask()}}
   }
   [void]$exitTask.GetAwaiter().GetResult();if(-not$Process.HasExited){throw 'process exit'}
   [pscustomobject]@{Buffer=$retained;Length=$length;StdoutOverflow=$stdoutOverflow;StdoutNonAscii=$stdoutNonAscii;StderrNonempty=$stderrNonempty}
  }finally{$cts.Dispose()}
 }
 function Stop-LinuxChildProcess([Diagnostics.Process]$Process){if(-not$Process.HasExited){$Process.Kill($true)};if(-not$Process.WaitForExit(30000)-or-not$Process.HasExited){throw 'process stop'}}
 function Wait-LinuxSupervisor([Diagnostics.Process]$Process,[int]$TimeoutMilliseconds=780000){if($Process.HasExited){if(-not$Process.WaitForExit(0)){throw 'process wait'};return};if(-not$Process.WaitForExit($TimeoutMilliseconds)){Stop-LinuxChildProcess $Process;throw 'process fence'};if(-not$Process.HasExited){throw 'process exit'}}
 function Get-RemainingLifecycleMilliseconds([int]$TotalMilliseconds,[double]$ElapsedMilliseconds){if($TotalMilliseconds-lt0-or[double]::IsNaN($ElapsedMilliseconds)){throw 'budget'};$remaining=[double]$TotalMilliseconds-$ElapsedMilliseconds;if($remaining-le0){return 0};if($remaining-ge$TotalMilliseconds){return $TotalMilliseconds};[int][Math]::Ceiling($remaining)}
 function Get-LinuxChildText($Output,[int]$Maximum){if($Output.StdoutOverflow-or$Output.StdoutNonAscii-or$Output.Length-gt$Maximum){throw 'output'};[Text.Encoding]::ASCII.GetString($Output.Buffer,0,$Output.Length)}
 function Get-LinuxChildDisposition([int]$ExitCode,$Output){
  if($ExitCode-ne1-or$Output.StderrNonempty){return 'internal'};try{$stdout=Get-LinuxChildText $Output 128}catch{return 'internal'};$codes=@('argument','source','top','prerequisite_dotnet','prerequisite_tool','clone','checkout','filesystem','test','result','cleanup');$lines=@($stdout-split"`n")
  if($lines.Count-cne2-or$lines[1].Length-ne0-or$lines[0]-cnotmatch'^issue158_linux_blocked=([a-z_]+)$'-or$Matches[1]-cnotin$codes){return 'internal'};return $Matches[1]
 }
 function Get-ValidatedPhysicalPath([string]$Path,[bool]$Directory){$lexical=Get-Issue158LexicalPath $Path;$physical=Get-Issue158PhysicalPath $Path;if($lexical-cne$physical){throw 'alias'};$item=Get-Item -LiteralPath $physical -Force;if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0-or$item.FullName-cne$physical-or($Directory-and-not$item.PSIsContainer)-or(-not$Directory-and$item.PSIsContainer)){throw 'type'};$physical}
 function Get-LinuxGitSource([string]$Repository,[string]$Candidate){$commonText=(& git -C $Repository rev-parse --path-format=absolute --git-common-dir 2>$null|Out-String).Trim();if($LASTEXITCODE-ne0-or[string]::IsNullOrWhiteSpace($commonText)-or$commonText.Contains("`n")-or$commonText.Contains("`r")){throw 'common'};$common=Get-ValidatedPhysicalPath $commonText $true;& git --git-dir=$common cat-file -e "$Candidate^{commit}" 2>$null;if($LASTEXITCODE-ne0){throw 'candidate'};$common}
 function ConvertTo-WslMount([string]$Path){$root=[IO.Path]::GetPathRoot($Path);$drive=$root.TrimEnd('\').TrimEnd(':').ToLowerInvariant();if($drive-cnotmatch'^[a-z]$'){throw 'drive'};'/mnt/'+$drive+'/'+$Path.Substring($root.Length).Replace('\','/')}
 if(-not$OperatorAuthorized){Block 'operator_authorization'}
 if([string]::IsNullOrWhiteSpace($CandidateSha)-or$CandidateSha-cnotmatch'^[0-9a-f]{40}$'){Block 'candidate_sha'}
 $wsl=Get-Command wsl.exe -ErrorAction SilentlyContinue;if($null-eq$wsl){Block 'wsl_unavailable'}
 . (Join-Path $PSScriptRoot 'common.ps1');$repo=Get-Issue158PhysicalPath (Join-Path $PSScriptRoot '..\..\..')
 $head=(& git -C $repo rev-parse HEAD 2>$null|Out-String).Trim();if($LASTEXITCODE-ne0-or$head-cne$CandidateSha){Block 'candidate_mismatch'}
 $branch=(& git -C $repo symbolic-ref -q HEAD 2>$null|Out-String).Trim();if($LASTEXITCODE-eq0-or$branch.Length-ne0){Block 'candidate_attached'}
 $dirty=(& git -C $repo status --porcelain=v1 --untracked-files=normal 2>$null|Out-String).Trim();if($LASTEXITCODE-ne0-or$dirty.Length-ne0){Block 'candidate_dirty'}
 $common=Get-LinuxGitSource $repo $CandidateSha;$helperPath=Get-ValidatedPhysicalPath (Join-Path $PSScriptRoot 'run-linux-current-file-matrix.sh') $false
 $source=ConvertTo-WslMount $common;$helper=ConvertTo-WslMount $helperPath;$runId=[Guid]::NewGuid().ToString('N');$marker='issue158-'+[Guid]::NewGuid().ToString('N');$linuxSdkRoot='/tmp/cao-issue158-dotnet-sdk-10.0.203'
 $psi=[Diagnostics.ProcessStartInfo]::new($wsl.Source);$psi.UseShellExecute=$false;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true;foreach($argument in @('--distribution','Ubuntu','--exec','timeout','--signal=TERM','--kill-after=30s','12m','bash',$helper,$CandidateSha,$source,$runId,$marker,$linuxSdkRoot)){$psi.ArgumentList.Add($argument)}
 $process=$null;$normalLifecycle=$false;$lifecycleClock=$null;$lifecycleBudgetMilliseconds=780000
 try{$process=[Diagnostics.Process]::Start($psi);$lifecycleClock=[Diagnostics.Stopwatch]::StartNew();$readBudget=Get-RemainingLifecycleMilliseconds $lifecycleBudgetMilliseconds $lifecycleClock.Elapsed.TotalMilliseconds;$child=Read-LinuxChildOutput $process $readBudget;$exitCode=$process.ExitCode;if(-not$process.HasExited){throw 'process exit'};$normalLifecycle=$true}finally{if($null-ne$process){try{if(-not$normalLifecycle){$waitBudget=if($null-eq$lifecycleClock){0}else{Get-RemainingLifecycleMilliseconds $lifecycleBudgetMilliseconds $lifecycleClock.Elapsed.TotalMilliseconds};Wait-LinuxSupervisor $process $waitBudget}elseif(-not$process.HasExited){throw 'process lifecycle'}}finally{if($null-ne$lifecycleClock){$lifecycleClock.Stop()};$process.Dispose();$process=$null}}}
 if($exitCode-ne0){Block (Get-LinuxChildDisposition $exitCode $child)}
 if($child.StderrNonempty){throw 'wsl_process'};$stdout=Get-LinuxChildText $child 65536;$lines=@($stdout-split"`n");if($lines.Count-cne2-or$lines[1].Length-ne0-or$lines[0]-cnotmatch'^issue158_linux_prepared=([A-Za-z0-9+/]+={0,2})$'){throw 'wsl_result'}
 $bytes=[Convert]::FromBase64String($Matches[1]);$pending=Get-Issue158ValidatedResult $bytes $CandidateSha linux_ext4_current_file $marker $false;$retained=$pending.Json;$retained.checks.cleanup_complete=$true;$finalText=$retained|ConvertTo-Json -Compress -Depth 8;$finalBytes=[Text.UTF8Encoding]::new($false).GetBytes($finalText)
 [void](Get-Issue158ValidatedResult $finalBytes $CandidateSha linux_ext4_current_file $marker $true);Write-Output $finalText;exit 0
} catch {Write-Output 'linux_result=BLOCKED check=internal';exit 1}
