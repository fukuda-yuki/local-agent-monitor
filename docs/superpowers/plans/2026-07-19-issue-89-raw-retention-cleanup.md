# Issue #89 Raw Retention Cleanup Implementation Plan

> **For Codex:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> to execute this plan one task at a time. Every implementation task uses a
> fresh `gpt-5.6-terra`, `reasoning_effort: medium`, `fork_turns: none` agent;
> every review uses a different `gpt-5.6-sol`, `reasoning_effort: low`,
> `fork_turns: none` agent.

**Goal:** Implement the approved unified retention catalog and exact
store-specific cleanup adapters, including durable cleanup, restart recovery,
sanitized diagnostics, migration from real fixtures, and repository-safe
closeout evidence.

**Architecture:** One separately versioned retention component lives in the
same physical Local Monitor SQLite database and coordinates a closed registry
of store-specific adapters. Every raw read is positively catalog-gated. SQLite
source mutation and catalog state share a transaction; filesystem capture and
deletion use durable exact-member journals and forward-only recovery.

**Tech Stack:** .NET 10, ASP.NET Core minimal routes, Microsoft.Data.Sqlite,
xUnit, Playwright through the repository wrapper.

**Immutable evidence:** `kickoff_sha` and `inventory_base_sha` are both
`11d6c587903f6ea97026d815f608231efea08d65`. The approved design commit is
`24967f3`.

## Global contract and finite v1 pins

- catalog schema and adapter coverage version: `1`;
- lifecycle: `expiring`, `retained_by_policy`,
  `expired_pending_deletion`, `deletion_queued`, `deleting`, `deleted`,
  `deletion_failed`;
- policies: `raw-default-90d` v1 (90 days) and `sensitive-bundle-7d` v1
  (7 days);
- expiry scan item limit: 100; claim batch limit: 100; scan elapsed budget:
  30 seconds; queue/claim order: `expires_at ASC, item_id ASC`; worker wake
  interval: 15 seconds; maximum active deletion workers: 2;
- access, operation, and deletion lease duration: 2 minutes;
- deletion-lease renewal deadline: no later than 1 minute after acquisition or
  prior renewal; active-operation quiescence bound: 2 minutes; shutdown/drain
  bound: 2 minutes; WAL-maintenance retry delay: 1 minute;
- maximum delete attempts: 5; retry eligibility after failures 1 through 4:
  1 minute, 5 minutes, 30 minutes, and 2 hours; failure 5 is terminal;
- file item maximum: 256 exact members and 128 MiB total journaled bytes;
  both limits apply independently to Sensitive Bundles and SDK directories;
- status item-summary maximum: 100, ordered by expiry then opaque item ID;
- fixed diagnostic codes:
  `retention_migration_blocked`, `retention_missing_timestamp`,
  `retention_invalid_identity`, `retention_ownership_mismatch`,
  `retention_capture_incomplete`, `retention_lease_conflict`,
  `retention_lease_lost`, `retention_delete_busy`,
  `retention_delete_permission_denied`, `retention_delete_io_failed`,
  `retention_unexpected_source_missing`, `retention_retry_exhausted`, and
  `retention_maintenance_busy`, `retention_adapter_coverage_mismatch`, and
  `retention_item_limit_exceeded`; Config CLI catalog open/validation uses the
  separate fixed public code `retention_catalog_unavailable`, and its sensitive
  input/output boundaries use `measurements_input_unavailable`,
  `measurements_input_invalid`, `raw_input_unavailable`, `raw_input_invalid`,
  and `diagnosis_output_unavailable` without a path or provider message.

No diagnostic supplements a code with exception text, raw values, source IDs,
private locators, local paths, credentials, secrets, or PII. `not_captured` and
`mixed` are aggregate-only and are never persisted item states.
`retention_adapter_coverage_mismatch` is worker-level only and never appears in
an item summary. `retention_item_limit_exceeded` is terminal/non-retryable.
On attempt five, the item retains the corresponding transient `error_code` and
sets `retry_exhausted=true`; `retention_retry_exhausted` is emitted only as the
fixed structured worker event for that boolean transition and aggregate count,
never persisted as the item's replacement error code.

The unique catalog ownership key is exactly
`(store_instance_id, store_kind, source_item_id)`. Item IDs are opaque and
stable after commit; replay/backfill upserts by the ownership key and neither
duplicates nor replaces an existing ID. Capture phases are exactly `reserved`,
`staging`, `published_pending_catalog`, and `complete`. Inventory categories are
exactly `required_cleanup`, `retained_by_policy`, `not_applicable`, and
`blocked`.

Store policies are fixed: Session content, raw records, analysis raw, and SDK
directories use `raw-default-90d` v1; Sensitive Bundles use
`sensitive-bundle-7d` v1. Existing Session timestamps remain byte/value exact.

Error disposition is fixed:

| Condition | Attempt | Retry | Item result |
| --- | --- | --- | --- |
| invalid identity, ownership mismatch, or unexpected absence detected before intent | zero | no | terminal `deletion_failed` with exact integrity code and null `retry_at` |
| member/byte limit detected before intent | zero | no | terminal `deletion_failed` or terminal capture blocker with `retention_item_limit_exceeded`, null `retry_at` |
| the same integrity condition discovered while recovering an existing intent | zero additional; the intent already consumed one | no | terminal `deletion_failed` with exact integrity code and null `retry_at` |
| transient SQLite busy, I/O, or permission failure after intent | consume once with that intent | yes through attempt 4 | scheduled `deletion_failed`; attempt 5 terminal |
| claim contention, lease conflict/loss before intent, renewal conflict, pre-intent cancellation | zero | re-scan only | queued or guarded queued transition |
| maintenance busy/failure | zero | after 1 minute | no item lifecycle change |

Every adapter theory uses these columns: `durable_phase_or_cursor`,
`failure_point`, `source_mutation`, `catalog_mutation`, `resulting_lifecycle`,
`error_code`, `attempt_delta`, `retry_at`, `terminal`, and `restart_action`.
The required rows are:

| Phase/failure | Source/catalog result | Lifecycle/code | Attempt/retry/terminal | Restart action |
| --- | --- | --- | --- | --- |
| pre-intent invalid identity | none / fixed failure committed | `deletion_failed` / `retention_invalid_identity` | +0 / null / yes | report only |
| pre-intent ownership mismatch | none / fixed failure committed | `deletion_failed` / `retention_ownership_mismatch` | +0 / null / yes | report only |
| pre-intent source absent | none / fixed failure committed | `deletion_failed` / `retention_unexpected_source_missing` | +0 / null / yes | report only |
| intent commit | none / intent+cursor committed | `deleting` / null | +1 / null / no | resume same cursor |
| process loss after intent, before source mutation | none / intent retained | `deleting` / null | +0 additional / null / no | reclaim expired lease, resume |
| failure after SQLite source mutation, before receipt | transaction rollback / intent cursor retained plus failure metadata | `deletion_failed` / transient fixed code | +0 additional / pinned retry / no | requeue same intent at `retry_at` |
| failure writing SQLite receipt/state | transaction rollback / intent cursor retained plus failure metadata | `deletion_failed` / transient fixed code | +0 additional / pinned retry / no | requeue same intent at `retry_at` |
| file member absent after matching intent and prior proof | already absent / cursor advances only | `deleting` / null | +0 additional / null / no | continue next cursor |
| transient busy/I/O/permission after intent | no unjournaled mutation / failure metadata | `deletion_failed` / corresponding fixed code | +0 additional / pinned schedule / attempt 5 only | requeue at `retry_at` |
| stale revision/owner/lease generation | none / none | unchanged / `retention_lease_lost` | +0 / null / no | current owner continues |
| successful final verification/receipt | exact source absent / receipt+tombstone | `deleted` / null | +0 additional / null / yes | no work |

