# Sprint15 M4 (child B remainder): self-review

Per `docs/agent-guides/review-workflow.md`. Scope per `plan.md` and D038
("child B remainder — Canvas side consumer"): a Canvas-owned `GET /api/summary`
proxy route plus a "Local Monitor 概要" card in the helper page. No Local
Monitor change — `GET /api/monitor/summary` already shipped in M2.

## Scope reviewed

- `.github/extensions/otel-monitor-canvas/extension.mjs`: added
  `fetchHelperSummary(monitorUrl, limitQuery)` next to the existing
  `fetchHelperTraceRows`/`fetchHelperSpans`. Unlike those two, it does not
  throw on a non-OK response — see "Findings from self-review" below for
  why. Added `GET /api/summary` inside `createHelperServer`'s handler (after
  the existing `x-canvas-token` check, alongside `/api/traces` and
  `/api/trace-detail/:traceId`): validates `isLoopbackUrl(monitorUrl)`,
  forwards an optional `?limit=` query string to
  `GET {monitorUrl}/api/monitor/summary`. On a non-OK Local Monitor response
  it passes that status code and body straight through (parsing the body as
  JSON when possible, falling back to a generic `monitor_unavailable`
  message otherwise); a genuine network-level failure (a thrown
  `CanvasError` from `fetchTextWithTimeout`) still maps to `502`. On success,
  the D037/D038 response body is passed through with every existing field
  unchanged; the only addition is an **additive derived field** per
  trace-shaped entry — `line` (via the new `summaryTraceLine` helper, not
  `formatTraceLine` directly — see "Findings from self-review" below) on
  `latest_trace`/`top_token_trace`/`error_trace`, and
  `total_tokens_formatted` (via `formatTokens`) on each
  `per_model_summary`/`per_client_kind_summary` row — mirroring the existing
  precedent in `/api/traces`, which already adds a derived `line` field to
  each `compactTrace` row without renaming or dropping any D037 field. This
  fulfills the plan's instruction to reuse `formatTraceLine`/`formatTokens`
  "on each" of the summary's trace-shaped fields while keeping the
  underlying D037/D038 contract intact (no field removed, renamed, or
  reshaped).
