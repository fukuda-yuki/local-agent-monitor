# Task 10: T2d PowerShell wrapper (`scripts/local-monitor/setup.ps1`)

**Objective:** Add the repository-mode PowerShell wrapper exposing the four
setup actions and forwarding the Config CLI's stdout byte-for-byte and its
exit code unchanged, with executable T2 transport-parity tests. This task does
not prove production setup success: before Issue #67 T7 composes `Program.cs`,
the `CliApplication` default null dispatcher intentionally maps parsed
`setup status` to `internal_error`.

> **Dated evidence correction (2026-07-16):** This retained T2 card proves
> wrapper transport only. A child-process `LOCALAPPDATA` value does not isolate
> production setup storage because `SystemSetupPlatform` uses
> `Environment.GetFolderPath` and exposes no setup environment override.
> Therefore, current subprocess evidence is restricted to pre-dispatch invalid
> input byte/exit parity. Successful `status_ready` and storage behavior, and
> the real #66→#67 path, require executable in-process production composition
> with an injected trusted platform. This correction supersedes any later
> reading of this card as a successful-status isolation procedure; the T2
> historical null-dispatcher transport record remains intact.

**Depends on:** Task 09 committed and reviewed.

**Files (T2d ownership, second half):**
- Create: `scripts/local-monitor/setup.ps1`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupWrapperTests.cs`

**Non-scope:** Release ZIP packaging and layout (`package-release.ps1`,
`LocalMonitorScriptTests`) — T8 owns adding the wrapper to the release
layout. No change to other `scripts/local-monitor/*.ps1`. No new dependency.

**Interfaces:**
- Consumes: the Task 09 process surface (`config-cli setup <verb> ...`,
  one-JSON stdout, fixed stderr, 29-code exit mapping). Repository mode
  invokes the Config CLI project the same way the existing
  `scripts/local-monitor` scripts invoke repository tools — read the sibling
  scripts first and mirror their conventions (parameter style, project
  invocation, comment-based help).
- Produces: the wrapper command contract specified in
  `docs/specifications/interfaces/config-cli.md` and the spec's "Public
  commands" section: same four actions, stdout forwarded without reshaping.

**Constraints (spec):**
- The wrapper adds no output of its own on the JSON stdout stream — no
  banners, no `Write-Host`, no progress records, no trailing newline changes.
- Exit code is the CLI's exit code, unmodified.
- Arguments are forwarded positionally/verbatim after the wrapper's own
  action selection; the wrapper performs no validation the CLI already owns
  (no re-parsing of `--adapter`/`--change-set` values).
- No secret, raw prompt, or user data ever appears in wrapper output or
  tests (use synthetic change-set IDs).

## Steps

- [ ] **Step 1: Read the governing sources.**
  `docs/specifications/interfaces/config-cli.md` (wrapper contract),
  existing `scripts/local-monitor/*.ps1` (conventions), and the Task 09
  process surface tests.

- [ ] **Step 2: Write the failing parity tests** in `SetupWrapperTests.cs`.
  Follow the repository's existing script-test technique (see
  `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorScriptTests.cs`
  for how `pwsh` processes are launched and observed — but this test class
  lives in the ConfigCli test project and runs in repository mode):

  - `SetupWrapper_ForwardsStatusStdoutByteForByte` — run the CLI directly
    (`dotnet <ConfigCli dll or project> setup status`) and through
    `pwsh scripts/local-monitor/setup.ps1 status`; capture both stdout
    streams as raw bytes; assert byte equality and equal exit codes.
    Before T7 composition, both commands use `CliApplication`'s null
    dispatcher, so parsed status deterministically returns `internal_error`
    and exit 5. Keep any test state in its test-harness seam, but do not treat
    a child environment root as storage isolation or as proof of
    `status_ready`.
  - `SetupWrapper_ForwardsFailureStderrAndExitCode` — an invalid invocation
    (`setup apply` without `--change-set`) through both paths: assert stderr
    `invalid_arguments\n`, exit 2, and byte-identical stdout JSON.
  - `SetupWrapper_BareSetupProducesNoStdout` — wrapper invoked with no
    action: byte-empty stdout, stderr `invalid_arguments\n`, exit 2 (the
    wrapper forwards; the CLI decides).

  Mark the tests with the repository's existing convention for tests that
  require `pwsh` on PATH (mirror how LocalMonitor script tests gate on tool
  availability — do not invent a new skip mechanism, and do not silently
  skip on Windows CI where pwsh is pinned).

- [ ] **Step 3: Run RED** (script does not exist).

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupWrapperTests
```

- [ ] **Step 4: Implement `scripts/local-monitor/setup.ps1`.** Skeleton
  (align parameter and invocation style with the sibling scripts before
  committing):

```powershell
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Action,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$project = Join-Path $repoRoot 'src' 'CopilotAgentObservability.ConfigCli' 'CopilotAgentObservability.ConfigCli.csproj'

$cliArgs = @('run', '--project', $project, '--') + @('setup') +
    @(if ($null -ne $Action -and $Action -ne '') { $Action }) + @($Arguments)

& dotnet @cliArgs
exit $LASTEXITCODE
```

  Byte-fidelity notes the implementation must satisfy (verify each with the
  parity tests, not by assumption): `dotnet run` build noise must not reach
  stdout (use `--verbosity quiet`/`-tl:off` or pre-build once in the test and
  invoke the built DLL — pick the approach the parity test proves clean);
  PowerShell must not re-encode or re-line-end the child stdout (invoking
  the native command directly, not via a pipeline into `Write-Output`,
  preserves the stream).

  Do not wire `Program.cs`, inject a production dispatcher, or weaken the
  expected `internal_error` result to make this test look like success. The
  wrapper is a transport surface; changing composition here would bypass the
  real #66/#67 producer/consumer gate. Issue #67 T7 owns the immediately
  deferred direct-CLI-versus-wrapper `setup status` `status_ready`
  success-parity test after production composition is wired.

- [ ] **Step 5: Run GREEN + full ConfigCli.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupWrapperTests
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
git diff --check
```

- [ ] **Step 6: Commit.**

```powershell
git add scripts/local-monitor/setup.ps1 tests/CopilotAgentObservability.ConfigCli.Tests/SetupWrapperTests.cs
git commit -m "Issue #66: feat(setup): add byte-faithful setup wrapper"
```

  Body: why — the spec requires the PowerShell wrapper to expose the four
  actions and forward stdout without reshaping so downstream consumers can
  parse setup.v1 identically from either surface.

## Validation

Step 5 commands.

## Completion criteria

- Byte-for-byte stdout parity and exit-code parity proven for the parsed
  null-dispatcher status result (`internal_error`, exit 5), an invalid apply
  result (`invalid_arguments`, exit 2), and the bare-invocation case. This is
  the T2 transport gate, not production `status_ready` success proof.
- Wrapper adds zero own output on stdout; no re-validation of CLI-owned
  arguments.
- Wrapper is NOT added to the release layout (T8 residual, stated in the
  ledger row); the production-success interface remains unverified and is
  explicitly deferred to Issue #67 T7; independent review PASS.

**Report destination:** chat + ledger row per README policy.
