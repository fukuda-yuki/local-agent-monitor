# Sprint11 M5 - UI Trigger (Plan)

Status: **Implemented** - Canvas UI-to-Copilot analysis trigger via
extension-owned loopback helper page + `session.send()` + token-protected
sanitized proxy (D029). Canvas runtime live validation is human-gated (tools
not available in the implementation surface); M6 will attempt in an active
Copilot app environment.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` -> `docs/spec.md` -> `docs/specifications/`. Sprint
context: [../../README.md](../../README.md).

## Objective

Add the minimal "Analyze selected trace with Copilot" affordance if the active
Canvas runtime supports UI-to-Copilot triggering. If it does not, document the
runtime limitation and keep the agent-callable actions as the supported workflow.

## Scope

In scope:

1. Re-check `.github/skills/create-canvas/SKILL.md` and the active Canvas runtime
   capabilities for a supported UI-to-Copilot trigger mechanism.
2. If supported, add an extension-owned UI affordance:

   ```text
   Analyze selected trace with Copilot
   ```

3. The trigger payload must contain only:
   - selected trace id;
   - selected span id, if available;
   - analysis focus: `latency`, `tokens`, `cache`, or `errors`;
   - instruction that Copilot must use sanitized monitor actions and must not
     request raw prompt/response bodies.
4. If a helper page/server is used, keep it loopback-only, token-protected per
   launch, themed with app variables, and closed in `onClose()`.
5. If unsupported, update Sprint11 docs and user guide text to describe the
   limitation and the direct Copilot action workflow.

Out of scope:

- Sending raw prompt/response bodies to Copilot.
- Passing tool arguments/results.
- Passing user id/email.
- Implementing a second monitor UI.
- Adding external services, CDN resources, or dependencies.

## Acceptance criteria

- Supported-runtime path: clicking the UI trigger starts or prepares a Copilot
  analysis request using only trace id, optional span id, focus, and sanitized
  action instructions.
- Unsupported-runtime path: the limitation is documented, and all M3/M4 actions
  remain callable from Copilot.
- No UI trigger payload contains raw/PII/credentials/local paths.
- Extension-owned routes, if any, are loopback-only and token-protected.
- Extension-owned servers close in `onClose()`.

## Validation

Canvas validation when trigger support exists:

```text
open_canvas({ canvasId: "otel-monitor", instanceId: "m5-smoke", input: { monitorBaseUrl: "http://127.0.0.1:4320" } })
```

Manual check:

- choose a trace in the Canvas UI;
- set focus to `latency`, `tokens`, `cache`, or `errors`;
- activate `Analyze selected trace with Copilot`;
- confirm the request references only sanitized action names and selected ids.

Fallback validation when trigger support is absent:

- docs explicitly say the runtime does not expose UI-to-Copilot triggering in
  the active environment;
- `monitor_health`, `list_recent_traces`, `get_trace_summary`,
  `get_trace_span_tree`, and `get_cache_summary` still work through
  `invoke_canvas_action`.

Repository validation after extension/doc changes:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

## Implementation notes

M5 implemented the supported-runtime path. The Canvas SDK exposes
`session.send({ prompt })` as the UI-to-Copilot trigger mechanism; the extension
captures the `session` object from `joinSession()` and the helper page's
`POST /analyze` route calls it after validating the trigger payload.

Changes to `.github/extensions/otel-monitor-canvas/extension.mjs`:

- `open()` now always starts an extension-owned loopback helper server
  (`127.0.0.1`, ephemeral port) with a per-launch token (`crypto.randomUUID()`)
  and returns `http://127.0.0.1:<port>/?t=<token>`. The previous "return the
  monitor URL directly when healthy" behavior is replaced by a helper page that
  links to the sanitized monitor pages.
- The helper page (`renderHelperHtml`) shows monitor health, a trace dropdown
  populated from the proxied sanitized `/api/monitor/traces?limit=50`, a focus
  selector (`latency` / `tokens` / `cache` / `errors`), an optional span id
  input, and the **"Analyze selected trace with Copilot"** button. The trigger
  is disabled when the monitor is not healthy.
- Helper server routes (all loopback, token-validated via `x-canvas-token`
  header or `?t=` query):
  - `GET /` → helper HTML;
  - `GET /api/traces` → proxies sanitized `/api/monitor/traces?limit=50` and
    returns `compactTrace`-shaped items only;
  - `POST /analyze` → validates trace id / optional span id / focus, builds a
    sanitized-only Copilot instruction, and calls `session.send({ prompt })`.
- The Copilot instruction references only the selected trace id, optional span
  id, focus, and sanitized action names (`get_trace_summary` /
  `get_trace_span_tree` / `get_cache_summary`, chosen by focus). It explicitly
  forbids requesting raw prompt/response bodies, tool arguments/results, PII,
  credentials, or local sensitive paths. No monitor payload is embedded.
- `onClose()` closes the helper server for the instance.
- M3/M4 actions are unchanged. `sanitizeDto`, loopback validation, and the
  `session.log()` / no-`console.log` invariant are preserved.

Spec/decision updates: D029 recorded in `docs/decisions.md`;
`docs/specifications/security-data-boundaries.md` and
`docs/specifications/layers/telemetry-ingestion.md` extended with the helper
page / proxy / token / `session.send` trigger invariants;
`docs/requirements.md` extended with the UI-trigger requirement.

Contract tests: `CanvasExtensionContractTests.cs` adds
`Extension_DeclaresM5UiTriggerSurface` asserting the trigger surface, token
protection, focus enum, raw-forbidding instruction, and the preserved
`/raw` / `console.log` negative checks.

Canvas runtime live validation (`extensions_manage`, `open_canvas`,
`invoke_canvas_action`, `list_canvas_capabilities`) is not available in the
implementation surface and is recorded as a human-gated blocker for M6.
Fallback evidence: contract tests (5 passing), `node --check` syntax check,
and boundary review.
