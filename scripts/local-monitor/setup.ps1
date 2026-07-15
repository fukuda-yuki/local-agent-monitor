#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $Action,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments
)

$ErrorActionPreference = 'Stop'
$setupArgs = @('setup')

if (-not [string]::IsNullOrEmpty($Action)) {
    $setupArgs += $Action
}

if ($null -ne $Arguments) {
    $setupArgs += $Arguments
}

$releaseCliDirectory = Join-Path $PSScriptRoot '..' 'app' 'config-cli'
if (Test-Path -LiteralPath $releaseCliDirectory -PathType Container) {
    $releaseCli = Join-Path $releaseCliDirectory 'CopilotAgentObservability.ConfigCli.exe'
    & $releaseCli @setupArgs
}
else {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $project = Join-Path $repoRoot 'src' 'CopilotAgentObservability.ConfigCli' 'CopilotAgentObservability.ConfigCli.csproj'
    $cliArgs = @('run', '--verbosity', 'quiet', '--project', $project, '--') + $setupArgs
    & dotnet @cliArgs
}
exit $LASTEXITCODE
