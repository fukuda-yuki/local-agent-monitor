# Sprint9 M3 — Storage + Migration (Plan)

Status: **Planned** — to be challenge-reviewed via the Codex companion `review`
path before implementation (per `CLAUDE.md`).
Author role: Claude (orchestrator) per `CLAUDE.md`.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` → `docs/spec.md` → `docs/specifications/`. Sprint context:
[../../README.md](../../README.md) (M3 milestone row; *Dependencies & risks*
backfill note F2/C2).

## Objective

Persist the M2 span projection. Apply an **additive** `schema_version` bump that
adds the `monitor_spans` table + rollup columns on `monitor_traces`, make span
projection idempotent in the projection worker, and backfill existing
Sprint8-processed `raw_records` — preserving the existing concurrency / readiness
/ lag contract.

## Scope

In scope:
1. Additive migration: new `monitor_spans` table; new `monitor_traces` rollup
   columns (input/output/total tokens, `turn_count`, `agent_invocation_count`,
   `duration_ms`, primary model). `schema_version` bump.
2. Idempotent span projection in the worker; **robust idempotency key including a
   span ordinal** (tolerates missing / duplicate `span_id`).
3. **Projection-version + backfill**: re-project already-processed records for
   spans + new rollup columns; span-projection progress tracked **independently
   of `monitor_ingestions`**, so a processed-but-span-less record is detected and
   not hidden as backlog 0; partial failure → retryable state / readiness, never
   a silent gap.
4. **Mandatory upgrade test from a Sprint8-populated DB.**

Out of scope (deferred):
- Read API (M4), UI (M5), security matrix + live validation (M6).
- Any change to existing concurrency / lag / readiness *behavior* beyond the
  additive table + columns.

## Tasks
- [ ] Add the additive migration (`monitor_spans` + `monitor_traces` columns,
      `schema_version` bump).
- [ ] Persist M2 projections with the ordinal-inclusive idempotency key.
- [ ] Add projection-version + backfill with independent span-projection progress.
- [ ] Add the Sprint8-populated-DB upgrade test + idempotency/backfill tests.

## Acceptance criteria
- Sprint8-populated-DB upgrade test green: spans + rollup columns backfilled.
- Independent span-projection progress: a processed-but-span-less record surfaces
  as remaining work, not backlog 0.
- Idempotency: re-running projection does not duplicate span rows; missing /
  duplicate `span_id` tolerated via the ordinal key.
- Existing concurrency / readiness / lag tests stay green.

## Validation
```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```
Pass criteria: 0 build errors; existing tests stay green; the Sprint8-DB upgrade
test + backfill/idempotency tests present and passing.

## Dependencies
- Depends on **M2** (projection shape) and **M1** (schema + migration spec).

## Review
- Challenge-reviewed via the Codex companion `review` / adversarial path
  (read-only) before implementation, per `CLAUDE.md`. Record the outcome here (or
  in a sibling `review.md`).
