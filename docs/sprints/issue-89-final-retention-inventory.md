# Issue #89 Final Retention Inventory

Base SHA: `11d6c587903f6ea97026d815f608231efea08d65`.
Implementation candidate SHA: `5c5540878a6731804084644d8a136be9ad748cf9`.

This is the finite, repository-safe closeout inventory for that exact range. F2
adds evidence and contracts only; it does not add a raw store, reader, creator,
policy, or cleanup implementation. The immutable starting inventory remains
`issue-89-raw-read-callsite-inventory.md`.

`issue-89-final-retention-inventory.json` is the executable finite manifest.
It records and classifies the exact complete candidate production `src/` path
set derived from `git diff --name-only` for this range. The contract parses the immutable
baseline and manifest, invokes that local Git diff, rejects an unavailable or
failing diff, and rejects an unlisted, unknown, blocked, bypassing, or
unregistered entry.

## Reconciliation result

| Check | Result |
| --- | --- |
| Unclassified | `0` |
| Uncovered required-cleanup stores | `0` |
| Blocked corpus items | `0` |
| Gate-bypassing readers | `0` |
| Unregistered creators | `0` |

The changed raw-bearing production surfaces in the base-to-candidate range are
reconciled below. Sanitized projections, receipts, tombstones, catalog rows,
and safe summaries are `retained_by_policy`, not raw stores. No receiver file
or external blob creator exists in the candidate. caller-supplied `--raw` is
`not_applicable`: it remains caller-owned and is neither catalogued nor deleted.

| Store kind | Inventory category | Creator and exact identity | Timestamp / policy | Read gate and cleanup | Adapter / classification test |
| --- | --- | --- | --- | --- | --- |
| session_event_content | required_cleanup | Session ingest and `SqliteSessionStore.cs`; Session/event GUID provenance plus source event ID | preserved capture time; `raw-default-90d` | readable catalog revision and access lease; atomic SQLite source delete plus tombstone | `SessionEventContentRetentionAdapter.cs`; `SessionEventContentRetentionAdapterTests.cs`, `RetentionReadPrimitiveTests.cs` |
| raw_record | required_cleanup | OTLP commit and `RawTelemetryStore.cs`; positive raw-record ID, received pair, schema version | valid `received_at`; `raw-default-90d` | catalog read/composite lease; atomic row delete plus tombstone | `RawRecordRetentionAdapter.cs`; `RawRecordRetentionAdapterTests.cs`, `RetentionCatalogStoreTests.cs` |
| analysis_run_raw | required_cleanup | `SqliteMonitorAnalysisStore.cs` and analysis runner; positive run ID, optional record/span null markers | valid `requested_at`; `raw-default-90d` | catalog operation/access lease; atomic analysis raw delete | `MonitorAnalysisRetentionAdapter.cs`; `MonitorAnalysisRetentionAdapterTests.cs`, `AnalysisSdkDirectoryCatalogTests.cs` |
| sensitive_bundle | required_cleanup | `RetentionSensitiveBundleStore.cs`; reservation identity, ownership marker, bounded plan/digests | reservation time; `sensitive-bundle-7d` | only verified owned members; forward-only journal and marker-last deletion | `SensitiveBundleRetentionAdapter.cs`; `SensitiveBundleRetentionAdapterTests.cs`, `SensitiveBundleDeletionCatalogTests.cs`, `LegacySensitiveBundleDeletionE2ETests.cs` |
| analysis_sdk_directory | required_cleanup | `AnalysisSdkDirectoryOwner.cs`; generated direct child, analysis-run binding, kind-bound marker | persisted owning `requested_at`; `raw-default-90d` | operation lease; bounded snapshot/journal and child-only marker-last deletion | `AnalysisSdkDirectoryRetentionAdapter.cs`; `AnalysisSdkDirectoryRetentionTests.cs`, `AnalysisSdkDirectoryCatalogTests.cs` |

