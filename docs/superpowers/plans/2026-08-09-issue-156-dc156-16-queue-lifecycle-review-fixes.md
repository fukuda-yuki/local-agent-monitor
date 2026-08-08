# Issue #156 DC156-16 Queue Lifecycle Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the two Important DC156-16 whole-Issue review findings by making the reconciliation-state singleton authoritative and making the renewable queue heartbeat progress through its own SQLite write contention without weakening expiry or publication fences.

**Architecture:** Keep `local_repository_catalog:1`, the single monitor-span discovery path, and every public/frozen contract unchanged. Fresh catalog installation creates the one authoritative projector-state row with a nullable initial frontier; all later discovery, validation, and restore paths require that row and fail closed if it is missing or corrupt. The worker keeps the existing typed SQLite `Busy` heartbeat outcome, treats it as transient only before the locally held exact queue lease expires, and relies on the unchanged exact-token, Retention, Session-snapshot, and in-transaction publication checks as the final authority.

**Tech Stack:** C# 14, .NET 10, Microsoft.Data.Sqlite, xUnit, `TimeProvider`, SQLite immediate/read transactions

## Global Constraints

- `docs/specifications/interfaces/local-repository-catalog-executable.md` DC156-16 is the accepted design authority; this repair changes no product/public/security specification.
- `local_repository_reconciliation_state` has exactly one row whose `projector_key` is `local-repository-catalog-v1`; its initial `last_discovered_span_id` is SQL `NULL`.
- Fresh installation seeds that row atomically with the other `local_repository_catalog:1` objects. Keep component version `1`; add no migration, compatibility reader, fallback cursor, repair-on-open path, or dual schema.
- Use the canonical non-domain initialization timestamp `1970-01-01T00:00:00.0000000+00:00`. It is not exposed as a product event.
- A missing, duplicate, malformed, or wrong-key projector state fails closed. Discovery must not read raw input, enqueue work, advance/recreate the cursor, or fabricate cursor zero after authority loss.
- Queue lease duration remains exactly 30 seconds and heartbeat period remains 10 seconds. Renewal remains permitted only before expiry with the exact current unexpired token.
- A typed SQLite `Busy` heartbeat is transient only while trusted time is strictly earlier than the locally held queue lease expiry. At or after expiry, or on `StaleOwner`/`Corrupt`/other non-applied authority loss, cancel/fence processing.
- Preserve the final in-transaction queue-token, Retention operation-lease, Session snapshot, digest, and atomic domain-publication checks. Do not turn `Busy` into success or extend an expired lease.
- Use real SQLite and deterministic barriers/trusted time for concurrency tests. Do not use `Thread.Sleep`, retry loops that hide a race, mock-only assertions, or tests that only grep source text.
- Preserve frozen `/api/monitor/*`, `/api/session-workspace/*`, and SSE bytes; sanitized-only absence; exact opaque identity; no heuristic joins; no missing-to-zero; and unresolved Issue #152.
- Do not add a route, DTO, background worker, cursor, queue writer, scan-on-read path, raw fallback lookup, or compatibility shim.
- Do not commit raw payloads containing user data, prompts/responses, local paths, credentials, PII, runtime databases, TRX files, or Playwright artifacts.

---

### Task 1: Restore exact projector-state and renewable-lease authority

**Files:**
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/LocalRepositoryCatalogSchemaV1.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/LocalRepositoryCatalogValidation.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/SqliteLocalRepositoryReconciliationStore.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/SqliteLocalRepositoryReconciliationStore.Restore.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Repositories/LocalRepositoryReconciliationWorker.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalRepositoryCatalogSchemaTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalRepositoryReconciliationQueueTests.cs`
- Test: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalRepositoryReconciliationTests.cs`
- Test utility/fixture as required: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalRepositoryAutomaticAdmissionTests.cs`
- Fixture fallout: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalRepositoryAutomaticAdmissionValidationTests.cs`
- Fixture fallout: `tests/CopilotAgentObservability.LocalMonitor.Tests/LocalRepositoryRuntimeBackupTests.cs`
- Fixture fallout: `tests/CopilotAgentObservability.LocalMonitor.Tests/RuntimeBackupWave3ComponentRoundTripTests.cs`
- Fixture fallout: `tests/CopilotAgentObservability.LocalMonitor.Tests/RuntimeBackupRestoreTests.cs`
- Include: `docs/superpowers/plans/2026-08-09-issue-156-dc156-16-queue-lifecycle-review-fixes.md`

