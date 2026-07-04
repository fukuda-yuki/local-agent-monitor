---
name: create-canvas
description: Author, validate, and debug GitHub Copilot app canvas extensions. Use when creating, reviewing, or troubleshooting a canvas extension for the app side panel, including project-scoped extensions under .github/extensions.
---

# Create Canvas

Use this skill when the user asks to create, review, or troubleshoot a GitHub
Copilot app canvas extension.

This skill is based on the GitHub Copilot app canvas guidance from:

- https://github.com/github/gh-aw/blob/main/.github/skills/create-canvas/SKILL.md
- https://github.com/github/docs/blob/main/content/copilot/how-tos/github-copilot-app/working-with-canvas-extensions.md
- https://github.com/github/copilot-sdk/blob/main/nodejs/docs/extensions.md
- https://github.com/github/copilot-sdk/blob/main/nodejs/src/canvas.ts

## Repository Safety

This repository may process raw prompts, responses, tool arguments, tool
results, identity attributes, and local paths. Do not commit runtime canvas
state or captured telemetry content.

Before creating or editing a project-scoped canvas, inspect the repository
source of truth required by `AGENTS.md`. Ask before changing product behavior,
public interfaces, security policy, adding dependencies, or committing
generated artifacts.

## Workflow

Do not implement `/create-canvas` as a `.github/prompts/*.prompt.md` prompt
file. GitHub's prompt-file documentation currently scopes prompt files to IDEs,
while the GitHub Copilot app canvas path is the `/create-canvas` skill.

1. Decide the canvas scope before creating files.
   - Project scope: `.github/extensions/<name>/`, committed and shared with the repository.
   - User scope: `$COPILOT_HOME/extensions/<name>/`, local to the current user.
   - Session scope: `$COPILOT_HOME/session-state/<sessionId>/extensions/<name>/`, temporary for one session.

2. Prefer the Copilot app extension scaffold when available.

   ```text
   extensions_manage({ operation: "scaffold", kind: "canvas", name: "<name>", location: "project" | "user" | "session" })
   ```

   If the scaffold tool is unavailable, report that blocker instead of
   silently switching workflows. Only hand-write the skeleton when the user
   explicitly asks for that fallback.

3. Implement the extension from the scaffold.
   - Entry file: `extension.mjs`.
   - Use ES modules.
   - Use `joinSession({ canvases: [createCanvas({...})] })`.
   - Bind embedded HTTP servers to `127.0.0.1` only.
   - Use `session.log()` for diagnostics. Do not use `console.log`, because stdout is reserved for JSON-RPC.
   - Do not hand-add `package.json`, `node_modules`, or dependencies unless the scaffold creates them, the user approves, and the repository guidance allows it.

4. Reload and inspect.

   ```text
   extensions_reload
   extensions_manage({ operation: "list" })
   extensions_manage({ operation: "inspect", name: "<name>" })
   ```

5. Validate the canvas from the agent side.

   ```text
   list_canvas_capabilities({ canvasId: "<canvas-id>" })
   open_canvas({ canvasId: "<canvas-id>", instanceId: "<instance-id>", input?: {...} })
   invoke_canvas_action({ instanceId: "<instance-id>", actionName: "<action-name>", input?: {...} })
   ```

## Extension Shape

Minimal project-scoped layout:

```text
.github/extensions/
  <name>/
    extension.mjs
```

Discovery scans immediate subdirectories of `.github/extensions/`,
`$COPILOT_HOME/extensions/`, and
`$COPILOT_HOME/session-state/<sessionId>/extensions/`.

The runtime derives an extension id from source and name, such as
`project:<name>`, `user:<name>`, or `session:<sessionId>:<name>`.

## Canvas API Pattern

```js
import { createServer } from "node:http";
import { createCanvas, CanvasError, joinSession } from "@github/copilot-sdk/extension";

const servers = new Map();

async function startServer(instanceId) {
  const server = createServer((req, res) => {
    res.setHeader("Content-Type", "text/html; charset=utf-8");
    res.end(renderHtml(instanceId));
  });

  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = server.address().port;
  return { server, url: `http://127.0.0.1:${port}/` };
}

await joinSession({
  canvases: [
    createCanvas({
      id: "main",
      displayName: "My canvas",
      description: "Short description shown to the agent.",
      inputSchema: {
        type: "object",
        additionalProperties: false,
      },
      actions: [
        {
          name: "refresh",
          description: "Refresh the canvas state.",
          inputSchema: {
            type: "object",
            additionalProperties: false,
          },
          handler: async (ctx) => {
            return { ok: true, instanceId: ctx.instanceId };
          },
        },
      ],
      open: async (ctx) => {
        let entry = servers.get(ctx.instanceId);
        if (!entry) {
          entry = await startServer(ctx.instanceId);
          servers.set(ctx.instanceId, entry);
        }

        return {
          title: "My canvas",
          status: "Ready",
          url: entry.url,
        };
      },
      onClose: async (ctx) => {
        const entry = servers.get(ctx.instanceId);
        if (entry) {
          servers.delete(ctx.instanceId);
          await new Promise((resolve) => entry.server.close(resolve));
        }
      },
    }),
  ],
});

function renderHtml(instanceId) {
  const escaped = escapeHtml(instanceId);
  return `<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <style>
    body {
      margin: 0;
      background: var(--background-color-default, #ffffff);
      color: var(--text-color-default, #1f2328);
      font-family: var(--font-sans, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif);
      font-size: var(--text-body-medium, 14px);
      line-height: var(--leading-body-medium, 20px);
      padding: 16px;
    }
  </style>
</head>
<body>
  <main>
    <h1>Canvas ${escaped}</h1>
  </main>
</body>
</html>`;
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (char) => {
    if (char === "&") return "&amp;";
    if (char === "<") return "&lt;";
    if (char === ">") return "&gt;";
    if (char === '"') return "&quot;";
    return "&#39;";
  });
}
```

## Runtime Rules

- `canvasId` is the declared canvas type id.
- `extensionId` disambiguates providers when two extensions declare the same `canvasId`.
- `instanceId` identifies one open panel instance. It must match `^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$`.
- Action names must be unique within the canvas and must not start with `canvas.`.
- Every action needs a `handler`.
- `open` should be idempotent because it may be called again after provider reconnect.
- Key durable state by a stable document, board, task, or artifact id, not by `instanceId` alone.
- Return structured raw values from handlers. Throw `CanvasError("code", "message")` for expected errors.
- Use app theme variables documented by the SDK guidance. Do not rely on internal host DOM or undocumented CSS tokens.

## Validation Checklist

Before calling the work complete:

1. Confirm the extension is discovered and not marked failed.
2. Confirm `list_canvas_capabilities` returns the declared canvas and actions.
3. Confirm `open_canvas` returns an instance and URL without error.
4. Confirm each action works through `invoke_canvas_action`.
5. Confirm invalid input is rejected by `inputSchema`.
6. Confirm loopback servers close on `onClose`.
7. Confirm no raw prompt, response, tool arguments, tool results, credentials, tokens, local sensitive paths, runtime artifacts, or generated telemetry files were added to the repository.
