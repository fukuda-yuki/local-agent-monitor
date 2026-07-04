# Sprint10 M2 — A3 Visual Polish (Plan)

Status: **Done** — code + tests; recorded self-review below. build 0/0, tests
537 passing (300 ConfigCli + 237 LocalMonitor). Author role: Claude
(orchestrator) per `CLAUDE.md`.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` → `docs/spec.md` → `docs/specifications/`. Sprint context:
[../../README.md](../../README.md) (milestone row + *Scope* A3 + *Safety boundary*
+ *Vendor provenance*). Decisions: `docs/decisions.md` D024–D028 (D027 theme,
D028 fonts).

## Objective

Deliver the **foundation-first** visual layer of Sprint10 (sequence A3 → A1 → A4
→ A2): a dark VS Code–styled theme, locally vendored Noto typography, tidied
layout/navigation, a readable trace list, and a TraceDetail tab shell — all as
**presentation only**. No telemetry input, schema, API field, route, or
raw/sanitized boundary change (D024). Gates nothing; gated by M1.

## Scope (in scope)

1. **Theme** — `wwwroot/monitor.css` rewritten to a VS Code Dark+ palette on CSS
   custom-property tokens + an 8px spacing scale; table/card/tab/row-expand
   styles; `.span-op` indent via a `--depth` custom property (replaces the inline
   `padding-left`).
2. **Fonts (D028, trimmed)** — Noto Sans JP 400/500/700 + Noto Sans Mono 400
   (latin + japanese subsets) vendored under `wwwroot/vendor/fonts/` with
   `OFL.txt`; `@font-face` (no-range JP face + latin-range face per weight); no
   CDN, no glyph subsetting, no build step. Provenance recorded in the README.
3. **Layout/nav** — themed `_Layout` header/nav across all five pages.
4. **Trace list** — `Traces.cshtml`: 7 primary columns (TraceId-link,
   ClientKind, TotalTokens, ToolCallCount, ErrorCount, TurnCount, PrimaryModel) +
   a per-row expand button revealing the remaining 14 columns (a collapsed detail
   row; all data stays server-rendered).
5. **TraceDetail tab shell** — `TraceDetail.cshtml`: `Summary | Timeline | Flow
   Chart | Cache` tablist. Summary + Timeline panes keep the existing
   server-rendered tables; Flow Chart + Cache are empty placeholder panes. Raw
   OTLP section unchanged below. Tab switching + row-expand in
   `wwwroot/monitor-views.js` (Vanilla JS, page-guarded).

## Decisions resolved with the user (2026-06-28)

- **Staged migration** for the TraceDetail upper section (vs. the README's
  "JS-rendered / empty container" wording). M2 ships the tab shell with
  Summary/Timeline still server-rendered, so the existing `MonitorTraceDetailTests`
  stay green **unmodified**. Per-tab JS rendering is deferred: Flow Chart → M3,
  Timeline filter/sort → M4, Cache → M5 (each validated by Playwright in M6).
- **Trimmed font set** (narrows D028's "full weight set"; recorded in D028 +
  README). Total ≈ 3.1 MB.

## Out of scope (deferred)

- Per-tab JS rendering of sanitized content (M3–M5); Cytoscape/dagre vendor (M3);
  Playwright smoke tests (M6).
- Any raw-boundary, route, schema, or API change.

## Acceptance criteria
- Existing C# tests green **unmodified**; the sanitized/raw boundary,
  `--sanitized-only` (TraceDetail 404), and same-origin/no-store on raw routes
  unchanged.
- Fonts served locally (`/vendor/fonts/*.woff2`, `font/woff2`); no CDN / external
  origin contacted at runtime (asserted by a no-CDN test + manual network check).
- Trace list shows 7 primary columns + working row disclosure; TraceDetail shows
  the four-tab shell with deferred Flow Chart/Cache panes.

## Validation
```powershell
dotnet build CopilotAgentObservability.slnx   # 0 warnings / 0 errors
dotnet test CopilotAgentObservability.slnx    # 537 passing (was 533 + 4 new)
```
Manual (loopback, prebuilt exe): theme renders dark; DevTools Network shows
`*.woff2` from `/vendor/fonts/` and **zero external requests**; `/health/ready`
= 200 ready. New additive tests: `VendoredFont_IsServedAsWoff2`,
`TracesPage_RendersRowExpandDisclosure`, `Theme_VendorsFontsLocallyWithNoExternalCdn`,
`TraceDetail_RendersTabShellWithDeferredFlowAndCachePanes`.

## Review

### Review outcome
Recorded self-review, 2026-06-28:
- Boundary preserved: `monitor-views.js` only toggles visibility of
  already-rendered sanitized DOM; it fetches no route and inserts no payload.
  Raw OTLP section and `/traces/{id}/raw` untouched; no `Html.Raw`; no CSP /
  sanitizer added (AGENTS.md Local-First Risk Posture, D020).
- No-CDN invariant (D025/D028) verified at runtime: index/CSS reference only
  `/monitor.css`, `/monitor.js`, `/monitor-views.js`, `/vendor/fonts/*`; no
  `googleapis`/`gstatic`/`jsdelivr`/`unpkg` host; woff2 served `font/woff2`.
- Existing tests unmodified and green (233 → 237 with 4 additive). The
  data-present assertions (`Summary`, `Sub-agent span tree`, `Per-turn token
  rollup`, `read_file`, `Raw OTLP payload`, raw markers) still hold because
  Summary/Timeline remain server-rendered (staged migration).
- D028 narrowing (trimmed weights) recorded in `decisions.md` D028 + README
  Vendor provenance with version/SHA-256/size/license.
- Codex companion `review` (read-only adversarial) available on request; not
  auto-delegated (AGENTS.md Subagent policy).

Open item (not blocking M2): per-tab JS rendering + Playwright are M3–M6; the
README architecture's full JS-rendered upper section is reached incrementally.
