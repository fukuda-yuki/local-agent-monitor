# Sprint10: Local Ingestion Monitor — Design Views

Sprint10 takes the **deferred design items** from Sprint9 (Flow Chart, Cache
Explorer, visual polish, timeline filter/sort — listed as Sprint9 Non-goals) and
delivers them as a **presentation-only** layer on top of the data the monitor
already projects. Sprint9 built the sanitized span projection, the read API, and
a *functional* agent-execution view; Sprint10 makes that view **readable and
explorable**, modeled on the four VS Code Agent Debug View panels
(Logs / Summary / Flow Chart / Cache Explorer).

The defining constraint: Sprint10 **adds no telemetry input, no schema, no API
field, and no raw-boundary change**. Every new view is rendered from the existing
sanitized spans JSON (`GET /api/monitor/traces/{traceId}/spans`), which already
exposes everything the four items need — `parent_span_id`, agent / tool / MCP
names, models, per-span token usage including `cache_read_tokens` /
`cache_creation_tokens`, `finish_reasons`, `conversation_id`, status /
`error.type`, and span timing (verified in
[MonitorHost.cs](../../../src/CopilotAgentObservability.LocalMonitor/MonitorHost.cs)
and
[MonitorProjectionRows.cs](../../../src/CopilotAgentObservability.Persistence.Sqlite/MonitorProjectionRows.cs)).

## Decision

Resolved with the product owner on 2026-06-28 (requirements brainstorm +
grill-me session). Recorded in `docs/decisions.md` during M1 as **D024–D028**.

