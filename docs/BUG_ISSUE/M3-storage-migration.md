# M3 — Storage + additive migration + backfill

Feature: additive `schema_version` v1→v2 migration (`monitor_spans` table +
rollup columns on `monitor_traces`), idempotent span projection, backfill of
Sprint8-processed records, with the concurrency / readiness / lag contract
preserved.

## Fix-unit index

| Card | Severity | Fix unit | Plan note |
| --- | --- | --- | --- |
| M3-1 | High | Span-backfill progress surfacing | Fixed: span projection backlog and failure count are surfaced in readiness body. |
| M3-2 | Medium | Missing/blank `traceId` poison record | Closed: missing trace id was already ignored; blank/null trace ids are explicitly skipped. |
| M3-3 | Low | Span cursor performance index | Closed without code change: defer until span volumes justify migration DDL. |

Primary next plan: M3-1 + M3-2 as one storage/backfill reliability fix plan.
Keep M3-3 optional unless the migration is already being touched.

Source of truth: README "Milestones" M3 row + "Token rollup rule";
`docs/specifications/layers/raw-store-normalization.md`;
`docs/specifications/layers/telemetry-ingestion.md`;
`docs/sprints/.../milestones/M3-storage-and-migration/plan.md`.

Key files: `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs`,
`src/CopilotAgentObservability.LocalMonitor/Projection/ProjectionWorker.cs`.

---

<a id="M3-1"></a>

## M3-1 — Span-projection backlog is computed but surfaced nowhere; readiness can report `ready` while spans are still missing — High (confidence: High) [Codex P1 + Claude sub-agent]

- **Location:** `src/CopilotAgentObservability.LocalMonitor/Projection/ProjectionWorker.cs:134-135`
  (sets health from `GetProjectionStatus()` **before** the span phase and never
  consults `GetSpanProjectionStatus()`); `RawTelemetryStore.cs:826-842`
  (`GetSpanProjectionStatus` is implemented but called only from tests);
  `MonitorHealthState` has no span-backlog field.
- **Spec:** M3 scope / acceptance (plan.md:28-33, 74-75) and
  raw-store-normalization.md: *"span-projection progress tracked **independently
  of `monitor_ingestions`**, so a processed-but-span-less record is detected and
  **not hidden as backlog 0**; partial failure → retryable state / readiness,
  **never a silent gap**."* Acceptance: *"a processed-but-span-less record
  surfaces as remaining work, not backlog 0."*
- **Observed:** The independent counter exists (`GetSpanProjectionStatus` counts
  `monitor_ingestions WHERE span_projected_at IS NULL`) but is **wired to
  nothing** — not `/health/ready`, not the readiness body's
  `projection_backlog`, not a log or metric. `/health/ready` reflects only the
  **trace** backlog (`GetProjectionStatus`).
- **Impact:** Concrete failure on the very upgrade path M3 exists for: on a
  Sprint8-populated DB all records already have `monitor_ingestions` rows, so the
  trace backlog is `0` immediately, while **every** record needs span backfill.
  The worker backfills `BatchSize = 100` per pass, so with >100 records the
  agent-execution view is empty/incomplete for many traces yet `/health/ready`
  returns `ready` with `projection_backlog: 0`. The same blind spot hides a
  permanently failing span projection (e.g. M3-2 below) — `RecordProjectionFailure`
  only bumps a counter that is likewise absent from the readiness body. This
  contradicts the acceptance criterion and the "never a silent gap" rule.
- **Nuance (for fairness):** plan.md:62-64 records a deliberate choice that span
  backlog *does not gate* `/health/ready`. Not gating can stand, but the data
  must still **surface** somewhere (readiness body, a degraded-reason, or a log)
  to satisfy "detected and not hidden / never a silent gap"; today it does not.
- **Recommendation:** Either include the span backlog in the readiness body
  (non-gating is acceptable) or emit a log/metric when span backlog > 0 after the
  trace backlog drains; add a test asserting a Sprint8-upgrade window reports
  remaining span work rather than a clean `ready`.
- **Resolution:** Fixed. Readiness body now includes independent
  `span_projection_backlog`, `span_projection_lag_seconds`, and
  `projection_failure_count`; span backlog is surfaced as a degraded reason
  without becoming a readiness gate.

<a id="M3-2"></a>

## M3-2 — A span with missing/blank `traceId` violates `monitor_spans.trace_id NOT NULL`, wedging that record's span projection on every pass — Medium (confidence: High) [Codex P2]

