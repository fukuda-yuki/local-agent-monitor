# M5 — Agent-execution UI + raw default

Feature: trace-detail page (Summary panel, sub-agent span tree, per-turn token
rollup), new trace-list columns, raw bodies inline by default,
`--sanitized-only` switch (`--enable-raw-view` removed).

## Fix-unit index

| Card | Severity | Fix unit | Plan note |
| --- | --- | --- | --- |
| M5-1 | Medium | Raw lookup for secondary trace ids | Fixed: raw lookup uses `monitor_spans.raw_record_id`. |
| M5-4 | Medium | Bounded inline raw rendering | Fixed: inline raw is a bounded preview with full-record links. |
| M5-2 | Low | `no-store` on early returns | Fixed: header is set before trace-detail early returns. |
| M5-3 | Low | DB busy status mapping | Fixed: trace-detail maps persistence busy to `503 persistence_busy`. |

Primary next plan: M5-1 + M5-4 as one trace-detail raw-surface fix plan.
Batch M5-2/M5-3 only if the same page handler is already being edited.

Source of truth: README "Safety boundary" + Decision D023;
`docs/specifications/security-data-boundaries.md`;
`docs/specifications/layers/telemetry-ingestion.md`;
`docs/sprints/.../milestones/M5-agent-execution-ui-raw-default/{plan,review}.md`.

Key files: `src/CopilotAgentObservability.LocalMonitor/Pages/TraceDetail.cshtml{,.cs}`,
`.../Pages/Traces.cshtml`, `.../MonitorHost.cs`, `.../MonitorOptions.cs`,
`src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs`.

---

<a id="M5-1"></a>

## M5-1 — Multi-trace raw payload is not shown for secondary trace ids on the trace-detail page — Medium (confidence: High) [Codex P2]

- **Location:** `RawTelemetryStore.cs:580-591` (`ListRawRecordsByTraceId` matches
  `raw_records.trace_id = $trace_id` only); `raw_records.trace_id` is a single
  value set from `record.TraceId` (`RawTelemetryStore.cs:236`), which is one
  trace id extracted per payload (`RawOtlpIngestor.cs:19` `FindTraceId(...)`);
  the trace-detail page consumes it at `TraceDetail.cshtml.cs:72`.
- **Spec:** the trace-detail page is the inline raw-bearing surface for *a trace*;
  every trace shown in the list should render its own raw payload.
- **Observed:** A single OTLP export request can carry **multiple** trace ids.
  Trace **and** span projection fan out one `monitor_traces` / `monitor_spans`
  set **per distinct trace_id** (`RawTelemetryStore.cs:732`; projection
  contributions in `MonitorProjectionBuilder.cs:40` where `record.TraceId` may not
  even match a contribution and falls back to `contributions[0]`). But
  `raw_records` stores only the **single** primary trace id. So the secondary
  trace appears in the list and its trace-detail page renders the Summary + span
  tree (from `monitor_spans`), yet `ListRawRecordsByTraceId(secondary)` returns
  empty → the inline raw section is blank for that trace.
- **Impact:** Raw bodies silently missing for every non-primary trace of a
  multi-trace raw record. Lower likelihood (the observed Copilot runs sent one
  trace per export, sometimes split across records — the reverse shape), but
  structurally valid for OTLP batched exports.
- **Recommendation:** Resolve raw records for a trace via the projected
  span→`raw_record_id` mapping (`monitor_spans.raw_record_id` for the trace), not
  via `raw_records.trace_id`. Add a multi-trace-in-one-record fixture asserting
  the secondary trace renders its raw payload.
- **Resolution:** Fixed. `ListRawRecordsByTraceId` now resolves raw records
  through `monitor_spans.raw_record_id`, and the trace-detail regression covers a
  secondary trace in one OTLP export.

<a id="M5-2"></a>

## M5-2 — `Cache-Control: no-store` missing on the trace-detail page's `403`/`--sanitized-only 404` short-circuits — Low (confidence: High) [Claude sub-agent]

- **Location:** `TraceDetail.cshtml.cs:35-54` — the `--sanitized-only`
  `NotFound()` (`:37`) and the cross-site `403` (`:44-49`) return **before**
  `Response.Headers["Cache-Control"] = "no-store"` (`:54`). (The unknown-trace
  `404` at `:62` is after `:54` and is covered.)
