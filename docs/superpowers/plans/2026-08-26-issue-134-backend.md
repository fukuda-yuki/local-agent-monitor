# Issue #134 Remaining Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete Issue #134's current-valid Skill aggregation and raw-default Session summary/timeline/node/content backend on one v4 Workspace projection and coherent snapshot.

**Architecture:** Extend the existing #154 Skill read authority and #156 Repository snapshot coordinator; do not create parallel readers. Migrate `local_workspace_projection` atomically to v4, persist exact execution/node facts and raw references, and serialize the four closed contracts from one host lease/connection/transaction with revision fencing.

**Tech Stack:** .NET 10/C#, ASP.NET Core minimal routes, Microsoft.Data.Sqlite, xUnit, JSON Schema Draft 2020-12, Playwright regression suite.

**Spec:** `docs/specifications/interfaces/local-monitor-v1-session-detail.md`

## Global Constraints

- Work only in `C:\Users\mwam0\Documents\Codex\copilot-agent-observability\.worktrees\issue-134-backend` on `codex/issue-134-backend`.
- Every production change follows red-green TDD. Record the failing command/output before implementation in the task report.
- Every commit title begins `Issue #134:` and follows Conventional Commits; feat/fix/refactor commits have a Why body.
- Do not push, create a PR, mutate a GitHub Issue, or edit Razor/JavaScript/CSS.
- Preserve exact Repository/Session collection bytes, cursors and property order; preserve frozen `/api/monitor/*`, `/api/session-workspace/*` v1 and SSE.
- No direct #134 catalog/archive SQL, second publication gate, second Skill authority, v3/v4 dual reader, read-time compatibility fallback, heuristic identity/parent/cross-arm join, silent truncation, or raw echo.
- Use one host-scoped publication lease, one SQLite connection and one coherent read transaction per detail response.
- Bounds are exact: 256 executions, 4,096 nodes, default 100/maximum 200 children, maximum 200 related rows, six content parts, 1,048,576 raw UTF-8 bytes, 8,388,608 JSON bytes.

---

### Task 1: Complete the single current-valid Skill projection authority

**Files:**
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/SkillProjection/SkillProjectionReadService.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceProjectionStore.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceSessionSnapshotContributor.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceProjectionModels.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/SkillProjectionGenerationTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalWorkspaceSessionSnapshotContributorTests.cs`

**Interfaces:**
- Consumes: #154 current OTel generation predicates, SDK Session/Event snapshot/claim/receipt/Retention equality, `ISkillRegistryGenerationAuthority`, one caller-supplied SQLite transaction and captured `now`.
- Produces: one transaction-capable `ReadCurrentSessionInvocations(...)` result keyed by Session with admitted names, exact execution/node source identities, aggregate `{state,count}`, and search eligibility. `ReadSessionInvocationAggregates` and search-fact refresh become projections of that result, not independent SQL meanings.

- [ ] **Step 1: Write failing Skill matrix tests**

Add literal-fixture integration tests proving: OTel-only count 1; SDK-only count 1 without trace/span; exact producer trace+span pair count 1; trace-only and mismatched pair produce `certification_pending/null`; stale, invalid, expired, unavailable SDK claims produce no current fact; duplicate exact pairs do not double count; q/summary/has_skill consume identical state.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~SkillProjectionGenerationTests|FullyQualifiedName~LocalWorkspaceSessionSnapshotContributorTests"
```

Expected: failures show the existing unconditional empty SDK reader and OTel-only aggregate/search semantics.

- [ ] **Step 3: Implement the minimal unified reader**

Read OTel and SDK candidates inside the caller transaction. Apply each arm's existing current predicate. Pair only on non-null exact producer trace ID plus span ID. Return OTel-only/SDK-only counts, exact-pair dedupe, and pending/null for unpaired positive cross-arm observations. Feed collection activity/search from the same result and captured instant.

- [ ] **Step 4: Re-run focused tests and collection regressions**

Run the Step 2 command, then:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~LocalMonitorV1Collection|FullyQualifiedName~LocalMonitorV1SessionSearch"
```

Expected: zero failures and unchanged collection goldens.

- [ ] **Step 5: Self-review and commit**

Review the diff for any arm-specific semantic reader left in collection paths. Commit only Task 1 files with `Issue #134: fix(skill): unify current invocation aggregation` and a Why body.

### Task 2: Freeze executable response contracts before runtime routes

**Files:**
- Create: `docs/specifications/contracts/local-monitor-v1/session-summary.response.schema.json`
- Create: `docs/specifications/contracts/local-monitor-v1/session-timeline.response.schema.json`
- Create: `docs/specifications/contracts/local-monitor-v1/session-node.response.schema.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/LocalMonitorV1SessionDetail/summary-empty.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/LocalMonitorV1SessionDetail/summary-full.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/LocalMonitorV1SessionDetail/timeline-empty.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/LocalMonitorV1SessionDetail/timeline-page.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/LocalMonitorV1SessionDetail/node-full.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1SessionDetailSpecificationTests.cs`

