# Sprint15 M3: Canvas trace detail summary card (child C)

Implements child C per D037. Adds a minimal, bounded trace-detail summary card
to the Canvas extension's own loopback helper page. Does **not** touch the
Local Monitor (`src/CopilotAgentObservability.LocalMonitor/`) — that is M2's
(child B) scope, running in parallel. Does **not** add a new Local Monitor
endpoint; the new route lives entirely on the Canvas extension's own
extension-owned HTTP server (same server that already serves `/`, `/api/traces`,
`/analyze` in `extension.mjs`).

Target files:

- `.github/extensions/otel-monitor-canvas/extension.mjs`
- `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs`
- `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs`
- `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs`
  (additive assertions only — do not remove or weaken any existing assertion
  from Sprint15 M1/child A)
- New: `docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M3-trace-detail-card/review.md`

The five Canvas **actions** (`monitor_health`, `list_recent_traces`,
`get_trace_summary`, `get_trace_span_tree`, `get_cache_summary`) and their
handlers are unchanged. This milestone only adds a new HTTP route on the
helper page's own server plus a small UI card — it does not add a new Canvas
action.

## New route: `GET /api/trace-detail/:traceId` (extension.mjs)

Add alongside the existing `/api/traces` and `/analyze` routes inside
`createHelperServer`'s request handler in `extension.mjs` (same `req.method`/
`path` dispatch pattern; same `x-canvas-token` header-or-query token check
that already gates every route in this server — do not bypass it).

- Validate `traceId` against the existing `TRACE_ID_PATTERN` /
  `matchesTraceId()` already defined in `extension.mjs`; on mismatch, `400`
  `{ error: "invalid_trace_id" }` (same shape `/analyze` already uses).
- Fetch `GET {monitorUrl}/api/monitor/traces/{traceId}/spans?limit=${MAX_SPAN_PAGE_SIZE}`
  using the existing `fetchTextWithTimeout` + `monitorApiUrl` + `parseJsonBody`
  helpers already in `extension.mjs` (same pattern `fetchSpanPage` uses for the
  action handlers — reuse `fetchSpanPage(ctx, traceId)` if its `ctx` shape can
  be satisfied outside an action context; if not, write a small local
  equivalent that calls the same fetch helpers directly with the route's own
  `monitorUrl` closure variable, to avoid duplicating the timeout/error-wrapping
  logic).
- Also fetch the trace's own row: reuse the same approach `findTraceSummary`
  uses (`fetchTracePage` then find by `trace_id`), OR — simpler and cheaper —
  call the existing `/api/traces` proxy logic already in this file (it already
  returns `compactTrace`-shaped items with `line`); refactor so both the
  `/api/traces` handler and the new `/api/trace-detail/:traceId` handler share
  one underlying "fetch sanitized trace row by id" function rather than
  duplicating the fetch+find logic. Whichever path is chosen, do not add a new
  Local Monitor endpoint — only consume `/api/monitor/traces` and
  `/api/monitor/traces/{traceId}/spans`, exactly as the existing action
  handlers already do.
- From the fetched spans, compute `cache_hit_rate` the same way
  `handleGetCacheSummary` does today: filter `isChatTurn`, `sumField(turns,
  "cache_read_tokens")`, `sumField(turns, "input_tokens")`,
  `cacheHitRate(cacheReadTokens, inputTokens)` (all already exported from
  `canvas-helpers.mjs` — reuse them, do not reimplement).
- Response DTO (bounded, all fields already sanitized elsewhere in this
  codebase — no new field category):

  ```json
  {
    "trace_id": "...",
    "status": "ok" | "error",
    "primary_model": "gpt-5" | null,
    "span_count": 12,
    "tool_call_count": 3,
    "total_tokens": 8420,
    "duration_ms": 18200,
    "cache_hit_rate": 0.42 | null,
    "last_seen_at": "2026-...Z"
  }
  ```

  If no trace/spans are found for the id, respond `404`
  `{ error: "trace_not_found" }` (same error code `handleGetTraceSummary`
  already throws via `CanvasError("trace_not_found", ...)`).
- On Local Monitor unavailability, mirror the existing `502`
  `monitor_unavailable` handling already used by `/api/traces`.

