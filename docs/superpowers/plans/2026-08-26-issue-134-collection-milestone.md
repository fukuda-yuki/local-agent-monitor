# Issue #134 Collection Milestone Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the existing Repository and Session collection contracts without implementing summary, timeline, node, or UI work.

**Architecture:** `local_workspace_projection:3` owns accepted Session instants and normalized source-lifetime-bound search facts. The existing single Repository scope snapshot remains the only collection data source; application serializers apply stable keysets and exact wire contracts without direct catalog/archive SQL or N+1 reads.

**Tech Stack:** .NET 10, C#, ASP.NET Core minimal routes, Microsoft.Data.Sqlite, xUnit, Draft 2020-12 JSON Schema.

**Spec:** `docs/superpowers/specs/2026-08-26-issue-134-collection-milestone-design.md`

## Global Constraints

- Preserve #171 Session response shape, property order, schema name, and golden bytes.
- Preserve #136 Session POST request grammar and 147-character cursor transport.
- Reuse the single #156/#161 `ILocalRepositoryScopeSnapshotService`; no direct catalog/archive SQL or second reader.
- Reuse #154/#158 Skill authorities; stale or invalid Skill claims are never searchable.
- Register human routes only in raw-default; no sanitized-only fallback.
- Do not change frozen `/api/monitor/*`, `/api/session-workspace/*` v1, SSE, or Canvas bytes.
- Preserve missing versus recorded zero and fixed-count set-based reads; no N+1 queries.
- Do not implement summary, timeline, node metadata/content, UI, primary-route activation, `/traces` retirement, Compare, or AI.
- Do not push, create a PR, or write to GitHub Issues.
- Every production change follows RED, GREEN, REFACTOR and records commands/output in the task report.

---

### Task 1: Freeze Repository collection and cursor contracts

**Files:**
- Create: `docs/specifications/interfaces/local-monitor-v1-repository-collection.md`
- Create: `docs/specifications/contracts/local-monitor-v1/repository-collection.response.schema.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/LocalMonitorV1RepositoryCollection/empty.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/LocalMonitorV1RepositoryCollection/final-page.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/LocalMonitorV1RepositoryCollection/more-page.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1RepositoryCollectionSpecificationTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj`
- Modify: `docs/specifications/interfaces/local-monitor-v1-route-transport.md`
- Modify: `docs/specifications/interfaces/local-monitor-v1-contract-index.md`
- Modify: `docs/specifications/interfaces/local-repository-catalog.md`
- Modify: `docs/spec.md`

**Interfaces:**
- Produces: exact `local-monitor-repositories.response.v1` envelope/item order and 135-character Repository cursor contract consumed by Task 3.
- Envelope order: `schema_version, workspace_revision, repositories, all_session_count, unassigned_active_session_count, archived_repository_count, next_cursor`.
- Item order: `repository_id, display_name, archive_state, archive_revision, active_session_count, last_observed_at, assignment_conflict_count, repository_revision`.

- [ ] **Step 1: Write the failing specification test**

Create tests that load the canonical specification, schema, and three fixtures; assert strict UTF-8/no newline, exact property order, closed schema, empty bytes, final/more cursor states, and a 135-character deterministic cursor generated from key bytes `00..1f`, `archive_scope=include_archived`, effective `limit=1`, and position `018f0000-0000-7000-8000-000000000101`.

- [ ] **Step 2: Run the specification test and verify RED**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~LocalMonitorV1RepositoryCollectionSpecificationTests
```

Expected: failure because the canonical Repository specification/schema/fixtures do not exist.

- [ ] **Step 3: Add the exact canonical contract artifacts**

Freeze the cursor frame and bytes exactly as the design specifies. The response schema must set `additionalProperties:false`, bound `repositories` to 0..200, UUIDv7/revision/canonical-UTC formats, nonnegative counts, and `next_cursor` to null or exactly 135 characters. Goldens use synthetic data only and never include a locator, path, owner, search value, or raw Repository ID as the cursor.

- [ ] **Step 4: Update only owning references**

Make the Repository collection specification the exact success/cursor authority from the contract index, system spec, route transport, and catalog route-ownership section. Also record that Session `from`/`to` use the accepted fallback instant and q uses the three closed normalized fact classes.

- [ ] **Step 5: Run the specification test and verify GREEN**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~LocalMonitorV1RepositoryCollectionSpecificationTests
```

Expected: all Task 1 tests pass.

- [ ] **Step 6: Self-review and commit**

```powershell
git add docs/specifications/interfaces/local-monitor-v1-repository-collection.md docs/specifications/contracts/local-monitor-v1/repository-collection.response.schema.json tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/LocalMonitorV1RepositoryCollection tests/CopilotAgentObservability.LocalMonitor.Tests/LocalMonitorV1RepositoryCollectionSpecificationTests.cs tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj docs/specifications/interfaces/local-monitor-v1-route-transport.md docs/specifications/interfaces/local-monitor-v1-contract-index.md docs/specifications/interfaces/local-repository-catalog.md docs/spec.md
git commit -m "Issue #134: docs(collection): freeze repository cursor contract"
```