**Interfaces:**
- Consumes: exact property order, nullability, enums, bounds, cursor layout, headers and errors from the Spec.
- Produces: three closed Draft 2020-12 schemas and literal golden bytes used unchanged by Tasks 4-5.

- [ ] **Step 1: Write the contract test first**

The test loads each schema and literal fixture, validates every fixture with `Json.Schema`, asserts exact property order recursively, asserts all objects use `additionalProperties:false`, asserts schema tokens, validates the 119-byte/159-character cursor layout with a literal zero-key golden, and asserts the fixed error byte literals and content success header contract.

- [ ] **Step 2: Run and verify RED because artifacts are absent**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~LocalMonitorV1SessionDetailSpecificationTests
```

Expected: missing schema/fixture failure.

- [ ] **Step 3: Add the minimal schemas and literal fixtures**

Encode every property and enum from the Spec. Use explicit null unions, numeric minima/maxima, array maxima and patterns. Hand-write compact one-line UTF-8 JSON fixtures in exact order; do not generate expected bytes with production serializers.

- [ ] **Step 4: Re-run contract and existing collection specification tests**

Run Step 2, then:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~LocalMonitorV1RepositoryCollectionSpecificationTests|FullyQualifiedName~LocalMonitorV1SessionCollectionSpecificationTests"
```

- [ ] **Step 5: Self-review and commit**

Commit with `Issue #134: docs(contract): freeze session detail responses`.

### Task 3: Migrate `local_workspace_projection` v3 to v4 with stable identities

**Files:**
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceProjectionSchemaV1.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceProjectionStore.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceProjectionTransactionParticipant.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/SqliteRuntimeBackupService.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/LocalWorkspaceProjectionBackupValidation.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalWorkspaceProjectionSchemaTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalWorkspaceProjectionBackfillTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/RuntimeBackupLocalWorkspaceProjectionTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/RuntimeBackupRestoreTests.cs`

**Interfaces:**
- Consumes: Session v14 Run/Event exact IDs and FKs, OTel trace/span exact identity, unified Task 1 Skill facts, Retention/raw availability, existing transaction participant and publication order.
- Produces: `local_workspace_projection:4` exact owned schema with execution, node, edge, and content-reference tables; deterministic execution UUID/node IDs; exact/explicit/unknown relationship and recorded/missing/invalid time facts.

- [ ] **Step 1: Add failing migration/identity/bound tests**

Cover exact v1->v2->v3->v4, v3 rollback, rerun idempotence, partial/future failure, deterministic backfill, restart/migration/restore identity stability, exact/explicit/unknown parents, missing/invalid time, 256/257 executions, 4,096/4,097 nodes, and prohibited name/time/index/cardinality identity inputs.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~LocalWorkspaceProjectionSchemaTests|FullyQualifiedName~LocalWorkspaceProjectionBackfillTests|FullyQualifiedName~RuntimeBackupLocalWorkspaceProjectionTests"
```

Expected: version remains 3 and v4 objects/identities are absent.

- [ ] **Step 3: Implement atomic v4 schema and deterministic backfill**

Validate exact v3, create normalized tables/indexes, derive IDs solely from domain-separated length-framed exact source identity, backfill in stable source order, validate semantics, update stamp last, and commit once. Keep only v4 runtime validation/reader behavior.

- [ ] **Step 4: Update backup/restore and publication participants**

Change supported version to 4, accept exact v1/v2/v3 only as staging migration inputs, validate canonical replica equality including v4 rows, refresh before backup/restore publication, and preserve Retention/Skill publication ordering.

- [ ] **Step 5: Re-run focused migration/backup tests**

Run Step 2 and:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~RuntimeBackupRestoreTests|FullyQualifiedName~LocalRepositoryRuntimeBackupTests"
```

- [ ] **Step 6: Self-review and commit**

Commit with `Issue #134: feat(workspace): migrate detail projection to v4` and a Why body.

### Task 4: Implement coherent Summary, Timeline and Node reads/routes

**Files:**
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/LocalRepositoryScopeContracts.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/SqliteLocalRepositoryScopeSnapshotService.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceSessionDetailSnapshotContributor.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceSessionDetailModels.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/LocalMonitorV1/LocalMonitorV1SessionDetailApplication.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/LocalMonitorV1/LocalMonitorV1SessionDetailRoutes.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalWorkspaceSessionDetailSnapshotTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1SessionDetailRouteTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1SessionDetailApplicationTests.cs`

**Interfaces:**
- Consumes: Task 3 v4 projection, Task 2 schemas/goldens, existing publication gate/Repository coordinator/archive contributor/Skill authority.
- Produces: bounded target-Session snapshot and exact Summary/Timeline/Node GET+HEAD routes with canonical revision and timeline keyset cursor.

- [ ] **Step 1: Add failing coherent-snapshot and route tests**

Cover one lease/connection/transaction, target-only query count, no N+1, 256 success/257 failure, 4,096 nodes, ordered headers, exact execution/ancestor resolution, root/execution/child lazy pages, stable ordering with missing/invalid time, no duplicate/drop, unknown group, wrong Session/execution node not-found, GET/HEAD/405, query/error precedence, exact success/error bytes and headers.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~LocalWorkspaceSessionDetailSnapshotTests|FullyQualifiedName~LocalMonitorV1SessionDetail"
```

