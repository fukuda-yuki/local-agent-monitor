# Issue #88 — Runtime Backup And Restore

This directory records repository-safe implementation and validation evidence
for the private raw-bearing runtime-backup profile. Current behavior is
canonical in `docs/specifications/interfaces/runtime-backup-restore.md`; this
directory is historical evidence only and must never contain a backup ZIP,
SQLite database, WAL/journal, credential, raw payload, private locator, or
sensitive local path.

| Milestone | Status | Evidence |
| --- | --- | --- |
| M1 — Runtime backup and restore | Functional candidate accepted locally; automated and security rows passed; genuine cross-machine validation blocked on an external machine | [validation matrix](validation-matrix.json) |

## Exact candidate

- Wave 3 kickoff: `c02c10ab18553acef1619ce12ec630f4f6f5aa5f`.
- Accepted local-main base after #79 then #86 storage integration:
  `4d371ccd808d07f894f4b8070bf4b236d412e57b`.
- Functional and validation SHA:
  `556811ef0bf96ef1267c4a9d00d9311154fc78e3`.
- Local candidate chain: `14c422c840cd815704fda93646b780479c273a96`
  → `cf6fa3a12cda1d0fbc44d8616e02023ef4a8e5e2`
  → `556811ef0bf96ef1267c4a9d00d9311154fc78e3`.
- Accepted inputs present as ancestors: #85 accepted
  `37a095d7c11dd180851e75bdcb290f0894ba01d5` with functional evidence
  `56c2033257a04751bc52468efa249c4058b20e7d`; #57/#91 registry candidate
  `40ac55974dc7788f4abd54dfa85abd97739b3201`; #89 final
  `de48d717479a40921a6fe70825f6e95a7a75037a`; and #90 integrated
  `5180a0424ff5488354a3e173c74b7e931d28679d`.
- Branch/worktree: `codex/issue-88-runtime-backup-restore` /
  `.worktrees/issue-88-runtime-backup-restore`.
- Publication: local-only; no push and no pull request.

## Migration and storage order

The candidate was rebased after accepted #86. It adds the
`runtime_backup=1` `schema_version` component after the existing
`historical_import=1` and `sanitized_import=1` components. The serialized Wave
3 storage order is therefore exactly `#79 → #86 → #88`. Fresh, partial,
supported older, current, future, malformed, dependency-invalid, and non-empty
Wave 3 round-trip cases are included in the focused runtime-backup tests. No
parallel retention state or numbered migration file was introduced.

## Candidate-pinned validation

All automated cases used bounded synthetic records, disposable local state,
loopback-only hosts, and repository-local Playwright Chromium. No private raw
archive, raw content, sensitive path, or reversible private identity was
retained as evidence.

| Command or gate | Result at `556811ef0bf96ef1267c4a9d00d9311154fc78e3` |
| --- | --- |
| `pwsh scripts\agent\sync-claude-skills.ps1 -Check` | 5 shared skills matched |
| `dotnet build CopilotAgentObservability.slnx` | passed; 0 warnings, 0 errors |
| `pwsh scripts\test\install-playwright-chromium.ps1` | exit 0 |
| `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeBackupCliTests"` | 12 passed, 0 failed, 0 skipped |
| `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-restore --filter "FullyQualifiedName~RuntimeBackup"` | 308 passed, 0 failed, 0 skipped |
| Packaged restart/readiness and script/docs filter (`RuntimeRestoreDocumentationUsesPackagedConditionalRestartSequence`, `PublishedStartWaitReady`, `LocalMonitorScriptTests`) | 45 passed, 0 failed, 0 skipped |
| `dotnet test CopilotAgentObservability.slnx` | 8,409 passed, 0 failed, 0 skipped: Instruction Findings 20, Alerts 451, Doctor 266, Config CLI 4,598, Local Monitor 3,074 |
| Exact-SHA WSL2 Ubuntu 24.04 filter (`Unix_native_path_classification_rejects_devices_fifos_and_sockets_but_accepts_regular_controls`, `Unix_special_file_in_an_allowed_runtime_root_slot_fails_closed`, `Unix_path_with_embedded_windows_separator_is_rejected_without_mutation`) with .NET SDK 10.0.203 | 3 passed, 0 failed, 0 skipped in an isolated temporary clone |
| Direct CLI inspect of a disposable sparse 513 MiB + 1 byte archive | `bundle_too_large`, exit 5, no fixture-path disclosure; fixture deleted |
| Issue #91 matrix validator | 3 rows passed; decision `release_ready_with_external_blockers` |
| Issue #91 scanner self-test | 118 transformation cases and 5 negative cases passed |
| Issue #91 evidence scan | 3 files, 354 variants, 0 matches |
| `dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --no-restore --filter "FullyQualifiedName~Issue91ValidationContractTests"` | 9 passed, 0 failed, 0 skipped |