The table above is the common deletion disposition; capture theories never bind
to deletion rows. Exact persisted matrices are:

**Sensitive Bundle capture (`SensitiveBundleCaptureFailureMatrix`):** cursor is
`phase/member_index`, with
`reserved/0`, `staging/0..member_count`,
`published_pending_catalog/member_count`, and `complete/member_count`.
Intent-commit concepts do not apply. Reservation-commit failure leaves no
journal/target and returns `retention_catalog_unavailable`. Loss in `reserved`
removes/finalizes nothing. Loss/failure in `staging/n` preserves the journal,
removes only exact verified unpublished members/child on recovery, and returns
`retention_capture_incomplete`. Loss in `published_pending_catalog/n` finalizes
only the exact identity/marker/manifest-matching published child; mismatch is
terminal `retention_ownership_mismatch`. Loss after catalog completion observes
`complete/n` and does no capture recovery. Bundle limit rejection during staging
removes exact owned unpublished staging, leaves no item, and returns terminal
`retention_item_limit_exceeded`. Every row
records source/catalog mutation, null `retry_at`, terminal flag, and exact
restart action.

Process loss after the exact atomic child publish but before committing
`published_pending_catalog` leaves `staging/member_count`. Recovery opens only
the journaled target and, after exact file identity, owner-marker, manifest, and
member-digest verification, advances to
`published_pending_catalog/member_count`; mismatch preserves the child and
terminally records `retention_ownership_mismatch`.

**SDK capture (`SdkDirectoryCaptureFailureMatrix`):** cursors are `reserved/0`,
`staging/owner_marker`, and `complete/0`. Exclusive empty-child creation plus
durable owner marker advances directly to complete before SDK start. SDK members
are created only afterward under the operation lease; there is no member cursor,
publish rename, or `published_pending_catalog` SDK capture phase. Loss in
reserved does nothing; loss in staging removes only the exact verified empty
owned child; loss after complete leaves the owned child for the active/recovered
operation lifecycle.

**SQLite deletion (`SqliteAdapterFailureMatrix`, expanded for Session, raw, and
analysis):** cursor is `source_delete/0` before intent and `source_delete/1`
only in the atomic deleted receipt. Intent-commit failure rolls back attempt and
intent, then guardedly requeues with attempt delta 0. After committed intent,
source deletion plus absence verification plus receipt/tombstone is one
transaction; any injected source/receipt/cursor failure rolls it all back,
leaves `deleting` with `source_delete/0`, and adds no attempt. Process loss after
rollback but before separately persisted failure metadata leaves that same
state; expired-lease recovery retries the same intent. If failure metadata
commits, it is `deletion_failed` with the exact transient code and pinned
`retry_at`. Success atomically reaches `deleted/source_delete/1`.

**Sensitive Bundle deletion (`SensitiveBundleDeleteFailureMatrix`):** cursors
are `member/0..N`, `marker/N+1`, `directory/N+2`, `receipt/N+3`. Each member
unlink occurs only after intent; cursor commits after verified absence. Loss or
cursor-commit failure after unlink leaves the prior cursor, and matching intent
plus prior proof advances it idempotently. Apply the same rule to marker and
final-directory deletion. Unexpected/replaced members fail terminally without
unlink. Limit rejection before intent is +0; after an existing intent it is +0
additional, terminal `retention_item_limit_exceeded`, preserving all remaining
members.

**SDK deletion (`SdkDirectoryDeleteFailureMatrix`):** uses the identical cursor
domain and crash rules as Bundle deletion after operation-lease quiescence;
member identities come from the durable SDK enumeration journal. Parent and
siblings are never cursor values or mutation targets.

Before `member/0`, SDK cleanup persists `enumeration/0` with no destructive
intent and attempt delta 0. Loss or enumeration/journal-commit failure uses the
guarded queued transition and retries enumeration with no error/attempt. Atomic
exact-member journal commit moves to `member/0`; only then can destructive
intent commit and consume one attempt. Member/byte overflow at enumeration is
terminal `retention_item_limit_exceeded`, null `retry_at`, leaves all files
untouched, and never reaches `member/0`.

Each matrix theory row includes phase/cursor, injected failure, source state,
catalog state, lifecycle, exact code, attempt delta, `retry_at`, terminal flag,
and restart action. Capture rows bind only to capture theories and deletion rows
only to their named deletion theory.

`IRetentionDeletionAdapter` and the closed registry are produced by 89-B before
the worker. The exact shared contract is:

```csharp
public interface IRetentionDeletionAdapter
{
    RetentionStoreKind StoreKind { get; }
    ValueTask<RetentionAdapterResult> DeleteAsync(
        RetentionDeleteContext context);
}

public sealed record RetentionDeleteContext(
    string ItemId,
    string StoreInstanceId,
    RetentionStoreKind StoreKind,
    long ExpectedRevision,
    string LeaseOwner,
    long LeaseGeneration,
    RetentionSourceIdentity SourceIdentity,
    RetentionPrivateLocatorHandle? PrivateLocator,
    int IntentCursor,
    CancellationToken CancellationToken);

public sealed record RetentionOwnershipKey(
    string StoreInstanceId,
    RetentionStoreKind StoreKind,
    string SourceItemId);

public sealed record RetentionSourceIdentity(
    string SourceItemId,
    string OwnershipReceipt);

public sealed record RetentionPrivateLocatorHandle(string OpaqueHandle);
```

These tokens use bounded identifier grammars and are never rendered in public
diagnostics except the catalog-generated opaque item ID explicitly allowlisted
for status. `OwnershipReceipt` is an immutable product-issued token/digest, not
a path, trace, timestamp, repository, or workspace value.

For both file adapters, `RetentionFileIdentity` is captured from an open handle:
Windows volume serial plus file index, or Unix device plus inode. It is compared
again from a non-following handle immediately before every mutation. The
exclusive owner-marker token and its digest, the handle identity, and the exact
member manifest digest must all match; path equality alone never authorizes a
mutation. Reparse points/symlinks are never followed. The 256-member limit
counts the marker and manifest; total bytes count every regular member.

Every destructive/result method compares item ID, revision, lease owner, and
lease generation. The registry rejects missing, duplicate, or unknown kinds and
must contain exactly one adapter for each of the five kinds at coverage v1.

## Task 89-A: Canonical contract, inventory, and compatibility

**Files:**

