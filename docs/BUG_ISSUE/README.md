# BUG_ISSUE - Fix Backlog

This directory is the staged bug backlog for Local Ingestion Monitor work. It is
organized for the next step:

1. pick one feature unit;
2. write a focused fix plan from the relevant bug cards;
3. implement and validate that unit before moving to the next unit.

Sprint9 scope reviewed: branch `sprint9-monitor-agent-execution-view` vs `main`
(56 files, +5479/-560). Source of truth: `docs/requirements.md` ->
`docs/spec.md` -> `docs/specifications/` -> current Sprint9 sprint docs.

Each filed finding was independently checked against source before being
recorded. Claims that did not hold up stay in each file's "Evaluated but not
filed" section and are not part of the fix backlog.

## Sprint9 Fix Cards (active — only Open cards remain)

| Card | Feature unit | Severity | Status | Plan boundary |
| --- | --- | --- | --- | --- |
| [M2-8](M2-span-projection.md#M2-8) | Custom operation name allowlist | Medium | Open | Drop unexpected operation names before projection. |
| [M2-9](M2-span-projection.md#M2-9) | Tool type allowlist | Medium | Open | Allowlist tool types before storing. |
| [M5-5](M5-agent-execution-ui.md#M5-5) | Raw-detail route headers | Low | Open | Set `Cache-Control: no-store` on raw-detail error/forbidden responses, not only successful raw responses. |

### Sprint9 Fix Cards (archived → `_archive/`)

All M3 (storage/migration) and M6 (security live-validation) cards are Fixed/Closed and archived. The M2-1 through M2-7 and M5-1 through M5-4 cards are Fixed/Closed but stay in the active feature files because those files still contain Open cards.

### Sprint10 Fix Cards (active — only Open cards remain)

| Card | Feature unit | Severity | Status | Plan boundary |
| --- | --- | --- | --- | --- |
| [S10-3](Sprint10-monitor-design-views.md#S10-3) | Sprint10 completion evidence/state | Medium | Open — live evidence blocked | Record real VS Code Copilot Chat live evidence before marking Sprint10 complete. |

### Sprint10 Fix Cards (archived)

S10-1, S10-2, S10-4, S10-5 are Fixed but stay in the active feature file because it still contains S10-3 (Open).

### Non-sprint Fix Cards (archived in place)

CM-1 is Fixed and remains in its feature file as the record for the Canvas VCS
repository metadata cleanup.

### Documentation UX & Readability Fix Cards (active)

| Card | Feature unit / Aspect | Severity | Status | Plan boundary |
| --- | --- | --- | --- | --- |
| [DOC-1A](doc-ux-readability-issues.md#DOC-1A) | Synthetic demo order in docs vs live setup | High | Open | Separate demo trial & live telemetry setup order |
| [DOC-1B](doc-ux-readability-issues.md#DOC-1B) | Config CLI `success: true` vs real trace arrival | High | Open | Clarify static config vs first-trace arrival verification |
| [DOC-1C](doc-ux-readability-issues.md#DOC-1C) | UI screenshot demo mode labeling | Medium | Open | Add explicit demo dataset watermark & status badge docs |
| [DOC-2A](doc-ux-readability-issues.md#DOC-2A) | `user-guide.md` missing Local Monitor images | Medium | Open | Embed Local Monitor overview images in main user-guide portal |
| [DOC-2B](doc-ux-readability-issues.md#DOC-2B) | `local-monitor.md` end-of-file image dump | Medium | Open | Move images from bottom dump directly below feature sections |
| [DOC-2C](doc-ux-readability-issues.md#DOC-2C) | README vs user-guide screenshot sync | Medium | Open | Harmonize image selections, captions, & UI versions |
| [DOC-3A](doc-ux-readability-issues.md#DOC-3A) | PowerShell ExecutionPolicy script block | Medium | Open | Add `-ExecutionPolicy Bypass` commands & corporate environment notes |
| [DOC-3B](doc-ux-readability-issues.md#DOC-3B) | Port 4320 conflict & VS Code env inheritance | Medium | Open | Add port conflict troubleshooting & terminal scope guidelines |
| [DOC-3C](doc-ux-readability-issues.md#DOC-3C) | Missing Troubleshooting navigation links | Medium | Open | Add direct links to Troubleshooting guide in README & user-guide |
| [DOC-4A](doc-ux-readability-issues.md#DOC-4A) | `user-guide.md` TOC & step-by-step structure | Medium | Open | Restructure user-guide portal with TOC (`cc-switch` model) |
| [DOC-4B](doc-ux-readability-issues.md#DOC-4B) | Task-oriented vs module-oriented layout | Medium | Open | Reorganize files by user goals rather than internal modules |
| [DOC-4C](doc-ux-readability-issues.md#DOC-4C) | Internal dev jargon leakage in user manual | Medium | Open | Remove internal issue #s & implementation jargon from user guide |
| [DOC-5A](doc-ux-readability-issues.md#DOC-5A) | AI-like redundant Japanese phrasing | Medium | Open | Refactor "〜することができる/となります" per `humanizer-ja` |
| [DOC-5B](doc-ux-readability-issues.md#DOC-5B) | Untranslated literal English phrases | Medium | Open | Translate raw English jargon (`reversible setup` etc.) |
| [DOC-5C](doc-ux-readability-issues.md#DOC-5C) | Style inconsistency (常体/敬体) & passive voice | Medium | Open | Standardize tone to natural polite Japanese (です/ます) |

### Commit `15fe4cbf` review cards (active)

| Card | Feature unit / Aspect | Severity | Status | Plan boundary |
| --- | --- | --- | --- | --- |
| [DOC-6A](doc-ux-readability-review-15fe4cbf.md#DOC-6A) | CRLF injected into 2 of 10 files, hiding the real diff | High | Open | Normalize line endings back to LF; add `.gitattributes` to prevent recurrence |
| [DOC-6B](doc-ux-readability-review-15fe4cbf.md#DOC-6B) | Still-valid "Sprint7 unconfirmed" caveat deleted, not reworded | High | Open | Restore the unconfirmed-status caveat in `telemetry-collection.md` without the `Sprint7` label |
| [DOC-6C](doc-ux-readability-review-15fe4cbf.md#DOC-6C) | `static-dashboard.md` intro drill-down targets changed beyond wording | Medium | Open | Confirm intended content change or restore `raw store` / `sensitive bundle` mentions |
| [DOC-6D](doc-ux-readability-review-15fe4cbf.md#DOC-6D) | `local-monitor.md` left ~unconverted despite "all user-facing documentation" claim | Medium | Open | Apply the same 5 readability patterns to `local-monitor.md`, or scope the claim down |
| [DOC-6E](doc-ux-readability-review-15fe4cbf.md#DOC-6E) | BUG_ISSUE index still shows DOC-1..5C as Open while cards are marked Fixed | Low | Open (pre-existing) | Sync this README's index table with each card's actual Status |

## Active Feature Files

| File | Purpose | Open cards |
| --- | --- | --- |
| [M2-span-projection.md](M2-span-projection.md) | Projection builder, token rollup, field sanitization | M2-8, M2-9 |
| [M5-agent-execution-ui.md](M5-agent-execution-ui.md) | Trace-detail page, raw default behavior, raw lookup/rendering | M5-5 |
| [Sprint10-monitor-design-views.md](Sprint10-monitor-design-views.md) | Sprint10 monitor design views, Playwright bootstrap, test reliability | S10-3 |
| [doc-ux-readability-issues.md](doc-ux-readability-issues.md) | Documentation UX, demo mode confusion, image sync, troubleshooting, & Japanese tone | DOC-1A~1C, DOC-2A~2C, DOC-3A~3C, DOC-4A~4C, DOC-5A~5C |
| [doc-ux-readability-review-15fe4cbf.md](doc-ux-readability-review-15fe4cbf.md) | Review of commit `15fe4cbf` (Japanese readability pass): line-ending corruption, dropped caveat, scope creep, `local-monitor.md` coverage gap | DOC-6A~6E |

## Archived Files (`_archive/`)

These files are fully resolved (all cards Fixed or Closed) and moved to
[`_archive/`](_archive/) on 2026-07-04:

| File | Purpose | Cards |
| --- | --- | --- |
| [M3-storage-migration.md](_archive/M3-storage-migration.md) | Additive migration, span backfill, projection progress | M3-1, M3-2, M3-3 (all Fixed/Closed) |
| [M6-security-live-validation.md](_archive/M6-security-live-validation.md) | Security boundary validation records and human-gated live evidence | M6-1 (Fixed) |
| [codex_adversarial_review.md](_archive/codex_adversarial_review.md) | Raw Codex review output retained as evidence | Duplicate source (all deduplicated cards resolved) |
| [codex_pr32_review.md](_archive/codex_pr32_review.md) | Raw Codex review output for PR #32 retained as evidence | Duplicate source (M2-8, M2-9 still tracked in M2 file) |

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
