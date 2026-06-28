# Sprint9 BUG_ISSUE - Fix Backlog

This directory is the staged bug backlog for Sprint9 (Local Ingestion Monitor -
Agent Execution Detail). It is organized for the next step:

1. pick one feature unit;
2. write a focused fix plan from the relevant bug cards;
3. implement and validate that unit before moving to the next unit.

Scope reviewed: branch `sprint9-monitor-agent-execution-view` vs `main`
(56 files, +5479/-560). Source of truth: `docs/requirements.md` ->
`docs/spec.md` -> `docs/specifications/` -> current Sprint9 sprint docs.

Each filed finding was independently checked against source before being
recorded. Claims that did not hold up stay in each file's "Evaluated but not
filed" section and are not part of the fix backlog.

## Recommended Fix Order

1. **M2 / M3 projection correctness**
   - Fix headline token rollup and poison/backfill projection gaps first.
   - These affect whether the monitor can be trusted.
2. **M5 trace-detail raw rendering**
   - Fix missing/unsafe raw-detail behavior after projection data is reliable.
3. **M6 milestone evidence consistency**
   - Reconcile the human-gated live-validation documents after the functional
     fixes and validation state are clear.
4. **Low-severity polish**
   - Batch only if already touching the same files.

## Fix Cards

| Card | Feature unit | Severity | Status | Plan boundary |
| --- | --- | --- | --- | --- |
| [M2-1](M2-span-projection.md#M2-1) | Token rollup | High | Fixed | Root `invoke_agent` is selected by span hierarchy; child-before-root regression added. |
| [M2-2](M2-span-projection.md#M2-2) | Token rollup | Medium | Fixed | Any emitted usage component counts as agent-level usage; total-only usage is preserved. |
| [M3-1](M3-storage-migration.md#M3-1) | Span backfill readiness | High | Fixed | Span backlog/failures are surfaced in readiness body without making span backlog a readiness gate. |
| [M3-2](M3-storage-migration.md#M3-2) | Span projection robustness | Medium | Closed | Missing trace id was already dropped by `INSERT OR IGNORE`; blank/null trace ids are explicitly skipped and backlog still drains. |
| [M5-1](M5-agent-execution-ui.md#M5-1) | Trace-detail raw lookup | Medium | Fixed | Trace-detail raw lookup resolves records through `monitor_spans.raw_record_id`. |
| [M5-4](M5-agent-execution-ui.md#M5-4) | Trace-detail raw rendering | Medium | Fixed | Trace-detail renders bounded raw previews and links to the single-record raw route. |
| [M6-1](M6-security-live-validation.md#M6-1) | Live-validation docs | Medium | Fixed | Part B remains pending unless user-confirmed; candidate evidence no longer marks the milestone complete. |
| [M2-3](M2-span-projection.md#M2-3) | Turn-count semantics | Low | Closed | Current spec defines `turn_count` as all `chat` / LLM spans; no behavior change made. |
| [M2-4](M2-span-projection.md#M2-4) | Error type sanitization | Low | Fixed | `error_type` now uses an identifier/class-token policy instead of the generic secret substring guard. |
| [M2-5](M2-span-projection.md#M2-5) | Finish reason sanitization | Low | Fixed | Malformed serialized finish-reason arrays are dropped; only string tokens are stored. |
| [M2-6](M2-span-projection.md#M2-6) | Multiple root token rollup | Medium | Fixed | Multiple root `invoke_agent` usage fields are summed; child `invoke_agent` spans remain excluded from trace-level totals. |
| [M2-7](M2-span-projection.md#M2-7) | Token rollup overflow | Low | Fixed | Summed / derived token fields use range-safe accumulation and become null when outside the nullable `int` projection range. |
| [M3-3](M3-storage-migration.md#M3-3) | Span query performance | Low | Closed | Optional composite index deferred until span volumes justify it. |
| [M5-2](M5-agent-execution-ui.md#M5-2) | Raw-bearing route headers | Low | Fixed | `Cache-Control: no-store` is set before trace-detail early returns. |
| [M5-3](M5-agent-execution-ui.md#M5-3) | Trace-detail busy handling | Low | Fixed | Trace-detail maps `PersistenceBusyException` to `503 persistence_busy`. |

## Feature Files

| File | Purpose | Active cards |
| --- | --- | --- |
| [M2-span-projection.md](M2-span-projection.md) | Projection builder, token rollup, field sanitization | M2-1 through M2-5 |
| [M3-storage-migration.md](M3-storage-migration.md) | Additive migration, span backfill, projection progress | M3-1 through M3-3 |
| [M5-agent-execution-ui.md](M5-agent-execution-ui.md) | Trace-detail page, raw default behavior, raw lookup/rendering | M5-1 through M5-4 |
| [M6-security-live-validation.md](M6-security-live-validation.md) | Security boundary validation records and human-gated live evidence | M6-1 |
| [codex_adversarial_review.md](codex_adversarial_review.md) | Raw Codex review output retained as evidence | Duplicate source for M2-1, M3-1, M3-2, M5-1, M5-4 |

**M4 - Sanitized read API:** reviewed by sub-agent and Codex; no valid defect
was filed. The sanitized-only invariant, cursor pagination on the unique key,
and invalid-query `400` behavior held during review.

## Fix Card Template

When creating a repair plan from one of these cards, keep the plan at this
granularity:

- **Problem:** one observable defect, not a theme.
- **Source of truth:** requirement/spec line or sprint acceptance item.
- **Touched surface:** smallest production files and tests needed.
- **Regression fixture:** synthetic input that failed before the fix.
- **Validation:** targeted test first, then repository-required build/test if
  code or workflow changed.

## Severity Legend

- **High** - incorrect headline data or a reliability failure that can persist
  silently; fix before relying on the feature.
- **Medium** - real correctness or robustness gap on an edge or upgrade path;
  fix recommended.
- **Low** - minor robustness, hygiene, performance, or usability; safe to defer
  unless already touching the same file.
