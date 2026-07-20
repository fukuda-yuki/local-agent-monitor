# Issue #90 M1 Integration, Security, And Closeout Review

Review date: 2026-07-21

Validation candidate: `4d966472bdbe1ccc27f57c05f3afc268025ed37a`

Environment: Windows, .NET 10 preview SDK, repository-local Playwright Chromium

## Scope and outcome

Issue #90 is complete on the local feature branch. The implementation provides
exact Session/item retention reads, preview and confirmation, atomic pin,
unpin, and delete-now mutations, operation/history reads, the Local Monitor
management UI, navigation-only Canvas handoff, and browser workflow coverage.
Delete-now queues the existing Issue #89 worker; it does not claim synchronous
physical deletion. No remote push, pull request, or external Issue transition
was performed.

Implementation commits:

- `78bc2e8fcf13db982f63293a1360cbae42773fa3` — E1 API verification,
  transaction/error classification, consumed linkage, and catalog contention.
- `fe1c49efac3410733a55ebb2806757970ac297d9` — E2 history reads and E3 Local
  Monitor/Canvas UI.
- `2b12f827be2b8322637d630410d11b077818d55e` — E4 browser mutation workflows.
- `4d966472bdbe1ccc27f57c05f3afc268025ed37a` — F1 lifecycle integration, F2
  security matrix, and finalized-tombstone write initialization correction.

## F1 lifecycle evidence

| Row | Expected invariant | Automated evidence | Result |
| --- | --- | --- | --- |
| F1-01 | Item pin suppresses expiry; expired unpin queues deletion | `RetentionLifecycleIntegrationTests.PinSuppressesExpiryUntilUnpinQueuesAndExistingWorkerPhysicallyDeletesAfterRestart` | PASS: pin revision 2, unpin queue revision 5, worker midpoint 6, terminal revision 7 |
| F1-02 | Pinned Session delete-now supersedes the pin and queues the exact target set | `RetentionLifecycleIntegrationTests.SessionDeleteNowQueuesExactTargetsAndExistingWorkerAlonePhysicallyDeletesThemAfterRestart` | PASS: both exact items moved from pinned revision 2 to unpinned queued revision 5 |
| F1-03 | Queueing denies reads immediately; only the existing #89 adapter physically deletes | Same integration test plus `RetentionReadPrimitiveTests` and worker suites | PASS: selector calls remained zero; source existed at deleting revision 6 and was absent only at deleted revision 7 |
| F1-04 | Tombstones prevent resurrection without blocking unrelated future writes | Same integration test plus catalog/migration/Session suites | PASS: late replay rolled back after source insertion and preserved the durable snapshot; unrelated fresh Session content was accepted |
| F1-05 | Every request/preview/confirmation/commit stage is reachable; empty and terminal targets retain their canonical outcomes | `RetentionMutationContractTests.ErrorRegistry_CoversEveryCodeWithCanonicalReachabilityAndHttpMapping`; `RetentionMutationHttpMatrixTests.ConfirmationRoute_MapsEmptyAndExpiredPreviewsWithoutTokenLeak`; `RetentionMutationTargetResolutionTests.ItemResolution_DeletedNonSessionTombstoneRemainsActionableWithOperationRejection`; `RetentionMutationHttpMatrixTests.MutationRoute_MapsOrderedCommitStageFailures` | PASS: the closed registry maps every stage/status; empty confirmation returns `retention_target_empty`; deleted targets return the operation-specific terminal rejection; all nine ordered commit checks return the first canonical failure without durable receipt/audit rows |
| F1-06 | Worker failure/restart remains forward-only and mutation results correlate to one audit/operation with exact per-step revisions | `RetentionCleanupWorkerTests.LossAfterIntent_RecoversForwardOnly`; `RetentionCleanupWorkerTests.RetrySchedule_FifthFailureIsTerminal`; both `RetentionLifecycleIntegrationTests` workflows | PASS: loss after intent recovered without resurrection, the fifth failure became terminal, restart completed through revisions 6/7, and each mutation operation/audit pair carried the final digest-derived `result_version` |

Independent F1 review initially found the missing pinned-Session and late-write
proofs, then exposed a production initialization defect that treated valid
terminal tombstones as source drift. The correction permits a missing source
only for an exact `deleted` item with a matching `deleted_at` tombstone; live,
mismatched, orphan, and count-drift cases remain fail-closed. Final independent
review: PASS, Critical 0 / Important 0 / Minor 0. Retention validation: 453/453.

## F2 security and no-leak evidence

The matrix schema is `retention-security-matrix/v1`. Every row is pinned to the
validation candidate and uses synthetic data only.

