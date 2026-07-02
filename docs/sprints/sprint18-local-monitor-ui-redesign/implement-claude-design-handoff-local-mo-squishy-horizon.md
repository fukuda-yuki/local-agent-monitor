# Local Monitor UI Redesign — Implementation Plan

## Context

The design handoff at `.claude/design_handoff_local_monitor/README.md` (2026-07-03, design/IA finalized) specifies a full UI redesign of the Local Ingestion Monitor (`http://127.0.0.1:4320`): developer-first, token-cost-centric, Console-type IA (208px sidebar + master-detail), 7 screens (Overview / Trace list / Trace detail flow+waterfall / Span inspector / Error analysis mode / Copilot drawer / Diagnostics-via-popover), a new blue-tinted dark hex token palette (§10), and pixel-faithful reproduction of the design canvas `ローカルモニター再設計.dc.html` (screenshots 01–08 as previews; canvas HTML is ground truth for exact values).

Target: existing `src/CopilotAgentObservability.LocalMonitor/` — Razor Pages + vanilla JS (no build step) + single `wwwroot/monitor.css`, sanitized DOM generation (`createElement`/`textContent` only, zero `innerHTML`), vendored Noto Sans JP/Mono fonts.

**User-confirmed decisions:**
1. **New raw-bearing JSON route** for the span inspector (`GET /traces/{traceId}/spans/{spanId}/detail`) following the D032/D039 pattern (same-origin, `no-store`, unregistered/404 under `--sanitized-only`).
2. **Copilot drawer follow-up chat = history resend**: each follow-up creates a new analysis run whose prompt embeds prior Q&A; client holds the transcript; no server-side session state.
3. **All 7 screens** in this work item, milestone-ordered, verify+commit per milestone.

Verified facts: `RawTelemetryStore.MonitorSchemaVersion = 3` (bump to 4); `docs/decisions.md` ends at D041 (new records start at D042); `MonitorTraceRollup` currently has no cache/status fields.

## Conflict resolutions (design vs existing invariants)

