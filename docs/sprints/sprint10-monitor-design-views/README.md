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

Resolved with the product owner on 2026-06-28 (requirements brainstorm). Recorded
in `docs/decisions.md` during M1 as **D024–D026**.

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
- **D025 — Cytoscape.js is the single permitted client-side visualization
  dependency, vendored locally.** A1's interactive graph (pan/zoom, node
  selection, auto-layout) needs one client library; the product owner approved
  exactly one. It is **vendored as a UMD single file under `wwwroot`** (no CDN,
  to keep loopback-only / offline operation and avoid a runtime external fetch),
  MIT-licensed, and **consumes only the sanitized spans JSON** — never raw, never
  PII. No second JS dependency, no CSS framework, no build step is added.
- **D026 — Cache Explorer is sanitized-metrics-only.** A2 shows cache-hit rate,
  cache-creation tokens, duration, model, timestamp, and the per-turn token
  breakdown. The VS Code "prefix diff of consecutive requests" feature compares
  **raw prompt bodies** and is therefore **explicitly out of scope** — including
  it would add a raw-bearing route and is rejected to preserve the D023 boundary.

## Scope

Presentation only. No backend / schema / API / boundary change. Sequenced
foundation-first (A3 → A4 → A1 → A2).

- **A3 — Visual polish (foundation, first).** A dark, VS Code-styled theme in
  `monitor.css` (color tokens, typography, spacing, table/card treatment); tidy
  layout and navigation across Overview / Ingestions / Traces / Diagnostics /
  TraceDetail; the 21-column trace-list table made readable via a primary-column
  set plus progressive disclosure. Routes, behavior, and data contracts
  unchanged.
- **A4 — Timeline filter / sort.** Operates on the spans **within a trace**,
  filtered **client-side** over the already-loaded spans JSON (no boundary
  change). Filters: event type (`llm_call` / `tool_call` / `agent_invocation` /
  `hook` / `error`), status (errors only), tool / MCP name, sub-agent name, time
  range. Sort: duration / tokens / time. View toggle: flat list ⇔ sub-agent
  tree.
- **A1 — Flow Chart.** Cytoscape.js (D025) renders an interactive graph of one
  trace: nodes for **agent, sub-agent, and tool/MCP** calls; edges from
  `parent_span_id` hierarchy; pan/zoom; clicking a node reveals that span's
  detail / scrolls the timeline. Error spans are visually distinguished. Built
  client-side from the existing spans JSON — **no new endpoint**.
