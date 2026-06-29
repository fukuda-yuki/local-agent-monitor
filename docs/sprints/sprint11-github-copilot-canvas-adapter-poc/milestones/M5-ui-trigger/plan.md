# Sprint11 M5 - UI Trigger (Plan)

Status: **Planned** - optional Canvas UI-to-Copilot analysis trigger. Gated by
M4 action set.

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