### Task 2: Migrate Local Workspace projection v2 to v3

**Files:**
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceProjectionSchemaV1.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceProjectionStore.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceProjectionModels.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceProjectionTransactionParticipant.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/LocalWorkspace/LocalWorkspaceSessionSnapshotContributor.cs`
- Modify only as needed to notify projection refresh: current Skill projection participants/stores under `src/CopilotAgentObservability.Persistence.Sqlite/SkillProjection/` and `SkillInvocationSnapshot/`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/LocalWorkspaceProjectionBackupValidation.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/SqliteRuntimeBackupService.cs`
- Modify: `docs/specifications/interfaces/runtime-backup-restore.md`
- Modify tests: `LocalWorkspaceProjectionSchemaTests.cs`, `LocalWorkspaceProjectionBackfillTests.cs`, `LocalWorkspaceSessionSnapshotContributorTests.cs`, `RuntimeBackupLocalWorkspaceProjectionTests.cs`, `LocalRepositoryRuntimeBackupTests.cs`, `RuntimeBackupRestoreTests.cs`

**Interfaces:**
- Produces: `LocalWorkspaceProjectionRow` with signed accepted `SortEpochMilliseconds`, nullable canonical `LastSeenAt`, nullable `LastSeenEpochMilliseconds`, and a distinct ordinal-sorted `SearchTexts` set.
- Produces: exact `local_workspace_projection:3` schema and v2-to-v3 migration; there is no runtime v2 reader.
- Search fact kinds are exactly `label`, `skill`, `tool`; stored text is NFKC plus invariant lowercase.

- [ ] **Step 1: Add failing timestamp, search lifecycle, and v2 migration tests**

Cover valid started/created/last-seen fallback, malformed fallback, all-missing invalid group, mixed offsets, canonical UTC timing, label/Skill/Tool search facts, prohibited body/path text, stale/invalid/expired facts, retention deletion, Skill-current recalculation, exact owned schema, v2 migration/backfill, rollback on injected failure, rerun equality, and no silent table addition.

- [ ] **Step 2: Run focused projection tests and verify RED**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~LocalWorkspaceProjectionSchemaTests|FullyQualifiedName~LocalWorkspaceProjectionBackfillTests|FullyQualifiedName~LocalWorkspaceSessionSnapshotContributorTests|FullyQualifiedName~RuntimeBackupLocalWorkspaceProjectionTests|FullyQualifiedName~LocalRepositoryRuntimeBackupTests"
```

Expected: failures identify missing v3 shape, fallback instants, and search facts.

- [ ] **Step 3: Implement exact v2-to-v3 migration and deterministic projection**

Use a nonthrowing C# timestamp parser registered as a SQLite scalar function for backfill/refresh. Select `started_at`, then `created_at`, then `last_seen_at`; convert the first valid instant with `ToUnixTimeMilliseconds`. Canonicalize valid timing/last-seen strings with UTC `O` formatting. Rebuild changed tables atomically, create search-fact indexes, backfill, update stamp last, and validate exact objects and rows.

- [ ] **Step 4: Populate and expire closed search facts**

Populate retained label facts, #154-current Skill names, and exact Tool names with their exact source authority/lifetime. Add the smallest transaction notification needed so Skill-current changes refresh affected Sessions. Retention deletion and source loss remove or make facts ineligible in the same transaction. Never persist body/path/input/result text.

- [ ] **Step 5: Update backup/restore v3 validation**

Declare v3 current, accept exact v2 only for staging migration, preserve all v3 tables, validate timestamp/epoch and source-lifetime invariants, rerun deterministically, and keep the fixed component order.

- [ ] **Step 6: Run focused projection/backup tests and verify GREEN**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~LocalWorkspaceProjectionSchemaTests|FullyQualifiedName~LocalWorkspaceProjectionBackfillTests|FullyQualifiedName~LocalWorkspaceSessionSnapshotContributorTests|FullyQualifiedName~RuntimeBackupLocalWorkspaceProjectionTests|FullyQualifiedName~LocalRepositoryRuntimeBackupTests|FullyQualifiedName~RuntimeBackupRestoreTests"
```

Expected: all Task 2 tests pass with fixed, set-based statement counts.

- [ ] **Step 7: Self-review and commit**

```powershell
git add src/CopilotAgentObservability.Persistence.Sqlite docs/specifications/interfaces/runtime-backup-restore.md tests/CopilotAgentObservability.LocalMonitor.Tests
git commit -m "Issue #134: feat(projection): migrate collection facts to v3" -m "Session ordering and q search previously depended on started-at and label-only runtime logic. Persist the accepted instant and authority-bound normalized facts so collection reads remain safe, current, and retention-aware."
```