**Interfaces:**
- Consumes: `LocalRepositoryCatalogSchemaV1.Ensure`, `LocalRepositoryCatalogValidation.ValidateRows`, `SqliteLocalRepositoryReconciliationStore.DiscoverAsync`, `SqliteLocalRepositoryReconciliationStore.ValidateRestorableState`, `SqliteLocalRepositoryReconciliationStore.Heartbeat`, `LocalRepositoryQueueLease.LeaseExpiresAt`, `ILocalRepositoryReconciliationCheckpoint`, and the production `SqliteLocalRepositoryCatalogStore.ProcessAsync` immediate transaction.
- Produces: one seeded/validated projector-state authority, fail-closed discovery/restore behavior, and a worker heartbeat that distinguishes transient self-contention from actual lease expiry/authority loss.
- Does not produce: any public API/schema-version change, new queue state/reason, second writer, or compatibility path.

- [ ] **Step 1: Preserve the untouched focused baseline evidence**

The coordinator already ran this exact command on clean base `f53555542f14293b4fe6d6ad48e282bb91ac108f`:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-restore --filter FullyQualifiedName~LocalRepositoryReconciliationQueueTests
```

Recorded result: exit `0`; 79 passed, 0 failed, 0 skipped. Do not overwrite or reinterpret this as RED evidence.

- [ ] **Step 2: Write projector-state RED tests before production edits**

Add focused tests that exercise real schema/SQLite behavior:

```csharp
[Fact]
public void FreshSchemaSeedsExactlyOneNullableProjectorStateAndRepeatEnsurePreservesIt()
{
    // Create monitor + Session prerequisites, call Ensure once and again.
    // Assert the literal row:
    // ("local-repository-catalog-v1", NULL,
    //  "1970-01-01T00:00:00.0000000+00:00")
    // Assert COUNT(*) remains exactly 1.
}

[Fact]
public async Task DiscoveryMissingProjectorStateFailsClosedWithoutQueueCursorOrRawPublication()
{
    // Advance a real frontier, delete the singleton under the test's corruption
    // setup, add a later monitor span, then run DiscoverAsync.
    // Assert Corrupt, no new queue row, no recreated state row, and no new
    // Retention operation/publication effect. Use a typed checkpoint if needed
    // to prove raw discovery was never entered.
}

[Fact]
public void RestoreRejectsMissingProjectorStateEvenWhenQueueIsEmpty()
{
    // Delete the singleton from an otherwise current catalog and assert the
    // existing local_repository_reconciliation_restore_invalid failure.
}
```

Also change existing rollback/cancellation expectations from zero projector-state rows to one fixed-key row with a `NULL` frontier. Do not change queue/domain rollback expectations.

Run the narrow RED set:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter 'FullyQualifiedName~LocalRepositoryCatalogSchemaTests|FullyQualifiedName~LocalRepositoryReconciliationQueueTests'
```

Expected RED: the fresh-schema test observes zero rows, missing-state discovery recreates/defaults instead of failing at the authority boundary, and/or restore accepts the absent empty state. Capture exact failing test names and assertions in the task report.

- [ ] **Step 3: Implement the minimum singleton-state authority**

During fresh installation, after creating owned objects/triggers and before committing the component declaration, insert the one row in the same transaction:

```sql
INSERT INTO local_repository_reconciliation_state(
    projector_key,
    last_discovered_span_id,
    updated_at)
VALUES(
    'local-repository-catalog-v1',
    NULL,
    '1970-01-01T00:00:00.0000000+00:00');
```

In `LocalRepositoryCatalogValidation`, add one state-authority validator that reads at most two rows and requires:

```text
exactly one row
projector_key == "local-repository-catalog-v1"
last_discovered_span_id == NULL or positive Int64
updated_at is a canonical timestamp
```