- Modify: `docs/requirements.md`
- Modify: `docs/spec.md`
- Modify: `docs/specifications/layers/raw-store-normalization.md`
- Modify: `docs/specifications/layers/telemetry-ingestion.md`
- Modify: `docs/specifications/interfaces/canvas-session-workspace.md`
- Modify: `docs/specifications/interfaces/config-cli.md`
- Modify: `docs/specifications/security-data-boundaries.md`
- Modify: `docs/architecture.md`
- Modify: `docs/decisions.md`
- Modify: `docs/task.md`
- Create: `docs/sprints/issue-89-raw-read-callsite-inventory.md`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/RetentionCatalogContracts.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionContractTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionCompatibilityContractTests.cs`

### Step 1: Write failing contract tests

RED methods: `CatalogDomains_MatchRetentionV1`,
`SessionV1Projection_CoversEveryCanonicalCondition`,
`ExpiredContentRoute_PreservesExactUtf8Bytes`, and
`RetentionDtos_ExposeOnlyAllowlistedFields`. Expected RED: missing retention
types/spec pins; the existing route-byte test remains GREEN as a compatibility
control.

Pin the seven states, the closed store-kind registry (`session_event_content`,
`raw_record`, `analysis_run_raw`, `sensitive_bundle`, `analysis_sdk_directory`),
the finite v1 policy values, fixed error-code allowlist, and the frozen Session
v1 projection. Verify the existing expired route body remains exactly:

```csharp
"{\"error\":\"raw_content_expired\",\"content_state\":\"expired_pending_deletion\"}"u8
```

It has no BOM and no trailing newline. Compare raw UTF-8 bytes; do not
deserialize this assertion. Use this normative table:

| Canonical condition | event `content_state` | Session `raw_retention_state` | Raw route |
| --- | --- | --- | --- |
| never captured | existing `not_captured`/`redacted`/`unsupported` | `not_captured` if no event was ever captured | 404, `application/json`, `{"error":"session_event_content_not_found"}` UTF-8, no BOM/newline |
| readable `expiring` | `available` | `expiring` | 200, existing content DTO/serialization unchanged |
| readable `retained_by_policy` | `available` | `expiring` | 200, existing content DTO/serialization unchanged |
| each of `expired_pending_deletion`, `deletion_queued`, `deleting`, `deletion_failed`, `deleted` | `expired_pending_deletion` | `expired_pending_deletion` if no readable sibling | 410, `application/json`, exact expired bytes above |
| stale revision, missing without receipt, or repair-blocked after capture | `expired_pending_deletion` | `expired_pending_deletion` if no readable sibling | exact 410 response above |
| readable sibling plus any denied sibling | per-event exact state | `expiring` | selected event's own 200 or 410 behavior |
| no readable sibling but any ever-captured/tombstoned item | `expired_pending_deletion` | `expired_pending_deletion` | exact 410 for captured event |
| unknown Session/event | no DTO | no DTO | 404, `application/json`, exact not-found bytes above |
| `--sanitized-only` | existing metadata behavior | existing metadata behavior | raw route is unregistered; same-host fallback is 404 `application/json` exact UTF-8 `{"accepted":false,"error":"unsupported_endpoint","message":"Only /v1/traces is supported."}`, no BOM/newline and no Cache-Control/ETag/Last-Modified |

Bind every row to `SessionV1Projection_CoversEveryCanonicalCondition`; bind
every byte-bearing 404/410 row to `ExpiredContentRoute_PreservesExactUtf8Bytes`.
The existing
Session v1 enum, property names, status codes, response bytes, and serialization
remain unchanged.

Verify retention DTO reflection has no raw, secret, PII, source-identity,
locator, exception-message, or path-bearing properties.

### Step 2: Run the RED test

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SessionWorkspaceRouteTests.RawContent_ReturnsExpiredContract
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~Retention
```

Expected: the first pre-change compatibility control is GREEN; the second is RED
because retention contracts do not exist.

### Step 3: Update the current sources of truth first

Replace the expiry-only/Issue-#57 deferral text with the approved Issue #89
contract. Specify exact inventory, timestamp authorities, catalog-gated reads,
irreversible denial, migration, leases, queue/retry/recovery, physical cleanup
boundary, additive retention v1 routes, frozen Session v1 mapping, no-leak
rules, and `retention-closeout-corpus-v1`. Keep pin/unpin/delete-now assigned to
Issue #90.

Create a checked-in baseline inventory table naming every raw HTTP read, monitor
raw projection load, analysis tool-data load, active analysis operation, bundle
read/resume/enumeration path, and SDK read/write path. Each row names its current
production callsite and the required catalog gate/access-or-operation lease.

For Sensitive Bundles, add the Oracle-resolved interface:

```text
--include-sensitive-content requires --raw and --retention-database.
--retention-database is the only catalog binding and is not inferred from --raw.
--sensitive-output-dir is an optional parent only.
```

Reject `--retention-database` without sensitive mode and repeated values. Open
and validate an existing Local Monitor database before raw reads or any output;
never create/discover/fallback. Keep `--sensitive-output-dir` accepted but inert
when sensitive mode is absent.

### Step 4: Add minimal persistence-neutral contracts

Create the typed enums/records needed by the tests in
`src/CopilotAgentObservability.Persistence.Sqlite/Retention/RetentionCatalogContracts.cs`.
Do not change the frozen Session v1 enum.

### Step 5: Run focused tests, self-review, and commit

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~Retention
git diff --check
```

Commit: `Issue #89: docs: define retention catalog v1 contract`

## Task 89-B: Catalog persistence, migration, and authoritative read denial

**Files:**

- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/RetentionCatalogStore.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/RetentionSchemaMigrator.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/IRetentionDeletionAdapter.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/RetentionAdapterRegistry.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/RetentionReadGate.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/RetentionCatalogContracts.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionStore.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Analysis/SqliteMonitorAnalysisStore.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Sessions/SessionRoutes.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.Overview.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/RawTelemetry/RawNormalizationInputReader.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/DashboardDataset/DashboardRawOperationReader.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/DiagnosisCandidates/RawEvidenceReader.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Cli/CliApplication.cs`
- Modify: every additional production callsite enumerated in
  `docs/sprints/issue-89-raw-read-callsite-inventory.md`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionCatalogStoreTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionCatalogMigrationFixtureTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionLifecycleIntegrationTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionNoLeakTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/retention/retention-catalog-v1.sqlite`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/retention/manifest.json`

### Step 1: Write failing migration and atomicity tests

RED methods: `CreateSchema_BackfillsEachHistoricalFixtureAndSurvivesTwoFreshHosts`,
`CreateSchema_InvalidLegacyAuthorityRollsBackEverything`,
`Capture_FailureAtEachStatementRestoresExactPreState`,
`RetentionCatalogReadGate_DeniesAtExpiryBeforePromotion`, and
`RetentionCatalogReadGate_RejectsEveryInventoriedBypass`. Expected RED: no catalog schema,
ownership backfill, or read gate exists.

Cover all committed Session and Monitor fixtures, two fresh restarts, immutable
database `store_instance_id`, exact timestamp copying, opaque stable item IDs
idempotently keyed by the exact ownership tuple,
idempotent backfill, schema-newer refusal, and injected-statement rollback.
Missing/invalid identity or timestamp must roll back the entire retention
migration and return only `retention_migration_blocked`.

Cover atomic capture for Session content, raw records, and analysis raw fields.
At expiry, `read_denied_at` and durable backlog must commit before reads return
denied. Restart, stale revisions, clock rollback, retry, and missing source must
never restore readability.

For each SQLite capture path, inject failure before source mutation, after source
mutation/before catalog registration, and after registration/before commit;
reopen and prove exact pre-transaction source/catalog/schema state. Prove stale
Session/monitor/analysis writes affect zero rows and cannot repopulate raw
fields after denial/deletion. Prove no lease begins when `now >= expires_at`
even before the asynchronous scanner transitions the item.

### Step 2: Run RED tests

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionCatalog
```

### Step 3: Implement the separate catalog component

Use an immutable `retention_component_versions` row and closed tables for items,
tombstones/receipts, durable queue/retry state, capture journals, access and
operation leases, deletion leases, delete-step journals, and adapter coverage.
Expose methods that accept an existing SQLite connection/transaction so source
capture/deletion and catalog mutation can be one transaction. `MonitorHost` is
the one startup composition point; existing stores must not independently
compete to backfill.

Create the deletion contracts and registry exactly as pinned globally. Add a
coverage test for one and only one adapter per closed kind. Define the one raw
gate as:

```csharp
ValueTask<RetentionReadLeaseHandle?> TryAcquireAsync(
    RetentionOwnershipKey ownershipKey,
    long expectedRevision,
    RetentionLeaseKind leaseKind,
    DateTimeOffset now,
    CancellationToken cancellationToken);
```