The focused set covers WAL-consistent snapshotting, manifest/checksum and
archive validation, strict inspect/preview, fresh and supported upgrades,
component order, exact identity, retention clocks/tombstones, explicit
resurrection confirmation, atomic swap/rollback, interrupted recovery,
readiness/Doctor, caller-selected pre-restore output, API/UI/Playwright,
Host/origin/CSRF/media-type controls, no-leak results, archive attacks, reparse
paths, the direct archive ceiling, and owned transient cleanup. Same-machine cross-directory portability is
automated evidence only and is not classified as a genuine cross-machine run.

## Preserved failures and corrections

Successful later runs do not erase these required failures:

- The kickoff baseline passed 7,429/7,429. The first dirty, unfrozen #88 run
  then passed 128 focused Local Monitor cases and failed 63. Handle-based
  native classification corrected self-conflicts in the restore lease and HTTP
  preview upload paths.
- TDD red runs preserved an online-snapshot assertion that expected a
  `SqliteException` but received none, and a cleanup assertion that expected
  `restore_rollback_failed` but received `restore_resurrection_blocked`.
  Deterministic checkpoint/cleanup seams replaced OS-lock-dependent behavior.
- During immutable-read hardening, one broad run failed 45/295; a later
  malformed-preflight run failed 3/295; and one constructor edit produced a
  compile failure. Live/current databases were returned to normal SQLite
  locking while immutable reads were restricted to closed service-owned
  snapshots/stages.
- Cleanup/platform focused rows first ran red and then green. A later broad run
  had one intermittent failure among 281 cases; its immediate isolated retry
  and three repetitions passed. That failure remains recorded and was not
  replaced by the diagnostic retries.
- A 302/303 run exposed a sanitized HTTP transport reset; the oversized request
  was moved to the end of its isolated host sequence and the unchanged product
  then passed 303/303.
- A subsequent 264/303 run exposed 39 pre-swap ordering failures. The strict
  source/target checks were restored before mutation, after which the focused
  set passed 303/303.
- At prior committed candidate
  `cf6fa3a12cda1d0fbc44d8616e02023ef4a8e5e2`, the required full solution run
  failed: Doctor 264/266, Config CLI 4,597/4,598, and Local Monitor
  3,012/3,069; Instruction Findings 20/20 and Alerts 451/451 passed. Diagnosis
  found a #88 startup-order defect, an owned-marker enumeration defect, and
  test-owned host/SQLite lifetime mismatches. The lone unchanged Config CLI
  Setup barrier test passed in isolation, 10/10 repetitions, and the full
  4,598-case Config suite, establishing a contention-sensitive test-harness
  timeout rather than a #88 product change.
- The first Local Monitor run after those corrections passed 3,058/3,071 and
  failed 13 interrupted-restore cases. Recovery validation had created empty
  read sidecars before the new restored-target guard was reacquired. Exact
  empty-sidecar cleanup before guard reacquisition corrected the cause;
  RuntimeBackupRestore then passed 99/99 and Local Monitor passed 3,074/3,074.

Final review also corrected the two-phase monitor startup lease, retention-only
unknown/future/malformed component rejection, relevant-owner-marker bounds,
Windows local-drive allowlisting, immutable-read scope, Unix non-product
SQLite ownership wording, cleanup-artifact preservation, API documentation,
and in-process test owner disposal. Final code, contract, migration, route/UI,
and test-lifetime reviews reported no remaining P0–P2 finding.

## Extension rows and artifacts

- `91-B-088`: `passed` at the functional SHA.
- `91-S-088`: `passed` at the functional SHA.
- `91-L-088`: exact `blocked_external` at the functional SHA.
- Repository-safe artifacts exist at
  `docs/sprints/issue-88-backup-restore/validation-matrix.json`, this ledger,
  and
  `docs/specifications/contracts/runtime-backup/v1/issue-91-validation-handoff.json`.
- No ZIP, SQLite database, WAL/SHM/journal, raw capture, private locator, or
  generated runtime artifact is repository evidence.

## External and unverified boundary

Row `91-L-088` is exactly `blocked_external`: no genuine compatible second
machine was available for private archive transfer, exact SHA-256 verification,
packaged stop/restore/start, accepted readiness, and Doctor. The packaged
restore sequence has static script/documentation and automated same-machine
coverage, but not genuine second-machine execution.

The Unix-only native path cases were executed 3/3 on a local WSL2 Ubuntu 24.04
host at the exact functional SHA. Generic enumeration of non-product Unix
SQLite connections is intentionally outside the supported restore boundary;
the operator must close every such client before restore, and no detection
capability is claimed. Content-enabled live capture was not authorized,
`OTEL_LOG_USER_PROMPTS=1` was not enabled, and no such capture was attempted.
