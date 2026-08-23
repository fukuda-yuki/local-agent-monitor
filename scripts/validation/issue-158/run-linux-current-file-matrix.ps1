[CmdletBinding()]
param([string]$CandidateSha,[switch]$OperatorAuthorized)
try {
 Set-StrictMode -Version Latest
 $ErrorActionPreference='Stop'
 function Block([string]$Code){Write-Output "linux_result=BLOCKED check=$Code";exit 1}
 if(-not$OperatorAuthorized){Block 'operator_authorization'}
 if([string]::IsNullOrWhiteSpace($CandidateSha)-or$CandidateSha-cnotmatch'^[0-9a-f]{40}$'){Block 'candidate_sha'}
 $wsl=Get-Command wsl.exe -ErrorAction SilentlyContinue;if($null-eq$wsl){Block 'wsl_unavailable'}
 . (Join-Path $PSScriptRoot 'common.ps1')
 $repo=Get-Issue158PhysicalPath (Join-Path $PSScriptRoot '..\..\..')
 $head=(& git -C $repo rev-parse HEAD 2>$null|Out-String).Trim();if($LASTEXITCODE-ne0-or$head-cne$CandidateSha){Block 'candidate_mismatch'}
 $branch=(& git -C $repo symbolic-ref -q HEAD 2>$null|Out-String).Trim();if($LASTEXITCODE-eq0-or$branch.Length-ne0){Block 'candidate_attached'}
 $dirty=(& git -C $repo status --porcelain=v1 --untracked-files=normal 2>$null|Out-String).Trim();if($LASTEXITCODE-ne0-or$dirty.Length-ne0){Block 'candidate_dirty'}
 $drive=[IO.Path]::GetPathRoot($repo).TrimEnd('\').TrimEnd(':').ToLowerInvariant();if($drive-cnotmatch'^[a-z]$'){Block 'source_path'}
 $relative=$repo.Substring([IO.Path]::GetPathRoot($repo).Length).Replace('\','/')
 $source="/mnt/$drive/$relative";$helperRelative=(Join-Path $PSScriptRoot 'run-linux-current-file-matrix.sh').Substring([IO.Path]::GetPathRoot($repo).Length).Replace('\','/')
 $helper="/mnt/$drive/$helperRelative";$runId=[Guid]::NewGuid().ToString('N');$marker='issue158-'+[Guid]::NewGuid().ToString('N')
 $psi=[Diagnostics.ProcessStartInfo]::new($wsl.Source);$psi.UseShellExecute=$false;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
 foreach($argument in @('--distribution','Ubuntu','--exec','bash',$helper,$CandidateSha,$source,$runId,$marker)){$psi.ArgumentList.Add($argument)}
 $process=[Diagnostics.Process]::Start($psi);$stdout=$process.StandardOutput.ReadToEnd();$stderr=$process.StandardError.ReadToEnd();$process.WaitForExit()
 if($process.ExitCode-ne0-or$stderr.Length-ne0-or$stdout.Length-gt65536){throw 'wsl_process'}
 $lines=@($stdout.Trim()-split"`n");if($lines.Count-ne1-or$lines[0]-cnotmatch'^issue158_linux_prepared=([A-Za-z0-9+/]+={0,2})$'){throw 'wsl_result'}
 $bytes=[Convert]::FromBase64String($Matches[1]);$pending=Get-Issue158ValidatedResult $bytes $CandidateSha linux_ext4_current_file $marker $false
 $retained=$pending.Json;$retained.checks.cleanup_complete=$true;$finalText=$retained|ConvertTo-Json -Compress -Depth 8;$finalBytes=[Text.UTF8Encoding]::new($false).GetBytes($finalText)
 [void](Get-Issue158ValidatedResult $finalBytes $CandidateSha linux_ext4_current_file $marker $true)
 Write-Output $finalText
 exit 0
} catch {
 Write-Output 'linux_result=BLOCKED check=internal'
 exit 1
}