It validates item/ownership/revision, verifies the exact matching source,
checks `now < expires_at` and null `read_denied_at`, and atomically acquires a
bounded access/operation lease. The returned handle exposes only a lease-bound
typed source reader, never a private locator. Convert every 89-A inventoried raw
read/operation callsite to this gate and add executable bypass tests for missing,
unknown, stale, failed, deleted, and expired-before-promotion cases.

### Step 4: Add the real fixture and restart evidence

Create
`scripts/test/GenerateRetentionSchemaFixtures/GenerateRetentionSchemaFixtures.csproj`
and its deterministic `Program.cs`, invoked exactly as:

```powershell
dotnet run --project scripts/test/GenerateRetentionSchemaFixtures/GenerateRetentionSchemaFixtures.csproj -- --output tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/retention
```

Generate the fixture only through that command, update its SHA-256 manifest,
then verify read-only pre-state,
migration, integrity, semantic schema, preserved sentinels, and two reopens.
A fresh restart means full host disposal, all SQLite connections/pools cleared,
and a new host/store instance opening a temporary copy; never mutate originals.

### Step 5: Focused validation and commit

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionCatalog
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~RetentionLifecycleIntegrationTests|FullyQualifiedName~RetentionNoLeakTests"
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SessionSchemaMigrationFixtureTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorSchemaMigrationFixtureTests
```

Commit: `Issue #89: feat: persist retention catalog lifecycle`

## Task 89-C: Durable scheduler, leases, retry, and recovery

**Files:**

- Create: `src/CopilotAgentObservability.LocalMonitor/Retention/RetentionWorkerPolicy.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Retention/RetentionCleanupCoordinator.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Retention/RetentionCleanupWorker.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Retention/RetentionSqliteMaintenance.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/RetentionCatalogStore.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionCleanupWorkerTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionLifecycleIntegrationTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionNoLeakTests.cs`

### Step 1: Write deterministic RED tests

RED methods: `TwoWorkers_ClaimAndDeleteExactlyOnce`,
`CancellationBeforeIntent_RequeuesWithoutAttempt`,
`LossAfterIntent_RecoversForwardOnly`, `RetrySchedule_FifthFailureIsTerminal`,
`StopAsync_DrainsWithinPinnedBound`, and
`SuccessfulBatch_CheckpointsWalOnce`. Expected RED: no durable coordinator,
lease transitions, retry schedule, or maintenance service exists.

Use `Barrier`, `TaskCompletionSource`, `ManualResetEventSlim`, controlled SQLite
locks, and `MutableTimeProvider`, never sleeps. Pin two-worker single-claim,
stale completion no-op, bounded scan/worker counts, retry schedule, fifth-failure
exhaustion, lease reclaim, cancellation before intent back to queued, and
cancellation/process loss after intent remaining forward-only `deleting`.
Assert attempt count increments exactly once with the first durable destructive
intent, never for claim/contention/renewal/pre-intent cancellation. Test
operation quiescence and two-minute bounded shutdown with explicit gates.

Add RED maintenance tests with controlled connections and cleared pools: one
bounded `PRAGMA wal_checkpoint(TRUNCATE)` runs after a successful bounded SQLite
batch only after conflicting readers/leases quiesce; it never runs per item,
never changes a verified deleted item, and busy/failure records only
`retention_maintenance_busy` with a one-minute retry and no lifecycle change.

### Step 2: Implement the durable coordinator

SQLite is the only source of work; an in-memory wake signal is optional. Reject
new access/operation leases after irreversible denial, quiesce existing leases,
then atomically claim a deletion lease only with no conflicts. Every renewal,
intent, completion, failure, and retry is owner/generation/revision fenced.

The coordinator dispatches only through the pre-existing closed
`RetentionAdapterRegistry`; 89-C must not invent or modify its public contract.
Implement the bounded WAL checkpoint exactly as tested. Never issue `VACUUM` or
`incremental_vacuum`, inspect database/WAL bytes, or claim freelist/media purge.
89-C uses exactly five strict test adapters and does not compose or enable the
production worker.

### Step 3: Focused validation and commit

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionCleanupWorkerTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~RetentionLifecycleIntegrationTests|FullyQualifiedName~RetentionNoLeakTests"
```

Commit: `Issue #89: feat: add durable retention cleanup worker`

## Task 89-D1: Exact SQLite adapters

**Files:**

- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/RetentionCatalogStore.SqliteDeletion.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/RetentionSqliteDeletionContracts.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/RetentionCatalogStore.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Retention/RetentionCleanupCoordinator.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/SessionEventContentRetentionAdapter.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/RawRecordRetentionAdapter.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Retention/MonitorAnalysisRetentionAdapter.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/Analysis/SqliteMonitorAnalysisStore.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/SessionEventContentRetentionAdapterTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RawRecordRetentionAdapterTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/MonitorAnalysisRetentionAdapterTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/SqliteRetentionDeletionBridgeTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/AtomicSqliteCompletionCoordinatorTests.cs`

### Step 1: Write RED adapter tests

RED methods: `SessionAdapter_DeletesOnlyExactContentAtomically`,
`RawAdapter_DeletesOwnedRowAndRetainsProjection`,
`AnalysisAdapter_RemovesOnlyRunOwnedRawFields`, and
`AdapterFailureMatrix_RollsBackSourceAndReceipt`. Expected RED: adapters are
absent.

Session deletes only exact content and retains Session/Event metadata. Raw
record deletes only the exact owned row and retains sanitized projections.
Analysis deletes exact events and nulls raw result/error while retaining run
metadata and safe summary. Physical mutation, absence verification, receipt,
and tombstone are one transaction. Prior absence without a receipt is a fixed
failure. Stale ownership/revision affects zero rows.

Ownership proofs are exact: Session content uses event ID plus joined exact
Session/Event provenance; raw records use store instance, primary key, and an
immutable ingestion-owner receipt; analysis raw uses store instance, run ID,
and exact run-owned event/result/error fields. For each adapter inject failure
after source mutation/before receipt and while writing receipt/state; prove the
source mutation and receipt roll back together, irreversible denial remains,
recovery is fenced, and no duplicate deletion occurs.

The catalog-owned internal bridge and coordinator ordering are the narrow D1
file-boundary correction approved by Oracle. The bridge keeps the public
adapter API unchanged and owns one `BEGIN IMMEDIATE` transaction across source
mutation, absence proof, cursor advancement, and catalog completion. For
Microsoft.Data.Sqlite 10.0.8 it uses `DefaultTimeout=1` with
`PRAGMA busy_timeout=0`: zero disables native busy waiting, while the provider's
minimum positive timeout bounds its otherwise-unlimited managed retry. There is
no application-owned retry or delay.

### Step 2: Implement, validate, and commit

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~RetentionAdapterTests|FullyQualifiedName~SqliteRetentionDeletionBridgeTests|FullyQualifiedName~AtomicSqliteCompletionCoordinatorTests"
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionLifecycleIntegrationTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~MonitorAnalysisStoreTests
```

Commit: `Issue #89: feat: clean exact SQLite raw stores`

## Task 89-D2: Sensitive Bundle capture and filesystem adapter

**Files:**

