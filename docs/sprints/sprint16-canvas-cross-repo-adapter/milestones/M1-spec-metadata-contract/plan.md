# Sprint16 M1: Spec and metadata contract

M1 promotes the Sprint16 behavior into the current source of truth before any
code or extension packaging changes. This milestone is documentation-only but
changes product/public-interface intent, so it must be reviewed as a spec
boundary change.

## Target files

- Modify: `docs/requirements.md`
- Modify: `docs/spec.md`
- Modify: `docs/specifications/layers/raw-store-normalization.md`
- Modify: `docs/specifications/layers/telemetry-ingestion.md`
- Modify: `docs/specifications/security-data-boundaries.md`
- Modify: `docs/decisions.md`
- Modify: `docs/task.md`
- Create: `docs/sprints/sprint16-canvas-cross-repo-adapter/milestones/M1-spec-metadata-contract/review.md`

## Tasks

- [ ] Read the current Canvas and Local Monitor sections in every target file
  before editing. Confirm the existing statements that say the Canvas adapter
  adds no new API field/schema are superseded only for the bounded sanitized
  metadata fields in this sprint.
- [ ] Add a new decision in `docs/decisions.md` for Sprint16. Record:
  `.github/extensions/otel-monitor-canvas/` is the single copyable distribution
  unit; no mirror folder; no new dependencies/package manifest; metadata source
  is only existing OTLP attributes `repo.name`, `workspace.name`, and
  `repo.snapshot`; no automatic backfill for existing projected rows.
- [ ] Update `docs/requirements.md` and `docs/spec.md` to allow the Canvas
  adapter and Local Monitor sanitized APIs to expose only
  `repository_name`, `workspace_label`, and `repo_snapshot`. Keep the ban on
  raw prompt/response bodies, tool arguments/results, PII, credentials, tokens,
  local sensitive paths, and raw OTLP payloads.
- [ ] Update `docs/specifications/layers/raw-store-normalization.md` with
  monitor projection schema version 3 and additive nullable columns on
  `monitor_traces`. Specify that values are sanitized and existing rows are not
  backfilled automatically.
- [ ] Update `docs/specifications/layers/telemetry-ingestion.md` to connect
  the existing recommended resource attributes to the Local Monitor projection
  fields. Do not make these attributes required for repository-safe datasets.
- [ ] Update `docs/specifications/security-data-boundaries.md` to state that
  the new metadata is allowed in `/api/monitor/*`, Canvas helper routes, and
  bounded Canvas action DTOs only after sanitization. Confirm this is not a new
  raw endpoint and does not weaken raw-preview or prompt-label boundaries.
- [ ] Update `docs/task.md` so Sprint16 appears in Planned Work and points to
  the sprint directory.
- [ ] Write `review.md` documenting the spec conflict resolved, files checked,
  and the exact behavior now authorized.

## Validation

```powershell
git diff -- docs\requirements.md docs\spec.md docs\specifications docs\decisions.md docs\task.md docs\sprints\sprint16-canvas-cross-repo-adapter
```

Expected: only documentation changes. No code, project files, lockfiles, or
runtime artifacts are changed.

## Commit

```powershell
git add docs\requirements.md docs\spec.md docs\specifications docs\decisions.md docs\task.md docs\sprints\sprint16-canvas-cross-repo-adapter
git commit -m "Sprint16 Canvas cross-repo adapter: docs: define metadata contract"
```