The registry is the strict five-kind composition in `RetentionAdapterRegistry.cs`;
`RetentionAdapterRegistryTests.cs` and `RetentionCompositionTests.cs` reject a
missing, duplicate, or mismatched adapter. `RetentionCleanupWorker.cs` checks
the same coverage before a mutating batch and per claim. `RetentionCatalogStore.cs`,
`RetentionCatalogStore.SqliteDeletion.cs`, `RetentionCatalogStore.FileCapture.cs`,
and `RetentionCatalogStore.FileDeletion.cs` are the shared ownership,
transaction, receipt, journal, and physical-delete boundaries. Their contract
is covered by `RetentionOwnershipReceiptTests.cs`, `RetentionFirstIntentProofTests.cs`,
`RetentionFileCaptureCatalogTests.cs`, `RetentionFileCaptureSchemaTests.cs`,
`AtomicSqliteCompletionCoordinatorTests.cs`, `SqliteRetentionDeletionBridgeTests.cs`,
and `RetentionWorkerFenceMatrixTests.cs`.

## Reader and creator reconciliation

Every candidate raw reader uses the catalog gate or is a migration/backfill-only
copy. The listed tests exercise the read boundary, lease ownership, expiry-first
denial, and absence of partial returns.

| Surface | Classification | Gate / test evidence |
| --- | --- | --- |
| `SqliteIngestionCommitStore.cs`, `RawLocalReceiverHandler.cs` | raw-record creators | receipt/catalog transaction commits capture before success; `RetentionCatalogStoreTests.cs`, `RawRecordRetentionAdapterTests.cs` |
| `RawTelemetryStore.cs`, `RawStoreLeaseReader.cs`, `ProjectionWorker.cs` | raw-record runtime reads and projection consumers | exact raw-record or composite lease; `RetentionReadPrimitiveTests.cs`, `RawTelemetryStoreTestReads.cs` |
| `MonitorHost.cs`, `Index.cshtml.cs`, `Traces.cshtml.cs`, `TraceDetail.cshtml.cs`, `SessionRoutes.cs` | raw-bearing local routes/pages | exact readable lease, same-origin/no-store and sanitized-only removal; `RetentionStatusRouteTests.cs`, `RetentionCompatibilityContractTests.cs` |
| `SqliteSessionStore.cs`, `SqliteSessionOtelEnricher.cs` | Session content and exact-linked enrichment | event-content/raw-record gate; `SessionEventContentRetentionAdapterTests.cs`, `RetentionReadPrimitiveTests.cs` |
| `DotNetCopilotRawAnalysisRunner.cs`, `SqliteMonitorAnalysisStore.cs` | analysis raw input/result and SDK child use | exact access/operation lease; `MonitorAnalysisRetentionAdapterTests.cs`, `AnalysisSdkDirectoryRetentionTests.cs` |
| `DashboardRawOperationReader.cs`, `RawNormalizationInputReader.cs`, `RawEvidenceReader.cs` | Config CLI raw-record consumers | shared lease reader; `RawTelemetryStoreTestReads.cs`, `RetentionReadPrimitiveTests.cs` |
| `ClaudeDoctorFactCollector.cs`, `ClaudeDoctorCandidateObserver.cs`, `GitHubCopilotDoctorEvidenceAdapter.cs` | Doctor raw-record consumers | shared lease reader; `ClaudeDoctorFactCollectorTests.cs`, `ClaudeDoctorCandidateObserverTests.cs` |
| `RetentionSensitiveBundleStore.cs` | bounded raw-file creator/reader | explicit catalog binding before file creation; `RetentionSensitiveBundleStoreTests.cs`, `SensitiveRetentionRuntimeTests.cs` |
| `MonitorSchemaMigrator.cs` | migration/backfill only, not an ordinary runtime read bypass | copies legacy raw columns only while migrating existing database schema; `RetentionCatalogMigrationFixtureTests.cs`, `MonitorSchemaMigrationFixtureTests.cs` |
| `SqliteSessionStore.cs` direct SQL copies | migration/backfill only, not an ordinary runtime read bypass | legacy Session schema copy is not a request/read path; `RetentionCatalogMigrationFixtureTests.cs`, `SessionSchemaMigrationFixtureTests.cs` |