### Task 3: Complete Session and Repository collection runtime

**Files:**
- Create: `src/CopilotAgentObservability.LocalMonitor/LocalMonitorV1/LocalMonitorV1RepositoryCursor.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/LocalMonitorV1/LocalMonitorV1CollectionApplication.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/LocalMonitorV1/LocalMonitorV1CollectionRoutes.cs`
- Modify tests: `LocalMonitorV1CollectionApplicationTests.cs`, `LocalMonitorV1CollectionRouteTests.cs`, `LocalMonitorV1SessionCursorTests.cs`, `LocalRepositoryScopeSnapshotTests.cs`
- Add focused Repository cursor tests if separation improves clarity.

**Interfaces:**
- Consumes: Task 1 Repository wire/cursor contract and Task 2 projection row/search facts.
- Produces: `LocalMonitorV1RepositoryCursorCodec.Encode/TryDecode`, stable Repository ID keyset paging, fallback-basis Session date filtering, three-source q OR search, and instant-safe `last_observed_at`.

- [ ] **Step 1: Add failing collection behavior tests**

Cover each timestamp fallback, malformed/all-missing ordering, mixed-offset order, date filtering and cursor pages without duplicate/drop, q hit for label/Skill/Tool, non-hit for Skill body/Tool body/path/prompt, stale/invalid/expired facts, Repository cursor tamper/filter mismatch/malformed/equal names/rename, and canonical Repository headers/bytes.

- [ ] **Step 2: Add failing 10,000-Session and query-plan tests**

Build 10,000 synthetic Sessions, combine q plus date/source/status/activity filters, page 200 rows twice, assert no overlap/drop at the boundary, assert the fixed statement count, and inspect `EXPLAIN QUERY PLAN` for the owned projection indexes without asserting SQLite-internal wording beyond index use and absence of per-row queries.

- [ ] **Step 3: Run collection tests and verify RED**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~LocalMonitorV1CollectionApplicationTests|FullyQualifiedName~LocalMonitorV1CollectionRouteTests|FullyQualifiedName~LocalMonitorV1SessionCursorTests|FullyQualifiedName~LocalMonitorV1RepositoryCursor|FullyQualifiedName~LocalRepositoryScopeSnapshotTests"
```

Expected: failures identify raw Repository cursors, label-only q, started-only date filtering, and lexical latest-observed comparison.

- [ ] **Step 4: Implement Repository opaque cursor and stable keyset**

Generate an independent random 32-byte Repository key at startup. Decode/validate `after` after closed query parsing so cursor defects map to `invalid_cursor`. Bind archive scope and effective limit, use constant-time HMAC verification, order/resume by `repository_id ASC`, and emit a cursor only after `limit+1` proves another row.

- [ ] **Step 5: Implement Session matching and Repository latest instant**

Compare `from`/`to` to the accepted epoch milliseconds. Match q against projected `SearchTexts` only. Compute each Repository's last observed value from exact assigned, effectively eligible Sessions with valid stored last-seen instants, choose the greatest instant, and emit canonical UTC. Pre-group Sessions once before serializing cards; do not repeatedly scan the full Session set per card.

- [ ] **Step 6: Run focused collection, raw-default, sanitized-only, and frozen regression tests and verify GREEN**

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~LocalMonitorV1Collection|FullyQualifiedName~LocalRepositoryScopeSnapshotTests|FullyQualifiedName~SanitizedOnly|FullyQualifiedName~Frozen"
```

Expected: all focused tests pass; exact response bytes and headers remain stable.

- [ ] **Step 7: Run affected-project build/tests, self-review, and commit**

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj
git add src/CopilotAgentObservability.LocalMonitor src/CopilotAgentObservability.Persistence.Sqlite tests/CopilotAgentObservability.LocalMonitor.Tests
git commit -m "Issue #134: fix(collection): complete repository and session semantics" -m "Collection paging and filtering previously exposed raw Repository IDs, compared timestamps lexically, and searched labels only. Bind opaque cursors and consume projection-owned instant/search facts so pages remain stable and authority-correct."
```

### Task 4: Final validation and branch review

**Files:**
- No planned production changes; fixes from review must be delegated as one bounded fix wave.

**Interfaces:**
- Consumes the complete branch from Tasks 1-3.
- Produces final review evidence and the exact command/exit-code report.

- [ ] **Step 1: Run the repository validation suite in order**

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Expected: every command exits 0 on the final HEAD. Record all prior failures, hangs, and flakes even if a later rerun succeeds.

- [ ] **Step 2: Verify history and scope**

```powershell
git status --short --branch
git log --oneline origin/main..HEAD
git diff --check origin/main..HEAD
```

Expected: clean working tree, only Issue #134 collection-milestone commits, no push/PR/Issue writes.
