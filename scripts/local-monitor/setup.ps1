#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $Action,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$project = Join-Path $repoRoot 'src' 'CopilotAgentObservability.ConfigCli' 'CopilotAgentObservability.ConfigCli.csproj'
$cliArgs = @('run', '--verbosity', 'quiet', '--project', $project, '--', 'setup')

if (-not [string]::IsNullOrEmpty($Action)) {
    $cliArgs += $Action
}

if ($null -ne $Arguments) {
    $cliArgs += $Arguments
}

& dotnet @cliArgs
exit $LASTEXITCODE