- **A2 — Cache Explorer.** Groups `chat` (LLM) turns by their **root
  `invoke_agent` span** (the available approximation of "user request" — see
  [Data mapping](#data-mapping)); cross-trace stitching by `conversation_id`.
  Per turn / per group it shows **cache-hit rate** (`cache_read_tokens /
  input_tokens`, divide-by-zero guarded), cache-creation tokens, duration,
  model, timestamp, and the token breakdown. Sanitized only.

## Non-goals

- A2 prefix-diff over raw prompt bodies; any raw-bearing route; any change to the
  raw default / `--sanitized-only` posture (D020 / D023 preserved).
- A second client-side JS dependency, a CSS framework, a CDN reference, or a
  front-end build step (only Cytoscape.js, vendored, per D025).
- Any new telemetry input, projection column, schema bump, or API field — the
  existing spans API already carries every field these views need.
- Changing the normalized measurement, candidate, or dashboard dataset
  contracts.
- VS Code internal logs / `workspaceStorage` / `chatSessions` as input; a replica
  of VS Code's in-editor debug UI (D001 / D021 preserved).

## Data mapping

The two non-obvious mappings these views rely on, pinned here for M1:

- **"User request" grouping (A2).** There is no per-user-request id in the
  telemetry. The available grouping is `trace_id` → root `invoke_agent` span →
  child `chat` spans (via `parent_span_id`), with `gen_ai.conversation.id` for
  cross-trace stitching
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

- Cytoscape.js and all A1/A2/A4 client code consume **only** the sanitized
  `/api/monitor/*` JSON (and SSE). Raw / PII is still served **only** by the
  existing server-rendered routes; the new views never read raw.
- Loopback bind, `Host`-header validation, CORS disabled, same-origin +
  `Cache-Control: no-store` on the existing raw-bearing routes, and "no raw / PII
  in logs or repository-committed outputs" are all unchanged.
- Cytoscape.js is vendored locally (no CDN), so no external fetch is introduced
  and offline / loopback operation is preserved.
- Captured content continues to render as escaped, inert text (framework default;
  no `Html.Raw`). No CSP / sanitizer / XSS-matrix apparatus is added (AGENTS.md
  Local-First Risk Posture).

`--sanitized-only` continues to remove the raw-bearing routes; the new views work
identically under it (they were sanitized to begin with), and M6 asserts this.

## Milestones

Each milestone produces its own `milestones/Mx-*/plan.md` at execution time
(Codex companion `review` / adversarial pass before implementation, per
`CLAUDE.md`). Cadence mirrors Sprint8/Sprint9.

| Milestone | Scope | Status |
| --- | --- | --- |
| M1 Specs & Decisions | Record D024–D026; update `requirements.md` §3/§4 (capability + narrow the deferred-design non-goal), `spec.md` (monitor views), `telemetry-ingestion.md` (note the views consume the existing spans API; no new field), `security-data-boundaries.md` (Cytoscape vendored + sanitized-only consumption invariant; A2 prefix-diff explicitly out), user guide. Confirm the spans API field coverage (done: all required fields present). No code. | Planned |
| M2 A3 Visual polish | Dark VS Code-styled theme + layout/navigation across all monitor pages; trace-list readability (primary columns + disclosure). No route/behavior/data change. UI tests assert pages still render and required data is present. | Planned |
| M3 A4 Timeline filter/sort | Client-side filter (event type / status / tool / MCP / sub-agent / time), sort (duration / tokens / time), flat ⇔ tree toggle on the trace-detail span timeline. Sanitized JSON only; no API change. | Planned |
| M4 A1 Flow Chart | Vendor Cytoscape.js (UMD, `wwwroot`, no CDN); render agent + sub-agent + tool/MCP nodes with `parent_span_id` edges, pan/zoom, node-click → span detail, error styling. Built from the existing spans JSON. | Planned |
| M5 A2 Cache Explorer | Group chat turns by root `invoke_agent` (≈ user request) + `conversation_id`; cache-hit rate / cache-creation / duration / model / timestamp / token breakdown. Sanitized only; prefix-diff out (D026). | Planned |
| M6 Validation | `dotnet build` / `dotnet test`; re-assert the sanitized-JSON/SSE invariant (new views read sanitized only; no raw via JSON/SSE); `--sanitized-only` health check (new views work, raw routes 404); dark-theme render sanity. Live VS Code Copilot Chat validation human-gated (inherited). | Planned (live human-gated) |

## Spec changes required (applied in M1)

- `docs/requirements.md` — §3: note the monitor's design views (Flow Chart /
  Cache Explorer / timeline filter / themed UI) as sanitized presentation; §4:
  narrow the "later design sprint" deferral per D024.
- `docs/spec.md` — monitor section: the four design views and the single
  client-side dependency.
- `docs/specifications/layers/telemetry-ingestion.md` — note the views consume
  the existing `/api/monitor/traces/{traceId}/spans`; no new endpoint/field.
- `docs/specifications/security-data-boundaries.md` — Cytoscape.js vendored
  (no CDN), client views consume sanitized JSON only, A2 prefix-diff out of scope
  (D026); boundary otherwise unchanged.
- `docs/decisions.md` — add D024–D026.
- `docs/task.md` — roadmap pointer.
- User guide (`docs/user-guide/local-monitor.md`) — the new views and theme.

## Validation

Per D015:

```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```

Automated tests use synthetic OTLP fixtures only. Client-side view behavior is
covered by the existing server-rendered UI tests plus assertions that the views
read only sanitized JSON. Live VS Code Copilot Chat validation is **human-gated**
(same constraint as Sprint8/Sprint9 M6).

## Dependencies & risks

- **Single new dependency (Cytoscape.js).** Vendored locally, MIT, UMD single
  file, no CDN, no build step (D025). Risk: vendored asset size / version drift —
  pin the version and record provenance in M4.
- **"User request" is an approximation.** A2 groups by root `invoke_agent`, not a
  true user-request id (none exists in the telemetry). Documented in
  [Data mapping](#data-mapping); acceptable for a local self-debugging view.
- **No boundary change, but new client code.** The risk is a view accidentally
  reading a raw route; M6 re-asserts the sanitized-only consumption invariant and
  that `--sanitized-only` leaves the new views functional.
- **Live validation human-gated** (inherited). Graph hierarchy and cache/token
  emission are confirmed only by a real run; M2–M5 build and test against the
  synthetic fixtures shaped to the documented contract.
