# Task 01a: Committed v1 fixture process-equivalent restart evidence

**Purpose:** Materialize Migration A as compatibility/restart evidence for the
first shipped ownership-ledger schema. This task proves that the committed v1
fixture survives a process-equivalent close/reopen boundary without changing
bytes or creating migration artifacts. It is not a migration: v1 is the first
shipped version, so migration is **N/A** and a fabricated v0 fixture is
forbidden.

**Depends on:** Task 01 contract audit recorded and reviewed; Task 01 Q4
Migration A decision (v1 restart compatibility evidence, migration N/A).

**Owned scope:**

- Test-only: `tests/CopilotAgentObservability.ConfigCli.Tests/SetupStorageTests.cs`.
- Consume the actual committed fixture
  `Fixtures/Setup/v1/ownership-ledger.v1.json`, copied to the physical setup
  runtime root by the test harness.
- Instantiate a first `SystemSetupPlatform`/runtime-path/store boundary, end
  its scope, instantiate fresh platform/path/store objects over the same root,
  load the ledger, and assert the expected shipped v1 state.

**Non-scope:** No production code, schema, migration loader, v0/v2 fixture,
compatibility fallback, specification, dependency, runtime, or public-interface
change. Do not edit any file outside the owned test file.

**Constraints:**

- Keep schema v1; do not invent a synthetic v0 or v2 migration layer.
- Preserve the fixture bytes exactly. Assert the ledger file bytes are
  identical to the committed fixture before and after load/restart.
- Assert no v0/v2/migration artifact or temporary file is created under the
  physical setup runtime root.
- Use deterministic filesystem assertions and existing test seams; no sleeps,
  timing polls, retries, real user data, or raw/PII values.
- Follow TDD: the new test must fail before the evidence harness is complete
  for the expected reason, then pass after the harness is complete.

**Completion criteria:**

- The test consumes the committed v1 fixture and proves process-equivalent
  close/reopen loading of the expected shipped state.
- Pre-load and post-load fixture bytes are byte-identical.
- No migration, v0/v2, or temporary artifact is present.
- The test and focused suite pass; independent read-only review is PASS;
  `git diff --check` is clean.

**Validation commands:**

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SetupStorageTests
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
dotnet build CopilotAgentObservability.slnx
git diff --check
```

**Worktree/branch:** repository root
on `codex/issues-66-67-guided-setup`.

**Report destination:** chat + Task 01a's own evidence row in
`docs/sprints/sprint23-configuration-ownership-github-copilot/ledger.md`.
The completed `.superpowers/sdd/plan-a-corrections-report.md` remains immutable
historical evidence for this planning correction and is not the later
implementation report.

**Local commit subject:**
`Issue #66: test(setup): prove committed v1 ledger restart compatibility`
