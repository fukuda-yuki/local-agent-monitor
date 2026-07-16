# Task 01 (T3a): Platform observations and common GitHub Copilot detection

**Objective:** Freeze the deterministic observation boundaries every later
#67 task consumes: OS/runtime identity, bounded process execution, bounded
registry/preferences/policy-file reads, bounded HTTP probe transport, and the
common GitHub Copilot detection facts (VS Code Stable/Insiders and Copilot
CLI versions, official managed-source locations, planning-OS capture).
Expose bounded observations only — no policy precedence resolution, no
endpoint classification, no plan construction.

**Depends on:** Issue #66 T2 gate PASS.

**Files (T3a ownership; exact per the sprint plan matrix):**
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Platform/ISetupPlatform.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Platform/SystemSetupPlatform.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupTestPlatform.cs`
- Create: `src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/GitHubCopilotDetection.cs`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/GitHubCopilotDetectionTests.cs`

**Non-scope:** policy precedence (task-02), endpoint classification
(task-03), aggregate/partition types (task-04), any target subdirectory, any
catalog/validator/dispatcher/coordinator edit.

**Interfaces:**
- Consumes: existing `ISetupPlatform` members (`PathStyle`,
  `LocalApplicationData`, `FileSystem`, `UserEnvironment`, `Clock`,
  `Identifiers`, `Execution`).
- Produces (frozen seam; tasks 02–07 consume these exact names — changing
  them later reopens T3a):

```csharp
// ISetupPlatform additions (each as a property-scoped observation boundary)
ISetupOperatingSystem OperatingSystem { get; }   // planning-OS identity
ISetupProcessRunner ProcessRunner { get; }        // bounded process execution
ISetupManagedSettingsSource ManagedSettings { get; } // registry/preferences/policy-file reads
ISetupHttpProbe HttpProbe { get; }                // bounded loopback GET transport

public interface ISetupOperatingSystem
{
    SetupPlanningOs Current { get; }              // Windows | MacOs | Linux
}

public interface ISetupProcessRunner
{
    // Bounded: fixed executable name (no shell), fixed argument list,
    // output capped, deterministic timeout classification; never throws
    // raw process text into the caller.
    SetupProcessObservation Run(string fileName, IReadOnlyList<string> arguments);
}

public sealed record SetupProcessObservation(
    SetupProcessOutcome Outcome,   // Completed | NotFound | Failed | TimedOut
    int? ExitCode,
    string StandardOutput);        // capped; empty when not Completed

public interface ISetupManagedSettingsSource
{
    // Bounded read of one official managed location; returns raw bytes or
    // absence, never interprets content. Location identity is a closed enum
    // so no caller can read an arbitrary path through this boundary.
    SetupManagedObservation Read(SetupManagedLocation location);
}

public interface ISetupHttpProbe
{
    // Transport only: GET origin + fixed path, redirects disabled, one total
    // budget, bounded body read (maxBytes + 1 sentinel). Classification of
    // the observation is task-03's job.
    SetupHttpProbeObservation Get(string origin, string path, int totalBudgetMilliseconds, int maxBodyBytes);
}
```

  Exact record shapes for `SetupManagedLocation`, `SetupManagedObservation`,
  and `SetupHttpProbeObservation` (outcome enum: `Response`, `Refused`,
  `TimedOut`, `TransportFailure`, `RedirectBlocked`; response carries status
  code, trustworthy content length, bounded body bytes, completeness flag)
  are finalized in this task and recorded in the commit body — they are the
  frozen vocabulary for tasks 02/03.

- Produces in `GitHubCopilotDetection` (static or instance over
  `ISetupPlatform`):

```csharp
internal sealed record GitHubCopilotObservations(
    SetupPlanningOs PlanningOs,
    ChannelObservation VsCodeStable,     // Detected, Version (sanitized semver or null)
    ChannelObservation VsCodeInsiders,
    ChannelObservation CopilotCli,
    bool StableHasNonDefaultProfiles,
    bool InsidersHasNonDefaultProfiles);

internal static GitHubCopilotObservations Observe(ISetupPlatform platform);
```

**Contract details to encode (from the spec):**
- VS Code detection runs exactly `code --version` /
  `code-insiders --version`; extension listing (consumed later by T4) is
  exactly `code --list-extensions --show-versions` (or `code-insiders`),
  never `--profile`. CLI detection runs `copilot version` (compare with the
  actual documented invocation before pinning the argument list; if the spec
  and the CLI disagree, stop and report).
