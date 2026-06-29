# Sprint11: GitHub Copilot Canvas Adapter PoC

Sprint11 builds a **thin GitHub Copilot app Canvas adapter** for the Local
Ingestion Monitor. The Local Monitor remains the source of truth for OTel
ingestion, SQLite persistence, sanitized projection, monitor APIs, and the
Sprint10 monitor views. The Canvas extension adds only the Copilot app
integration layer: canvas registration, `open()` returning a loopback URL,
agent-callable actions over sanitized monitor data, and an optional
UI-to-Copilot analysis trigger.

This sprint is intentionally adapter-first. It does **not** reimplement the
monitor UI, add telemetry inputs, change raw store schemas, or expose raw prompt
/ response bodies to Copilot actions.

## Controlling guidance

Sprint11 implementation and review must load and follow the repository-local
Copilot app skill:

```text
.github/skills/create-canvas/SKILL.md
```

That skill is the `/create-canvas` workflow. Do **not** implement it as a
`.github/prompts/*.prompt.md` prompt file.

Key decisions for this sprint:

- **Scope:** project-scoped Canvas extension.
- **Location:** `.github/extensions/otel-monitor-canvas/`.
- **Entrypoint:** `extension.mjs`, ES modules.
- **Runtime pattern:** `joinSession({ canvases: [createCanvas({...})] })`.
- **Scaffold-first:** use the Copilot app scaffold when available:

  ```text
  extensions_manage({ operation: "scaffold", kind: "canvas", name: "otel-monitor-canvas", location: "project" })
  ```

- If `extensions_manage` or the canvas validation tools are unavailable in the
  active Copilot app environment, record the blocker and stop. Do not silently
  hand-write the skeleton.
- Hand-written fallback is allowed only after explicit product-owner approval.

## Architecture

```text
CopilotAgentObservability.LocalMonitor (.NET)
  - OTel ingestion
  - SQLite raw store
  - sanitized monitor projections
  - /api/monitor/* read APIs
  - Sprint10 Summary / Timeline / Flow Chart / Cache views

GitHub Copilot Canvas extension (Node / Copilot SDK)
  - project-scoped extension under .github/extensions/otel-monitor-canvas/
  - extension.mjs entrypoint
  - joinSession({ canvases: [createCanvas({...})] })
  - open() returning a loopback URL
  - agent-callable actions over sanitized monitor data
  - optional loopback helper page / proxy
  - optional UI-to-Copilot analysis trigger
```

The Canvas adapter must not become a second monitor. It reuses the existing
Local Monitor sanitized APIs and views wherever possible.

## Canvas-safe posture

Sprint11 requires the Local Monitor to be launched in `--sanitized-only` mode
for Canvas display. This uses the D030 behavior already in the product: the
TraceDetail sanitized tab shell remains available, while the raw section and
full raw links are omitted and the full raw route returns `404`.

Canvas surfaces may open only sanitized monitor pages or an explicit diagnostic
page. The extension must fail fast or show a clear diagnostic when the monitor
is unavailable or not in a Canvas-safe posture.

Raw boundary invariants:

- Canvas actions consume sanitized `/api/monitor/*` data only.
- Canvas action responses never include raw prompt bodies, raw response bodies,
  tool arguments, tool results, PII, credentials, tokens, local sensitive paths,
  or raw OTLP payloads.
- Extension-owned HTTP servers bind only to `127.0.0.1`.
- Extension-owned servers close in `onClose()`.
- Diagnostics use `session.log()`, not `console.log()`.
- No CDN/runtime external fetches are introduced.
- No runtime canvas state, generated telemetry, captured monitor data, or local
  monitor output is committed.

## Scope

### A1 - Scope decision and scaffold

Record the project-scoped decision, load `.github/skills/create-canvas/SKILL.md`,
and use the Copilot app canvas scaffold when available. The preferred project
extension path is:

```text
.github/extensions/otel-monitor-canvas/
  extension.mjs
```

Do not hand-add `package.json`, `node_modules`, dependencies, or custom scaffold
files unless the scaffold creates them, the user approves them, and repository
guidance allows them.

### A2 - Canvas runtime shape

Implement the provider with `joinSession({ canvases: [createCanvas({...})] })`
from `extension.mjs`. `open()` is idempotent because providers may reconnect.
Durable state is keyed by stable monitor concepts such as trace id or selected
analysis target, not by `instanceId` alone. Expected failures use
`CanvasError("code", "message")`. Action names are unique and do not use the
reserved `canvas.*` prefix.

### A3 - Sanitized monitor surface only

Open only a sanitized Local Monitor surface or diagnostic page. The sprint
default is to require the monitor to run with `--sanitized-only`. Directly
opening a raw-bearing default TraceDetail page is out of scope.

### A4 - Agent-callable monitor actions

Implement this minimal action set:

```text
monitor_health()
list_recent_traces({ limit, status?, model? })
get_trace_summary({ traceId })
get_trace_span_tree({ traceId })
get_cache_summary({ traceId })
```

Responses are bounded LLM-oriented DTOs, not raw monitor payload dumps.

### A5 - UI-to-Copilot analysis trigger

Add a minimal UI affordance if supported by the active Canvas runtime:

```text
Analyze selected trace with Copilot
```

The trigger passes only selected trace id, optional span id, analysis focus
(`latency`, `tokens`, `cache`, or `errors`), and an instruction that Copilot must
use sanitized actions and must not request raw prompt/response bodies.

If the runtime does not expose a direct UI-to-Copilot trigger, document the
limitation and keep agent-callable actions usable from Copilot.

### A6 - Security posture