| row_id | matrix_schema_version | surface | operation | profile | source/app/adapter | expected invariant | automated evidence | live evidence | bounded actual result | classification | severity | blocker/retry | owner | exact validation SHA/date/environment |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| R90-RET-001 | retention-security-matrix/v1 | status, Session/item reads, history, Razor pages | read | default local | Local Monitor | Raw, path, credential, prompt-label, row-id, and token markers never reach retention responses; only opaque `rid1_` workflow identity appears where contracted | `RetentionMutationSecurityMatrixTests`; route/history/status/no-leak suites | N/A | Synthetic marker scan passed across all listed surfaces | PASS | none | none | #91 | `4d966472bdbe1ccc27f57c05f3afc268025ed37a`; 2026-07-21; Windows/.NET 10 preview |
| R90-RET-002 | retention-security-matrix/v1 | confirmation, mutation, SQLite DB/WAL/SHM | issue and consume confirmation | default local | Local Monitor + SQLite | Plaintext confirmation material appears only in the successful issue response and immediate mutation request; persistence is hash-only; consumed linkage uses relative same-origin `Location` without token replay | `RetentionMutationSecurityMatrixTests`; confirmation persistence, HTTP matrix, route, and browser suites | N/A | Response and artifact scans passed; failure diagnostics use fixed token-free messages | PASS | none | none | #91 | `4d966472bdbe1ccc27f57c05f3afc268025ed37a`; 2026-07-21; Windows/.NET 10 preview |
| R90-RET-003 | retention-security-matrix/v1 | HTTP routes | reads and all mutation POSTs | default local | Local Monitor | Loopback bind, Host validation, same-origin reads, CSRF POST gate, JSON-only body, `no-store`, no CORS, and fixed error bodies remain enforced | `RetentionMutationSecurityMatrixTests`; `RetentionMutationHttpMatrixTests` | N/A | Cross-site, invalid Host, missing CSRF, content/error contract rows passed | PASS | none | none | #91 | `4d966472bdbe1ccc27f57c05f3afc268025ed37a`; 2026-07-21; Windows/.NET 10 preview |
| R90-RET-004 | retention-security-matrix/v1 | catalog/read/worker | delete-now | default local | Session content adapter + #89 worker | Queueing causes immediate read denial; physical deletion is asynchronous and owned only by #89 | `RetentionLifecycleIntegrationTests`; read/worker suites | N/A | Zero selector materialization before worker; exact source removal at terminal worker transition | PASS | none | none | #91 and #89 | `4d966472bdbe1ccc27f57c05f3afc268025ed37a`; 2026-07-21; Windows/.NET 10 preview |
| R90-RET-005 | retention-security-matrix/v1 | restart and replay | delete/retry | default local | SQLite catalog + Session adapter | Restart/retry preserves revision, tombstone, and no-resurrection state | lifecycle, catalog, migration, WorkerRace, and adapter suites | N/A | Restart worker completed exact revisions; late replay rolled back; unrelated fresh write succeeded | PASS | none | none | #91 | `4d966472bdbe1ccc27f57c05f3afc268025ed37a`; 2026-07-21; Windows/.NET 10 preview |
| R90-RET-006 | retention-security-matrix/v1 | mutation target resolution | pin, unpin, delete-now | default local | retention catalog | Ownership is exact store instance/kind/source identity; no path, proximity, or inferred membership authorizes mutation | target-resolution, ownership, mutation HTTP, and lifecycle suites | N/A | Exact target set and binding mismatch rows passed | PASS | none | none | #91 | `4d966472bdbe1ccc27f57c05f3afc268025ed37a`; 2026-07-21; Windows/.NET 10 preview |
| R90-RET-007 | retention-security-matrix/v1 | Canvas | navigation only | default local | Canvas helper | Canvas can navigate to the Local Monitor retention surface but cannot preview, confirm, mutate, or hold confirmation material | Canvas helper/contract suites and E3 independent review | N/A | Navigation-only contract passed; no mutation action was added | PASS | none | none | #91 | `4d966472bdbe1ccc27f57c05f3afc268025ed37a`; 2026-07-21; Windows/.NET 10 preview |
| R90-RET-008 | retention-security-matrix/v1 | Local Monitor display | read/UI | sanitized-only and content-disabled applicability | Razor/JavaScript | Existing framework text encoding remains the display boundary; unavailable content does not create a mutation bypass or add defense-in-depth machinery outside D020 | UI contract and Playwright suites | N/A | Inert rendering and unavailable/rejected flows passed within the canonical local-first boundary | PASS | none | none | #91 | `4d966472bdbe1ccc27f57c05f3afc268025ed37a`; 2026-07-21; Windows/.NET 10 preview |
| R90-RET-009 | retention-security-matrix/v1 | repository evidence and test diagnostics | all | all tested local profiles | tests/docs | No raw/PII, credential, private locator, or plaintext token is committed or exposed through assertion formatting | repository scan; F2 semantic assertion audit; `git diff --check` | N/A | No live confirmation/owner token or token-bearing response/DOM/URL/request collection reaches xUnit formatting | PASS | none | none | #91 | `4d966472bdbe1ccc27f57c05f3afc268025ed37a`; 2026-07-21; Windows/.NET 10 preview |