- **Location:** schema `RawTelemetryStore.cs:136` (`trace_id TEXT NOT NULL`);
  builder emits every span unconditionally `MonitorSpanProjectionBuilder.cs:99`
  with `TraceId: span.TraceId` (`:143`), which is null when the OTLP span has no
  `traceId` (`OtlpSpanReader.cs:72` `ReadString(span, "traceId")`); insert sends
  `DBNull` `RawTelemetryStore.cs:701`
  (`span.TraceId ?? (object)DBNull.Value`).
- **Spec:** M2/M3 robustness intent — missing attributes should **degrade**, not
  fail; the ingestion/trace-projection contract already lets a no-trace record
  complete (`raw_records.trace_id` is nullable, `:60`).
- **Observed:** For accepted OTLP JSON containing a span object with a
  missing/blank `traceId`, the span builder produces a `TraceId == null` row and
  the insert pushes `DBNull` into the `NOT NULL` column → SQLite constraint
  violation → `ApplySpanProjection` throws → the transaction rolls back →
  `span_projected_at` is never stamped. The worker's non-busy `catch`
  (`ProjectionWorker.cs:163-167`) records a failure and moves on, but the record
  stays unprojected and **retries every pass forever** (poison record).
- **Impact:** A single malformed/edge span permanently fails span projection for
  its raw record and (compounded with M3-1) does so invisibly. Asymmetric with
  trace projection, which tolerates the same record.
- **Recommendation:** Skip (or route to the unknown-span evidence path) spans
  whose `traceId` is null/blank **before** insert, consistent with "degrade, do
  not fail". Add a fixture with a trace-less span and assert the record still
  stamps `span_projected_at` (drops only the bad span).
- **Resolution:** Closed with hardening. The missing-`traceId` wedge did not
  reproduce because `INSERT OR IGNORE` already drops the NOT NULL violation, but
  span projection now explicitly skips null/blank trace ids and still stamps the
  raw record as span-projected.

<a id="M3-3"></a>

## M3-3 — Span cursor query has no composite index for `(trace_id, id)` — Low (confidence: High behavior / Low impact) [Claude sub-agent]

- **Location:** DDL `RawTelemetryStore.cs:166-171` (only
  `IX_monitor_spans_trace_id` and `IX_monitor_spans_raw_record_id`); query
  `ListMonitorSpans` `RawTelemetryStore.cs:849-865` uses
  `WHERE trace_id = $trace_id AND id > $after ORDER BY id`.
- **Observed:** The `trace_id` index supports the equality seek, but the
  `id > $after` range + `ORDER BY id` within a trace is not covered, so SQLite
  filters then sorts per page.
- **Impact:** Negligible on a single-user local DB with small per-trace span
  counts; only a deeply paginated, very large trace would notice. No correctness
  impact.
- **Recommendation:** Optional — add `IX_monitor_spans_trace_id_id ON
  monitor_spans(trace_id, id)` if span volumes grow.

---

## Evaluated but not filed (verified correct)

- **Additive, idempotent migration:** `CREATE TABLE IF NOT EXISTS` +
  pragma-guarded `AddColumnIfMissing` ALTERs; `schema_version` upsert is
  `ON CONFLICT DO UPDATE` (safe on a fresh-v2 DB); no DROP/recreate of Sprint8
  tables. The mandatory Sprint8→v2 upgrade test builds a genuinely populated
  pre-v2 DB and asserts backfill + rollup + backlog drain.
- **Ordinal-inclusive idempotency:** `UNIQUE(raw_record_id, span_ordinal)` +
  `INSERT OR IGNORE`; ordinal assigned by enumeration order, independent of
  `span_id`; tolerates null/duplicate `span_id`; re-projection does not duplicate.
- **Transaction boundaries / partial-failure safety:** span inserts + rollup
  update + stamp are one transaction; a throw rolls back the whole unit (no
  partial spans, no false "done" — except the M3-2 poison-record case, which is
  the *symptom*, not a transactional leak).
- **Concurrency / busy handling preserved:** per-call connections, WAL +
  `busy_timeout`, `PersistenceBusyException` → retry next pass; busy status read →
  `projection_status_unknown` so readiness withholds `ready`.
- **Independent span progress source:** the span queue uses
  `monitor_ingestions.span_projected_at IS NULL` (correct), not record existence.
  (The gap is only that the resulting backlog is never surfaced — M3-1.)