- Modify: `src/CopilotAgentObservability.ConfigCli/DiagnosisCandidates/DiagnosisCandidateOptions.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/DiagnosisCandidates/DiagnosisCandidateGenerator.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/DiagnosisCandidates/DiagnosisCandidateOutputWriter.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/DiagnosisCandidates/SensitiveBundleWriter.cs`
- Create: `src/CopilotAgentObservability.ConfigCli/DiagnosisCandidates/RetentionSensitiveBundleStore.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Retention/SensitiveBundleRetentionAdapter.cs`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/RetentionSensitiveBundleStoreTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/SensitiveBundleRetentionAdapterTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionLifecycleIntegrationTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionNoLeakTests.cs`
- Modify: relevant Config CLI option/help tests

### Step 1: Write RED option and ownership tests

RED methods: `SensitiveOptions_MatchCompleteRetentionDatabaseMatrix`,
`Capture_ValidatesCatalogBeforeAnyInputOrOutput`,
`BundleCapture_RecoversEachJournalPhase`,
`BundleDelete_UsesExactOwnershipAndMarkerLast`, and
`BundleDelete_RejectsUnexpectedMembersWithoutRemovingThem`. Expected RED: the
catalog option/store/adapter and exact ownership journal are absent.

Pin the Oracle-resolved parse rules and fixed sanitized failures. Prove catalog
validation happens before raw read, output publication, or directory creation.
Prove the catalog-generated child uses exclusive creation, exact owner marker,
exact member/byte journal, authoritative reservation timestamp, and no path in
diagnostics. Reject existing child, reparse point, replaced/unowned member,
unexpected missing pre-intent source, member/byte overflow, and parent deletion.

Use the following table-driven CLI matrix. Test symbols are fixed safe fixture
paths: `M=measurements.json`, `R=raw.json`, `R0=raw-no-bundle.json`,
`D=monitor.db`, `D2=other.db`,
`O=out.json`, `P=parent`; `NL=Environment.NewLine`. Every vector starts with
`generate-diagnosis-candidates`.

| Case and complete remaining vector | Phase/precedence | Exit; exact stdout; exact stderr | Opens/modifies | Permitted side effects |
| --- | --- | --- | --- | --- |
| no raw: `M --include-sensitive-content --retention-database D --json O` | parse validation after option syntax | 1; empty; `error: --include-sensitive-content requires --raw <raw-store.db\|raw-otlp.json>.{NL}` | neither opened/modified | none |
| no catalog: `M --raw R --include-sensitive-content --json O` | parse after missing-raw check | 1; empty; `error: --include-sensitive-content requires --retention-database <local-monitor.db>.{NL}` | neither | none |
| catalog without sensitive: `M --retention-database D --json O` | parse combination check | 1; empty; `error: --retention-database is valid only with --include-sensitive-content.{NL}` | neither | none; `P` is inert if present |
| repeated catalog: `M --raw R --include-sensitive-content --retention-database D --retention-database D2 --json O` | left-to-right option syntax | 1; empty; `error: --retention-database may be specified only once.{NL}` | neither | none |
| missing catalog option value: `M --raw R --include-sensitive-content --retention-database --json O` | immediate option syntax | 1; empty; `error: --retention-database requires a Local Monitor SQLite database path.{NL}` | neither | none |
| missing output: `M --raw R --include-sensitive-content --retention-database D` | semantic parse after catalog requirement | 1; empty; `error: generate-diagnosis-candidates requires --csv, --json, or both.{NL}` | neither | none |
| nonexistent measurements `M`: `M --raw R --include-sensitive-content --retention-database D --json O` | first runtime path check | 1; empty; `error: measurements_input_unavailable{NL}` | neither | none |
| nonexistent/non-SQLite/unrelated/absent-v1/older-v0/newer-v2/read-only-or-unwritable `D`: `M --raw R --include-sensitive-content --retention-database D --json O` | runtime after parse and existing measurement-path check, before raw existence/open | 1; empty; `error: retention_catalog_unavailable{NL}` | catalog open attempted, never modified; raw not opened | no file/directory/output |
| existing `M` open/read failure: valid vector | runtime after catalog validation | 1; empty; `error: measurements_input_unavailable{NL}` | D validated read-only; M attempted; raw not opened | no output/child; M unchanged |
| existing `M` parse/normalization failure: valid vector | runtime after catalog validation | 1; empty; `error: measurements_input_invalid{NL}` | D validated read-only; M opened; raw not opened | no output/child; M unchanged |
| missing/unreadable raw `R` with valid `D`: same valid vector | runtime after read-only catalog validation | 1; empty; `error: raw_input_unavailable{NL}` | catalog opened read-only, not modified; raw open absent/fails | none |
| raw parse/normalization failure after open: same valid vector with invalid R | runtime after measurements read | 1; empty; `error: raw_input_invalid{NL}` | D validated read-only, M/R opened, D not modified | no output/child; M/R unchanged |
| valid supplied parent: `M --raw R --include-sensitive-content --retention-database D --sensitive-output-dir P --json O` | runtime success | 0; `Generated 1 diagnosis candidate record(s).{NL}`; empty | validate D, open R, then reserve/modify D | exact child under P, marker/journal/manifest/members, O only |
| valid omitted parent: `M --raw R --include-sensitive-content --retention-database D --json O` | runtime success | 0; same stdout; empty | same | exact child under pinned temp parent and O only |
| valid but no qualifying bundle fragment: `M --raw R0 --include-sensitive-content --retention-database D --json O` | runtime success | 0; `Generated 1 diagnosis candidate record(s).{NL}`; empty | D validated read-only/not modified; raw opened | O only; no parent/child/journal/item |
| inert parent: `M --sensitive-output-dir P --json O` | non-sensitive success | 0; `Generated 1 diagnosis candidate record(s).{NL}`; empty | no catalog/raw | O only; P is neither opened nor created |
| reservation commit failure: valid supplied-parent vector | capture runtime | 1; empty; `error: retention_catalog_unavailable{NL}` | D/raw opened, no committed D mutation | R unchanged; no child/O |
| staging create/write/flush failure: valid supplied-parent vector | capture runtime after reservation | 1; empty; `error: retention_capture_incomplete{NL}` | D has exact reserved/staging journal; raw read-only | R unchanged; only exact journaled staging may exist and recovery removes it; no O |
| ownership/reparse/publication verification failure: valid supplied-parent vector | pre-publication verification | 1; empty; `error: retention_ownership_mismatch{NL}` | D journal records fixed blocker; raw read-only | R unchanged; unexpected target preserved; no O |
| member/byte limit exceeded: valid supplied-parent vector | staging before publication | 1; empty; `error: retention_item_limit_exceeded{NL}` | D records terminal capture blocker, no lifecycle item; raw read-only | R unchanged; only exact owned unpublished staging removed; no O |
| failure after atomic publication before catalog completion: valid supplied-parent vector | `published_pending_catalog` | 1; empty; `error: retention_capture_incomplete{NL}` | D journal/cursor committed; raw read-only | R unchanged; exact published child retained for forward startup finalization; no O |
| output create/write/flush/atomic-publication failure with no bundle: valid R0 vector | output runtime | 1; empty; `error: diagnosis_output_unavailable{NL}` | D validated only; inputs read | M/R unchanged; exact output staging removed, O absent; no bundle |
| output failure after bundle catalog completion: valid R vector | output runtime after `complete` | 1; empty; `error: diagnosis_output_unavailable{NL}` | D bundle item/journal remain complete; inputs read | M/R unchanged; valid owned bundle remains under retention; exact output staging removed, O absent |

Syntax errors are selected left-to-right; after syntax the semantic precedence is
missing raw, catalog-without-sensitive, missing catalog, missing output. Runtime
precedence is existing measurement-path check, catalog validation,
measurement open/read/parse, raw existence/open/read/parse, capture, then atomic
diagnosis-output publication. Validating the catalog is
read-only; its first modification is the transactional bundle reservation after
raw evidence proves a bundle is required. Config CLI never creates, discovers,
falls back to, or migrates the catalog.
For sensitive mode only, diagnosis CSV/JSON publication uses an exclusive
same-parent staging file, flush-to-disk, and atomic rename; failure removes only
that exact staging file and leaves the requested output absent.

When no parent is supplied, use exactly
`Path.Combine(Path.GetTempPath(), "copilot-agent-observability", "sensitive-bundles")`;
never derive it from raw input, database location, cwd, or neighbors. The parent
option is fully inert outside sensitive mode.

### Step 2: Implement forward-only capture/deletion

The parent is configuration only. Reserve the ID/timestamp/journal before
staging; publish only after exact verification. Cleanup deletes journaled
members and the exact child, never scans/adopts a neighbor or recursively
deletes the parent. Legacy arbitrary-location mode is retired, not migrated by
path inference.

Shared ownership/manifest/journal/private-locator types live only in
Persistence.Sqlite, which Config CLI already references. Local Monitor must not
reference Config CLI, and no second catalog is allowed. Register the new Local
Monitor adapter for `sensitive_bundle`.

Pin bundle ownership to catalog child ID, exclusive creation, owner token,
stable target identity, manifest schema plus SHA-256, and each relative member's
type/length/digest. Delete in journal order, owner marker last, and remove only
an empty non-reparse identity-matching child. Preserve unexpected, replaced,
reparse, or unowned members and terminally fail with
`retention_ownership_mismatch`. Caller-owned `--raw` remains byte-untouched on
success, failure, restart, and cleanup. Restart tests cover every capture phase
and member cursor; absence after matching intent/prior proof advances only that
cursor, while pre-intent absence is terminal unexpected missing.

### Step 3: Focused validation and commit

```powershell
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~RetentionSensitiveBundleStoreTests
dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~SensitiveOptions_MatchCompleteRetentionDatabaseMatrix
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SensitiveBundleRetentionAdapterTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~RetentionLifecycleIntegrationTests|FullyQualifiedName~RetentionNoLeakTests"
```

Commit: `Issue #89: feat: catalog sensitive bundle retention`

