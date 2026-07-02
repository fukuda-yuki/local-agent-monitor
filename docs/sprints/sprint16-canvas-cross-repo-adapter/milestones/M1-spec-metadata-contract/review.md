# Sprint16 M1 self-review

## Scope reviewed

Sprint16 M1 is documentation-only. It promotes the Canvas cross-repo
distribution and sanitized repository metadata contract into the current source
of truth before M2 projection or M3/M4 Canvas implementation.

## Files checked

- `docs/requirements.md`
- `docs/spec.md`
- `docs/specifications/layers/raw-store-normalization.md`
- `docs/specifications/layers/telemetry-ingestion.md`
- `docs/specifications/security-data-boundaries.md`
- `docs/decisions.md`
- `docs/task.md`
- `docs/sprints/sprint16-canvas-cross-repo-adapter/README.md`

## Spec conflict resolved

Older Sprint11/Sprint12/Sprint15 text said the Canvas adapter adds no schema,
projection column, or API response field. M1 keeps that rule as the default and
adds only the Sprint16 scoped exception: sanitized `repository_name`,
`workspace_label`, and `repo_snapshot`, derived from existing recommended OTLP
Resource Attributes `repo.name`, `workspace.name`, and `repo.snapshot`.

The raw boundary is unchanged. Raw prompt / response bodies, tool arguments /
results, PII, credentials, tokens, local sensitive paths, and raw OTLP payloads
remain forbidden in Canvas action responses, logs, committed outputs,
repository-safe artifacts, static dashboard, and GitHub Pages snapshots.

## Behavior now authorized

- `.github/extensions/otel-monitor-canvas/` is the single copyable Canvas
  extension distribution unit for Sprint16.
- Local Monitor projection schema version 3 may add nullable `monitor_traces`
  columns `repository_name`, `workspace_label`, and `repo_snapshot`.
- Sanitized `/api/monitor/*`, Canvas helper routes, and bounded Canvas action
  DTOs may expose these fields after monitor projection sanitization.
- Existing projected rows are not automatically backfilled; missing metadata
  remains null and Canvas helper UX uses `unknown repository`.
- No mirror folder, package manifest, lockfile, dependency, current repo
  auto-match, new raw endpoint, or extra repository identity fields are
  authorized.

## Validation

Ran on 2026-07-02:

```powershell
git diff -- docs\requirements.md docs\spec.md docs\specifications docs\decisions.md docs\task.md docs\sprints\sprint16-canvas-cross-repo-adapter
```

Result: documentation-only changes. Build and test were not run for this
milestone because no executable code, project files, or runtime artifacts
changed.

## Findings

No blocking issues found in the documentation contract. The remaining risk is
implementation drift in M2-M4; those milestones must add tests that enforce the
sanitization, nullable migration, no-backfill, and Canvas bounded DTO behavior
defined here.
