# Task 04a (T0a): Current-process environment observation correction

**Objective:** Reopen the T3a platform boundary so current-process environment
reads are explicit, read-only, and cannot be confused with the Windows
current-user persistent environment mutation surface.

**Depends on:** task-04 (T3d) frozen seam. This correction must pass its
independent review before task-04b starts and before any T4/T5/T6 target task
starts.

**Files (ownership):**

- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Platform/ISetupPlatform.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Setup/Platform/SystemSetupPlatform.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupTestPlatform.cs`
- Modify: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupPlatformTests.cs`

**Non-scope:** `UserEnvironment` behavior, environment writes/notifications,
all adapters/partitions, private-plan/storage/transaction code, catalog or
validator changes, the ledger, and all Issue #66 historical cards.

**Interface contract:** Add `ISetupProcessEnvironment` with exactly
`string? Get(string name)`. Add one `ProcessEnvironment` property to
`ISetupPlatform`. `SystemSetupPlatform` reads this value with the current
process environment API; `SetupTestPlatform` exposes a deterministic keyed
fake and operation log. The interface has no set/delete/notify member. Existing
`ISetupPlatform.UserEnvironment` remains the current-user persistent
environment API, including its existing write and notification behavior.

**Required tests:**

- `ProcessEnvironment.Get` reads a current-process value through the real
  platform boundary and never invokes `UserEnvironment.Get`, `Set`, or
  `NotifyChange`.
- The test fake distinguishes identical variable names in process and
  current-user stores; reading one cannot observe, mutate, or notify the other.
- Missing/present/empty process values are deterministic. A marker value is
  only asserted inside the platform fake and is absent from exception text and
  operation evidence.
- No sleep, retry, shell invocation, or ambient machine-user mutation is used.

## Steps

- [ ] Write the failing `SetupPlatformTests` cases for the distinct read-only
  interface and the process/user isolation matrix.
- [ ] Run RED:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupPlatformTests
```

- [ ] Implement only the four owned files and make every direct platform
  constructor/test double compile without changing fixture meaning.
- [ ] Run GREEN and the platform regression suite:

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~SetupPlatformTests|FullyQualifiedName~SetupRuntimeTests|FullyQualifiedName~GitHubCopilotDetectionTests"
dotnet build CopilotAgentObservability.slnx
git diff --check
```

- [ ] Commit:

```powershell
git add src/CopilotAgentObservability.ConfigCli/Setup/Platform tests/CopilotAgentObservability.ConfigCli.Tests/SetupTestPlatform.cs tests/CopilotAgentObservability.ConfigCli.Tests/SetupPlatformTests.cs
git commit -m "Issues #66-#67: fix(setup): separate process environment observation"
```

Body: why — CLI planning must report the current process without granting that
observation the authority to mutate the current user's persistent environment.

## Completion criteria

- `ISetupProcessEnvironment` is the only current-process boundary and is
  demonstrably read-only.
- `UserEnvironment` remains the only current-user persistent writer.
- Focused suites, build, and `git diff --check` pass; fresh security review
  PASS confirms no raw process value becomes an output surface.

**Report destination:** chat only. Do not update the ledger in this task.