## Task 89-D3: Per-analysis SDK directory ownership

**Files:**

- Modify: `src/CopilotAgentObservability.LocalMonitor/Analysis/DotNetCopilotRawAnalysisRunner.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Retention/AnalysisSdkDirectoryRetentionAdapter.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/AnalysisSdkDirectoryRetentionTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionLifecycleIntegrationTests.cs`

### Step 1: Write RED lifecycle tests

RED methods: `SdkRun_UsesExclusiveCatalogOwnedChild`,
`SdkRun_CatalogFailureStartsNothing`,
`SdkCleanup_WaitsForOperationLeaseAndNeverTouchesParent`, and
`SdkCleanup_RecoversEveryMemberCursor`. Expected RED: the runner still uses the
shared base and no SDK retention adapter exists.

The configured SDK base is a parent only. Each run reserves a catalog item
whose `captured_at` is the owning run's `requested_at`, exclusively creates one
owned child, passes only that child to the SDK, and holds an operation lease.
Cleanup waits for disposal/release, rejects replacements/reparse points, and
never scans/deletes the parent or siblings. Missing timestamp fails before SDK
start.

Pin exclusive empty-child creation and owner marker before SDK start, private
locator plus stable child identity, and the operation lease through client and
session disposal. After quiescence, enumerate only inside the exact child within
the 256-member/128-MiB limits, persist the complete deterministic member journal
before first unlink, delete marker last, and remove only an empty matching
child. Catalog failure occurs before child creation/SDK start. Restart tests
cover every capture phase, member cursor, and final-directory boundary. No
parent or sibling enumeration/deletion is permitted.

### Step 2: Implement, validate, and commit

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~AnalysisSdkDirectoryRetentionTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionLifecycleIntegrationTests
```

Commit: `Issue #89: feat: own analysis SDK retention directories`

## Task 89-D4: Atomic production adapter composition

**Files:**

- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionCompositionTests.cs`

89-B creates the validating registry without production composition. D1–D3
must not construct partial registries or start the worker. First add RED method
`MonitorHost_RegistersExactlyFiveRetentionAdaptersAtCoverageV1`; run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionCompositionTests
```

Expected RED: no production composition exists. Then atomically construct one
registry containing the Session, raw-record, analysis, Sensitive Bundle, and SDK
adapters at coverage v1. Only after strict construction succeeds may the host
start the cleanup worker. Missing/duplicate/unknown adapter disables startup of
that worker and logs only `retention_adapter_coverage_mismatch`; this worker code
never enters item summaries. It never runs a partial registry. Rerun the
identical command GREEN.

Commit: `Issue #89: feat: compose complete retention cleanup registry`

## Task 89-E: Versioned sanitized diagnostics

**Files:**

- Create: `src/CopilotAgentObservability.LocalMonitor/Retention/RetentionStatusModels.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/Retention/RetentionStatusRoutes.cs`
- Modify: `src/CopilotAgentObservability.Persistence.Sqlite/Retention/RetentionCatalogStore.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionStatusRouteTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionNoLeakTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Retention/status-empty.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Retention/status-unavailable.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Retention/status-full-item.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Retention/session-not-captured.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Retention/session-mixed.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/Retention/session-not-found.json`

### Step 1: Write RED route and no-leak tests

RED methods: `StatusRoute_MatchesExactV1Schema`,
`SessionStatus_PreservesMixedAndAllLifecycleCounts`,
`Diagnostics_PreserveUnknownValues`, and
`RetentionStatusSurfaces_DoNotLeakInjectedMarkers`. Expected RED: routes/read model
and cross-surface no-leak projection are absent.

Pin `GET /api/retention/v1/status` and
`GET /api/retention/v1/sessions/{sessionId}` shapes, exact aggregate rules,
per-state/readable/read-denied counts, worker/last-success/coverage data,
bounded deterministic ordering, unknown preservation, and no-store headers.
Inject synthetic raw/path/credential/PII/exception markers and prove none leave.

Pin exact snake_case JSON before production code. Status is HTTP 200/no-store
with: integer `schema_version`=`1`, nullable integer counts
`pending_count`, `queued_count`, `deleting_count`, `failed_count`,
`retry_exhausted_count`, `orphan_or_unexpected_missing_count`, and
`expired_but_readable_violation_count`; nullable integer
`oldest_pending_age_seconds`; string `worker_state` from
`disabled|idle|running|degraded|unknown`; nullable RFC3339
`last_successful_run_at`; integer `inventory_version`; integer
`adapter_coverage_version`; and `items` of at most 100.

The positive item-summary allowlist is exactly: `item_id`, `store_kind`,
`inventory_category`, `state`, `policy_id`, `policy_version`, `captured_at`,
`expires_at`, `read_denied_at`, `queued_at`, `deletion_started_at`, `deleted_at`,
`attempt_count`, `retry_exhausted`, `error_code`, and `retry_at`. IDs are opaque;
timestamps are nullable RFC3339 strings; counts/versions are nonnegative
integers; error code is nullable and allowlisted. No string except fixed enums,
codes, policy IDs, opaque IDs, the listed RFC3339 timestamp fields, and existing
public `session_id` may appear.

Aggregate formulas use one transactionally consistent catalog snapshot:

- `pending_count`: state exactly `expired_pending_deletion`;
- `queued_count`: state exactly `deletion_queued`;
- `deleting_count`: state exactly `deleting`;
- `failed_count`: every `deletion_failed`, including exhausted;
- `retry_exhausted_count`: `deletion_failed` with `retry_exhausted=true`;
- `orphan_or_unexpected_missing_count`: items whose fixed error is
  `retention_unexpected_source_missing`;
- `expired_but_readable_violation_count`:
  `state <> 'retained_by_policy' AND expires_at IS NOT NULL AND
  expires_at <= snapshot_now AND (read_denied_at IS NULL OR state = 'expiring')`;
