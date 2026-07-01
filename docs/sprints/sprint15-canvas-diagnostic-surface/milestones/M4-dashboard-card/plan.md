# Sprint15 M4: Canvas dashboard card (child B remainder)

Implements the child B remainder per D038. Child B's Local Monitor endpoint
(`GET /api/monitor/summary`, `MonitorSummaryService`) already shipped in M2.
This milestone adds the missing Canvas-side consumer: a proxy route on the
Canvas extension's own loopback server plus a summary card in the helper
page. No Local Monitor changes in this milestone — `src/CopilotAgentObservability.LocalMonitor/**`
is out of scope here.

Target files:

- `.github/extensions/otel-monitor-canvas/extension.mjs` (new `GET /api/summary` route)
- `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs` (new pure formatter(s) + `renderHelperHtml` card)
- `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs` (additive tests)
- `tests/CopilotAgentObservability.LocalMonitor.Tests/CanvasExtensionContractTests.cs` (additive assertions)
- New `docs/sprints/sprint15-canvas-diagnostic-surface/milestones/M4-dashboard-card/review.md`

Read `extension.mjs` and `canvas-helpers.mjs` in full first — this milestone
follows the exact pattern M3 (child C) already established for `/api/traces`
and `/api/trace-detail/:traceId`: same token gate, same `fetchTextWithTimeout`/
`monitorApiUrl`/`parseJsonBody` helpers, same `sendJson`/`CanvasError` error
shape.

## Route: `GET /api/summary` (extension.mjs)

Add alongside the existing `/api/traces` and `/api/trace-detail/:traceId`
routes in `createHelperServer`'s request handler (same `x-canvas-token`
gate already enforced at the top of that handler — do not bypass it).

- No path parameters. Optional `?limit=N` passthrough (same default/range as
  the Local Monitor's own `/api/monitor/summary`: default 50, 1–200) — if
  provided and invalid, let the Local Monitor's own `400` response pass
  through rather than re-validating client-side (simplest: just forward the
  query string).
- Validate `isLoopbackUrl(monitorUrl)` first, same as every other route.
- Fetch `GET {monitorUrl}/api/monitor/summary` (append `?limit=...` if the
  request had one) using `fetchTextWithTimeout` + `monitorApiUrl` +
  `parseJsonBody`, mirroring `fetchHelperTraceRows`'s shape (add a new
  `fetchHelperSummary(monitorUrl, limitQuery)` helper next to
  `fetchHelperTraceRows`/`fetchHelperSpans` for consistency — same error
  handling: non-OK response → `CanvasError("monitor_unavailable", ...)`).
- Response: return the Local Monitor's `/api/monitor/summary` JSON body
  **as-is** (it's already the exact bounded contract from D037/D038 — do not
  reshape it). `sendJson(res, 200, summary)`.
- On `CanvasError` / fetch failure: `502` `{ error: code, message }`, same
  shape as `/api/traces`.

## Helper page card (canvas-helpers.mjs `renderHelperHtml`)

Add a "Local Monitor 概要" card. Placement: above the existing trace
dropdown card (it's a dashboard-level overview, conceptually "zoom out" from
the per-trace cards below it).

- Populated by client-side JS on page load (same pattern as the existing
  trace-dropdown `fetch("/api/traces?t=" + ...)` call already in the
  `<script>` block) — add a second `fetch("/api/summary?t=" + ...)` call
  alongside it, not a competing script block.
- Render: `scope.trace_count`（直近 N 件中）, then up to the top 5 entries
  each of `per_model_summary` and `per_client_kind_summary` (model/client_kind,
  trace_count, total_tokens formatted via the existing exported
  `formatTokens`, error_count), and one line each for `latest_trace` /
  `top_token_trace` / `error_trace` when non-null (reuse `formatTraceLine`
  on each, since they're already `compactTrace`-shaped — this gives a
  consistent one-line format matching the trace dropdown's own formatting).
- All dynamic values via `textContent`, not `innerHTML` with interpolated
  values (same rule M3 followed).
- When the endpoint fails (502/etc.), show a small inline message ("概要を
  取得できませんでした") rather than leaving the card silently blank — reuse
  the existing `setResult`-style pattern already in the script, or a
  dedicated small status element for this card.
- No raw prompt/response, no PII — only fields already present in the Local
  Monitor's `/api/monitor/summary` response.

## Tests

`canvas-helpers.test.mjs` (extend, do not remove/modify existing tests):

- If a new pure formatter is added (e.g. a small helper to build the
  top-N-lines list from `per_model_summary`/`per_client_kind_summary`), unit
  test it directly with a sample summary object.
- `renderHelperHtml(...)` output contains the new card's Japanese heading
  ("Local Monitor 概要" or whatever heading is chosen) and no raw/`payload_json`.

`CanvasExtensionContractTests.cs` (extend `ReadExtension()`-based facts or add
a new `[Fact]`, following M3's `Extension_DeclaresTraceDetailSummaryCardSurface`
as the template):

- Assert `/api/summary` route string is present.
- Assert the new card's Japanese heading is present.
- Keep all existing assertions from M1/M3 unchanged (do not weaken any).

## Validation

```powershell
node --check .github/extensions/otel-monitor-canvas/extension.mjs
node --check .github/extensions/otel-monitor-canvas/canvas-helpers.mjs
node --test .github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs
dotnet build CopilotAgentObservability.slnx
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
```

Record a self-review in `review.md` next to this file per
`docs/agent-guides/review-workflow.md`. Note that live Canvas runtime
rendering is explicitly OUT of this milestone's scope — it's covered once,
for all of A–D together, by the Sprint15 README's "Live validation handoff"
(D038). Do not attempt to verify live rendering yourself; do not touch
`src/CopilotAgentObservability.LocalMonitor/**`.

## Sequencing note

M5 (child D, raw preview) touches the same two files
(`extension.mjs`/`canvas-helpers.mjs`) as this milestone. Implement M4 and M5
**sequentially in one session** (not as two parallel subagents) to avoid a
merge conflict — finish and commit M4 first, then start M5 on top of it.