- `.github/extensions/otel-monitor-canvas/canvas-helpers.mjs`: added
  `summaryTraceLine(trace)` — a small pure helper that routes a raw
  `/api/monitor/summary` highlight-trace DTO through `compactTrace()` before
  calling `formatTraceLine()`, added during self-review to fix a status bug
  (see "Findings from self-review" below) — and the "Local Monitor 概要" card
  to `renderHelperHtml`, placed above the existing
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
- `.github/extensions/otel-monitor-canvas/canvas-helpers.test.mjs`: added 2
  tests — the card heading/`/api/summary` fetch/no-raw assertion described
  above, plus a regression test for `summaryTraceLine` (added during
  self-review — see below) asserting it renders "エラーあり" for an
  `error_count > 0` row and "OK" otherwise. Additive only; all pre-existing
  tests are unchanged and still present.
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
At initial M4 commit: `tests 13, pass 13, fail 0` (12 pre-existing + 1 new).
After the self-review round 2 fixes below (which added a second, regression
test): `tests 18, pass 18, fail 0` (17 pre-existing across M1–M5 + 1 new for
this milestone's fix — see "Findings from self-review" below; the M5
milestone's own tests account for the rest of the growth from 13 to 18).

```
dotnet build CopilotAgentObservability.slnx
```
`ビルドに成功しました。 0 個の警告 0 エラー` (both at initial commit and after
the round-2 fixes).

```
dotnet test tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj --filter FullyQualifiedName~CanvasExtensionContractTests
```
At initial M4 commit: `失敗: 0、合格: 11、スキップ: 0、合計: 11` (10 pre-existing
+ 1 new). After M5 and the round-2 fixes (no new `[Fact]` was needed for the
fixes themselves — the existing `Extension_DeclaresDashboardSummaryCardSurface`
fact still passes unchanged): `失敗: 0、合格: 12、スキップ: 0、合計: 12`.

All required validation commands ran and passed; no pre-existing test was
removed, weakened, or worked around.

## Findings

No blocking issues found at the time of the initial implementation and
review below. Two real bugs were found and fixed in a later self-review
round — see "Findings from self-review (round 2)" further down. Notable
design choice, within plan/D038 scope, from the initial review:

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

## Findings from self-review (round 2, requested by the user after M4/M5/M6 landed)

A second read-through of the actual diff (`git diff <pre-Sprint15-M4
commit>..HEAD -- extension.mjs`) found two real bugs in the M4 code that the
initial review above missed. Both are fixed on top of the original M4
commit, verified, and re-tested; no scope beyond these two fixes was
touched.

1. **Status bug (functional correctness, CONFIRMED and fixed).**
   `withLine()` originally called `formatTraceLine(trace)` directly on the
   raw `latest_trace`/`top_token_trace`/`error_trace` objects from
   `GET /api/monitor/summary`. Those objects come from
   `MonitorHost.ToTraceDto` in `src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs`,
   which serializes `error_count` (a count) but **never a precomputed
   `status` string**. `formatTraceLine` reads `trace.status` (via
   `statusLabel`), so on these objects it was always reading `undefined` —
   `statusLabel(undefined)` always evaluates to `"OK"`. In practice this
   meant the "Local Monitor 概要" card's "注目トレース" (highlight) lines
   would show `エラー: OK / ...` for the error-trace highlight instead of
   `エラー: エラーあり / ...`, silently defeating the purpose of that
   highlight. This bug existed only in the new `/api/summary` proxy path —
   the pre-existing `/api/traces` route was never affected, because it
   already runs its rows through `compactTrace()` (which derives `status`
   from `error_count`) before calling `formatTraceLine`.

   Fix: added a new pure helper, `summaryTraceLine(trace)`, in
   `canvas-helpers.mjs`, that runs the raw DTO through `compactTrace()`
   first and then `formatTraceLine()` — exactly the same two-step sequence
   `/api/traces` and `/api/trace-detail/:traceId` already use for their own
   rows. `extension.mjs`'s `withLine()` now calls `summaryTraceLine` instead
   of `formatTraceLine` directly. A regression test,
   `summaryTraceLine: derives status from error_count...`, asserts the
   corrected output for both an `error_count: 1` row (expects `エラーあり`)
   and an `error_count: 0` row (expects `OK`), using the same
   `SAMPLE_TRACE_ROW` fixture the rest of the test file already uses (which
   deliberately has no `status` key, matching the real DTO shape).

2. **Plan-deviation bug (masked passthrough, CONFIRMED and fixed).**
   `fetchHelperSummary()` originally converted *any* non-OK Local Monitor
   response into a generic `CanvasError("monitor_unavailable", ...)`, which
   the route then always turned into a `502`. This contradicts the M4
   plan's explicit instruction: "if provided and invalid, let the Local
   Monitor's own `400` response pass through rather than re-validating
   client-side." A client-supplied `?limit=` outside 1–200 genuinely
   produces a `400 {"error":"invalid_query",...}` from
   `GET /api/monitor/summary` (confirmed against `MonitorHost.cs`'s
   `TryParseLimitQuery`/`WriteInvalidLimitQueryAsync`), and the old code
   silently replaced that specific, actionable `400` with an unrelated,
   misleading `502 monitor_unavailable`.

   Fix: `fetchHelperSummary` no longer throws on a non-OK response — it now
   just returns `{ response, body }` (mirroring `fetchTextWithTimeout`
   itself). The route handler checks `response.ok` itself: on failure it
   parses the body as JSON if possible and forwards it with the Local
   Monitor's own status code; only a genuine network-level failure (a
   thrown `CanvasError`, e.g. connection refused or timeout) still falls
   through to the outer `catch` and becomes a generic `502`. No automated
   test was added for this specific passthrough (this extension has no HTTP
   integration-test harness — the established test depth here is
   pure-function unit tests plus string-presence contract tests, per the F8
   tech-debt note in the Sprint15 README); it was verified by code
   inspection against `MonitorHost.cs`'s actual `400` response shape instead.

Neither bug was a data-safety/boundary violation — no raw/PII field was
ever involved in either code path, and both bugs are scoped entirely to the
`/api/summary` proxy's own response-shaping logic, not to any Canvas action,
the M3 route, or M5's raw-preview route. Re-ran the full validation suite
after both fixes (see the updated "Validation commands and results" above,
plus a fresh `dotnet test CopilotAgentObservability.slnx` full-suite pass —
606 passing, 0 failed).
