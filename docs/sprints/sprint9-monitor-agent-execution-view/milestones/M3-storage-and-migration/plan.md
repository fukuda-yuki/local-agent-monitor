# Sprint9 M3 — Storage + Migration (Plan)

Status: **Implemented** (2026-06-27). Implementation delegated to a Sonnet
subagent and verified by the orchestrator (build/test + baseline-diff review).
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
- [x] Add the additive migration (`monitor_spans` + `monitor_traces` columns,
      `schema_version` 1→2 bump; `AddColumnIfMissing` pragma-guarded ALTERs).
- [x] Persist M2 projections with the ordinal-inclusive idempotency key
      (`UNIQUE(raw_record_id, span_ordinal)` + `INSERT OR IGNORE`).
- [x] Add projection-version + backfill with independent span-projection progress
      (`monitor_ingestions.span_projected_at`; `GetSpanProjectionStatus`).
- [x] Add the Sprint8-populated-DB upgrade test + idempotency/backfill tests.

## Outcome (2026-06-27)

Implemented per the design in `~/.claude/plans/sprint9-m3-buzzing-sundae.md`.
Key files: `RawTelemetryStore.cs` (schema + `ApplySpanProjection` /
`ListUnprocessedForSpanProjection` / `GetSpanProjectionStatus` / `GetSpansForTrace`
/ `GetTraceRollup`), `MonitorProjectionRows.cs` (`MonitorSpanRow`,
`MonitorTraceRollupRow`), `IMonitorProjectionStore.cs` (interface + adapter),
`ProjectionWorker.cs` (Phase-2 span loop, single event per pass),
`MonitorSpanProjectionStoreTests.cs` (8 new tests incl. mandatory v1→v2 upgrade),
`raw-store-normalization.md` (`span_id` / `start_time` → `TEXT NULL` reconciliation).

Span rollup is recomputed from the full persisted span set per trace via
`MonitorTraceRollupBuilder.ComputeRollup` (single source of truth, correct across
multi-record traces). Readiness/lag contract unchanged: span backlog is tracked
independently but does **not** gate `/health/ready`; span failures use the existing
`RecordProjectionFailure` retry path.

Validation: `dotnet build` 0 warnings / 0 errors; `dotnet test` 496/496 green
(ConfigCli 300, LocalMonitor 196), stable across 5 consecutive LocalMonitor runs.
A pre-existing Windows file-lock flake in the shared `MonitorTempDirectory.Dispose`
(surfaced under the added parallel test load) was hardened with a bounded
retry/best-effort cleanup — test-infra only, no product behavior change.

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
