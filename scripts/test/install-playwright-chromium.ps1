[CmdletBinding()]
param(
    [switch]$WithDeps
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

if ([string]::IsNullOrWhiteSpace($env:PLAYWRIGHT_BROWSERS_PATH)) {
    $env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $repoRoot 'artifacts\playwright-browsers'
}

New-Item -ItemType Directory -Force -Path $env:PLAYWRIGHT_BROWSERS_PATH | Out-Null

$playwrightScript = Join-Path $repoRoot 'tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1'
if (-not (Test-Path -LiteralPath $playwrightScript)) {
    throw "Playwright bootstrap script was not found at '$playwrightScript'. Run 'dotnet build CopilotAgentObservability.slnx' first."
}

$installArgs = @('install')
if ($WithDeps) {
    $installArgs += '--with-deps'
}

$installArgs += 'chromium'

& $playwrightScript @installArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
