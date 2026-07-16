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
    if (-not (Test-Path -LiteralPath $releaseCli -PathType Leaf)) {
        [Console]::Error.Write("internal_error`n")
        exit 5
    }

    try {
        $PSNativeCommandUseErrorActionPreference = $false
        & $releaseCli @setupArgs
        $cliExitCode = $LASTEXITCODE
    }
    catch {
        [Console]::Error.Write("internal_error`n")
        exit 5
    }

    exit $cliExitCode
}
else {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
    $project = Join-Path $repoRoot 'src' 'CopilotAgentObservability.ConfigCli' 'CopilotAgentObservability.ConfigCli.csproj'
    $cliArgs = @('run', '--verbosity', 'quiet', '--project', $project, '--') + $setupArgs
    & dotnet @cliArgs
}
exit $LASTEXITCODE
