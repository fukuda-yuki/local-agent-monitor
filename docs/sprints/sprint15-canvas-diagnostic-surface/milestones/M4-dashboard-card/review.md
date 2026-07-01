# Sprint15 M4 (child B remainder): self-review

Per `docs/agent-guides/review-workflow.md`. Scope per `plan.md` and D038
("child B remainder — Canvas side consumer"): a Canvas-owned `GET /api/summary`
proxy route plus a "Local Monitor 概要" card in the helper page. No Local
Monitor change — `GET /api/monitor/summary` already shipped in M2.

## Scope reviewed

- `.github/extensions/otel-monitor-canvas/extension.mjs`: added
  `fetchHelperSummary(monitorUrl, limitQuery)` next to the existing
  `fetchHelperTraceRows`/`fetchHelperSpans`, same fetch+parse+error shape.
  Added `GET /api/summary` inside `createHelperServer`'s handler (after the
  existing `x-canvas-token` check, alongside `/api/traces` and
  `/api/trace-detail/:traceId`): validates `isLoopbackUrl(monitorUrl)`,
  forwards an optional `?limit=` query string to
  `GET {monitorUrl}/api/monitor/summary`, and returns `502
  { error: code, message }` on fetch failure, matching `/api/traces`'s error
  shape. On success, the D037/D038 response body from the Local Monitor is
  passed through with every existing field unchanged; the only addition is
  an **additive derived field** per trace-shaped entry — `line` (via the
  already-imported `formatTraceLine`) on `latest_trace`/`top_token_trace`/
  `error_trace`, and `total_tokens_formatted` (via `formatTokens`) on each
  `per_model_summary`/`per_client_kind_summary` row — mirroring the existing
  precedent in `/api/traces`, which already adds a derived `line` field to
  each `compactTrace` row without renaming or dropping any D037 field. This
  fulfills the plan's instruction to reuse `formatTraceLine`/`formatTokens`
  "on each" of the summary's trace-shaped fields while keeping the
  underlying D037/D038 contract intact (no field removed, renamed, or
  reshaped).
- `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs`: added the
  "Local Monitor 概要" card to `renderHelperHtml`, placed above the existing
  "選択したトレースの要約" card (dashboard-level overview above the per-trace
  cards, per the plan). Populated by a second `fetch("/api/summary?t=" + ...)`
  call added to the existing `<script>` block (not a competing script),
  alongside the pre-existing `/api/traces` fetch. Renders `scope.trace_count`,
  the top 5 `per_model_summary`/`per_client_kind_summary` entries (using the
  server-precomputed `total_tokens_formatted`), and one line each for
  `latest_trace`/`top_token_trace`/`error_trace` (using the
  server-precomputed `line`, falling back to `trace_id` if absent). All
  dynamic values assigned via `textContent`/`appendChild` with `li.textContent`,
  never `innerHTML` with interpolated values. On a non-200 response or fetch
  failure, shows "概要を取得できませんでした: ..." instead of leaving the card
  blank.
- `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs`: added 1
  test asserting the new card heading, that `renderHelperHtml`'s output
  fetches `/api/summary`, and that no raw/`payload_json` substring appears —
  additive only; all 12 pre-existing tests are unchanged and still present.
- `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`:
  added one new `[Fact]`, `Extension_DeclaresDashboardSummaryCardSurface`,
  asserting the new route/helper/card-heading strings, continued presence of
  `x-canvas-token`, and continued absence of `/raw`, `payload_json`,
  `console.log`. All 10 pre-existing `[Fact]`s and their assertions are
  unchanged.
- New: this `review.md`.

No file outside the owned write scope was modified. In particular,
`src/CopilotAgentObservability.LocalMonitor/**` and all other
`tests/CopilotAgentObservability.LocalMonitor.Tests/*.cs` files were not
touched, and no other `docs/` file was edited.

## Files checked

- Read `docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M4-dashboard-card/plan.md`
  and the D037/D038 (child B) sections of `docs/decisions.md` before
  implementing.
- Read `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`'s
  `/api/monitor/summary` handler and `Projection/MonitorSummaryService.cs` to
  confirm the exact response field names (`scope.limit`/`scope.trace_count`,
  `latest_trace`/`top_token_trace`/`error_trace`, `per_model_summary`/
  `per_client_kind_summary` with `model`/`client_kind`/`trace_count`/
  `total_tokens`/`error_count`) before wiring the proxy and the card.
- Read the full pre-change `extension.mjs` and `canvas-helpers.mjs` to reuse
  `fetchTextWithTimeout`, `monitorApiUrl`, `parseJsonBody`, `formatTraceLine`,
  `formatTokens`, `isLoopbackUrl`, and the existing `/api/traces` route's
  error-handling shape, rather than reimplementing any of them.
- Confirmed the 5 Canvas actions and the M1/M3 routes/handlers are
  byte-for-byte unchanged; the new route is an HTTP route on the helper
  page's own server, not a new `invoke_canvas_action` action, and no new
  Local Monitor endpoint was added.

## Validation commands and results

```
node --check .github/extensions/otel-monitor-canvas/extension.mjs
node --check .github/extensions/otel-monitor-canvas/canvas-helpers.mjs
```
Both: exit 0, no output (pass).

```
node --test .github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs
```
`tests 13, pass 13, fail 0` (12 pre-existing + 1 new).

```
dotnet build CopilotAgentObservability.slnx
```
`ビルドに成功しました。 0 個の警告 0 エラー`.

```
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
```
`失敗: 0、合格: 11、スキップ: 0、合計: 11` (10 pre-existing + 1 new).

All required validation commands ran and passed; no pre-existing test was
removed, weakened, or worked around.

## Findings

No blocking issues found. Notable design choice, within plan/D038 scope:

- The plan's route section says to return the Local Monitor's
  `/api/monitor/summary` body "as-is ... do not reshape it", while its card
  section says to reuse `formatTraceLine`/`formatTokens` "on each" of the
  summary's trace-shaped fields. Those two instructions are in tension
  because `formatTraceLine`/`formatTokens` are ES-module exports not
  reachable from the helper page's inline non-module `<script>` (the same
  constraint M3's review already noted for its own client-side card).
  Resolved by adding the formatted values as **additive derived fields**
  server-side (`line`, `total_tokens_formatted`) rather than either dropping
  the formatting or duplicating formatter logic inline in client JS — this
  is the same non-destructive-enrichment pattern already used by the
  pre-existing `/api/traces` route (which adds a `line` field to each
  `compactTrace` row). No D037/D038 field was removed, renamed, or filtered;
  the proxy is additive-only, consistent with the plan's core intent.

## Residual risks / out of scope

- Live Canvas runtime rendering of the new "Local Monitor 概要" card (actual
  browser fetch behavior, visual layout inside a real Copilot Canvas
  session) is human-gated and out of scope for this review — covered once,
  for all of Sprint15's children together, by the README's "Live validation
  handoff" (D038).
- No new Local Monitor endpoint was added and no raw/PII field was
  introduced; checked by both the JS tests (`doesNotMatch(/\/raw/)`,
  `doesNotMatch(/payload_json/)`) and the new contract test fact, consistent
  with `docs/specifications/security-data-boundaries.md`.