Call it from current row validation. Absence is invalid even with an empty queue.

In discovery, replace the `long cursor = 0`/`ExecuteScalar` fallback with an exact-row reader. Map absent/malformed authority to the existing `Corrupt` outcome before iterating raw IDs. Interpret only the present row's SQL `NULL` frontier as the initial numeric scan boundary.

Replace cursor publication's `INSERT ... ON CONFLICT DO UPDATE` repair behavior with an exact-key `UPDATE` whose affected-row count must be exactly one:

```sql
UPDATE local_repository_reconciliation_state
SET last_discovered_span_id=$last_discovered_span_id,
    updated_at=$updated_at
WHERE projector_key=$projector_key;
```

If the update affects anything other than one row, roll back and return `Corrupt`; never recreate the authority.

In restore validation, delete the branch that accepts `projectorKey is null` when `queueCount == 0`. A present `NULL` frontier remains valid only with an empty queue.

Keep schema version and table DDL unchanged. Do not seed or repair a row on the validation/reopen path.

- [ ] **Step 4: Repair seed-aware fixtures and verify projector-state GREEN**

Schema initialization now owns the row. Convert test helper/direct state INSERTs that intentionally set a frontier into exact-key `UPDATE` or `INSERT ... ON CONFLICT(projector_key) DO UPDATE`, in these known owners:

```text
LocalRepositoryAutomaticAdmissionValidationTests.cs
LocalRepositoryReconciliationQueueTests.cs
LocalRepositoryRuntimeBackupTests.cs
RuntimeBackupWave3ComponentRoundTripTests.cs
RuntimeBackupRestoreTests.cs
```

Do not make production validation permissive to preserve old fixtures.

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter 'FullyQualifiedName~LocalRepositoryCatalogSchemaTests|FullyQualifiedName~LocalRepositoryReconciliationQueueTests|FullyQualifiedName~LocalRepositoryAutomaticAdmissionValidationTests|FullyQualifiedName~LocalRepositoryRuntimeBackupTests|FullyQualifiedName~RuntimeBackupWave3ComponentRoundTripTests|FullyQualifiedName~RuntimeBackupRestoreTests'
```

Expected GREEN: zero failures. Record exact counts and exit code.

- [ ] **Step 5: Write deterministic real-SQLite heartbeat RED tests**

Replace the existing sleep/range-accepting `HeartbeatFenceLossDuringPublicationCancelsAndReturnsPendingWithoutRows` coverage with deterministic production-processor cases. A bounded `ManualResetEventSlim`/task-completion barrier and an internal reconciliation checkpoint are acceptable; `Thread.Sleep` is not.

Test A must hold `SqliteLocalRepositoryCatalogStore.ProcessAsync` after its own `BEGIN IMMEDIATE` and domain inserts while the worker's first ten-second heartbeat attempts the second connection:

```csharp
// Arrange one exact raw record/session event and production catalog processor.
// At AfterContexts, advance trusted time by exactly 10 seconds and wait until
// the heartbeat reports its typed Busy outcome while the transaction is held.
// Release processing.
// Assert ProcessorInvoked, queue state completed, attempt_count 1, one domain
// graph, and no duplicate Repository/locator/context/history rows.
```

Test B must hold the same production transaction through exact queue expiry and exercise a competing worker:

```csharp
// At AfterContexts, advance trusted time to exactly LeaseExpiresAt and wait
// until the heartbeat fences/cancels processing.
// While the first transaction is still held, a competing RunOnce cannot steal
// or publish.
// Release the first transaction and assert all its domain writes rolled back,
// the expired token did not complete, and no Retention success was fabricated.
// Run recovery/second worker once; assert attempt_count 2, exactly one completed
// graph, and no duplicate/retry-loop publication.
```

If deterministic observation needs a checkpoint, extend the existing internal `LocalRepositoryReconciliationCheckpoint` with narrowly named heartbeat outcomes (for example, `AfterHeartbeatBusy` and `HeartbeatLeaseExpired`) and call them only at those boundaries. Do not expose a public or HTTP seam and do not assert merely that a mock/checkpoint was called; assertions must cover the real SQLite state graph.

Run the exact tests by fully qualified name. Expected RED on current production: Test A returns `Retrying`/`StaleOwner` with no graph because `Busy` cancels valid work. Test B must expose any expiry/steal/duplicate weakness without using wall-clock sleeps.

- [ ] **Step 6: Implement the minimum heartbeat distinction**

In `LocalRepositoryReconciliationWorker.HeartbeatAsync`, sample trusted time once per tick and apply this order:

```csharp
var at = timeProvider.GetUtcNow().ToUniversalTime();
if (at >= lease.LeaseExpiresAt)
{
    // Optional deterministic internal checkpoint.
    cancellation.Cancel();
    return false;
}

