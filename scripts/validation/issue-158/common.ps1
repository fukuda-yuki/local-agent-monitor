Set-StrictMode -Version Latest
$script:Issue158OwnerName='.cao-issue-158-owner.json'
function Get-Issue158PhysicalPath([string]$Path,[bool]$AllowAbsentLeaf=$false){
 $full=[IO.Path]::GetFullPath($Path);$root=[IO.Path]::GetPathRoot($full);$parts=@($full.Substring($root.Length)-split'[\\/]'|Where-Object{$_.Length-ne0});$current=$root
 for($i=0;$i-lt$parts.Length;$i++){$next=[IO.Path]::Combine($current,$parts[$i]);if(Test-Path -LiteralPath $next){$item=Get-Item -LiteralPath $next -Force;if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne 0){$target=$item.ResolveLinkTarget($true);if($null-eq$target){throw 'reparse'};$current=[IO.Path]::GetFullPath($target.FullName)}else{$current=[IO.Path]::GetFullPath($item.FullName)}}elseif($AllowAbsentLeaf-and$i-eq$parts.Length-1-and(Test-Path -LiteralPath $current -PathType Container)){$current=[IO.Path]::Combine($current,$parts[$i])}else{throw 'missing'}}
 $current.TrimEnd('\','/')
}
function Get-Issue158LexicalPath([string]$Path){[IO.Path]::GetFullPath($Path).TrimEnd('\','/')}
function Test-Issue158Within([string]$Child,[string]$Parent){$Child.StartsWith($Parent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)}
function Get-Issue158Hash([string]$Text){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()}
function Get-Issue158Owner([string]$Directory,[string]$RunId,[string]$Candidate,[string]$Kind,[string]$RuntimePhysical,[string]$ResultPhysical){
 $path=Join-Path $Directory $script:Issue158OwnerName;if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'owner'};$item=Get-Item -LiteralPath $path -Force;if($item.PSIsContainer-or$item.LinkType-or($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0-or(Get-Issue158LexicalPath $path)-cne(Get-Issue158PhysicalPath $path)){throw 'owner'};$bytes=[IO.File]::ReadAllBytes($path);$text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes);$owner=$text|ConvertFrom-Json -NoEnumerate;$keys=@('schema_version','run_id','candidate_sha','kind','runtime_path_sha256','result_path_sha256');$actual=@($owner.PSObject.Properties.Name);if($actual.Count-ne$keys.Count-or@($actual|Where-Object{$_-cnotin$keys}).Count-ne 0){throw 'owner'}
 if($owner.schema_version-cne'issue-158-validation-owner.v1'-or$owner.run_id-cne$RunId-or$owner.candidate_sha-cne$Candidate-or$owner.kind-cne$Kind-or$owner.runtime_path_sha256-cne(Get-Issue158Hash $RuntimePhysical)-or$owner.result_path_sha256-cne(Get-Issue158Hash $ResultPhysical)){throw 'owner'};$owner
}
function Write-Issue158Owner([string]$Directory,[string]$RunId,[string]$Candidate,[string]$Kind,[string]$RuntimePhysical,[string]$ResultPhysical){$o=[ordered]@{schema_version='issue-158-validation-owner.v1';run_id=$RunId;candidate_sha=$Candidate;kind=$Kind;runtime_path_sha256=Get-Issue158Hash $RuntimePhysical;result_path_sha256=Get-Issue158Hash $ResultPhysical};[IO.File]::WriteAllText((Join-Path $Directory $script:Issue158OwnerName),($o|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))}
function Get-Issue158DecodedTexts([string]$Text){
 $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);$queue=[Collections.Generic.Queue[object]]::new();$queue.Enqueue(@($Text,0));$out=[Collections.Generic.List[string]]::new()
 while($queue.Count){$entry=$queue.Dequeue();$value=[string]$entry[0];$depth=[int]$entry[1];if($value.Length-gt32768-or-not$seen.Add($value)){continue};$out.Add($value);if($depth-ge2){continue};try{$u=[Uri]::UnescapeDataString($value);if($u-cne$value){$queue.Enqueue(@($u,$depth+1))}}catch{}
  foreach($token in [regex]::Matches($value,'(?<![A-Za-z0-9+/=])[A-Za-z0-9+/]{8,}={0,2}(?![A-Za-z0-9+/=])')){try{$b=[Convert]::FromBase64String($token.Value);foreach($e in @([Text.UTF8Encoding]::new($false,$true),[Text.UnicodeEncoding]::new($false,$false,$true),[Text.UnicodeEncoding]::new($true,$false,$true),[Text.UTF32Encoding]::new($false,$false,$true),[Text.UTF32Encoding]::new($true,$false,$true))){try{$queue.Enqueue(@($e.GetString($b),$depth+1))}catch{}}}catch{}}
  foreach($token in [regex]::Matches($value,'(?i)(?<![0-9a-f])[0-9a-f]{16,}(?![0-9a-f])')){try{$b=[Convert]::FromHexString($token.Value);foreach($e in @([Text.UTF8Encoding]::new($false,$true),[Text.UnicodeEncoding]::new($false,$false,$true),[Text.UnicodeEncoding]::new($true,$false,$true),[Text.UTF32Encoding]::new($false,$false,$true),[Text.UTF32Encoding]::new($true,$false,$true))){try{$queue.Enqueue(@($e.GetString($b),$depth+1))}catch{}}}catch{}}
 };$out
}
function Get-Issue158ValidatedResult([byte[]]$Bytes,[string]$Candidate,[string]$Lane,[string]$RuntimeMarker,[bool]$CleanupComplete){
 function Exact($o,[string[]]$keys){if($null-eq$o-or$o-isnot[pscustomobject]){return $false};$a=@($o.PSObject.Properties.Name);$a.Count-eq$keys.Count-and@($a|Where-Object{$_ -cnotin $keys}).Count-eq0}
 function Integral($v){($v-is[long]-or$v-is[int]-or($v-is[decimal]-and$v-eq[decimal]::Truncate($v)))-and$v-ge0}
 if($Bytes.Length-eq0-or$Bytes.Length-gt32768){throw 'result_size'}
 $rawHex=[Convert]::ToHexString($Bytes);foreach($encoding in @([Text.Encoding]::UTF8,[Text.Encoding]::Unicode,[Text.Encoding]::BigEndianUnicode,[Text.UTF32Encoding]::new($false,$false),[Text.UTF32Encoding]::new($true,$false))){if($rawHex.Contains([Convert]::ToHexString($encoding.GetBytes($RuntimeMarker)),[StringComparison]::OrdinalIgnoreCase)){throw 'runtime_marker_leak'}}
 try{$text=[Text.UTF8Encoding]::new($false,$true).GetString($Bytes)}catch{throw 'result_json'}
 $leak='(?i)(?:[A-Z]:\\(?:Users|Windows\\Temp|[^\\]+\\AppData\\Local\\Temp)\\|/(?:home|Users|mnt/c|tmp)/|\b(?:sk-|gh[pousr]_|github_pat_|AKIA|xox[baprs]-)[A-Za-z0-9_-]{8,}|authorization\s*[:=]|cookie\s*[:=]|-----BEGIN [A-Z ]*PRIVATE KEY-----|://[^/\s:@]+:[^/\s@]+@|"(?:prompt|response|content|message|delta|tool_arguments?|tool_results?|session_id|trace_id|snapshot_id|receipt_id|raw_envelope|sql|database|table_dump)"\s*:|\b(?:sqlite|create\s+table|insert\s+into|select\s+.+\s+from)\b)'
 foreach($decoded in(Get-Issue158DecodedTexts $text)){if($decoded.IndexOf($RuntimeMarker,[StringComparison]::OrdinalIgnoreCase)-ge0){throw 'runtime_marker_leak'};if([regex]::IsMatch($decoded,$leak,[Text.RegularExpressions.RegexOptions]::CultureInvariant)){throw 'prohibited_content'}}
 try{$json=$text|ConvertFrom-Json -NoEnumerate}catch{throw 'result_json'}
 $wt=@('schema_version','candidate_sha','lane','outcome','source_application_version','protocol_version','counts','checks','exit_code');$lt=@('schema_version','candidate_sha','lane','outcome','filesystem','counts','checks','exit_code')
 $wc=@('retained_roots','retained_skills','probe_sessions','execution_sessions','user_invoked','agent_invoked','task_complete','v2_imported','v1_imported','snapshot_rows');$lc=@('retained_roots','retained_skills','matrix_cases')
 $wk=@('operator_gate','cli_override_absent','retained_only_inventory','exact_tool_union','native_reproof','current_generation','metadata_route','historical_route','current_file_route','shutdown_drain','cleanup_complete');$lk=@('operator_gate','detached_clean_candidate','kernel_supported','native_ext4','retained_root_reproof','strict_utf8_read','unsafe_path_rejected','missing_rejected','oversized_rejected','binary_rejected','metadata_route','historical_route','current_file_route','cleanup_complete')
 if($Lane-ceq'windows_owned_session'){$top=$wt;$counts=$wc;$checks=$wk}elseif($Lane-ceq'linux_ext4_current_file'){$top=$lt;$counts=$lc;$checks=$lk}else{throw 'lane'}
 if(-not(Exact $json $top)-or-not(Exact $json.counts $counts)-or-not(Exact $json.checks $checks)){throw 'result_schema'}
 if($json.schema_version-cne'issue-158-live-validation.v1'-or$json.candidate_sha-cne$Candidate-or$json.lane-cne$Lane-or$json.outcome-cne'passed'-or($json.exit_code-isnot[long]-and$json.exit_code-isnot[int])-or$json.exit_code-ne0){throw 'result_literal'}
 if($Lane-ceq'windows_owned_session'-and($json.source_application_version-cne'1.0.75'-or$json.protocol_version-ne3-or($json.protocol_version-isnot[long]-and$json.protocol_version-isnot[int]))){throw 'result_literal'}
 if($Lane-ceq'linux_ext4_current_file'-and$json.filesystem-cne'ext4'){throw 'result_literal'}
 foreach($key in $counts){if(-not(Integral $json.counts.$key)){throw 'result_count'}}
 foreach($key in $checks){if($json.checks.$key-isnot[bool]){throw 'result_check'};if($key-ceq'cleanup_complete'){if($json.checks.$key-ne$CleanupComplete){throw 'result_check'}}elseif($json.checks.$key-ne$true){throw 'result_check'}}
 [pscustomobject]@{Json=$json;Text=$text}
}