- Version strings are sanitized to a semantic version of at most 128 UTF-16
  units or null; no path or free-form process output escapes the boundary.
- `SetupManagedLocation` closed members cover exactly the official sources:
  Copilot native (Windows `HKLM\SOFTWARE\Policies\GitHubCopilot`; macOS
  managed preferences for `com.github.copilot`; none on Linux), Copilot file
  (`%ProgramFiles%\GitHubCopilot\managed-settings.json`,
  `/Library/Application Support/GitHubCopilot/managed-settings.json`,
  `/etc/github-copilot/managed-settings.json`), VS Code enterprise policy
  (Windows `HKLM` and `HKCU` `Software\Policies\Microsoft\VSCode`; macOS
  configuration profiles; Linux `/etc/vscode/policy.json`).
- Non-default profile presence: enumerate only the documented sibling
  `profiles/<profile-id>/` directories; never open/hash/parse a non-default
  profile settings file (the observation is a boolean).
- Default Profile settings paths per channel/OS (consumed by T4; declare the
  path rule here so both channels share one implementation):
  Stable `%APPDATA%\Code\User\settings.json` (Windows),
  `$HOME/Library/Application Support/Code/User/settings.json` (macOS),
  `$HOME/.config/Code/User/settings.json` (Linux); Insiders replaces the
  intermediate `Code` folder with `Code - Insiders`.

## Steps

- [ ] **Step 1: Write the failing platform-seam tests.** Extend
  `SetupTestPlatform` with deterministic fakes for the four new boundaries
  (scripted process results, scripted managed reads keyed by
  `SetupManagedLocation`, scripted HTTP observations) and add
  `GitHubCopilotDetectionTests` covering: all-absent (nothing installed),
  Stable-only, both channels, CLI-only, malformed version output → null
  version with `Detected=true`, process not-found → `Detected=false`,
  process timeout → deterministic non-detection (no exception), non-default
  profile flags per channel, and planning-OS capture for all three OS fakes.

- [ ] **Step 2: Run RED.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~GitHubCopilotDetectionTests
```

- [ ] **Step 3: Implement.** Interface additions, `SystemSetupPlatform`
  implementations (real registry via `Microsoft.Win32.Registry` guarded by
  OS checks — no new package; real bounded process via
  `System.Diagnostics.Process` with output caps and kill-on-timeout; real
  `HttpClient` with `AllowAutoRedirect=false` and the bounded read),
  `SetupTestPlatform` fakes, and `GitHubCopilotDetection.Observe`.

- [ ] **Step 4: Run GREEN + the runtime suites that touch the platform
  surface.**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupRuntimeTests|FullyQualifiedName~GitHubCopilotDetectionTests|FullyQualifiedName~SetupPlatformTests"
dotnet build CopilotAgentObservability.slnx
git diff --check
```

  Extending `ISetupPlatform` forces `SetupTestPlatform` construction updates;
  those are compile-only — no frozen assertion or fixture meaning changes.
  Any test suite that constructs the platform directly may get a
  compile-only constructor hunk, and nothing more.

- [ ] **Step 5: Commit.**

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Platform tests/CopilotAgentObservability.ConfigCli.Tests/SetupTestPlatform.cs src/CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/GitHubCopilotDetection.cs tests/CopilotAgentObservability.ConfigCli.Tests/GitHubCopilotDetectionTests.cs
git commit -m "Issue #67: feat(setup): observe Copilot setup platforms"
```

  Body: why — every #67 decision (policy, endpoint, targets) must consume
  bounded deterministic observations so adapters stay testable and no raw
  process/registry/path output can leak into public DTOs; record the frozen
  seam type shapes.

## Validation

Step 4 commands.

## Completion criteria

- Four new observation boundaries frozen, each with a deterministic fake and
  a real `SystemSetupPlatform` implementation.
- `GitHubCopilotDetection.Observe` covers the detection matrix above with no
  raw process output, path, or registry value escaping.
- No policy resolution, endpoint classification, or plan/record construction
  anywhere in this task's diff.
- Solution builds 0/0; focused suites pass; independent review PASS
  (reviewer explicitly checks the boundary is observation-only and the
  managed-location enum is closed).

**Report destination:** chat + ledger row per README policy. Record real
Windows execution versus fake-only macOS/Linux coverage as unverified scope.
