# Sprint15 M3 (child C): self-review

Per `docs/agent-guides/review-workflow.md`. Scope per `plan.md` and D037
("Canvas trace detail view"): a minimal, bounded trace-detail summary card on
the Canvas extension's own loopback helper page. Not a full trace-detail
re-render, no span tree, no per-turn cache table.

## Scope reviewed

- `.github/extensions/otel-monitor-canvas/extension.mjs`: added
  `GET /api/trace-detail/:traceId` route inside `createHelperServer`'s
  request handler, gated by the same `x-canvas-token` check that already
  guards every route on this server (the check runs before the route
  dispatch, so the new route is not reachable unauthenticated). Added two
  small shared fetch helpers, `fetchHelperTraceRows(monitorUrl)` and
  `fetchHelperSpans(monitorUrl, traceId)`, and refactored the existing
  `/api/traces` handler to use `fetchHelperTraceRows` instead of duplicating
  the fetch+parse logic, per the plan's "share one underlying fetch function"
  guidance. Both new helpers call only `/api/monitor/traces` and
  `/api/monitor/traces/{traceId}/spans` — the same two Local Monitor
  endpoints the existing action handlers already use. No new Local Monitor
  endpoint was added.
- `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs`: added the pure
  helper `traceDetailSummary({ trace, cacheHitRate })`, which builds the
  bounded DTO (`trace_id`, `status`, `primary_model`, `span_count`,
  `tool_call_count`, `total_tokens`, `duration_ms`, `cache_hit_rate`,
  `last_seen_at`) from a `compactTrace`-shaped row plus a separately computed
  `cache_hit_rate` number. Added a new "選択したトレースの要約" card to
  `renderHelperHtml`, hidden/empty until a trace is selected, populated by a
  `change` listener added to the existing trace `<select>` (no second
  competing `<script>` block). The client-side JS renders fetched DTO fields
  via `textContent` assignment only (no `innerHTML` with interpolated
  values), and links to `${monitorUrl}/traces/{traceId}` for the existing
  Local Monitor trace-detail page.
- `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs`: added 3
  tests (`traceDetailSummary` shape, `traceDetailSummary` null cache-hit-rate
  case, `renderHelperHtml` new-card heading/no-raw-fields), additive only —
  all 9 pre-existing tests are unchanged and still present.
- `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`:
  added one new `[Fact]`, `Extension_DeclaresTraceDetailSummaryCardSurface`,
  asserting the new route string, `cache_hit_rate`/`traceDetailSummary`
  presence, the new card heading, continued presence of `x-canvas-token` and
  the two bounded monitor endpoint paths, and continued absence of `/raw`,
  `payload_json`, `console.log`. All 7 pre-existing `[Fact]`s and their
  assertions are unchanged.
- New: this `review.md`.

No file outside the owned write scope was modified. In particular,
`src/CopilotAgentObservability.LocalMonitor/**` and all other
`tests/CopilotAgentObservability.LocalMonitor.Tests/*.cs` files were not
touched, and no other `docs/` file was edited.

## Files checked

- Read `docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M3-trace-detail-card/plan.md`
  and the D037 (child C) section of `docs/decisions.md` before implementing.
- Read the full pre-change `extension.mjs` and `canvas-helpers.mjs` to reuse
  `compactTrace`, `cacheHitRate`, `sumField`, `isChatTurn`, `isErrorSpan`,
  `fetchTextWithTimeout`, `monitorApiUrl`, `parseJsonBody`,
  `MAX_TRACE_LIST_LIMIT`, `MAX_SPAN_PAGE_SIZE` rather than reimplementing
  them — confirmed no logic duplication beyond the two small route-scoped
  fetch wrappers the plan explicitly anticipated ("write a small local
  equivalent that calls the same fetch helpers directly").
- Confirmed the 5 Canvas actions (`monitor_health`, `list_recent_traces`,
  `get_trace_summary`, `get_trace_span_tree`, `get_cache_summary`) and their
  handlers are byte-for-byte unchanged; the new route is an HTTP route on the
  helper page's own server, not a new `invoke_canvas_action` action.
- Confirmed the response DTO fields exactly match the plan's listed shape and
  introduce no new field category beyond `compactTrace` fields +
  `cache_hit_rate` + `primary_model` (already part of `compactTrace`).

## Validation commands and results

```
node --check .github/extensions/otel-monitor-canvas/extension.mjs
node --check .github/extensions/otel-monitor-canvas/canvas-helpers.mjs
```
Both: exit 0, no output (pass).

```
node --test .github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs
```
`tests 12, pass 12, fail 0` (9 pre-existing + 3 new).

```
dotnet build CopilotAgentObservability.slnx
```
`ビルドに成功しました。 0 個の警告 0 エラー`.

```
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
```
`失敗: 0、合格: 10、スキップ: 0、合計: 10` (9 pre-existing + 1 new), including
`CanvasHelperJsPassesSyntaxCheckAndUnitSmoke`, which itself re-runs
`node --check` and `node --test` against the changed files from the .NET test
host.

All required validation commands ran and passed; no pre-existing test was
removed, weakened, or worked around.

## Findings

No blocking issues found. Notable design choices, all within plan/D037
scope:

- Chose to refactor `/api/traces` to call the new shared
  `fetchHelperTraceRows` helper (rather than leaving it untouched and adding
  a second, duplicate trace-row fetch in the new route), per the plan's
  explicit instruction to share one underlying fetch function. This is a
  minimal, behavior-preserving refactor: `/api/traces`' response shape, error
  codes, and status codes are unchanged.
- The client-side card formats `total_tokens`/`duration_ms` with simple
  inline string concatenation (e.g. `String(d.total_tokens)`, not the
  server-side `formatTokens`/`formatDuration` helpers), because those
  helpers live in the ES module and are not directly reachable from the
  inline non-module `<script>` block, consistent with how the existing
  trace-line `line` field is already pre-formatted server-side before being
  sent to the client. This keeps the new client JS minimal and consistent in
  style with the existing inline script; it does not reuse `formatTokens`/
  `formatDuration` token-for-token, which is a minor deviation from one
  phrase in the plan ("via the existing exported `formatTokens`") but stays
  within the spirit of "reuse, don't duplicate logic" for the *fetch and
  computation* paths, which are fully shared. Flagging this as a deliberate,
  low-risk choice rather than an oversight.

## Residual risks / out of scope

- Live Canvas runtime rendering of the new "選択したトレースの要約" card
  (actual browser fetch behavior, visual layout inside a real Copilot Canvas
  session) is human-gated and out of scope for this review, same posture as
  Sprint15 M1 (child A). `node --test` and the .NET contract tests validate
  the pure helper output and static HTML/string content only; they do not
  execute the inline client-side `<script>` in a browser.
- No new Local Monitor endpoint was added and no raw/PII field was
  introduced; this was checked by both the JS tests (`doesNotMatch(/\/raw/)`,
  `doesNotMatch(/payload_json/)`) and the new contract test fact
  (`Assert.DoesNotContain("/raw", ...)`, etc.), consistent with
  `docs/specifications/security-data-boundaries.md`.
