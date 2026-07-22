# Issue #88 Runtime Backup and Restore Implementation Plan

> Scope: implement the accepted Issue #88 and Wave 3 contracts from kickoff
> `c02c10ab18553acef1619ce12ec630f4f6f5aa5f`. This plan owns only #88; the
> integration owner reconciles shared Wave 3 edits after rebasing #79 -> #86 ->
> #88.

## Contract decisions

- `local-runtime-backup` v1 is explicitly raw-bearing and not repository-safe.
- A bundle has exactly two stored ZIP members, in order: `manifest.json` and
  `database.sqlite`. The database member comes from SQLite's online backup API,
  not a filesystem copy of a live WAL database.
- The SQLite database is the restore unit. Runtime PID/state/log files,
  executables, credentials, setup storage, proposal-apply private state, and
  caller-owned files are not silently omitted: the manifest carries a bounded
  product-owned external-state inventory and restore prerequisite for each.
  Setup storage is host-bound and intentionally not restored; a cross-machine
  restore must rerun configuration setup. Any proposal-apply private file or
  active DB-external raw store makes a database-only backup inconsistent and is
  rejected with a fixed reason.
- A backup is rejected when an active `sensitive_bundle` or
  `analysis_sdk_directory` catalog item exists. Those stores are outside the
  SQLite restore unit, so accepting the database alone would violate the
  consistent-restore contract.
- Restore is an offline CLI operation. The loopback HTTP surface may create an
  online backup and inspect/preview an uploaded bundle, but never swaps the live
  database.
- A current tombstone or irreversible read denial is carried forward into
  staging with its exact lifecycle/audit state and exact raw-source removal;
  confirmation can never drop it. The opt-in digest is available only for a
  non-terminal catalog identity whose source is currently absent and would be
  reintroduced, bound to the archive checksum and current comparison set.
- Restore stages and validates the candidate, creates a pre-restore runtime
  backup by default, applies supported component migrations in staging,
  atomically replaces the database, performs post-swap readiness and Doctor
  store checks, and restores the old database after any injected or observed
  post-swap failure.
- #88 owns `runtime_backup` component schema v1. It does not change Monitor 7,
  Session 13, Retention 1, or add a retention store kind. Its provisional
  integration sequence is after #86; numeric renumbering is not applicable
  unless a later shared-component collision is discovered during rebase.
- Backup artifacts are explicit operator-owned/caller-selected files outside
  #89 cleanup. Every result and manifest carries `retention_backup_not_purged`;
  no sixth retention kind, backup inventory claim, or automatic purge exists.
- The `runtime_backup` table stores only bounded sanitized receipts (operation,
  artifact digest, result code, timestamps, and counts), never archive bytes or
  filesystem paths.

## Task 1: Pin public and security contracts

**Files**

- Modify: `docs/requirements.md`
- Modify: `docs/spec.md`
- Modify: `docs/architecture.md`
- Modify: `docs/decisions.md`
- Create: `docs/specifications/interfaces/runtime-backup-restore.md`
- Modify: `docs/specifications/security-data-boundaries.md`

Record the archive profile, manifest fields, limits, compatibility and
migration rules, online snapshot semantics, preview/confirmation/apply state
machine, atomic rollback, exclusions, readiness/Doctor checks, and fixed error
codes before implementation.

## Task 2: Write RED archive, migration, and lifecycle tests

**Files**

- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/RuntimeBackupArchiveTests.cs`
- Create: `tests/CopilotAgentObservability.LocalMonitor.Tests/RuntimeBackupRestoreTests.cs`
- Create: `tests/CopilotAgentObservability.ConfigCli.Tests/RuntimeBackupCliTests.cs`

Cover online WAL consistency; canonical manifest/checksum/version vector;
original capture/policy/expiry preservation; tombstone default rejection and
bound opt-in; non-purge warning; staging/migration; atomic rollback under fault;
corrupt, traversal, duplicate, symlink-attributed, compressed, oversized,
forbidden-extra, and incompatible archives; no partial output or target change.
Run the focused filters and preserve the expected compilation/test failures.

## Task 3: Implement persistence and archive core

**Files**

- Create: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/RuntimeBackupContracts.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/RuntimeBackupJson.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/RuntimeBackupArchive.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/SqliteRuntimeBackupService.cs`
- Create: `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/RuntimeBackupSchemaV1.cs`

Use BCL ZIP and SHA-256 plus `Microsoft.Data.Sqlite.BackupDatabase`; add no
dependency. Validate archive bytes before extraction and SQLite content before
swap. Keep all temporary paths sibling-local, bounded, and fail-closed.

## Task 4: Implement CLI and loopback surface

**Files**

- Create: `src/CopilotAgentObservability.ConfigCli/Cli/RuntimeBackupCli.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Cli/CliApplication.cs`
- Modify: `src/CopilotAgentObservability.ConfigCli/Cli/CliHelpText.cs`
- Create: `src/CopilotAgentObservability.LocalMonitor/RuntimeBackupRoutes.cs`
- Modify: `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`

CLI commands are `runtime-backup create`, `preview`, `inspect`, and `restore`.
HTTP routes create/download an online backup and inspect a bounded uploaded
archive with same-origin, CSRF, Host-header, and no-store controls. No remote or
non-loopback restore route exists.

## Task 5: User workflow, #91 evidence, and validation

**Files**

- Modify: `docs/user-guide/local-monitor.md`
- Modify: `scripts/local-monitor/README.md`
- Create: `docs/specifications/contracts/runtime-backup/v1/issue-91-validation-handoff.json`
- Create: `docs/sprints/issue-88-backup-restore/validation-matrix.json`
- Modify: `docs/specifications/contracts/validation-matrix/v1/future-surface-registry.json`

Remove only the `backup-restore` future placeholder and publish active rows
`91-B-088`, `91-S-088`, and `91-L-088`. Run focused tests, skill mirror check,
solution build, Playwright install, and the full solution tests. Self-review the
diff, record evidence, and commit with `#88:` conventional messages and a Why
body.