- **Spec:** security-data-boundaries.md ties `no-store` to "every route in the
  raw-bearing set", rationale "raw / PII is not left in the browser cache".
- **Observed:** The cross-site `403` body is a small JSON error and the
  `--sanitized-only` `404` is empty — **neither contains raw / PII** — so the
  spec's *intent* (no cached raw) is still met; only the header is absent on these
  non-raw responses. The M6 test `AllRawBearingRoutes_SetNoStore` asserts
  `no-store` on the `200` (raw-bearing) responses, which is the case that matters.
- **Impact:** Hygiene gap only; no raw-exposure consequence.
- **Recommendation:** Set `no-store` before the early returns (or in middleware
  for the route) so the header is unconditional on the raw-bearing route.
- **Resolution:** Fixed. Trace-detail sets `Cache-Control: no-store` before
  `--sanitized-only` and cross-site early returns.

<a id="M5-3"></a>

## M5-3 — Trace-detail page returns `500` instead of `503` under DB contention — Low (confidence: High) [Claude sub-agent]

- **Location:** `TraceDetail.cshtml.cs:59,66,72` call `GetMonitorTrace` /
  `GetSpansForTrace` / `ListRawRecordsByTraceId`, each of which can throw
  `PersistenceBusyException` (`IMonitorProjectionStore.cs` adapter guard). The
  page has no busy handler, so it propagates to `UseExceptionHandler` →
  `500 internal_error`, whereas `/api/monitor/*` and `/raw` map busy → `503
  persistence_busy`.
- **Observed:** Inconsistent status code under SQLite busy; the `500` body is the
  generic handler output (`MonitorHost.cs:90`) with **no raw / path / exception
  text**, so there is no information leak.
- **Impact:** Less accurate status under contention; no security impact.
- **Recommendation:** Catch `PersistenceBusyException` on the page and return
  `503` consistent with the other routes.
- **Resolution:** Fixed. Trace-detail catches `PersistenceBusyException` and
  returns `503 persistence_busy`.

<a id="M5-4"></a>

## M5-4 — Trace detail renders unbounded raw payloads inline — Medium (confidence: High) [Codex]

- **Location:** `src/CopilotAgentObservability.LocalMonitor/Pages/TraceDetail.cshtml.cs:72`
- **Spec:** The trace-detail page is the inline raw-bearing surface for a trace. It must be bounded to avoid browser hangs or high memory usage under large trace datasets.
- **Observed:** The page loads every raw record for a trace, and the Razor view renders each full `PayloadJson` inline. With raw default-on and a 30 MiB per-request limit, a trace split over many accepted records can produce hundreds of MiB of HTML, freezing the local UI or exhausting memory.
- **Impact:** Opening `/traces/{traceId}` with many accepted OTLP exports for the same trace id, each near `MaxRequestBodyBytes`, synchronously reads and renders all of them, causing UI freezes or OOM.
- **Recommendation:** Make raw inline bounded: paginate raw records, collapse by default with size limits/previews, and link to the single-record raw route for full payloads. Add a test that the trace-detail page does not render unlimited raw bodies.
- **Resolution:** Fixed. Trace-detail renders bounded raw previews and links to
  `GET /traces/{rawRecordId}/raw` for the full payload.

---

## Evaluated but not filed (verified correct)

- **Raw inline by default; `--sanitized-only` restores metadata-only** (raw routes
  `404`, PII excluded); **`--enable-raw-view` fully removed** (rejected as unknown
  by the parser; only historical doc references remain).
- **Escaped, inert text — no `Html.Raw` bypass:** zero
  `Html.Raw`/`MarkupString`/`IHtmlContent`/`WriteLiteral` in the LocalMonitor
  project; `.cshtml` uses Razor default encoding; `/raw` uses
  `HtmlEncoder.Default.Encode`. A stored `<script>` renders escaped. (Per
  AGENTS.md the absence of extra CSP / XSS-matrix is an accepted de-scope, not a
  finding.)
- **SSE / JSON stay sanitized:** the list, `/api/monitor/*`, and `/events`
  (`data: {}`) carry no raw; raw is consumed only by the two raw-bearing routes.
- **Both raw-bearing routes enforce same-origin + `no-store` on success;**
  sub-agent tree handles null/foreign `parent_span_id` (root fallback) and
  cycles/duplicate ids (`visited` set); empty/missing trace handled before span
  access.
- **Token rollup on the page uses the (single-source) rollup column,** not a
  re-summed double count.
