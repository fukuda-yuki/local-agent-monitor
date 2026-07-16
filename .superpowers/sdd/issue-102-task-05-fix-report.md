# Issue #102 Task 5 Fix Report — CLI Classification and Safety

## Identity

- Worktree: linked Issue #102 worktree
- Branch: `codex/issue-102-cli`
- Reviewed candidate: `fe38bb71bee2f61e184ebab952b0144cbeff9f69`
- Fix scope: Doctor CLI adapter and Doctor CLI tests only

## Finding disposition

### I1 — Unsupported schemas were classified too late

Resolved. Both direct evaluation and completion now validate the required schema token immediately after strict JSON parsing and duplicate-property rejection, before v1 exact-shape validation, deserialization, or application invocation. Unsupported nonblank string versions return the fixed `unsupported_schema_version` result with exit 2. Missing, null, numeric, or blank schema values remain `invalid_input`. A production-default completion regression proves the no-store result cannot override schema classification.

### I2 — Optional snapshot members were required

Resolved. Root shape validation now distinguishes required and optional properties. `expected_source_adapter` and `verification_id` may be omitted or explicitly null for both direct and completion snapshots. All canonical mandatory root, family, and nested properties remain required, and unknown or duplicate properties remain rejected.

### I3 — Unsafe evidence references were accepted

Resolved. Evidence references now reuse `DiagnosisValidator.ContainsUnsafeMaterial` and additionally reject leading/trailing whitespace, controls, RFC 3986 scheme syntax, path separators and local-path aliases, and SSN-form identifiers. The same check applies to direct observations and completion selections. Tests cover raw prompt/response and tool payload markers with case variants, authorization and credentials, PII-like values, URIs, drive-relative/rooted/current/parent/home/local paths, controls, empty/whitespace values, and the 129-character sentinel. Every unsafe case proves a canonical fixed result, fixed stderr, and zero application calls, so the supplied value cannot reach stdout or stderr.

### I4 — Actual-command and off-by-one coverage was incomplete

Resolved. Actual CLI command tests now pin completion reference counts 0/1/16/17, evidence-reference lengths 0/1/128/129 for direct and completion inputs, completion non-ready/partial/conflict/store/internal outcomes, start/status/cancel success/conflict/store outcomes, completion input sizes 65,536/65,537 bytes, JSON/human projection with one application invocation per command, and all production-default lifecycle commands without database creation.

## TDD evidence

### Schema classification and optional members

RED:

```text
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~Schema|FullyQualifiedName~OptionalSnapshotMembers"
Exit 1: 10 failures. Unsupported schemas were reduced to invalid/store outcomes, optional omissions were rejected, and malformed schema values reached the wrong path.
```

GREEN after the adapter change and final helper review:

```text
Exit 0: 41 passed, 0 failed, 0 skipped.
```

### Unsafe evidence references

RED:

```text
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~UnsafeEvidenceReference
Exit 1: 10 failures among 60 cases. URI/URN, SSN-form, drive-relative, and tool payload variants reached the application.
```

GREEN after reusing the repository guard and adding URI/path/trim boundaries:

```text
Exit 0: 60 passed, 0 failed, 0 skipped.
```

### Actual commands and boundaries

The added actual-command matrix was GREEN on its first run because the production checks it pins already existed or were introduced by the preceding RED/GREEN changes. An analyzer warning in the test was removed by replacing a count equality with `Assert.Single`.

```text
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~OffByOneBoundaries
Exit 0: 12 passed, 0 failed, 0 skipped.

dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~ActualCommand
Exit 0: 28 passed, 0 failed, 0 skipped.
```

## Final verification

All commands ran from the Task 5 worktree after the final test-helper adjustment.

```text
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter "FullyQualifiedName~Schema|FullyQualifiedName~OptionalSnapshotMembers"
41 passed, 0 failed, 0 skipped.

dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~UnsafeEvidenceReference
60 passed, 0 failed, 0 skipped.

dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~OffByOneBoundaries
12 passed, 0 failed, 0 skipped.

dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~ActualCommand
28 passed, 0 failed, 0 skipped.

dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~Doctor
152 passed, 0 failed, 0 skipped.

dotnet build CopilotAgentObservability.slnx
Build succeeded: 0 warnings, 0 errors.

dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj
3,636 passed, 0 failed, 0 skipped.

git diff --check
Exit 0; no output.
```

Artifact inspection found no database files and no SQLite construction, sleep, delay, or retry reference in `DoctorCli.cs`.

## Self-review and handoff

- Rechecked each of I1–I4 against the independent review report; all are covered by production behavior and direct regressions.
- Restored the completion helper's default non-null `verification_id` after noticing that making it null by default would have weakened the existing completion-context coverage. Optional-member cases provide their snapshot explicitly.
- The change adds no dependency, fallback, compatibility path, polling, persistence composition, source-specific behavior, specification edit, or Local Monitor change.
- Task 7 still owns the approved store/application composition and trusted candidate resolution described by the independent review handoff.
