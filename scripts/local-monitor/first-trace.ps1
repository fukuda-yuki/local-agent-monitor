#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $Action,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\common.ps1"

$hasDatabaseArgument = @($Arguments | Where-Object {
    $_ -eq '--database' -or $_.StartsWith('--database=', [StringComparison]::Ordinal)
}).Count -gt 0
if ($Action -notin @('begin', 'status', 'complete', 'cancel') -or $hasDatabaseArgument) {
    [Console]::Error.Write("invalid_arguments`n")
    exit 2
}

if (-not (Test-Path -LiteralPath $script:DefaultDbPath -PathType Leaf)) {
    [Console]::Error.Write("runtime_database_not_found`n")
    exit 5
}

$firstTraceArgs = @('first-trace')
if (-not [string]::IsNullOrEmpty($Action)) {
    $firstTraceArgs += $Action
}
$firstTraceArgs += @('--database', $script:DefaultDbPath)
if ($null -ne $Arguments) {
    $firstTraceArgs += $Arguments
}

$releaseCli = Join-Path $PSScriptRoot '..' 'app' 'config-cli' 'CopilotAgentObservability.ConfigCli.exe'
if (-not (Test-Path -LiteralPath $releaseCli -PathType Leaf)) {
    [Console]::Error.Write("internal_error`n")
    exit 5
}

try {
    $PSNativeCommandUseErrorActionPreference = $false
    & $releaseCli @firstTraceArgs
    $cliExitCode = $LASTEXITCODE
}
catch {
    [Console]::Error.Write("internal_error`n")
    exit 5
}

exit $cliExitCode