The final two entries are deliberately not runtime gate-bypass findings: they
are finite migration/backfill copies with no raw response materialization. All
ordinary runtime reads above are registered catalog consumers. The inventory
contract itself (`RetentionInventoryContractTests.cs`) fails closed if this
document is absent, a required store kind is missing, the range changes, a
zero-result claim is altered, or a required creator/reader/adapter boundary is
not named. It does not enumerate machine-specific paths or use network access.

## Candidate CSV/JSON publication audit

`CliApplication.cs` requests optional candidate CSV/JSON publication and
`AtomicDiagnosisOutputPublisher.cs` creates sibling staging files, publishes
new targets with `CreateNew`/non-overwrite semantics, and removes only bytes it
can prove this invocation published after a failure. Candidate files can contain
`sensitive_bundle_path`, but the candidate-pipeline contract defines them as
caller-selected local runtime artifacts. They are therefore `not_applicable` to
the #89 catalog: no retention adapter is invented for caller-owned output or
the publisher's short-lived staging files. Atomic rollback/cleanup remains the
publisher's boundary, with `AtomicDiagnosisOutputPublisherTests.cs` proving
successful cleanup, owned-output rollback, replacement preservation, collision
preservation, and sanitized cleanup failure.

## Lifecycle, policy, and exclusion evidence

The closed lifecycle is `expiring`, `retained_by_policy`,
`expired_pending_deletion`, `deletion_queued`, `deleting`, `deleted`, and
`deletion_failed`. `not_captured` and `mixed` are aggregate-only. Expiry first
commits read denial, then queues cleanup; retry, restart, clock movement, and
repair cannot restore readability. `RetentionCleanupWorkerTests.cs`,
`RetentionWorkerRaceTests.cs`, `RetentionWorkerMaintenanceMatrixTests.cs`,
`RetentionWorkerMigrationQueryPlanTests.cs`, and
`RetentionLifecycleIntegrationTests.cs` cover bounded scan/order, retry,
recovery, cancellation, race fences, and maintenance. `RetentionNoLeakTests.cs`
and `RetentionStatusRouteTests.cs` cover repository-safe diagnostics and
no-leak status surfaces.

No active operation is a cleanup target: read leases protect materialized raw
use, operation leases protect analysis SDK ownership, and deletion validates
the receipt, revision, source identity, lease, and adapter coverage before
mutation. File adapters additionally validate marker, digest, bounded plan, and
journal before each mutation. The closeout corpus contract
(`RetentionInventoryContractTests.Corpus.cs`) validates the finite synthetic
migration and file fixture set and hashes/sentinels. The two fresh-host restart
claims apply only to the 19 SQLite fixtures (13 Session, 5 Monitor, and 1
retention catalog); all five safe file members are explicitly not applicable.

## Required handoffs

- **#90 mutation/queue contracts:** pin, unpin, and delete-now must create only
  catalog-authorized state transitions, preserve irreversible read denial, and
  use the existing item ID/revision/lease/adapter fence. They must not add a
  reader or direct physical-delete path.
- **#91 immutable validation gate/evidence:** validate this exact base/candidate
  range, five-adapter coverage, corpus hash contract, no-leak evidence, and the
  required build/test commands before any release-matrix claim. A changed
  candidate requires a fresh delta inventory rather than inheriting this one.
- **#79 / #87 / #88 exclusion extension checklist:** before a new raw-bearing
  producer or consumer is enabled, register an exact store kind/identity and
  authoritative timestamp; classify it in a refreshed delta inventory; bind a
  policy, receipt, catalog transaction, reader lease, cleanup adapter, and
  bounded exclusion/operation lease; add lifecycle, no-leak, and registry tests;
  and do not treat an extension as covered by the five current kinds.

## Closeout scope

This inventory is evidence for the implementation candidate only. It makes no
claim about future raw stores, runtime data outside the synthetic corpus, live
network validation, or the out-of-scope #90 mutation UI/API.
