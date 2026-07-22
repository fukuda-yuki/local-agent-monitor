# Issue #88 — Runtime Backup And Restore

This directory records repository-safe implementation and validation evidence
for the private raw-bearing runtime-backup profile. The canonical behavior is
defined by `docs/specifications/interfaces/runtime-backup-restore.md`. This
directory must never contain a backup ZIP, SQLite database, WAL/journal,
credential, raw payload, private locator, or sensitive local path.

## Candidate state

- Wave 3 kickoff: `c02c10ab18553acef1619ce12ec630f4f6f5aa5f`.
- Functional candidate: not frozen at this preparation checkpoint.
- Integration order: after accepted #79 and #86 storage migrations; final-main
  component reconciliation is pending coordinator rebase.
- Publication: local-only; no push and no pull request.

## Preserved preliminary execution

These commands ran against an earlier dirty, unfrozen implementation and are
diagnostic history, not acceptance evidence for the current candidate:

| Command | Result |
| --- | --- |
| `dotnet build CopilotAgentObservability.slnx` | passed with 0 warnings and 0 errors |
| Config CLI focused `RuntimeBackup` tests | 6 passed, 0 failed |
| Local Monitor focused `RuntimeBackup` tests | 128 passed, 63 failed |

The Local Monitor failures were preserved. Static diagnosis found that the
exclusive restore lease and HTTP preview upload handle reopened their own paths
for native type classification on Windows, causing
`monitor_must_be_stopped`/`snapshot_store_unavailable`. The candidate now uses
handle-based native classification, but no post-fix .NET result is claimed in
this checkpoint.

## Required final evidence

Before acceptance, the frozen SHA must replace the preparation SHA in the
validation matrix. Rows `91-B-088` and `91-S-088` require exact build, focused,
Playwright, migration, security, bootstrap, and full-suite results, including
every failed first attempt. Row `91-L-088` remains `blocked_external` until a
genuine second machine completes private transfer, packaged offline restore,
Published readiness, and Doctor verification. Same-machine directory
separation is not cross-machine evidence.