- `oldest_pending_age_seconds`: null when pending+queued+deleting+failed is
  empty; otherwise floor whole UTC seconds from the minimum `expires_at` in
  that set to `snapshot_now`, clamped at zero and `Int64.MaxValue`.

`worker_state` precedence is: `unknown` when catalog state cannot be read;
`disabled` until the strict five-adapter registry is enabled; `degraded` for a
persisted worker/maintenance blocker or any exhausted item; `running` when at
least one valid deletion lease is active; otherwise `idle`. Last successful run
is updated only when one bounded scan/claim/process cycle durably completes with
no item failure or cancellation; a later checkpoint busy result does not erase
it. It is null before the first such cycle.

Session status is HTTP 200/no-store with integer `schema_version`=`1`, existing
public `session_id`, `raw_retention_state`, integer `readable_count`, integer
`read_denied_count`, and a `lifecycle_counts` object containing exactly the
seven lifecycle property names with integer values. Unknown Session remains the
existing 404 behavior. Arrays/order follow expiry then item ID; JSON property
order follows the declarations above.

From the same snapshot, `readable_count` counts items with state `expiring` or
`retained_by_policy`, null `read_denied_at`, and `snapshot_now < expires_at`
(except `retained_by_policy`, whose explicit policy makes expiry non-applicable).
`read_denied_count` counts every other ever-captured item/tombstone, including
expired-before-promotion. The two counts exclude never-captured absence and sum
to the number of represented items/tombstones for that Session.

Unknown Session reuses the body/status contract of
`GET /api/session-workspace/sessions/{sessionId}`: HTTP 404,
`Content-Type: application/json`, no Cache-Control/ETag/Last-Modified, and exact
no-BOM/no-newline UTF-8 `{"error":"session_not_found"}`. Commit this as
`session-not-found.json`. The new retention Session route uses the same
status/content-type/body bytes, adds `Cache-Control: no-store`, and also omits
ETag/Last-Modified.

Session aggregation precedence is exact: any proven readable `expiring` or
`retained_by_policy` child => `expiring`; else any ever-captured item, tombstone,
stale/missing/repair-blocked item, or denied lifecycle =>
`expired_pending_deletion`; else `not_captured`. The retention v1 route itself
uses `mixed` only when its represented canonical item lifecycles contain more
than one distinct value; one distinct value returns it, zero returns
`not_captured`.

Commit exact no-BOM/no-newline UTF-8 fixtures:
`status-unavailable.json` (all nullable counts/timestamps null,
`worker_state` unknown, empty items), `status-empty.json` (known counts zero,
oldest/last-success null, idle, empty items), `status-full-item.json` (every allowlisted item field),
`session-not-captured.json`, and `session-mixed.json` (all seven count keys).
Tests compare bytes and pin `Content-Type: application/json`,
`Cache-Control: no-store`, and absence of `ETag` and `Last-Modified`.

Their exact single-line bytes are:

```json
{"schema_version":1,"pending_count":null,"queued_count":null,"deleting_count":null,"failed_count":null,"retry_exhausted_count":null,"orphan_or_unexpected_missing_count":null,"expired_but_readable_violation_count":null,"oldest_pending_age_seconds":null,"worker_state":"unknown","last_successful_run_at":null,"inventory_version":1,"adapter_coverage_version":1,"items":[]}
{"schema_version":1,"pending_count":0,"queued_count":0,"deleting_count":0,"failed_count":0,"retry_exhausted_count":0,"orphan_or_unexpected_missing_count":0,"expired_but_readable_violation_count":0,"oldest_pending_age_seconds":null,"worker_state":"idle","last_successful_run_at":null,"inventory_version":1,"adapter_coverage_version":1,"items":[]}
{"schema_version":1,"pending_count":0,"queued_count":1,"deleting_count":0,"failed_count":0,"retry_exhausted_count":0,"orphan_or_unexpected_missing_count":0,"expired_but_readable_violation_count":0,"oldest_pending_age_seconds":60,"worker_state":"idle","last_successful_run_at":"2026-07-19T00:00:00.0000000+00:00","inventory_version":1,"adapter_coverage_version":1,"items":[{"item_id":"ret-item-0001","store_kind":"raw_record","inventory_category":"required_cleanup","state":"deletion_queued","policy_id":"raw-default-90d","policy_version":1,"captured_at":"2026-04-20T00:00:00.0000000+00:00","expires_at":"2026-07-19T00:00:00.0000000+00:00","read_denied_at":"2026-07-19T00:00:00.0000000+00:00","queued_at":"2026-07-19T00:00:00.0000000+00:00","deletion_started_at":null,"deleted_at":null,"attempt_count":0,"retry_exhausted":false,"error_code":null,"retry_at":null}]}
{"schema_version":1,"session_id":"018f0000-0000-7000-8000-000000000001","raw_retention_state":"not_captured","readable_count":0,"read_denied_count":0,"lifecycle_counts":{"expiring":0,"retained_by_policy":0,"expired_pending_deletion":0,"deletion_queued":0,"deleting":0,"deleted":0,"deletion_failed":0}}
{"schema_version":1,"session_id":"018f0000-0000-7000-8000-000000000001","raw_retention_state":"mixed","readable_count":2,"read_denied_count":5,"lifecycle_counts":{"expiring":1,"retained_by_policy":1,"expired_pending_deletion":1,"deletion_queued":1,"deleting":1,"deleted":1,"deletion_failed":1}}
```

Extend no-leak tests beyond routes to HTTP headers, structured/rendered logs,
worker/migration error records, surfaced exception messages, fixture manifests,
and generated closeout evidence. Use distinct synthetic markers for raw
content, source IDs, absolute and relative paths, credentials, secrets, PII,
private locators, and exception text. Unknown/unavailable stays null or
`unknown`, never zero/healthy.

### Step 2: Implement one safe projection reader

Routes read only catalog-safe projections, never source tables. Preserve
Session v1 wire shapes and exact `410` behavior.

### Step 3: Focused validation and commit

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionStatus
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SessionWorkspaceRouteTests
```

Commit: `Issue #89: feat: expose sanitized retention diagnostics`

## Task 89-F: Integrated lifecycle, fixture corpus, and final inventory

**Files:**

- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionLifecycleIntegrationTests.cs`
- Modify: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionNoLeakTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/Retention/RetentionInventoryContractTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/retention/sensitive-bundle-v1/manifest.json`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/retention/sdk-directory-v1/manifest.json`
- Create: safe synthetic members and owner markers under both fixture trees
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/TestData/SchemaMigrations/retention/retention-closeout-corpus-v1/manifest.json`
- Create: `docs/sprints/issue-89-final-retention-inventory.md`
- Modify: retention fixture manifest(s)
- Modify: `docs/task.md`

### Step 1: Re-run the previously introduced integrated tests

The lifecycle/no-leak assertions were introduced RED before their owning
production slices in 89-B through 89-E. At closeout, exercise the complete
automatic lifecycle, retry/exhaustion, restart at every
durable boundary, cancellation, two-worker races, stale state, idempotent
recovery, bounded shutdown, exact physical/query absence, WAL checkpoint plus
bounded `wal_checkpoint(TRUNCATE)` maintenance boundary, and irreversible read
denial. Never issue `VACUUM` or `incremental_vacuum`. Prove
no item ever reports `not_captured`; heterogeneous states report `mixed`.