Preserve D020/D023/D030 and the Sprint9/Sprint10 sanitized-JSON/SSE invariant.
Add per-launch token protection for extension-owned loopback/proxy routes when
such routes expose monitor state. Keep CORS disabled unless a later explicit
decision justifies it.

### A7 - Documentation

Document how to load and use the Canvas extension, that `/create-canvas` is a
skill rather than a prompt file, required Local Monitor startup posture, scope
decision, scaffold-vs-fallback behavior, action contracts, known limitations,
security/data boundaries, and relationship to Sprint10 design views.

## Action contracts

`monitor_health()`:

- Returns Local Monitor reachability, `/health/ready` status/body, configured
  monitor base URL, and whether Sprint11 considers the posture Canvas-safe.
- Does not return raw store paths beyond non-sensitive endpoint shape.

`list_recent_traces({ limit, status?, model? })`:

- `limit` is required and bounded to `1..50` for action output, even though the
  monitor API allows larger pages.
- Optional `status` filters by derived `ok` / `error` trace state from
  sanitized error counts.
- Optional `model` filters by sanitized `primary_model`.
- Returns trace id, client kind, span count, tool call count, error count,
  token totals, duration, primary model, first/last seen timestamps.

`get_trace_summary({ traceId })`:

- Returns one bounded summary for Copilot analysis: totals, status, model,
  timeline bounds, high-level error/cache/token signals, and a small set of top
  spans by duration/tokens.

`get_trace_span_tree({ traceId })`:

- Returns sanitized parent-child relationships using `span_id`,
  `parent_span_id`, `span_ordinal`, category, operation, sanitized names, status,
  error type, duration, model, and token counts.
- Does not return full span payloads or raw row dumps.

`get_cache_summary({ traceId })`:

- Returns cache-read tokens, cache-creation tokens, input tokens, cache hit rate,
  model, duration, and per-turn token breakdown within one trace.
- Does not implement raw prompt prefix diffing or cross-trace stitching.

## Non-goals

- Reimplementing the Local Monitor UI in the Canvas extension.
- Adding new telemetry input sources.
- Reading VS Code internal logs, `workspaceStorage`, or `chatSessions`.
- Exposing raw prompt bodies, raw response bodies, tool arguments, or tool
  results to Copilot actions.
- Exposing PII through Canvas action responses.
- Cross-trace conversation stitching by `conversation_id`.
- Building a shared team backend.
- Replacing Langfuse or the existing Local Monitor.
- Making Canvas the only supported monitor UI.
- Creating `.github/prompts/*.prompt.md` as a substitute for `/create-canvas`.
- Committing `node_modules`, generated telemetry files, runtime artifacts, or
  captured monitor data.

## Milestones

| Milestone | Scope | Status |
| --- | --- | --- |
| M1 Specs & guidance alignment | Read `/create-canvas`; record project scope, scaffold-first workflow, action contracts, `--sanitized-only` Canvas posture, and security boundary in canonical specs. No product code. | Implemented |
| M2 Extension scaffold | Use scaffold when available; add project-scoped extension skeleton; implement `open()` and monitor health diagnostics. Stop and record blocker if Canvas tools are unavailable. | Blocked |
| M3 Minimal actions | Implement `monitor_health`, `list_recent_traces`, and `get_trace_summary` with bounded sanitized DTOs. | Planned |
| M4 Trace analysis actions | Implement `get_trace_span_tree`, `get_cache_summary`, schema validation, size bounds, and `CanvasError` expected failures. | Planned |
| M5 UI trigger | Add or document the `Analyze selected trace with Copilot` trigger. | Planned |
| M6 Validation & docs | Build/test, Canvas validation, sanitized-only assertions, loopback server lifecycle checks, and user guide updates. | Planned |

## Validation

Repository validation for implementation milestones:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

Canvas validation, when the active Copilot app environment provides the tools:

```text
extensions_reload
extensions_manage({ operation: "list" })
extensions_manage({ operation: "inspect", name: "otel-monitor-canvas" })
list_canvas_capabilities({ canvasId: "<canvas-id>" })
open_canvas({ canvasId: "<canvas-id>", instanceId: "<instance-id>", input?: {...} })
invoke_canvas_action({ instanceId: "<instance-id>", actionName: "<action-name>", input?: {...} })
```

Additional checks:

- run Local Monitor with `--sanitized-only`;
- verify `open()` returns the expected loopback URL or diagnostic;
- verify every action returns bounded sanitized DTOs;
- verify invalid inputs are rejected by `inputSchema`;
- verify no raw prompt/response content, tool arguments/results, credentials,
  tokens, PII, local sensitive paths, runtime artifacts, or generated telemetry
  content appears in action responses, logs, or committed outputs;
- verify extension-owned HTTP servers close when the Canvas instance closes.

## Risks

- Canvas extension SDK/API behavior may change or differ across Copilot app
  surfaces.
- The active Copilot app environment may not expose the scaffold or validation
  helpers; M2 must record that as a blocker rather than switching workflows.
- `127.0.0.1` points to each user's machine, so this does not create a shared
  team monitor.
- If the default raw-bearing TraceDetail route is opened without
  `--sanitized-only`, Canvas could display raw local data. Sprint11 rejects that
  posture.
- Action responses can become too large if they return full span pages; all
  actions must summarize and bound output.
- UI-to-Copilot session triggering may be unavailable; agent-callable actions
  are the minimum viable integration.
- Live validation depends on GitHub Copilot app Canvas extension availability
  and may require human-gated verification.

## Issue source

Sprint11 was planned from the attached Issue #33 text because unauthenticated
GitHub issue access returned `401` in this environment.
