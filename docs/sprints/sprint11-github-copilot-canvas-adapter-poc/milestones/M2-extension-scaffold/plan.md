# Sprint11 M2 - Extension Scaffold (Plan)

Status: **Blocked** - Canvas scaffold and validation tools are unavailable in
the current Codex environment; no hand-written skeleton was created.
Gated by M1 canonical docs.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` -> `docs/spec.md` -> `docs/specifications/`. Sprint
context: [../../README.md](../../README.md).

## Objective

Create the project-scoped Canvas extension skeleton with the Copilot app
scaffold, then implement the minimal runtime shape: `extension.mjs`,
`joinSession({ canvases: [createCanvas({...})] })`, idempotent `open()`, and a
Canvas-safe Local Monitor diagnostic.

## Scope

In scope:

1. Re-read `.github/skills/create-canvas/SKILL.md`.
2. Use the scaffold-first command in an active Copilot app environment:

   ```text
   extensions_manage({ operation: "scaffold", kind: "canvas", name: "otel-monitor-canvas", location: "project" })
   ```

3. Inspect scaffold output and keep only scaffold-owned files under:

   ```text
   .github/extensions/otel-monitor-canvas/
   ```

4. Implement the extension entrypoint in `extension.mjs` with ES modules and
   `joinSession({ canvases: [createCanvas({...})] })`.
5. Add a single canvas, stable id `otel-monitor`, display name
   `OTel Monitor`, and a strict top-level `inputSchema`.
6. Implement idempotent `open()`:
   - accepts optional monitor base URL input, defaulting to
     `http://127.0.0.1:4320`;
   - rejects non-loopback monitor URLs with `CanvasError`;
   - calls `/health/ready`;
   - returns a diagnostic page URL when the monitor is unavailable or not
     Canvas-safe;
   - returns a sanitized Local Monitor loopback URL only when the monitor is
     expected to be launched with `--sanitized-only`.
7. If an extension-owned helper server is needed, bind it to `127.0.0.1`, protect
   stateful routes with a per-launch token, use app theme variables, and close it
   in `onClose()`.
8. Use `session.log()` for diagnostics. Do not use `console.log()`.

Blocker rule:

- If `extensions_manage`, `extensions_reload`, or inspect/open/action validation
  tools are unavailable in the active Copilot app environment, stop and record
  the blocker in the M2 review notes. Do not create a hand-written skeleton.
- Hand-written fallback requires explicit product-owner approval in a later
  instruction.

Out of scope:

- Agent actions beyond a health diagnostic.
- UI-to-Copilot analysis trigger.
- New Local Monitor routes.
- Dependencies not created by the scaffold.
- `package.json`, `node_modules`, or lockfiles unless created by scaffold and
  accepted by repository guidance.

## Acceptance criteria

- The extension is scaffolded under `.github/extensions/otel-monitor-canvas/`.
- `extension.mjs` uses ES modules and
  `joinSession({ canvases: [createCanvas({...})] })`.
- `open()` is idempotent and never opens a raw-bearing default monitor page.
- Invalid monitor URLs are rejected by schema or `CanvasError`.
- Extension-owned servers, if any, bind to `127.0.0.1` and close in `onClose()`.
- Diagnostics use `session.log()`, not `console.log()`.
- If scaffold tooling is unavailable, the milestone records the blocker and no
  extension skeleton is committed.

## Validation

Canvas environment validation:

```text
extensions_reload
extensions_manage({ operation: "list" })
extensions_manage({ operation: "inspect", name: "otel-monitor-canvas" })
list_canvas_capabilities({ canvasId: "otel-monitor" })
open_canvas({ canvasId: "otel-monitor", instanceId: "m2-smoke", input: { monitorBaseUrl: "http://127.0.0.1:4320" } })
```

Repository validation after scaffold/code changes:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh tests\CopilotAgentObservability.LocalMonitor.Tests\bin\Debug\net10.0\playwright.ps1 install chromium
dotnet test CopilotAgentObservability.slnx
```

Manual checks:

- no `node_modules` committed;
- no generated telemetry or runtime canvas state committed;
- no raw prompt/response/tool content appears in extension files or logs.
