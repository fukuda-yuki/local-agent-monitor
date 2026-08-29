# Issue #166 Task 1 Report

## Status

Implemented the Compare-only coherent SQLite contribution. Production Compare input now freezes every admitted named Skill, Tool, and Sub-agent node selected from Workspace v5, including exact source references, lifecycle/status, activity (failure/retry), token facts, and coverage states. Source application versions and adapter versions are read into separate collections. Summary behavior and public Workspace DTOs are unchanged.

## Implementation

- Added `LocalWorkspaceComparisonDetailContribution` and attached it only to internal `LocalRepositoryComparisonSessionInput`.
- Added an internal Compare read kind whose node predicate selects all admitted `skill`, `tool`, and `subagent` nodes while reusing existing Skill admission and semantic/current-Skill/core owner validation.
- Read the Compare contribution through the existing publication lease, SQLite connection, read transaction, pinned registry, and `ReadTransactionCapability`.
- Preserved `workspace_too_large` at the existing 4,096-node bound.
- Split `source_application_version` and `adapter_version` reads without changing the Summary `Versions` projection.
- Derived the comparison session revision from the Compare contribution's canonical revision input and pinned registry identity, so every consumed persisted Compare fact participates in stale-preview detection.

## Changed files

- `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/LocalRepositoryScopeContracts.cs`
- `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/SqliteLocalRepositoryScopeSnapshotService.cs`
- `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceSessionDetailModels.cs`
- `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceSessionDetailSnapshotContributor.cs`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalRepositoryComparisonInputSnapshotTests.cs`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalWorkspaceSessionDetailSnapshotTests.cs` (fixture visibility only)

## TDD evidence

### RED

Command:

```powershell
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~LocalRepositoryComparisonInputSnapshotTests"
```

Exit code: `1`.

Expected failure: compilation failed because `LocalRepositoryComparisonSessionInput` did not contain `ComparisonDetail` (`CS1061`). The first run also exposed a test-local clock type visibility error (`CS0246`), which was corrected before production behavior was implemented.

### Focused GREEN

Same command as RED.

Exit code: `0`. Passed `2`, failed `0`, skipped `0`, total `2`.

### Nearby scope/detail/coherence GREEN

Command:

```powershell
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~LocalRepositoryComparisonInputSnapshotTests|FullyQualifiedName~LocalRepositoryScopeSnapshotTests|FullyQualifiedName~LocalWorkspaceSessionDetailSnapshotTests|FullyQualifiedName~LocalWorkspaceSessionDetailRevisionMatrixTests|FullyQualifiedName~LocalWorkspaceSessionDetailAuthorityMatrixTests"
```

Exit code: `0`. Passed `284`, failed `0`, skipped `0`, total `284`.

## Self-review

- No public Workspace response or Summary node predicate changed.
- No new connection, reader service, table, dependency, route, UI, calculator, or store was added.
- Compare nodes remain immutable arrays and retain existing exact reference/metadata models.
- The Compare query applies the current Skill admission predicate and all existing owner-graph validation before returning facts.
- The canonical revision already hashes the full relevant source/projection rows; the frozen Compare contribution now supplies that input directly to comparison revision calculation.
- `git diff --check` passed.

## Concerns

- The focused synthetic fixture contains semantic Tool and Sub-agent rows; admitted Skill behavior is exercised by the reused production admission path and nearby Skill/detail tests, but the new class does not add a second full Skill registry fixture.
- Existing project-wide nullable warnings remain in unrelated tests; no new warning was introduced by this task.