Expected: missing detail contributor/application/routes.

- [ ] **Step 3: Refactor the existing coordinator minimally**

Add a target-Session request/contributor mode that runs under the existing phase capability, then composes exact catalog assignment and archive facts. Preserve collection behavior and statement counts. Do not expose the SQLite connection outside the capability.

- [ ] **Step 4: Implement revision and bounded readers**

Build one domain-separated length-framed SHA-256 revision over all Spec inputs in the same transaction. Query exact Session IDs, cap executions/nodes/relations before materialization, and expose immutable models for serializers.

- [ ] **Step 5: Implement serializers, cursor and routes**

Use `Utf8JsonWriter` with the Task 2 order, pre-buffer complete entities for byte ceilings and HEAD length, use the exact 119-byte cursor frame, map fixed errors without reflected values, and register only in raw-default composition.

- [ ] **Step 6: Run focused and frozen-surface regressions**

Run Step 2, then:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~LocalMonitorV1Collection|FullyQualifiedName~GenericRoute|FullyQualifiedName~ServerSentEvents"
```

- [ ] **Step 7: Self-review and commit**

Commit with `Issue #134: feat(workspace): add coherent session detail reads` and a Why body.

### Task 5: Add raw content leases, revision invalidation, backup synchronization and full regressions

**Files:**
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceSessionDetailSnapshotContributor.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceNodeContentReader.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/LocalMonitorV1/LocalMonitorV1SessionDetailApplication.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/LocalMonitorV1/LocalMonitorV1SessionDetailRoutes.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/LocalWorkspaceProjectionBackupValidation.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalWorkspaceSessionDetailRevisionTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1NodeContentRouteTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/RuntimeBackupLocalWorkspaceProjectionTests.cs`

**Interfaces:**
- Consumes: exact node/part raw reference from Task 3, existing Retention committed access lease, Task 4 revision recomputation.
- Produces: exact inert text content route and complete revision invalidation/backup-retention synchronization.

- [ ] **Step 1: Add failing revision and content matrix tests**

Revision tests mutate active Session, assignment, archive, Run/Event/Span, Skill generation, Retention expiry/delete, and raw availability between Summary and follow-up. Assert exact stale bytes and no old/new fact mixture. Content tests cover available, not captured, expired, deleted, read denied, oversized, invalid UTF-8, lease race/loss, wrong binding, HEAD, 405, no raw echo in errors/logs.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~LocalWorkspaceSessionDetailRevisionTests|FullyQualifiedName~LocalMonitorV1NodeContentRouteTests"
```

Expected: content route/lease semantics and revision inputs are incomplete.

- [ ] **Step 3: Implement the exact committed-lease content reader**

Resolve only the persisted node/part carrier, acquire the existing access lease, read the complete UTF-8 value into a bounded buffer, reject more than 1 MiB without partial output, keep/re-prove the lease through response completion, and return only fixed outcomes. Never log/format raw values.

- [ ] **Step 4: Complete revision inputs and lifecycle synchronization**

Include every Spec revision source. Ensure Retention cleanup and Skill publication update v4 through the existing transaction participant and publication gate. Extend backup canonical validation for content references and availability semantics.

- [ ] **Step 5: Run focused operational and frozen regressions**

Run Step 2 and:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~RuntimeBackup|FullyQualifiedName~Retention|FullyQualifiedName~SkillProjection|FullyQualifiedName~LocalMonitorV1Collection|FullyQualifiedName~GenericRoute|FullyQualifiedName~ServerSentEvents"
```

- [ ] **Step 6: Self-review and commit**

Commit with `Issue #134: feat(workspace): secure node content and revision fencing` and a Why body.

### Task 6: Final branch verification and integration preparation

**Files:**
- Modify only files required to fix findings from focused tests or review.

**Interfaces:**
- Consumes: all prior task commits.
- Produces: one reviewed branch whose final HEAD passes the required validation suite.

- [ ] **Step 1: Run focused Issue #134 tests**

Run all test classes created/changed by Tasks 1-5 in one filter and record counts.

- [ ] **Step 2: Run the pinned validation suite in exact order**

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Expected: every command exits 0; the final complete solution test run is against the unchanged final HEAD.

- [ ] **Step 3: Inspect status/history and prepare final review range**

Confirm no generated artifacts are tracked, every commit contains `Issue #134`, no scope-excluded files changed, and record `git merge-base main HEAD` plus final HEAD.

