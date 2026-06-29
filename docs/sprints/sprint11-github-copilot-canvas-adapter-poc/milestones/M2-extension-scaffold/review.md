# Sprint11 M2 Review

## 2026-06-29 (initial): Canvas scaffold tool blocker review

Scope reviewed:

- Sprint11 M2 extension scaffold plan and blocker rule.
- Repository-local `/create-canvas` skill guidance.
- Active Codex tool surface for GitHub Copilot app Canvas scaffold and
  validation helpers.
- Existing repository extension paths.

Findings:

- Blocking issue found: the active Codex environment does not expose the Copilot
  app Canvas scaffold or validation helpers required by Sprint11 M2.
- `.github/extensions/` does not exist, and no
  `.github/extensions/otel-monitor-canvas/` scaffold exists.
- No hand-written fallback was performed because the current source of truth
  requires explicit product-owner approval before bypassing the scaffold-first
  workflow.

Result: M2 is blocked in this environment.

## 2026-06-29 (update): Blocker cleared — M2 implemented

The Canvas extension tool surface (`extensions_manage`, `extensions_reload`,
`list_canvas_capabilities`, `open_canvas`, `invoke_canvas_action`) is now
callable in the active Codex environment. M2 proceeded with the scaffold-first
workflow.

Changes committed:
- `.github/extensions/otel-monitor-canvas/extension.mjs` — project-scoped Canvas
  extension entrypoint.

Implementation:
- Canvas id: `otel-monitor`, display name: `OTel Monitor`.
- `joinSession({ canvases: [createCanvas({...})] })`, ES modules.
- `inputSchema`: optional `monitorBaseUrl` (default `http://127.0.0.1:4320`).
- `open()`: validates loopback-only URL (rejects non-loopback with
  `CanvasError("invalid_monitor_url", ...)`), checks `/health/ready`, returns
  monitor URL when healthy or extension-owned diagnostic page when unavailable.
- `onClose()`: tears down extension-owned HTTP servers.
- No actions (M3 scope).
- No `session.log()` inside callbacks (not available before `joinSession`
  resolves); diagnostic content is rendered in the HTML page instead.

Validation:
- `extensions_reload` → extension running (project:otel-monitor-canvas).
- `extensions_manage inspect` → status: running, no errors.
- `list_canvas_capabilities({ canvasId: "otel-monitor" })` → correct id,
  display name, input schema, empty actions array.
- `open_canvas({ canvasId: "otel-monitor", instanceId: "m2-smoke-2" })` →
  returns `OTel Monitor — Offline` with diagnostic URL on ephemeral port
  (monitor not running — expected).
- Diagnostic server serves valid HTML on `127.0.0.1`.
- `dotnet build CopilotAgentObservability.slnx` → 0 warnings, 0 errors.
- `dotnet test CopilotAgentObservability.slnx` → ConfigCli 300/300 passed.
  LocalMonitor 249/250 passed; 1 pre-existing concurrency test flake
  (`Worker_DrainsAlreadyAcceptedQueueItemsDuringShutdown`) unrelated to
  Canvas changes (no .NET code modified).

Out of scope (not performed):
- No `package.json`, `node_modules`, or lockfiles added.
- No dependencies introduced beyond Copilot SDK (`@github/copilot-sdk/extension`).
- No raw prompt/response, tool arguments/results, PII, credentials, tokens,
  local sensitive paths, runtime artifacts, or generated telemetry committed.

Residual:
- `onClose()` server cleanup verified via code review; runtime lifecycle
  validation requires a long-running Canvas instance (deferred to M3+).
- Live validation with a running Local Monitor (actual `dotnet run
  --sanitized-only`) deferred to M3+ when monitor actions are testable.
- No `session.log()` diagnostics inside callbacks due to `joinSession()` timing;
  acceptable for M2 — diagnostic info is fully available in the rendered HTML.
