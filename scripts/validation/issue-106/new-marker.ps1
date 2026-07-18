Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$marker = 'SYNTH-{0}' -f ([Guid]::NewGuid().ToString('N'))
$digest = [System.Security.Cryptography.SHA256]::HashData(
    [System.Text.Encoding]::UTF8.GetBytes($marker))
$reference = [Convert]::ToHexString($digest).ToLowerInvariant().Substring(0, 12)

Write-Output ('marker={0}' -f $marker)
Write-Output ('marker_sha256_12=sha256:{0}' -f $reference)