Add a pure helper to `canvas-helpers.mjs` (not `extension.mjs`, so it can be
unit tested via `node --test` without a live server) that builds this DTO
shape from a `compactTrace` row + a `cache_hit_rate` number, e.g.
`traceDetailSummary({ trace, cacheHitRate })` returning the object above. Keep
`extension.mjs`'s route handler thin: fetch → compute `cache_hit_rate` via
existing exported helpers → call `traceDetailSummary()` → `sendJson`.

## Helper page card (canvas-helpers.mjs `renderHelperHtml`)

Add a new card below the existing trace `<select>` (inside the existing
"Copilotでこのトレースを分析" card or as its own card directly above it — pick
whichever keeps the HTML readable; a separate card titled
"選択したトレースの要約" is recommended for clarity). Requirements:

- Hidden/empty by default (no trace selected yet); populated via client-side
  JS when the trace `<select>`'s `change` event fires (the existing inline
  `<script>` in `renderHelperHtml` already populates the `<select>` from
  `/api/traces` on load — add a `change` listener next to the existing code,
  not a second competing script block).
- On change: `fetch("/api/trace-detail/" + encodeURIComponent(traceId) + "?t=" + token, { headers: { "x-canvas-token": token } })`,
  then render: 状態 (status, reuse `statusLabel`-style wording), 主要モデル
  (primary_model), トークン合計 (formatted via the existing exported
  `formatTokens`), 所要時間 (via the existing exported `formatDuration`),
  キャッシュヒット率 (format `cache_hit_rate` as a percentage, e.g.
  `Math.round(rate * 100) + "%"`; show `—` when `null`).
- Add a "Local Monitorで詳細を見る" link to
  `${monitorUrl}/traces/{traceId}` (server-rendered `monitorUrl` already
  available to `renderHelperHtml`; build the per-trace href client-side from
  the known `monitorUrl` base + the selected `traceId`, mirroring how the
  existing "Local Monitor をブラウザで開く" link is built from `escapedUrl`).
- All rendering must go through `escapeHtml` for any value sourced from the
  fetched DTO before insertion via `textContent`/`innerHTML` — prefer
  `textContent` assignment in the client-side JS (no `innerHTML` with
  interpolated values) so escaping is moot for the dynamic parts; only the
  static card skeleton uses server-side `escapeHtml`-templated HTML.
- No raw prompt/response text anywhere in this card — only the bounded fields
  listed above.

## Tests

`canvas-helpers.test.mjs` (extend, do not replace existing tests from M1):

- `traceDetailSummary()` returns the expected shape for a sample `compactTrace`
  row + a sample `cache_hit_rate`, including the `null` case.
- `renderHelperHtml(...)` output contains the new card's Japanese heading
  ("選択したトレースの要約" or whatever heading is chosen) and contains
  neither `/raw` nor any raw field name (same style of assertion as the
  existing M1 tests).

`CanvasExtensionContractTests.cs` (extend `Extension_DeclaresM5UiTriggerSurface`
or add a new `[Fact]`, following the existing pattern of reading the
concatenated `extension.mjs` + `canvas-helpers.mjs` via `ReadExtension()`):

- Assert `/api/trace-detail/` route string is present.
- Assert `cache_hit_rate` and `traceDetailSummary` are present.
- Assert the new card's Japanese heading is present.
- Keep all existing boundary-invariant assertions unchanged (no `/raw`, no
  `console.log`, `--sanitized-only` absent, `x-canvas-token`, `randomUUID`,
  the `BOUNDARY_NOTE` constant, the focus enum values) — do not delete or
  weaken them.
- Add the new route to the existing
  `Extension_ActionsFetchOnlyBoundedMonitorEndpoints` fact's expectations if
  appropriate (it already asserts `/api/monitor/traces` and `/spans` are
  present and `/raw`/`payload_json`/`console.log` are absent — the new route
  must keep those absences true).

## Validation

```powershell
node --check .github/extensions/otel-monitor-canvas/extension.mjs
node --check .github/extensions/otel-monitor-canvas/canvas-helpers.mjs
node --test .github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs
dotnet build CopilotAgentObservability.slnx
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
```

Record a short self-review in `review.md` next to this file (scope, files
touched, validation commands + results, findings, residual risks — including
that live Canvas runtime rendering of the new card is human-gated and handed
off, same as M1) per `docs/agent-guides/review-workflow.md`. Do not touch
`src/CopilotAgentObservability.LocalMonitor/**` — that is M2's (child B)
scope.