- **D024 — Narrow the "design views deferred" non-goal.** Sprint9's README and
  `docs/requirements.md` §4 defer the graphical Flow Chart, the Cache Explorer,
  and visual/design polish to "a later design sprint." Sprint10 **is** that
  sprint and narrows the non-goal: the monitor **may** present a graphical Flow
  Chart, a Cache Explorer, a polished theme, and a timeline filter/sort UI — all
  as **sanitized, client-side presentation over the existing spans API**. **D001
  and D021 are preserved**: input stays the official OTel signals the monitor
  receives; VS Code internal logs / `workspaceStorage` / `chatSessions` remain
  non-inputs, and we still do not re-implement VS Code's in-editor debug UI.
  **D020 and D023 are preserved**: the raw boundary and the sanitized-JSON/SSE
  invariant are unchanged (see [Safety boundary](#safety-boundary)).
- **D025 — Cytoscape.js and its dagre layout extension are the permitted
  client-side visualization dependencies, vendored locally.** A1's interactive
  graph (pan/zoom, node selection, auto-layout) needs a graph library and a DAG
  layout algorithm; the product owner approved Cytoscape.js with its dagre
  extension (cytoscape-dagre + dagre). All three files are **vendored as UMD
  single files under `wwwroot/vendor/`** (no CDN, to keep loopback-only / offline
  operation and avoid a runtime external fetch), MIT-licensed, and **consume only
  the sanitized spans JSON** — never raw, never PII. All other interactive UI
  (filters, sort, tabs, Cache Explorer) is implemented in **Vanilla JS** with no
  additional library. No CSS framework, no build step is added.
- **D026 — Cache Explorer is sanitized-metrics-only, trace-internal only.** A2
  shows cache-hit rate, cache-creation tokens, duration, model, timestamp, and
  the per-turn token breakdown, grouped within a single trace. The VS Code
  "prefix diff of consecutive requests" feature compares **raw prompt bodies**
  and is therefore **explicitly out of scope** — including it would add a
  raw-bearing route and is rejected to preserve the D023 boundary.
  **Cross-trace stitching by `conversation_id`** is deferred — the current API
  is trace-scoped, and implementing cross-trace grouping would require a new
  query parameter or endpoint, violating the "no API change" constraint.
- **D027 — VS Code-styled dark theme; DADS not applied to Local Monitor.**
  The Local Monitor is a developer-facing debugging tool. Its visual design
  follows VS Code conventions (dark theme, system fonts, VS Code color
  vocabulary) rather than the Digital Agency Design System (DADS). DADS
  accessibility baselines (`[official-must]` rules) are also not applied;
  accessibility follows VS Code conventions. The Static Dashboard retains its
  existing design independently. DADS skills (`dads-foundations-core`,
  `dads-ui-review`, `project-dads-policy`) are removed in a separate task
  **before Sprint10 execution** (Sprint10 scope-external).
- **D028 — Noto Sans JP and Noto Sans Mono vendored as monitor typography.**
  Noto Sans JP (full weight set) and Noto Sans Mono are vendored under
  `wwwroot/vendor/fonts/` (no CDN). Combined size is approximately 5–10 MB.
  This is accepted for a local-only tool where network cost is zero.

## Scope

Presentation only. No backend / schema / API / boundary change. Sequenced
foundation-first: **A3 → A1 → A4 → A2**.

- **A3 — Visual polish (foundation, first).** A dark, VS Code-styled theme in
  `monitor.css` (color tokens, Noto Sans JP / Noto Sans Mono typography, 8px
  spacing scale, table/card treatment); tidy layout and navigation across
  Overview / Ingestions / Traces / Diagnostics / TraceDetail. The 21-column
  trace-list table made readable via 7 **primary columns** (TraceId with link,
  ClientKind, TotalTokens, ToolCallCount, ErrorCount, TurnCount, PrimaryModel)
  plus a **row-expand button** for remaining columns (progressive disclosure).
  TraceDetail page restructured into **two sections**: an upper **sanitized
  section** (JS-rendered, with tab navigation) and a lower **raw section**
  (Razor-rendered, raw OTLP preview — unchanged). Other pages (Overview,
  Ingestions, Traces, Diagnostics) remain Razor-rendered with CSS-only changes.
  Routes, behavior, and data contracts unchanged.
- **A1 — Flow Chart.** Vendor Cytoscape.js + dagre extension (D025) under
  `wwwroot/vendor/`. Render an interactive graph of one trace in the **Flow
  Chart tab**: nodes for every span by category (`agent_invocation` /
  `llm_call` / `tool_call` / `hook`); edges from `parent_span_id` hierarchy;
  dagre layout (top-to-bottom DAG); pan/zoom; **clicking a node switches to the
  Timeline tab and highlights/scrolls to that span**. Error spans are visually
  distinguished (red border/badge). Built client-side from the existing spans
  JSON — **no new endpoint**. No filter on the Flow Chart (full trace overview).
- **A4 — Timeline filter / sort.** Operates on the spans **within a trace**,
  displayed as a **flat list** in the **Timeline tab** (no tree toggle — the
  tree view is covered by A1 Flow Chart). Filtered **client-side** over the
  already-loaded spans JSON (no boundary change). **Filter: status (errors
  only)**. **Sort: tokens / time (default)**. Minimal filter bar implemented in
  Vanilla JS. Event type, tool/MCP name, sub-agent name, and time-range filters
  are deferred (can be added later with no architecture change).
- **A2 — Cache Explorer.** The **Cache tab** within TraceDetail. Groups `chat`
  (LLM) turns within the current trace by their **root `invoke_agent` span**
  (the available approximation of "user request" — see
  [Data mapping](#data-mapping)). Per turn / per group it shows **cache-hit
  rate** (`cache_read_tokens / input_tokens`, divide-by-zero guarded),
  cache-creation tokens, duration, model, timestamp, and the token breakdown.
  Sanitized only. **Cross-trace stitching by `conversation_id` is deferred**
  (see D026).

## TraceDetail page architecture

The TraceDetail page is restructured as two distinct rendering zones:

```
┌─────────────────────────────────────────────┐
│  ← Traces                                   │
│  Trace {traceId}                             │
│                                              │
│  ┌─────────────────────────────────────────┐ │
│  │ [Summary] [Timeline] [Flow Chart] [Cache]│ │
│  │                                          │ │
│  │   JS-rendered sanitized content          │ │
│  │   (fetches /api/monitor/traces/{id}/spans│ │
│  │    via client-side JS)                   │ │
│  │                                          │ │
│  └─────────────────────────────────────────┘ │
│                                              │
│  ─── Raw OTLP payload ────────────────────── │
│  (Razor-rendered, server-side, unchanged)    │
│  raw record previews / full raw links        │
└─────────────────────────────────────────────┘
```

- **Upper section (sanitized, JS)**: Empty container divs are output by Razor.
  Client-side JS fetches the spans JSON, then builds all four tab views
  (Summary, Timeline, Flow Chart, Cache). Tab state is managed in JS; only one
  tab is visible at a time. SSE `projection` events trigger a re-fetch and view
  update (preserving selected tab and scroll position).
- **Lower section (raw, Razor)**: The existing raw OTLP preview remains
  server-rendered by Razor. Unchanged from Sprint9. D023 boundary preserved —
  raw is never served via JSON API.
- **Flow Chart → Timeline interaction**: Clicking a node in the Flow Chart tab
  switches to the Timeline tab and scrolls to / highlights the corresponding
  span row.

## Non-goals

- A2 prefix-diff over raw prompt bodies; any raw-bearing route; any change to the
  raw default / `--sanitized-only` posture (D020 / D023 preserved).
- A2 cross-trace stitching by `conversation_id` (deferred — requires API change).
- A4 tree view toggle (the tree/hierarchy view is Flow Chart's role).
- A4 event-type filter, tool/MCP name filter, sub-agent name filter, time-range
  filter (deferred — flat list + errors-only filter + sort is Sprint10 scope).
- A CSS framework, a CDN reference, or a front-end build step. Only Cytoscape.js
  + dagre extension (vendored) and Vanilla JS.
- Any new telemetry input, projection column, schema bump, or API field — the
  existing spans API already carries every field these views need.
- Changing the normalized measurement, candidate, or dashboard dataset
  contracts.
- VS Code internal logs / `workspaceStorage` / `chatSessions` as input; a replica
  of VS Code's in-editor debug UI (D001 / D021 preserved).
- DADS design system application to Local Monitor (D027). DADS skills are removed
  in a separate pre-Sprint10 task.

## Data mapping

The non-obvious mapping these views rely on, pinned here for M1:

- **"User request" grouping (A2).** There is no per-user-request id in the
  telemetry. The available grouping is `trace_id` → root `invoke_agent` span →
  child `chat` spans (via `parent_span_id`)
  ([MonitorSpanProjectionBuilder.cs](../../../src/CopilotAgentObservability.Telemetry/Monitoring/MonitorSpanProjectionBuilder.cs)).
  A2 therefore treats the **root `invoke_agent` span as the "user request"
  group** and labels it as an approximation. The Sprint9 `parent_span_id`-absent
  fallback (flat grouping, no failure) is reused.
- **Cache-hit rate (A2).** `gen_ai.usage.cache_read.input_tokens` is the cached
  subset of `input_tokens`; hit rate = `cache_read_tokens / input_tokens` with a
  divide-by-zero / null guard. `cache_creation_tokens` is shown as a separate
  count, not folded into the rate.

## Safety boundary

**Unchanged from Sprint9 (D020 / D023).** Sprint10 adds no raw-bearing surface.
The key invariant that makes the new client-side views safe:

- Cytoscape.js, dagre, and all A1/A2/A4 client code consume **only** the
  sanitized `/api/monitor/*` JSON (and SSE). Raw / PII is still served **only**
  by the existing server-rendered routes; the new views never read raw.
- Loopback bind, `Host`-header validation, CORS disabled, same-origin +
  `Cache-Control: no-store` on the existing raw-bearing routes, and "no raw / PII
  in logs or repository-committed outputs" are all unchanged.
- Cytoscape.js and dagre are vendored locally (no CDN), so no external fetch is
  introduced and offline / loopback operation is preserved.
- Noto Sans JP / Noto Sans Mono are vendored locally (no Google Fonts CDN).
- Captured content continues to render as escaped, inert text (framework default;
  no `Html.Raw`). No CSP / sanitizer / XSS-matrix apparatus is added (AGENTS.md
  Local-First Risk Posture).

`--sanitized-only` continues to remove raw-bearing surfaces. After S10-1,
TraceDetail keeps the sanitized Summary / Timeline / Flow Chart / Cache tabs
available in this mode, while omitting the raw section and full raw links and
keeping `GET /traces/{rawRecordId}/raw` at `404`.

## Milestones

Each milestone produces its own `milestones/Mx-*/plan.md` at execution time
(Codex companion `review` / adversarial pass before implementation, per
`CLAUDE.md`). Cadence mirrors Sprint8/Sprint9.

| Milestone | Scope | Status |
| --- | --- | --- |
| M1 Specs & Decisions | Record D024–D028; update `requirements.md` §3/§4 (capability + narrow the deferred-design non-goal + DADS non-applicability), `spec.md` (monitor views + TraceDetail architecture), `telemetry-ingestion.md` (note the views consume the existing spans API; no new field), `security-data-boundaries.md` (Cytoscape + dagre vendored + sanitized-only consumption invariant; A2 prefix-diff and cross-trace out), user guide. Confirm the spans API field coverage (done: all required fields present). No code. | Done |
| M2 A3 Visual polish | Dark VS Code-styled theme (D027) + Noto Sans JP/Mono vendor (D028, trimmed to 400/500/700 + mono 400) + layout/navigation across all monitor pages; trace-list readability (7 primary columns + row-expand disclosure); TraceDetail restructured into a sanitized **tab shell** (Summary/Timeline server-rendered, Flow Chart/Cache empty panes) + raw Razor section. Staged migration: per-tab JS rendering deferred (Flow Chart M3, Timeline filter/sort M4, Cache M5), so existing UI tests stay green unmodified. No route/behavior/data/boundary change. | Done |
| M3 A1 Flow Chart | Vendor Cytoscape.js + dagre + cytoscape-dagre (UMD, `wwwroot/vendor/`, no CDN); render span nodes by category with `parent_span_id` edges, dagre layout, pan/zoom, node-click → Timeline tab switch + highlight, error styling. Built from the existing spans JSON. Record version + SHA in this README. | Done |
| M4 A4 Timeline filter/sort | Client-side filter (status: errors only) and sort (tokens / time) on the flat span list in the Timeline tab. Vanilla JS; sanitized JSON only; no API change. | Done |
| M5 A2 Cache Explorer | Cache tab: group chat turns within the current trace by root `invoke_agent` (≈ user request); cache-hit rate / cache-creation / duration / model / timestamp / token breakdown. Sanitized only; prefix-diff out (D026); cross-trace deferred (D026). | Done |
| M6 Validation | `dotnet build` / Playwright Chromium bootstrap / `dotnet test`; Playwright smoke tests (tab switch, filter apply, Flow Chart render); re-assert the sanitized-JSON/SSE invariant (new views read sanitized only; no raw via JSON/SSE); `--sanitized-only` health check (raw route 404; sanitized tabs available); dark-theme render sanity. Live VS Code Copilot Chat validation human-gated (inherited). | Automated validation added; blocked by live validation |

## Spec changes required (applied in M1)

- `docs/requirements.md` — §3: note the monitor's design views (Flow Chart /
  Cache Explorer / timeline filter / themed UI) as sanitized presentation; §4:
  narrow the "later design sprint" deferral per D024; note DADS non-applicability
  to Local Monitor (D027).
- `docs/spec.md` — monitor section: the four design views, the TraceDetail tab
  architecture, the client-side dependency set (Cytoscape.js + dagre), Noto font
  vendor.
- `docs/specifications/layers/telemetry-ingestion.md` — note the views consume
  the existing `/api/monitor/traces/{traceId}/spans`; no new endpoint/field.
- `docs/specifications/security-data-boundaries.md` — Cytoscape.js + dagre
  vendored (no CDN), Noto fonts vendored (no CDN), client views consume sanitized
  JSON only, A2 prefix-diff and cross-trace out of scope (D026); boundary
  otherwise unchanged.
- `docs/decisions.md` — add D024–D028.
- `docs/task.md` — roadmap pointer.
- User guide (`docs/user-guide/local-monitor.md`) — the new views and theme.

## Validation

Per D015:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

Automated tests use synthetic OTLP fixtures only. Client-side view behavior is
covered by **Playwright smoke tests** (tab switching, filter application, Flow
Chart rendering) plus assertions that the views read only sanitized JSON. Live
VS Code Copilot Chat validation is **human-gated** (same constraint as
Sprint8/Sprint9 M6).

## Dependencies & risks

- **Vendored client-side assets (~3.9 MB total).** Cytoscape.js (~424 KB) +
  dagre (~277 KB) + cytoscape-dagre (~12 KB) + the M2 trimmed Noto font set
  (~3.1 MB). All vendored locally, MIT/OFL, UMD/woff2 single files, no CDN, no
  build step (D025, D028). Risk: repository size increase — accepted for a
  local-only tool. Version pinned; provenance recorded in M2 (fonts) and M3
  (Cytoscape/dagre).
- **Playwright dev dependency.** Required for M6 client-side smoke tests. Added
  as a test-project NuGet package (`Microsoft.Playwright` 1.61.0). Risk:
  browser binary download (~100 MB+) in CI. The standard validation bootstrap
  installs Chromium before `dotnet test`; Linux CI uses
  `install --with-deps chromium`. Recorded in `docs/decisions.md` as D029.
- **"User request" is an approximation.** A2 groups by root `invoke_agent`, not
  a true user-request id (none exists in the telemetry). Documented in
  [Data mapping](#data-mapping); acceptable for a local self-debugging view.
- **No boundary change, but new client code.** The risk is a view accidentally
  reading a raw route; M6 re-asserts the sanitized-only consumption invariant and
  S10-1 verifies that `--sanitized-only` leaves the new views functional while
  omitting the raw section.
- **TraceDetail Razor/JS dual rendering.** The page has two rendering engines
  (JS for sanitized tabs, Razor for raw preview). This is accepted for Sprint10
  to preserve the D023 boundary. Future unification direction recorded in
  `docs/decisions.md`.
- **Live validation human-gated** (inherited). Graph hierarchy and cache/token
  emission are confirmed only by a real run; M2–M5 build and test against the
  synthetic fixtures shaped to the documented contract.

## Vendor provenance

Recorded during milestone execution. Updated in-place.

| Asset | Version | SHA-256 | Size | License | Milestone |
| --- | --- | --- | --- | --- | --- |
| `wwwroot/vendor/cytoscape.min.js` | cytoscape 3.33.1 | `f55947f3daa3bae53209d4b885c195c157f595c225e508a6b382598d9452d6e2` | 434037 B | MIT | M3 |
| `wwwroot/vendor/dagre.min.js` | dagre 0.8.5 | `62eb9787ccfdbdf4148d4d99d31dbf9ee4770eafee81e637d759b52aac22cd51` | 283803 B | MIT | M3 |
| `wwwroot/vendor/cytoscape-dagre.js` | cytoscape-dagre 2.5.0 | `bf70fe402991dcbff33e05a7e4a5271c78020bb75e85d1c80ab7538e4157112e` | 12665 B | MIT | M3 |
| `wwwroot/vendor/fonts/noto-sans-jp-japanese-400-normal.woff2` | fontsource 5.2.9 | `4a7b928d4d75e7fc0bace614030664a7ea7eb7d2f754fd2b2da9c3c0ed350570` | 1017536 B | OFL 1.1 | M2 |
| `wwwroot/vendor/fonts/noto-sans-jp-japanese-500-normal.woff2` | fontsource 5.2.9 | `116eacf750caa59db9d404d43d2daf0f02ae01c439825716972da8dcc97ce024` | 1030340 B | OFL 1.1 | M2 |
| `wwwroot/vendor/fonts/noto-sans-jp-japanese-700-normal.woff2` | fontsource 5.2.9 | `a5861823629995d9abb4b16b96a1c57139d9663d7a256209cb6b40640ed5431e` | 1039792 B | OFL 1.1 | M2 |
| `wwwroot/vendor/fonts/noto-sans-jp-latin-400-normal.woff2` | fontsource 5.2.9 | `c3ca2d64070bf809fad5aec44a65f65ea88082e1a13faab4ef903f46a8c6f024` | 13072 B | OFL 1.1 | M2 |
| `wwwroot/vendor/fonts/noto-sans-jp-latin-500-normal.woff2` | fontsource 5.2.9 | `16399f6ffc142d4d427ba56b203b48cb1c5adf71bc9f018b53f1e9d5d4ad5783` | 13092 B | OFL 1.1 | M2 |
| `wwwroot/vendor/fonts/noto-sans-jp-latin-700-normal.woff2` | fontsource 5.2.9 | `1baabeedde8b3dfce0aa05cb784362d9dc42e7cf05abf87d83c9ffc3e8c69fb5` | 13080 B | OFL 1.1 | M2 |
| `wwwroot/vendor/fonts/noto-sans-mono-latin-400-normal.woff2` | fontsource 5.2.10 | `1e4b885e90f8e794d33fff5095497e4ce847d8c5fa2b7810d1b10a770d0f8e34` | 10876 B | OFL 1.1 | M2 |

Fonts vendored from [Fontsource](https://fontsource.org) (`@fontsource/noto-sans-jp@5.2.9`,
`@fontsource/noto-sans-mono@5.2.10`), OFL 1.1 license text at
`wwwroot/vendor/fonts/OFL.txt`. Per D028's M2 narrowing, only the UI-needed weights are
vendored (JP 400/500/700 + Mono 400; latin + japanese subsets), total ≈ 3.1 MB — not the
full weight set. No glyph subsetting or build step; woff2 served by the default static
content-type provider.

## Requirements brainstorm record (2026-06-28)

The requirements were refined through a structured grill-me session (14 design
decisions). Key changes from the initial draft:

| Topic | Initial draft | Final decision |
| --- | --- | --- |
| Sequence | A3→A4→A1→A2 | **A3→A1→A4→A2** (Flow Chart first) |
| DADS | Implicit (skills existed) | **Not applied to Local Monitor** (D027) |
| Accessibility | Implicit DADS | **VS Code conventions** |
| Typography | Not specified | **Noto Sans JP/Mono full vendor** (D028) |
| A4 view toggle | flat ⇔ sub-agent tree | **Flat only** (tree = Flow Chart) |
| A4 filters | 6 filter types | **Status (errors only)** |
| A4 sort | duration / tokens / time | **Tokens / time** |
| A2 cross-trace | conversation_id stitching | **Trace-internal only** (deferred) |
| Testing | Existing tests only | **Playwright smoke tests added** |
| Graph layout | Not specified | **dagre** (D025 updated) |
| TraceDetail | Not specified | **2-section split** (JS sanitized + Razor raw) |
| Tab UI | Not specified | **Summary \| Timeline \| Flow Chart \| Cache** |
