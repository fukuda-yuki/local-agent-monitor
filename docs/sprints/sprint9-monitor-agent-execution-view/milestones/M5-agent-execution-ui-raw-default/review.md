# Sprint9 M5 — Agent-Execution UI + raw default (Review)

Status: **Implemented** — recorded self-review (per `docs/agent-guides/review-workflow.md`).
Implementer: Claude (orchestrator) with direct edits; verified against acceptance criteria.

Sprint-local evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` → `docs/spec.md` → `docs/specifications/`.

## What was implemented

1. **Raw default flip (D023).** `MonitorOptions.EnableRawView` → `SanitizedOnly`;
   `--enable-raw-view` removed (now an unknown-option error), `--sanitized-only`
   added. Raw-detail route `GET /traces/{rawRecordId:long}/raw` is now active by
   default and gated `if (!options.SanitizedOnly)`.
   - `MonitorOptions.cs`, `MonitorHost.cs`, `Pages/Ingestions.cshtml(.cs)`
     (`RawViewEnabled` → `RawAvailable = !SanitizedOnly`).

2. **Trace-detail page `/traces/{traceId}`** (new Razor page, raw-bearing):
   Summary panel (tool calls / tokens / error count / duration), sub-agent span
   tree (built from `parent_span_id` with a flat fallback for absent/unknown
   parents), per-turn token rollup (`llm_call` / `chat` spans), and the raw OTLP
   payload(s) inline as escaped inert text.
   - `Pages/TraceDetail.cshtml` + `.cshtml.cs`.

3. **Trace-list rollup columns + detail link.** `Pages/Traces.cshtml` renders
   `InputTokens`, `OutputTokens`, `TotalTokens`, `TurnCount`,
   `AgentInvocationCount`, `DurationMs`, `PrimaryModel`; the `TraceId` cell links
   to `/traces/{traceId}`.

4. **Raw-bearing route guards.** `IsCrossSiteRequest` made `internal static` and
   reused by the trace-detail PageModel. The page returns `404` under
   `--sanitized-only`, `403` (`cross_origin_forbidden`) on cross-site / foreign
   origin, sets `Cache-Control: no-store`, and `404` for an unknown trace. The
   existing `/raw` route keeps its same-origin + no-store controls.

5. **Read seams (additive, read-only).** `GetMonitorTrace(traceId)` and
   `ListRawRecordsByTraceId(traceId)` added to `RawTelemetryStore`; the existing
   `GetSpansForTrace(traceId)` exposed on `IMonitorProjectionStore`. Raw is fetched
   directly by the indexed `raw_records.trace_id`.

## Spec-compliance notes

- **`--sanitized-only` ⇒ trace-detail page `404`.** The trace-detail page is in the
  raw-bearing route set (`security-data-boundaries.md` L125-152; D023), so under
  `--sanitized-only` the whole page returns `404` (sanitized span data stays
  reachable via `/api/monitor/traces/{traceId}/spans` and the list). This is the
  documented behavior, implemented literally.
- **Inert text only.** All rendering uses default Razor encoding; no `Html.Raw`.
- **List / API / SSE stay sanitized.** No change to `/api/monitor/*` or SSE; the
  trace list is not raw-bearing.

## Verification

```
dotnet test CopilotAgentObservability.slnx
```
Result: build 0 warnings / 0 errors; **509 passing** (300 ConfigCli + 209
LocalMonitor), 0 failed, 0 skipped.

New/updated test coverage:
- `MonitorTraceDetailTests` (new): default render (sanitized sections + raw inline
  + no-store), `--sanitized-only` ⇒ `404` with no raw, cross-site `403`, foreign
  origin `403`, unknown trace `404`.
- `MonitorOptionsTests`: default raw-shown (`SanitizedOnly == false`),
  `--sanitized-only` parsed, removed `--enable-raw-view` rejected.
- `MonitorRawViewTests` / `MonitorUiTests` / `MonitorSecurityBoundaryTests` /
  `MonitorHostTests`: flipped to the new default (raw on; absent only under
  `--sanitized-only`); the `MonitorOptions` parameter rename swept across all test
  call sites.
- `ProjectionWorkerTests.FakeProjectionStore`: implements the three new interface
  members.

## Residual / deferred (M6)

- Full DR6 negative security matrix (no-store on every raw-bearing route incl. the
  trace-detail page across all cases, cross-site matrix, per-field sanitization
  under `--sanitized-only`) and the human-gated live VS Code validation remain M6.
- Sub-agent hierarchy shape is M6-confirmed; the page uses the parent-absent flat
  fallback until then.
- Docs were already aligned to `--sanitized-only` in M1 (`user-guide/local-monitor.md`,
  current specs). `decisions.md` D020/D023 retain `--enable-raw-view` only as
  historical record; Sprint8 `task.md` history is unchanged.
