# Issue #95 Cost Analytics And Budget Alerts Implementation Plan

> Local-only execution plan. Base: P2 foundation
> `245de89b0d016012a68e29ed00309c9cc768e81a`.

## Objective

Implement Issue #95 without weakening the exact #94 pricing boundary or
forking the accepted alert, lifecycle, Alert Center, export, or backup
authorities.

## Fixed ownership

- #95 owns pricing configuration, exact estimate persistence, explicit
  recalculation, cost API/UI, analytics, budget-rule implementations, evidence,
  and direct validation rows `91-A-095`, `91-S-095`, and `91-L-095`.
- #80 owns additive alert snapshot/config/evaluation/receipt v2 contracts and
  the version-2 migration of the existing alert-engine component.
- #83 keeps lifecycle v1 unchanged and accepts immutable v1 or v2 receipt
  parents from the same alert-engine store.
- #84 owns additive version-aware Alert Center reads and presentation.
- #85 keeps sanitized evidence bundle v1 closed and exports exact receipt-v1
  rows only from a recognized alert-engine-v2 database.
- #88 recognizes the pricing component in backup/restore. The migration tail is
  `historical_instruction_analysis -> historical_import -> sanitized_import ->
  runtime_backup -> pricing`.

## Contract order

1. Add canonical Issue #95 requirements, interface, security, architecture,
   decision, migration, and validation-matrix contracts.
2. Write failing contract/schema tests for pricing persistence and alert v2
   while pinning all v1 golden bytes and public shapes.
3. Implement alert v2 in the existing Alerts domain and SQLite engine store.
4. Implement append-only pricing component v1 and strict #94 byte reload.
5. Implement exact Session-to-pricing composition and explicit billing/budget
   configuration.
6. Implement `/api/costs/v1/*`, `/costs`, safe catalog/immutable configuration
   reads, recalculation history/delta, analytics, accessibility, and the
   repository/Release-ZIP startup-wrapper override forwarding contract.
7. Add lifecycle, Alert Center, sanitized-export, and runtime-backup owner
   compatibility.
8. Complete and close the #80/#83/#84/#85/#88 compatibility corrections,
   integrate every accepted revision into the clean #95 worktree, and prove
   their ancestry and migration order before candidate freeze.
9. Freeze the exact immutable #95 candidate, activate the #95 validation
   matrix, run focused/full validation and independent final reviews on that
   unchanged SHA, and materialize repository-safe evidence bound to it. Any
   production/spec/fixture/test behavior change requires a new candidate and
   rerun of invalidated rows.
10. Close #95 only after its candidate-bound matrix and evidence are complete,
    then integrate the accepted P2 revisions into and validate one clean
    immutable P2 final candidate.
11. Reconcile #92/#93 and #94/#95 terminal outcomes, exact SHAs, evidence, and
    blockers in #60. Close #60 only after #93 and #95 are terminal and no
    explicit acceptance item remains; a #92 NO-GO is recorded honestly and
    never rewritten as GO merely to close the parent.

## Non-negotiable invariants

- Persist the exact canonical catalog-snapshot bytes and canonical estimate
  bytes; never reconstruct or substitute the current catalog.
- Use exact Session, Run, trace/event, estimate, predecessor, and receipt
  identities. Never bind by repository, workspace, model name, path, or time
  proximity.
- Missing, partial, unknown, unsupported, stale, and not-estimable facts never
  become zero. Included zero-incremental cost is an explicit estimated result.
- Coverage numerator is estimated Sessions; denominator is all eligible
  Sessions. Partial, missing, failed, and not-estimable Sessions remain in the
  denominator.
- Budget rules are disabled until an explicit configuration supplies currency,
  warning/critical thresholds, window, and minimum coverage.
- Cost reduction is never presented as quality improvement or an automatic
  model recommendation.
- Existing alert v1 bytes, golden hashes, consumers, lifecycle events/routes,
  Alert Center v1 route, sanitized bundle v1, and runtime-backup receipts remain
  compatible.
- No raw prompt/tool/result/body, credential, PII, private override, invoice,
  account/contract identifier, or local path enters cost DTOs, alerts, logs, or
  repository-safe evidence.

## Validation gates

- Nonzero focused tests for Pricing, alert v1/v2, pricing persistence,
  Session composition, cost API/UI, lifecycle/Alert Center, sanitized export,
  runtime backup, startup wrappers/Task Scheduler serialization, and
  cross-migration.
- Skill mirror, solution build, Playwright Chromium bootstrap, and full
  solution tests on the unchanged functional candidate.
- Fresh and supported-upgrade migrations, restart, backup/restore, strict
  malformed/tamper/future-version rejection, scanner self-tests, changed-file
  and evidence scans, artifact existence, and SHA-256 verification.
- `91-A-095` and `91-S-095` must pass. `91-L-095` may be
  `blocked_external/high` only when reviewed genuine provider mappings or
  authorization remain unavailable after all repository-safe work passes.
- Content-enabled capture and Codex Desktop execution are not authorized.