- **C1 Nav**: written IA wins — 2-item nav (概要/トレース) everywhere, including on `/diagnostics` itself (canvas A4's 3-item sidebar is stale).
- **C2 Tokens hex vs OKLCH**: `monitor.css` `:root` switches to literal hex per §10 (pixel-exact mandate); `DESIGN.md` updated to declare hex authoritative. Tests asserting `oklch(0.15 0.012 264)` (`MonitorUiTests`, `MonitorDesignViewPlaywrightTests`) are rewritten.
- **C3 Detail tabs removed** (`タブは廃止`): `#tab-summary/#tab-timeline/#tab-tree/#tab-cache` ids disappear; dependent tests rewritten in M5.
- **C4 Trace list**: card list → table + master-detail; `TracesPage_RendersTraceCards` etc. rewritten in M4. Route `/traces` unchanged.
- **C5 取り込み履歴**: no new route; collapsible ingestion-history section at the bottom of Diagnostics (from existing `GET /api/monitor/ingestions`); popover button links to `/diagnostics#ingestion-history`.
- **C6 Backward compat**: existing public routes (`/api/monitor/*`, `/health/*`, `/events`, `/v1/traces`, raw-bearing routes) never change shape/ordering. All new needs = **new** endpoints. `CanvasExtensionContractTests.cs` must pass unmodified.
- **C7 Font weight 600**: not vendored → map design "600" to vendored 700 in CSS (documented accepted deviation).
- **C8 Prompt search**: server-side TraceId substring match + client-side filtering of loaded rows' prompt labels only. Full-corpus prompt search out of scope (documented limitation + follow-up in `docs/task.md`).

## Architecture

**Pages** (keep 4 Razor Pages, same routes, same raw-bearing/sanitized split):
- `Pages/Index.cshtml(.cs)` → Overview (raw-bearing: prompt labels in TOP5/recent server-rendered).
- `Pages/Traces.cshtml(.cs)` → Trace list master-detail (raw-bearing: per-row prompt labels server-rendered).
- `Pages/TraceDetail.cshtml(.cs)` → Trace detail; inspector/error-mode/drawer are **in-page states**, not routes.
- `Pages/Diagnostics.cshtml(.cs)` → Diagnostics (no nav item; reached from popover; direct URL still works).
- `Pages/Shared/_Layout.cshtml` → new shell: 208px sidebar, logo, 2-item nav (トレース has count badge), status badge + popover, endpoint label.

**JS**: retire `monitor-views.js`; small page-scoped modules, each no-ops when its root element is absent; all keep the sanitized-DOM invariant and a boundary header comment:
`monitor-shell.js` (badge/popover), `monitor-overview.js`, `monitor-tracelist.js`, `monitor-flow.js`, `monitor-waterfall.js`, `monitor-cache-panel.js` (adapt existing cache logic from `monitor-views.js`), `monitor-inspector.js`, `monitor-error-mode.js`, `monitor-drawer.js`. `monitor.js` trimmed to SSE gap-recovery only.

**CSS**: rewrite `monitor.css` `:root` to §10 hex values; keep existing `--monitor-*` names where 1:1 (e.g. `--monitor-bg: #14171e`, `--monitor-accent: #4da3e8`), add new tokens (surfaces `#11141a/#1a1e28/#12151c/#161a22/#1c2a3d/#2a3141/#212736`; borders `#2a3141/#232a38/#1c212c/#2f4a68`; ink `#e5e9f2/#9aa4b8/#697487`; semantic gold `#e0aa4e`, amber `#d9a942`, green `#4cc38f`/`#2c5844`, red `#e05e50`, purple `#9d7bf0`, teal `#56c8d8`/`#2e7d8c`, elbow `#2a3a4e`; Copilot set `#c0b6f2/#2b2340/#4a4080/#3d3560/#8f8ffa`). Delete component CSS in the same milestone its markup dies.

**Data-fetch split**: raw-bearing content (prompt labels, span detail, analysis) = server-rendered on raw-bearing pages or explicit raw-bearing JSON routes gated by `!SanitizedOnly`; numeric aggregates/span metadata/readiness = client fetch from `/api/monitor/*` + `/health/ready`.

**URL state** (§8): `?view=flow|waterfall&span=<id>` on `/traces/{id}` via `history.replaceState`; selected span / errors-only / inspector state survive flow⇄waterfall toggle.

## Backend work

### Schema v3→v4 + rollup (additive, no backfill)
- `MonitorTraceRollup.cs`: add `int? CacheReadTokens, int? CacheCreationTokens, string TraceStatus` ("ok"|"recovered"|"unrecovered"). Cache sums use the same root-invoke-agent-else-chat no-double-count rule as tokens. `TraceStatus`: no error spans → ok; last span (by StartTime, fallback SpanOrdinal) error → unrecovered; else recovered.
- `RawTelemetryStore.cs`: `AddColumnIfMissing` for `cache_read_tokens`, `cache_creation_tokens`, `trace_status` on `monitor_traces`; bump `MonitorSchemaVersion` to 4; extend `ApplySpanProjection` read-back SELECT + UPDATE; extend `ListMonitorTraces`/`GetMonitorTrace`. Old rows stay NULL (excluded from rates; list filter treats NULL as "unknown", neutral marker) — documented limitation, D040 precedent.
- `MonitorProjectionRows.cs`: extend `MonitorTraceRow` (+2 cache fields, `TraceStatus`).
- New `RawTelemetryStore.Overview.cs` (make class `partial`): `GetPeriodSummary(start,end)`, `GetPerModelPeriodSummary`, `GetHourlyTokenDistribution`, `ListTopTokenTraces`, `ListRecentMonitorTraces(limit)` (ORDER BY last_seen_at DESC), `ListMonitorTracesFiltered(MonitorTraceListQuery)` (offset paging; search/model/status/period/sort). Add signatures to `IMonitorProjectionStore`.

### New sanitized endpoints (register unconditionally in `MonitorHost.cs`)
- `GET /api/monitor/overview?period=today|7d|30d` → `{period, range, kpi:{tokens_total, tokens_previous_period, tokens_change_pct, effective_input_tokens, cache_compression_pct, cache_read_rate_pct, error_trace_count, trace_count}, per_model:[…], hourly_tokens:[…]}`. `effective_input = cache_read*0.1 + (input − cache_read)`. Implemented in new `Projection/MonitorOverviewService.cs` (mirrors `MonitorSummaryService` DI pattern).
- `GET /api/monitor/trace-list?q&model&status&period&sort&offset&limit` → `{items:[trace dto + cache fields + trace_status], total_matched, offset, limit}`. Numeric/enum fields only — no prompt text (sanitized invariant).

### New raw-bearing route (inside `!options.SanitizedOnly` block)
- `GET /traces/{traceId}/spans/{spanId}/detail`: same-origin check (`IsCrossSiteRequest` → 403), `no-store`, 404 unknown span. New `IMonitorProjectionStore.GetMonitorSpan(traceId, spanId)` single-row lookup → `RawRecordId` → `GetRawRecordById` → extract span object from `PayloadJson`.
- New `SpanDetailExtractor.cs` (modeled on `MonitorPromptExtractor`: pure, exception-safe, best-effort): returns tool info (arguments, result tail lines, `chars/4` token estimate labeled 推定, exit code), llm info (per-role message sizes/previews, response preview, cache/uncached breakdown), and always `RawSpanJson` (raw tab works even when formatted extraction fails). **Risk**: real payload key names unconfirmed — same live-validation caveat as the existing prompt extractor; note in `docs/task.md` open follow-ups.

### Copilot drawer chat (history resend)
- Extend `AnalysisStartPayload` with `string? Question, IReadOnlyList<AnalysisHistoryTurnPayload>? History`; thread through `MonitorAnalysisContext`; `DotNetCopilotRawAnalysisRunner.BuildPrompt` appends history Q/A block + follow-up question. No `monitor_analysis_runs` schema change; history not persisted server-side.

## Milestones

Each M1–M9: `dotnet build CopilotAgentObservability.slnx` → targeted tests → `dotnet test CopilotAgentObservability.slnx` → commit. Commit prefix: `Local Monitor UI Redesign <type>: <summary>` (work item name + Conventional Commits, matching Sprint17 style).

- **M0 Docs/decisions**: append D042 (redesign IA/tokens/screens + C1–C8 resolutions), D043 (span-detail raw route), D044 (schema v4 rollup columns), D045 (chat = history resend) to `docs/decisions.md`; update `docs/spec.md` route list (~lines 48–96); `docs/specifications/security-data-boundaries.md` (new route subsection D039-style + analysis Question/History note); `docs/task.md` new work item (実装中) + `docs/sprints/sprint18-local-monitor-redesign/README.md`; `DESIGN.md` token rewrite (hex authoritative, 600→700 note); check `docs/requirements.md` for UI-screen mentions.
- **M1 Backend foundation**: schema v4, rollup, overview/trace-list store queries + endpoints. Tests: new `MonitorOverviewEndpointTests.cs`, `MonitorTraceListEndpointTests.cs` (model on `MonitorSummaryEndpointTests.cs`); extend projection store/builder tests for cache columns + trace_status (ok/recovered/unrecovered/null). No UI change; pages render as before.
- **M2 Shell + tokens**: `_Layout.cshtml` sidebar/popover, `monitor-shell.js`, `monitor.js` trim, `monitor.css` token swap. Rewrite token-string assertions (`MonitorUiTests.Theme_…`, Playwright `--monitor-bg` → `#14171e`); new `MonitorShellPlaywrightTests.cs` (popover open/Esc/診断 link).
- **M3 Overview** (§6.1): rewrite `Index.cshtml(.cs)` (KPI ×4, mid 3 cards, lower 2 cards; TOP5/recent server-rendered with prompt labels), `monitor-overview.js` (period toggle → `/api/monitor/overview`). Rewrite dashboard tests; new `MonitorOverviewPlaywrightTests.cs`. KPI/行 links → filtered `/traces`.
- **M4 Trace list** (§6.2): rewrite `Traces.cshtml(.cs)` (toolbar, grid table `minmax(0,1fr) 128px 148px 64px 64px 56px`, token heat bar, default tokens-desc sort, "さらに読み込む" paging, 392px preview panel), `monitor-tracelist.js` (filters via `/api/monitor/trace-list`, per-row prompt via existing `/traces/{id}/prompt-label`, preview via existing spans API). Rewrite `TracesPage_*` tests; new `MonitorTraceListPlaywrightTests.cs`.
- **M5 Trace detail flow/waterfall + cache column** (§6.3): rewrite `TraceDetail.cshtml(.cs)` (breadcrumb + Copilot button + prev/next, title/pill/meta, token-total card, 2-column body); `monitor-flow.js` (vertical rail, turn cards, elbow connectors, parallel groups = same parent + overlapping [start,end], turn collapsing, intent-label heuristic from operation/tool sequence per §9 "or span 名" fallback), `monitor-waterfall.js` (time axis, `├─`/`└─` aligned parallel rows), `monitor-cache-panel.js`; delete `monitor-views.js`; URL state. Major rewrite of `MonitorDesignViewPlaywrightTests.cs`, update `MonitorTraceDetailTests.cs` + `MonitorViewsScript_*` assertions; add `SeedRichTrace` fixture to `MonitorTestHelpers.cs` (parallel trio, cache turn, recovered retry pair, unrecovered terminal error — reused M5–M8).
- **M6 Span inspector + raw route** (§6.4): `SpanDetailExtractor.cs`, route in `MonitorHost.cs`, `monitor-inspector.js` (panel swaps into cache-column slot; 整形 default / raw tab; Esc/✕/re-click closes; gated on `Model.RawAvailable`). New `MonitorSpanDetailRouteTests.cs` (404-under-sanitized / no-store / 403 cross-site / 404 unknown, tool vs llm shapes); extend `MonitorSecurityBoundaryTests.cs`; new `MonitorInspectorPlaywrightTests.cs`.
- **M7 Error analysis mode** (§6.5): error summary strip, errors-only default ON for error traces, recovered (amber) / unrecovered (red) cards, right column error list/detail/token-trend (128K red dashed line); exception message from M6 route. `monitor-error-mode.js`. New `MonitorErrorModePlaywrightTests.cs`. Resolve against canvas 4a-2 whether recovered-only traces default to error mode.
- **M8 Copilot drawer + chat** (§6.6): payload/runner extension, drawer markup in `TraceDetail.cshtml` (replace old inline analysis script), `monitor-drawer.js` (472px drawer, focus → run → findings, suggestion chips, follow-up chat with history resend, "該当スパンを表示" cross-highlight into flow, required copy "ローカル SDK 経由 · raw はローカルから出ません"). Extend `MonitorAnalysisRouteTests.cs` (question/history captured by stub runner); new `MonitorDrawerPlaywrightTests.cs` with fake runner.
- **M9 Diagnostics** (§6.7): pixel alignment (pipeline 4 cards + component table + thresholds), ingestion-history section (C5). Update `Diagnostics_RendersReadinessWithoutRawOrPii`; extend shell popover test click-through.
- **M10 Reconciliation + validation + docs closeout**: full suite green incl. unmodified `CanvasExtensionContractTests.cs`; finalize `DESIGN.md`, `docs/task.md` (完了 + validation summary), spec wording pass. Pinned validation: `dotnet build CopilotAgentObservability.slnx` → `pwsh scripts\test\install-playwright-chromium.ps1` → `dotnet test CopilotAgentObservability.slnx`.

## Test strategy

- Stable hooks: `id` for singletons (`#span-inspector`, `#copilot-drawer`, `#trace-list-table`), `data-*` for parametrized (`data-span-id`, `data-view`, `data-period`), semantic classes for repeats (`.flow-node`, `.trace-row`) — continues existing convention, no `data-testid` layer.
- Every new Playwright class: `[Collection(PlaywrightBrowserPathCollection.Name)]`, theory-parametrized `sanitizedOnly` where relevant, asserts no raw-route fetch from sanitized contexts.
- New sanitized endpoints get negative assertions (no prompt-shaped strings in JSON), reusing the sensitive-marker seed pattern from `MonitorUiTests.cs`.

## Risks

1. `RawTelemetryStore.cs` schema sensitivity → additive-only migration, new queries in partial-class file.
2. Span-detail extraction key names unconfirmed → defensive fallback ("raw tab always works"), live-validation caveat at closeout (no false "done").
3. Pixel fidelity has no automated gate → per-milestone Playwright screenshots at 1440px into scratchpad (never committed) compared against `screenshots/0N-*.png`; canvas HTML consulted for exact values during each screen milestone.
4. Scope size → foundation-first ordering; each milestone independently shippable/committable.

## Verification

Per milestone: build + targeted tests + full `dotnet test CopilotAgentObservability.slnx`; Playwright smoke for the milestone's screen; manual screenshot comparison vs handoff. Final: pinned three-command validation sequence, full suite, `CanvasExtensionContractTests` unmodified-green as the backward-compat proof. Live VS Code Copilot Chat payload validation for the span-detail extractor remains a documented human-gated follow-up.