Before producing closeout files, add RED methods
`FinalInventory_CoversEveryBaseToHeadRawCallsite` and
`CloseoutCorpusManifest_IsExact` to `RetentionInventoryContractTests.cs` and
run:

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionInventoryContractTests
```

Expected RED: final inventory/corpus manifest are absent or incomplete. After
Steps 2 and 3, rerun the identical command GREEN.

### Step 2: Build `retention-closeout-corpus-v1`

The authoritative corpus manifest explicitly enumerates Session
`session-v1.sqlite` through `session-v10.sqlite` plus
`session-v10-from-v4.sqlite`, `session-v10-from-v5.sqlite`, and
`session-v10-from-v6.sqlite`; Monitor `monitor-v1.sqlite` through
`monitor-v5.sqlite`; `retention-catalog-v1.sqlite`; and every actual safe
synthetic file in `sensitive-bundle-v1` (`.retention-owner.json`,
`manifest.json`, `evidence/evidence-0001.json`) and `sdk-directory-v1`
(`.retention-owner.json`, `member-0001.dat`) trees. Contents use only fixed
synthetic tokens and no captured data. For each entry record relative path,
SHA-256, fixture kind/version,
pre-migration sentinels, migration result, first fresh-host restart, and second
fresh-host restart. Reject missing, duplicate, renamed, unlisted, or extra
members. Runtime instances outside the corpus remain
fail-closed operational diagnostics and are not silently counted as repository
close evidence.

### Step 3: Produce the delta inventory

Compare `11d6c587903f6ea97026d815f608231efea08d65..HEAD`. Record every final raw
creator, persistence/read/cleanup path, exact identity/ownership/timestamp,
policy, journal/transaction, exclusion, adapter, classification, and executable
test. Record zero unclassified final stores, uncovered required stores, blocked
corpus items, gate-bypassing reads, and unregistered creators. Record safe
handoffs to #90, #91, #79, #87, and #88.

Add an executable inventory contract test that parses the checked-in baseline
and final inventory, reconciles every raw-bearing creator/reader changed in the
base-to-HEAD diff, and fails for an unregistered creator, missing adapter,
unknown classification, or gate-bypassing reader. Do not rely on prose review
alone.

### Step 4: Focused then full validation

```powershell
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionLifecycleIntegrationTests
dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionNoLeakTests
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

### Step 5: Independent final review and commit

Review the Issue body/acceptance/close condition, base SHA, HEAD SHA, entire
Issue #89 commit range, diff, and fresh test output independently. Explicitly
check security, migration, atomicity, rollback, stale state, idempotency,
concurrency, WAL physical-cleanup boundary, and handoff contracts. Return every
blocking finding to the responsible Terra implementer and repeat the same
review after correction.

Commit: `Issue #89: docs: record raw retention closeout evidence`

## Per-task review protocol

### Mandatory RED/GREEN command binding

Each row's command is placed immediately before its task's first production edit
and run RED for the stated missing behavior; the byte-compatibility control is
the sole pre-change GREEN. After the minimum edit, run the identical command
unchanged and require PASS.

| Task / RED method set | Exact test file/class | Exact command |
| --- | --- | --- |
| 89-A `CatalogDomains_*`, `SessionV1Projection_*`, `ExpiredContentRoute_PreservesExactUtf8Bytes`, `RetentionDtos_*` | `RetentionContractTests`, `RetentionCompatibilityContractTests` | `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~Retention` |
| 89-A compatibility control `RawContent_ReturnsExpiredContract` | `SessionWorkspaceRouteTests` | `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SessionWorkspaceRouteTests.RawContent_ReturnsExpiredContract` |
| 89-B migration/capture/read-gate methods | all methods in `RetentionCatalogStoreTests` / `RetentionCatalogMigrationFixtureTests`; lifecycle methods use names containing `RetentionCatalog` | `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionCatalog` |
| 89-C worker/retry/WAL methods | methods in `RetentionCleanupWorkerTests`; lifecycle methods use names containing `RetentionCleanup` | `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionCleanup` |
| 89-D1 exact SQLite adapters/bridge matrix | three `*RetentionAdapterTests`, `SqliteRetentionDeletionBridgeTests`, and `AtomicSqliteCompletionCoordinatorTests` | `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~RetentionAdapterTests|FullyQualifiedName~SqliteRetentionDeletionBridgeTests|FullyQualifiedName~AtomicSqliteCompletionCoordinatorTests"` |
| 89-D2 option/capture methods | all methods in `RetentionSensitiveBundleStoreTests`, including option matrix | `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~RetentionSensitiveBundleStoreTests` |
| 89-D2 deletion adapter/matrix | `SensitiveBundleRetentionAdapterTests`; lifecycle/no-leak methods use names containing `SensitiveBundleRetentionAdapter` | `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~SensitiveBundleRetentionAdapter` |
| 89-D3 SDK methods/matrices | `AnalysisSdkDirectoryRetentionTests`; lifecycle methods use names containing `AnalysisSdkDirectoryRetention` | `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~AnalysisSdkDirectoryRetention` |
| 89-D4 exact registry composition | `RetentionCompositionTests` | `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionCompositionTests` |
| 89-E route/aggregate/no-leak methods, including `RetentionStatusSurfaces_DoNotLeakInjectedMarkers` | `RetentionStatusRouteTests`; `RetentionNoLeakTests` method names contain `RetentionStatus` | `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionStatus` |
| 89-F corpus/inventory methods | `RetentionInventoryContractTests` | `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~RetentionInventoryContractTests` |

### Agent handoff contracts

| Task | Consumes | Produces |
| --- | --- | --- |
| 89-A | approved design `24967f3`, kickoff/inventory SHA `11d6c587903f6ea97026d815f608231efea08d65` | canonical v1 specs, `RetentionCatalogContracts`, baseline raw-read inventory |
| 89-B | the reviewed 89-A commit SHA supplied verbatim in the agent brief | catalog schema/store/migrator, read gate, deletion-adapter contract/registry, real catalog fixture |
| 89-C | the reviewed 89-B commit SHA and exact public types/signatures above | durable worker/coordinator, lease/retry/recovery implementation, bounded WAL maintenance |
| 89-D1 | reviewed 89-B and 89-C SHAs | three exact SQLite adapters and transactional failure matrices |
| 89-D2 | reviewed 89-B and 89-C SHAs | Config CLI bundle capture plus worker-visible bundle adapter; no reverse project reference |
| 89-D3 | reviewed 89-B and 89-C SHAs | SDK child capture/lease plus worker-visible SDK adapter |
| 89-D4 | reviewed 89-C/D1/D2/D3 SHAs | atomic five-adapter production registry and enabled worker |
| 89-E | reviewed 89-D4 SHA | exact sanitized v1 DTOs/routes and cross-surface no-leak tests |
| 89-F | all reviewed Issue #89 commit SHAs | finite corpus validation, delta inventory, full validation and close-readiness evidence |

Before dispatch, the orchestrator replaces each dependency description in the
agent brief with the actual `git rev-parse HEAD` and required predecessor commit
SHAs. The agent must abort on a different HEAD rather than implement against an
unstated revision. Every brief includes the design file as committed at
`24967f3` and the current plan.

For every scenario bullet in a task, the implementer first names the xUnit
method after the behavior, runs the task's exact focused command, and records a
failure caused by the missing/incorrect production behavior—not compilation
noise from unrelated unfinished slices. It then makes only the minimum
production edit, reruns the identical command GREEN, refactors without behavior
change, and reruns GREEN. Integration/no-leak assertions are introduced in the
task that owns their production behavior; 89-F only reruns them.

After each implementation commit, provide a fresh Sol reviewer with the exact
Issue #89 body, acceptance criteria, task slice, task base SHA, HEAD SHA, commit
range, diff, and focused test output. The reviewer must inspect repository
evidence and rerun relevant tests; the implementer's completion claim is not
evidence. A task is not accepted while any blocking finding remains.

Do not stage `.codex/config.toml` or `.superpowers/sdd/*`. Do not push, create a
PR, update/close Issues, or include raw/private evidence in commits.