Independent F2 review found token-bearing failure diagnostics in the initial
tests and one owner-token-bearing snapshot. Both were replaced by boolean
predicates with fixed generic messages and redacted snapshots. Final independent
review: PASS with no findings. Focused security review suites: 38/38; affected
F2 validation: 185/185; Retention validation: 453/453.

## F3 handoff to Issue #88

Schema: `retention-restore-handoff/v1`. These are consumer invariants, not a
claim that restore or backup purge is implemented by Issue #90.

| invariant_id | consumer_issue | trigger | required_consumer_behavior | fixed_state_or_code | evidence_refs | validation_candidate_sha |
| --- | --- | --- | --- | --- | --- | --- |
| R90-BR-001 | #88 | Restore input refers to an item already deleted by retention | Preserve the deleted tombstone and read denial; do not silently recreate raw bytes | `deleted` tombstone remains terminal | F1-03, F1-04, R90-RET-004, R90-RET-005 | `4d966472bdbe1ccc27f57c05f3afc268025ed37a` |
| R90-BR-002 | #88 | A restore would reintroduce bytes for a tombstoned identity | Reject by default; if #88 defines reintroduction, require a new explicit restore confirmation contract rather than reusing #90 mutation authority | no implicit resurrection | F1-04, R90-RET-005 | `4d966472bdbe1ccc27f57c05f3afc268025ed37a` |
| R90-BR-003 | #88 | Restore imports retained raw material | Preserve the original authoritative capture time, policy, version, and TTL clock; never replace them with restore/import/current time | original retention clock | canonical raw-store normalization spec; F1 lifecycle evidence | `4d966472bdbe1ccc27f57c05f3afc268025ed37a` |
| R90-BR-004 | #88 | Restore imports catalog/lifecycle state | Restore a transactionally consistent lifecycle, revision, operation/audit linkage, and exact ownership proof or reject the restore | exact ownership; no proximity inference | R90-RET-005, R90-RET-006 | `4d966472bdbe1ccc27f57c05f3afc268025ed37a` |
| R90-BR-005 | #88 | Backup artifacts may outlive primary deletion | Surface backup non-purge as a warning only. #90 does not inventory backups, purge backups, or claim that a backup exists | `backup_not_purged` warning semantics belong to #88 | scope statement in this review | `4d966472bdbe1ccc27f57c05f3afc268025ed37a` |

Handoff records may carry only opaque identifiers, store kind, revision, safe
digests, and timestamps required by the consumer contract. They must not carry
raw content, comments or PII, credentials, confirmation tokens, workflow keys,
database/source keys, local paths/private locators, filenames, backup artifact
locations, backup checksums/counts/cursors, or inferred membership.

## F4 pinned validation

| Command | Result |
| --- | --- |
| `pwsh scripts\agent\sync-claude-skills.ps1 -Check` | PASS, 5 shared skills up to date |
| `dotnet build CopilotAgentObservability.slnx` | PASS, 0 warnings, 0 errors |
| `pwsh scripts\test\install-playwright-chromium.ps1` | PASS, exit 0 |
| `dotnet test CopilotAgentObservability.slnx` | PASS, Doctor 263 + ConfigCli 4,257 + LocalMonitor 1,976 = 6,496; 0 failed, 0 skipped |
| `git diff --check` before the implementation commit | PASS |

The collector example validation was not applicable because Issue #90 changed
no `infra/otel-collector` files.

## Independent review summary

- E1: initial independent validation exposed catalog contention and route error
  classification defects; corrections were independently re-reviewed PASS.
- E2: history ordering, cursor binding, 100/101 boundaries, restart behavior,
  same-origin, and no-leak handling independently reviewed PASS.
- E3: committed/replayed UI state, status supplement, failure recovery, and
  navigation-only Canvas contract independently reviewed PASS.
- E4: real-host Playwright lifecycle and bounded error matrix independently
  reviewed PASS after canonical fixture and token-safe assertion corrections.
- F1/F2: final independent reviews both PASS with no remaining findings.

## Unverified scope

No required Issue #90 implementation or automated validation scope remains
unverified. Issue #91 consumes the security rows above, and Issue #88 consumes
the restore/backup invariants above; those follow-up implementations remain
outside Issue #90. Remote publication and external Issue closure were not
authorized and were not performed.

## Outcome

PASS. Issue #90 is locally complete and validated at
`4d966472bdbe1ccc27f57c05f3afc268025ed37a`.
