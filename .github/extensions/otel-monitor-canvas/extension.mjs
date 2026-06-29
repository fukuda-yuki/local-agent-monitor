// Extension: otel-monitor-canvas
//
// Sprint11 M2: project-scoped Canvas extension skeleton for the Local Ingestion
// Monitor. This is a thin adapter — it does not reimplement the monitor UI or
// expose raw telemetry data. The Local Monitor must be launched with
// --sanitized-only for Canvas-safe posture.
//
// Canvas id: otel-monitor
// Display name: OTel Monitor

import { createServer } from "node:http";
import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";

const DEFAULT_MONITOR_URL = "http://127.0.0.1:4320";

// Per-instance HTTP servers for diagnostic / status pages.
const servers = new Map();

// --------------- helpers ---------------

function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, (char) => {
        if (char === "&") return "&amp;";
        if (char === "<") return "&lt;";
        if (char === ">") return "&gt;";
        if (char === '"') return "&quot;";
        return "&#39;";
    });
}

function renderDiagnosticHtml({ instanceId, monitorUrl, healthStatus, healthBody, error }) {
    const escapedUrl = escapeHtml(monitorUrl);
    const escapedInstance = escapeHtml(instanceId);
    const escapedHealth = escapeHtml(healthStatus ?? "unknown");
    const escapedBody = escapeHtml(healthBody ?? "");
    const escapedError = escapeHtml(error ?? "");

    return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>OTel Monitor — Diagnostic</title>
  <style>
    :root {
      --bg: var(--background-color-default, #ffffff);
      --fg: var(--text-color-default, #1f2328);
      --muted: var(--text-color-muted, #656d76);
      --border: var(--border-color-default, #d0d7de);
      --accent: var(--accent-color-default, #0969da);
      --danger: #cf222e;
      --success: #1a7f37;
      --font: var(--font-sans, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif);
      --size: var(--text-body-medium, 14px);
      --leading: var(--leading-body-medium, 20px);
    }
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
      font-family: var(--font);
      font-size: var(--size);
      line-height: var(--leading);
      background: var(--bg);
      color: var(--fg);
      padding: 24px;
    }
    h1 { font-size: 1.25rem; margin-bottom: 16px; }
    .card {
      border: 1px solid var(--border);
      border-radius: 6px;
      padding: 16px;
      margin-bottom: 16px;
    }
    .card h2 { font-size: 1rem; margin-bottom: 8px; }
    .kv { display: grid; grid-template-columns: 160px 1fr; gap: 4px 12px; }
    .kv dt { color: var(--muted); font-weight: 500; }
    .status-ok { color: var(--success); font-weight: 600; }
    .status-err { color: var(--danger); font-weight: 600; }
    pre {
      background: #f6f8fa;
      border: 1px solid var(--border);
      border-radius: 4px;
      padding: 12px;
      overflow: auto;
      font-size: 12px;
      line-height: 1.4;
    }
    .banner {
      padding: 12px 16px;
      border-radius: 6px;
      margin-bottom: 16px;
      font-weight: 600;
    }
    .banner-warn { background: #fff8c5; border: 1px solid #d4a72c; color: #5c4b00; }
    .banner-err  { background: #ffebe9; border: 1px solid #cf222e; color: #5c0000; }
  </style>
</head>
<body>
  <h1>OTel Monitor — Diagnostic</h1>

  ${error ? `<div class="banner banner-err">${escapedError}</div>` : ""}
  ${healthStatus === "healthy" ? "" : `<div class="banner banner-warn">Monitor is not reporting healthy. Ensure the Local Monitor is running with <code>--sanitized-only</code>.</div>`}

  <div class="card">
    <h2>Connection</h2>
    <dl class="kv">
      <dt>Monitor URL</dt><dd><code>${escapedUrl}</code></dd>
      <dt>Instance</dt><dd><code>${escapedInstance}</code></dd>
      <dt>Health status</dt><dd><span class="${healthStatus === "healthy" ? "status-ok" : "status-err"}">${escapedHealth}</span></dd>
    </dl>
  </div>

  ${escapedBody ? `<div class="card"><h2>Health Response</h2><pre>${escapedBody}</pre></div>` : ""}

  <div class="card">
    <h2>Canvas-safe posture</h2>
    <p>This Canvas adapter requires the Local Monitor to be launched with <code>--sanitized-only</code>. Raw prompt bodies, tool arguments, and PII must not be exposed through Canvas actions or display.</p>
  </div>
</body>
</html>`;
}

function createDiagnosticServer(instanceId, monitorUrl, healthStatus, healthBody, error) {
    const server = createServer((_req, res) => {
        res.setHeader("Content-Type", "text/html; charset=utf-8");
        res.end(renderDiagnosticHtml({ instanceId, monitorUrl, healthStatus, healthBody, error }));
    });
    return server;
}

async function startDiagnosticServer(instanceId, monitorUrl, healthStatus, healthBody, error) {
    const server = createDiagnosticServer(instanceId, monitorUrl, healthStatus, healthBody, error);
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const address = server.address();
    const port = typeof address === "object" && address ? address.port : 0;
    return { server, url: `http://127.0.0.1:${port}/` };
}

function isLoopbackUrl(urlString) {
    try {
        const url = new URL(urlString);
        return url.hostname === "127.0.0.1" || url.hostname === "localhost" || url.hostname === "[::1]";
    } catch {
        return false;
    }
}

async function checkMonitorHealth(monitorUrl) {
    const healthUrl = `${monitorUrl.replace(/\/$/, "")}/health/ready`;
    try {
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 5000);
        const response = await fetch(healthUrl, { signal: controller.signal });
        clearTimeout(timeout);
        const body = await response.text();
        return { healthy: response.ok, statusCode: response.status, body };
    } catch (err) {
        return { healthy: false, statusCode: null, body: null, error: err.message };
    }
}

// --------------- canvas ---------------

const session = await joinSession({
    canvases: [
        createCanvas({
            id: "otel-monitor",
            displayName: "OTel Monitor",
            description:
                "Local Ingestion Monitor Canvas adapter. Requires the Local Monitor to run with --sanitized-only. Opens sanitized monitor pages and provides agent-callable actions over existing /api/monitor/* data.",

            inputSchema: {
                type: "object",
                properties: {
                    monitorBaseUrl: {
                        type: "string",
                        description: "Base URL of the Local Ingestion Monitor (default: http://127.0.0.1:4320).",
                        default: DEFAULT_MONITOR_URL,
                    },
                },
                additionalProperties: false,
            },

            actions: [
                // Actions will be added in M3 (monitor_health, list_recent_traces, etc.).
            ],

            open: async (ctx) => {
                const monitorUrl = ctx.input?.monitorBaseUrl ?? DEFAULT_MONITOR_URL;

                // Validate loopback-only.
                if (!isLoopbackUrl(monitorUrl)) {
                    throw new CanvasError(
                        "invalid_monitor_url",
                        `Monitor URL must be loopback (127.0.0.1 / localhost / ::1). Received: ${monitorUrl}`
                    );
                }

                // Clean up any previous server for this instance (idempotent).
                const prev = servers.get(ctx.instanceId);
                if (prev) {
                    await new Promise((resolve) => prev.server.close(() => resolve()));
                    servers.delete(ctx.instanceId);
                }

                // Check monitor health.
                const health = await checkMonitorHealth(monitorUrl);

                if (health.healthy) {
                    return {
                        title: "OTel Monitor",
                        status: "Connected",
                        url: monitorUrl,
                    };
                }

                // Monitor is not healthy. Start a diagnostic server on an
                // ephemeral loopback port and show the diagnostic page.
                const entry = await startDiagnosticServer(
                    ctx.instanceId,
                    monitorUrl,
                    health.statusCode !== null ? `unhealthy (${health.statusCode})` : "unreachable",
                    health.body,
                    health.error,
                );
                servers.set(ctx.instanceId, entry);
                return {
                    title: "OTel Monitor — Offline",
                    status: "Monitor unavailable",
                    url: entry.url,
                };
            },

            onClose: async (ctx) => {
                const entry = servers.get(ctx.instanceId);
                if (entry) {
                    servers.delete(ctx.instanceId);
                    await new Promise((resolve) => entry.server.close(() => resolve()));
                }
            },
        }),
    ],
});