var renewed = queue.Heartbeat(lease, retentionLease, at);
if (renewed.Status == LocalRepositoryQueueTransitionResult.Busy)
{
    // Optional deterministic internal checkpoint.
    continue;
}

if (renewed.Status != LocalRepositoryQueueTransitionResult.Applied
    || renewed.Lease is null)
{
    cancellation.Cancel();
    return false;
}

lease = renewed.Lease;
```

Do not change `SqliteLocalRepositoryReconciliationStore.Heartbeat` transaction semantics or convert `Busy` to `Applied`. A successful renewal still updates the local lease expiry. A Busy tick retains the prior local expiry, so repeated contention is fenced exactly at that deadline. The processor's unchanged final transaction decides whether publication authority is still valid.

- [ ] **Step 7: Verify heartbeat GREEN and nearby queue/Retention regressions**

Run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter 'FullyQualifiedName~LocalRepositoryReconciliationTests|FullyQualifiedName~LocalRepositoryReconciliationQueueTests|FullyQualifiedName~LocalRepositoryAutomaticAdmissionTests|FullyQualifiedName~LocalRepositoryCatalogHostedServiceTests'
```

Expected: zero failures, no hangs, no wall-clock sleep dependency. Confirm existing direct heartbeat Busy, stale-token, Retention-fence, restart-recovery, waiting-session, and cancellation cases remain green.

- [ ] **Step 8: Run the complete affected gate**

Run exactly:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter 'FullyQualifiedName~LocalRepositoryCatalogSchemaTests|FullyQualifiedName~LocalRepositoryReconciliationQueueTests|FullyQualifiedName~LocalRepositoryReconciliationTests|FullyQualifiedName~LocalRepositoryAutomaticAdmissionTests|FullyQualifiedName~LocalRepositoryAutomaticAdmissionValidationTests|FullyQualifiedName~LocalRepositoryRuntimeBackupTests|FullyQualifiedName~RuntimeBackupWave3ComponentRoundTripTests|FullyQualifiedName~RuntimeBackupRestoreTests|FullyQualifiedName~LocalRepositoryCatalogHostedServiceTests'
dotnet build src\CopilotAgentObservability.Persistence.Sqlite\CopilotAgentObservability.Persistence.Sqlite.csproj --no-restore
dotnet build src\CopilotAgentObservability.LocalMonitor\CopilotAgentObservability.LocalMonitor.csproj --no-restore
git diff --check
```

All commands must exit `0`. Report exact counts, warnings/errors, and commands. Do not substitute a narrower run for a failed command.

- [ ] **Step 9: Self-review and create the local task commit**

Inspect every changed path and verify:

```text
one seed authority, no reopen repair
no cursor-zero fallback and no UPSERT recreation
missing authority fails before raw discovery
Busy is transient only before local expiry
actual expiry/token/Retention loss still cancels/fences
real-SQLite tests catch the former behavior and contain no Thread.Sleep
no public/frozen/sanitized/#152 change
no sensitive/generated artifact
```

Stage only the plan, owned production files, and directly affected tests. Commit locally with:

```powershell
git commit -m "Issue #156: fix(local-monitor): enforce reconciliation authority"
```

The commit body must explain that the exact singleton was previously absent/fallback and that a second immediate heartbeat transaction self-contended with production admission. Write the complete implementation/test/self-review report to the SDD task report path supplied by the coordinator.
