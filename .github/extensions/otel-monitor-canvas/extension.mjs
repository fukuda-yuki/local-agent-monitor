// Extension: otel-monitor-canvas
//
// Sprint11 M3/M4/M5: project-scoped Canvas extension for the Local Ingestion
// Monitor. This is a thin adapter — it does not reimplement the monitor UI or
// expose raw telemetry through Canvas action responses or logs. It may be used
// with the Local Monitor's normal raw-default UI posture.
//
// M5 adds an extension-owned loopback helper page with a trace dropdown
// (proxied from sanitized /api/monitor/traces) and an "Analyze selected trace
// with Copilot" trigger that calls session.send() with a bounded monitor
// analysis instruction. See D029.
//
// Canvas id: otel-monitor
// Display name: OTel Monitor

import { createServer } from "node:http";
import { randomUUID } from "node:crypto";
import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";

const DEFAULT_MONITOR_URL = "http://127.0.0.1:4320";
const TRACE_ID_PATTERN = "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$";
const MAX_TRACE_LIST_LIMIT = 50;
const MAX_SPAN_PAGE_SIZE = 200;
const MAX_TOP_SPANS = 10;
const MAX_TREE_NODES = 50;
const MAX_CACHE_TURNS = 50;
const REQUEST_TIMEOUT_MS = 5000;
const FOCUS_VALUES = ["latency", "tokens", "cache", "errors"];

const traceIdSchema = {
    type: "object",
    properties: {
        traceId: {
            type: "string",
            pattern: TRACE_ID_PATTERN,
            maxLength: 128,
        },
    },
    required: ["traceId"],
    additionalProperties: false,
};

// Per-instance HTTP servers for the helper page (M5).
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

function renderHelperHtml({ instanceId, monitorUrl, healthStatus, healthBody, error, token }) {
    const escapedUrl = escapeHtml(monitorUrl);
    const escapedInstance = escapeHtml(instanceId);
    const escapedHealth = escapeHtml(healthStatus ?? "unknown");
    const escapedBody = escapeHtml(healthBody ?? "");
    const escapedError = escapeHtml(error ?? "");
    const escapedToken = escapeHtml(token);
    const healthy = healthStatus === "healthy";

    return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>OTel Monitor — Canvas</title>
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
    label { display: block; font-weight: 500; margin-bottom: 4px; }
    select, input[type="text"] {
      width: 100%;
      padding: 6px 8px;
      border: 1px solid var(--border);
      border-radius: 4px;
      font: inherit;
      background: var(--bg);
      color: var(--fg);
    }
    .row { margin-bottom: 12px; }
    button {
      padding: 8px 16px;
      border: 1px solid var(--border);
      border-radius: 6px;
      background: var(--accent);
      color: #ffffff;
      font: inherit;
      font-weight: 600;
      cursor: pointer;
    }
    button:disabled { opacity: 0.5; cursor: not-allowed; }
    #result { margin-top: 12px; font-size: 12px; }
    .ok { color: var(--success); }
    .err { color: var(--danger); }
    a { color: var(--accent); }
  </style>
</head>
<body>
  <h1>OTel Monitor — Canvas</h1>

  ${error ? `<div class="banner banner-err">${escapedError}</div>` : ""}
  ${healthy ? "" : `<div class="banner banner-warn">Monitor is not reporting healthy. Check that the Local Monitor is running and ready.</div>`}

  <div class="card">
    <h2>Connection</h2>
    <dl class="kv">
      <dt>Monitor URL</dt><dd><code>${escapedUrl}</code></dd>
      <dt>Instance</dt><dd><code>${escapedInstance}</code></dd>
      <dt>Health status</dt><dd><span class="${healthy ? "status-ok" : "status-err"}">${escapedHealth}</span></dd>
    </dl>
  </div>

  ${escapedBody ? `<div class="card"><h2>Health Response</h2><pre>${escapedBody}</pre></div>` : ""}

  <div class="card">
    <h2>Analyze selected trace with Copilot</h2>
    <p style="margin-bottom:12px;color:var(--muted);">Select a trace and a focus, then trigger a Copilot analysis. Copilot will use bounded monitor actions and will not receive raw monitor payloads from this helper.</p>
    <div class="row">
      <label for="trace">Trace</label>
      <select id="trace" ${healthy ? "" : "disabled"}></select>
    </div>
    <div class="row">
      <label for="focus">Focus</label>
      <select id="focus" ${healthy ? "" : "disabled"}>
        <option value="latency">latency</option>
        <option value="tokens">tokens</option>
        <option value="cache">cache</option>
        <option value="errors">errors</option>
      </select>
    </div>
    <div class="row">
      <label for="span">Span id (optional)</label>
      <input type="text" id="span" placeholder="optional span id" ${healthy ? "" : "disabled"} />
    </div>
    <button id="analyze" ${healthy ? "" : "disabled"}>Analyze selected trace with Copilot</button>
    <div id="result"></div>
  </div>

  <div class="card">
    <h2>Monitor pages</h2>
    <p>Open the Local Monitor pages in your browser: <a href="${escapedUrl}" target="_blank" rel="noopener noreferrer">${escapedUrl}</a></p>
  </div>

  <div class="card">
    <h2>Local monitor posture</h2>
    <p>This adapter works with the Local Monitor's normal raw-default UI. Canvas action responses and logs must not contain raw telemetry or PII.</p>
  </div>

  <script>
    (function () {
      var token = ${JSON.stringify(escapedToken)};
      var traceSel = document.getElementById("trace");
      var focusSel = document.getElementById("focus");
      var spanInput = document.getElementById("span");
      var btn = document.getElementById("analyze");
      var result = document.getElementById("result");

      function setResult(msg, ok) {
        result.textContent = msg;
        result.className = ok ? "ok" : "err";
      }

      if (!token) { setResult("Missing launch token.", false); return; }

      fetch("/api/traces?t=" + encodeURIComponent(token), { headers: { "x-canvas-token": token } })
        .then(function (r) {
          if (!r.ok) { throw new Error("HTTP " + r.status); }
          return r.json();
        })
        .then(function (data) {
          (data.items || []).forEach(function (t) {
            var opt = document.createElement("option");
            opt.value = t.trace_id;
            opt.textContent = t.trace_id + " — " + (t.status || "?") + " — spans:" + (t.span_count || 0);
            traceSel.appendChild(opt);
          });
          if (!traceSel.options.length) { setResult("No recent traces found.", true); }
        })
        .catch(function (e) { setResult("Failed to load traces: " + e.message, false); });

      btn.addEventListener("click", function () {
        var traceId = traceSel.value;
        if (!traceId) { setResult("Select a trace first.", false); return; }
        var focus = focusSel.value;
        var spanId = spanInput.value.trim();
        btn.disabled = true;
        setResult("Dispatching…", true);
        fetch("/analyze", {
          method: "POST",
          headers: { "Content-Type": "application/json", "x-canvas-token": token },
          body: JSON.stringify({ traceId: traceId, spanId: spanId || undefined, focus: focus })
        })
          .then(function (r) { return r.json().then(function (b) { return { status: r.status, body: b }; }); })
          .then(function (out) {
            if (out.status === 200) { setResult("Analysis dispatched to Copilot. Check the Copilot chat.", true); }
            else { setResult("Failed: " + (out.body && out.body.error || ("HTTP " + out.status)), false); }
          })
          .catch(function (e) { setResult("Failed: " + e.message, false); })
          .finally(function () { btn.disabled = false; });
      });
    })();
  </script>
</body>
</html>`;
}

function matchesTraceId(value) {
    return typeof value === "string" && new RegExp(TRACE_ID_PATTERN).test(value);
}

function buildAnalysisPrompt({ traceId, spanId, focus }) {
    const focusActions = {
        latency: ["get_trace_summary", "get_trace_span_tree"],
        tokens: ["get_trace_summary", "get_cache_summary"],
        cache: ["get_cache_summary"],
        errors: ["get_trace_span_tree"],
    };
    const actions = focusActions[focus] ?? ["get_trace_summary"];
    const spanLine = spanId ? `Selected span id: ${spanId}\n` : "";
    return [
        `Analyze the selected Local Ingestion Monitor trace using bounded Canvas actions.`,
        `Trace id: ${traceId}`,
        spanLine,
        `Analysis focus: ${focus}.`,
        ``,
        `Call the following monitor actions via invoke_canvas_action on the open otel-monitor canvas instance to gather bounded data:`,
        ...actions.map((a) => `  - ${a}({ traceId: "${traceId}" })`),
        ``,
        `Constraints:`,
        `- Use the monitor actions listed above; they return bounded DTOs from existing Local Monitor APIs.`,
        `- Do not copy raw prompt bodies, raw response bodies, tool arguments, tool results, PII, credentials, tokens, or local sensitive paths into Canvas action responses, logs, committed files, or static artifacts.`,
        `- Raw details, when needed, remain local Monitor UI data under its existing loopback and same-origin boundary.`,
        `- Summarize findings and suggest improvements for the ${focus} focus.`,
    ].filter(Boolean).join("\n");
}

function readRequestBody(req) {
    return new Promise((resolve) => {
        let data = "";
        req.on("data", (chunk) => { data += chunk; if (data.length > 8192) { req.destroy(); } });
        req.on("end", () => resolve(data));
        req.on("error", () => resolve(""));
    });
}

function sendJson(res, statusCode, payload) {
    res.setHeader("Content-Type", "application/json; charset=utf-8");
    res.statusCode = statusCode;
    res.end(JSON.stringify(payload));
}

function createHelperServer({ instanceId, monitorUrl, healthStatus, healthBody, error, token, session }) {
    const server = createServer(async (req, res) => {
        const url = new URL(req.url, "http://127.0.0.1");
        const path = url.pathname;

        // Token validation for all routes.
        const headerToken = req.headers["x-canvas-token"];
        const queryToken = url.searchParams.get("t");
        const suppliedToken = headerToken || queryToken;
        if (suppliedToken !== token) {
            sendJson(res, 401, { error: "unauthorized" });
            return;
        }

        if (req.method === "GET" && path === "/") {
            res.setHeader("Content-Type", "text/html; charset=utf-8");
            res.end(renderHelperHtml({ instanceId, monitorUrl, healthStatus, healthBody, error, token }));
            return;
        }

        if (req.method === "GET" && path === "/api/traces") {
            try {
                const monitorUrlValidated = monitorUrl;
                if (!isLoopbackUrl(monitorUrlValidated)) {
                    sendJson(res, 400, { error: "invalid_monitor_url" });
                    return;
                }
                const { response, body } = await fetchTextWithTimeout(monitorApiUrl(monitorUrlValidated, `/api/monitor/traces?limit=${MAX_TRACE_LIST_LIMIT}`));
                if (!response.ok) {
                    sendJson(res, 502, { error: "monitor_unavailable", status: response.status });
                    return;
                }
                const page = body ? parseJsonBody(body) : { items: [] };
                const items = Array.isArray(page.items) ? page.items.map(compactTrace) : [];
                sendJson(res, 200, { items, count: items.length });
            } catch (err) {
                const code = err instanceof CanvasError ? err.code : "monitor_unavailable";
                sendJson(res, 502, { error: code, message: err.message });
            }
            return;
        }

        if (req.method === "POST" && path === "/analyze") {
            try {
                const raw = await readRequestBody(req);
                const payload = raw ? parseJsonBody(raw, "invalid_input") : {};
                const traceId = payload.traceId;
                const spanId = payload.spanId;
                const focus = payload.focus;
                if (!matchesTraceId(traceId)) {
                    sendJson(res, 400, { error: "invalid_trace_id" });
                    return;
                }
                if (spanId !== undefined && spanId !== null && spanId !== "" && !matchesTraceId(spanId)) {
                    sendJson(res, 400, { error: "invalid_span_id" });
                    return;
                }
                if (!FOCUS_VALUES.includes(focus)) {
                    sendJson(res, 400, { error: "invalid_focus" });
                    return;
                }
                const prompt = buildAnalysisPrompt({ traceId, spanId: spanId || null, focus });
                await session.send({ prompt });
                sendJson(res, 200, { ok: true, dispatched: true });
            } catch (err) {
                const code = err instanceof CanvasError ? err.code : "analyze_failed";
                sendJson(res, 500, { error: code, message: err.message });
            }
            return;
        }

        sendJson(res, 404, { error: "not_found" });
    });
    return server;
}

async function startHelperServer({ instanceId, monitorUrl, healthStatus, healthBody, error, token, session }) {
    const server = createHelperServer({ instanceId, monitorUrl, healthStatus, healthBody, error, token, session });
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const address = server.address();
    const port = typeof address === "object" && address ? address.port : 0;
    return { server, url: `http://127.0.0.1:${port}/?t=${token}` };
}

function isLoopbackUrl(urlString) {
    try {
        const url = new URL(urlString);
        return url.protocol === "http:"
            && (url.hostname === "127.0.0.1" || url.hostname === "localhost" || url.hostname === "[::1]");
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

function configuredMonitorUrl(ctx) {
    return ctx.canvasInput?.monitorBaseUrl
        ?? ctx.openInput?.monitorBaseUrl
        ?? ctx.instanceInput?.monitorBaseUrl
        ?? DEFAULT_MONITOR_URL;
}

function validateMonitorUrl(monitorUrl) {
    if (!isLoopbackUrl(monitorUrl)) {
        throw new CanvasError(
            "invalid_monitor_url",
            `Monitor URL must be loopback (127.0.0.1 / localhost / ::1). Received: ${monitorUrl}`
        );
    }
}

function monitorApiUrl(monitorUrl, path) {
    const base = monitorUrl.replace(/\/$/, "");
    return `${base}${path}`;
}

async function fetchTextWithTimeout(url) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
    try {
        const response = await fetch(url, { signal: controller.signal });
        const body = await response.text();
        return { response, body };
    } catch (err) {
        if (err?.name === "AbortError") {
            throw new CanvasError("monitor_unavailable", "The Local Monitor request timed out.");
        }

        throw new CanvasError("monitor_unavailable", "The Local Monitor is unavailable.");
    } finally {
        clearTimeout(timeout);
    }
}

function parseJsonBody(body, code = "unsupported_response_shape") {
    try {
        return JSON.parse(body);
    } catch {
        throw new CanvasError(code, "The Local Monitor returned a response shape the Canvas adapter does not support.");
    }
}

async function fetchMonitorJson(ctx, path) {
    const monitorUrl = configuredMonitorUrl(ctx);
    validateMonitorUrl(monitorUrl);

    const { response, body } = await fetchTextWithTimeout(monitorApiUrl(monitorUrl, path));
    const parsed = body ? parseJsonBody(body) : null;
    if (!response.ok) {
        const code = parsed?.error === "persistence_busy" ? "persistence_busy" : "monitor_unavailable";
        const message = typeof parsed?.message === "string"
            ? parsed.message
            : `The Local Monitor returned HTTP ${response.status}.`;
        throw new CanvasError(code, message);
    }

    return parsed;
}

async function fetchReadiness(ctx) {
    const monitorUrl = configuredMonitorUrl(ctx);
    validateMonitorUrl(monitorUrl);

    const { response, body } = await fetchTextWithTimeout(monitorApiUrl(monitorUrl, "/health/ready"));
    return {
        monitorUrl,
        reachable: true,
        statusCode: response.status,
        readiness: body ? parseJsonBody(body) : null,
        ok: response.ok,
    };
}

async function fetchTracePage(ctx, limit = MAX_TRACE_LIST_LIMIT) {
    const page = await fetchMonitorJson(ctx, `/api/monitor/traces?limit=${limit}`);
    if (!page || !Array.isArray(page.items)) {
        throw new CanvasError("unsupported_response_shape", "The trace list response did not contain an items array.");
    }

    return page;
}

async function fetchSpanPage(ctx, traceId) {
    const encodedTraceId = encodeURIComponent(traceId);
    const page = await fetchMonitorJson(ctx, `/api/monitor/traces/${encodedTraceId}/spans?limit=${MAX_SPAN_PAGE_SIZE}`);
    if (!page || !Array.isArray(page.items)) {
        throw new CanvasError("unsupported_response_shape", "The span list response did not contain an items array.");
    }

    return page;
}

function numberOrZero(value) {
    return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

function statusFromTrace(row) {
    return numberOrZero(row.error_count) > 0 ? "error" : "ok";
}

function modelForSpan(span) {
    return span.response_model || span.request_model || null;
}

function spanSubject(span) {
    return span.tool_name || span.mcp_tool_name || span.agent_name || modelForSpan(span) || null;
}

function isErrorSpan(span) {
    return span.status === "error" || Boolean(span.error_type);
}

function compareByTimeThenOrdinal(a, b) {
    if (a.start_time && b.start_time && a.start_time !== b.start_time) {
        return String(a.start_time).localeCompare(String(b.start_time));
    }

    if (a.start_time && !b.start_time) return -1;
    if (!a.start_time && b.start_time) return 1;
    const ordinal = numberOrZero(a.span_ordinal) - numberOrZero(b.span_ordinal);
    return ordinal !== 0 ? ordinal : numberOrZero(a.id) - numberOrZero(b.id);
}

function compactTrace(row) {
    return {
        trace_id: row.trace_id,
        client_kind: row.client_kind ?? null,
        status: statusFromTrace(row),
        span_count: row.span_count ?? null,
        tool_call_count: row.tool_call_count ?? null,
        error_count: row.error_count ?? null,
        input_tokens: row.input_tokens ?? null,
        output_tokens: row.output_tokens ?? null,
        total_tokens: row.total_tokens ?? null,
        turn_count: row.turn_count ?? null,
        agent_invocation_count: row.agent_invocation_count ?? null,
        duration_ms: row.duration_ms ?? null,
        primary_model: row.primary_model ?? null,
        first_seen_at: row.first_seen_at ?? null,
        last_seen_at: row.last_seen_at ?? null,
    };
}

function compactSpan(span) {
    return {
        span_ref: span.span_id || `row:${span.id}`,
        span_id: span.span_id ?? null,
        parent_span_id: span.parent_span_id ?? null,
        span_ordinal: span.span_ordinal ?? null,
        operation: span.operation ?? null,
        category: span.category ?? null,
        subject: spanSubject(span),
        tool_name: span.tool_name ?? null,
        tool_type: span.tool_type ?? null,
        mcp_tool_name: span.mcp_tool_name ?? null,
        mcp_server_hash: span.mcp_server_hash ?? null,
        agent_name: span.agent_name ?? null,
        model: modelForSpan(span),
        status: span.status ?? null,
        error_type: span.error_type ?? null,
        duration_ms: span.duration_ms ?? null,
        start_time: span.start_time ?? null,
        end_time: span.end_time ?? null,
        input_tokens: span.input_tokens ?? null,
        output_tokens: span.output_tokens ?? null,
        total_tokens: span.total_tokens ?? null,
        reasoning_tokens: span.reasoning_tokens ?? null,
        cache_read_tokens: span.cache_read_tokens ?? null,
        cache_creation_tokens: span.cache_creation_tokens ?? null,
    };
}

function summarizeTopSpans(spans) {
    return [...spans]
        .sort((a, b) => {
            const tokens = numberOrZero(b.total_tokens) - numberOrZero(a.total_tokens);
            if (tokens !== 0) return tokens;
            const duration = numberOrZero(b.duration_ms) - numberOrZero(a.duration_ms);
            return duration !== 0 ? duration : compareByTimeThenOrdinal(a, b);
        })
        .slice(0, MAX_TOP_SPANS)
        .map(compactSpan);
}

function sumField(spans, field) {
    return spans.reduce((sum, span) => sum + numberOrZero(span[field]), 0);
}

function uniqueModels(spans) {
    return [...new Set(spans.map(modelForSpan).filter(Boolean))].sort();
}

function isChatTurn(span) {
    return span.operation === "chat" || span.category === "llm_call";
}

function cacheHitRate(cacheReadTokens, inputTokens) {
    return inputTokens > 0 ? cacheReadTokens / inputTokens : null;
}

function cacheTurn(span) {
    return {
        span_ref: span.span_id || `row:${span.id}`,
        timestamp: span.start_time ?? null,
        model: modelForSpan(span),
        duration_ms: span.duration_ms ?? null,
        input_tokens: span.input_tokens ?? null,
        output_tokens: span.output_tokens ?? null,
        total_tokens: span.total_tokens ?? null,
        reasoning_tokens: span.reasoning_tokens ?? null,
        cache_read_tokens: span.cache_read_tokens ?? null,
        cache_creation_tokens: span.cache_creation_tokens ?? null,
        cache_hit_rate: cacheHitRate(numberOrZero(span.cache_read_tokens), numberOrZero(span.input_tokens)),
        status: span.status ?? null,
        error_type: span.error_type ?? null,
    };
}

function treeNode(span) {
    return {
        ...compactSpan(span),
        child_refs: [],
        children: [],
    };
}

function hierarchyFromSpans(spans) {
    const ordered = [...spans].sort(compareByTimeThenOrdinal);
    const truncated = ordered.length > MAX_TREE_NODES;
    const returned = ordered.slice(0, MAX_TREE_NODES);
    const hasAnyParent = ordered.some((span) => Boolean(span.parent_span_id));
    const allHaveSpanId = ordered.every((span) => Boolean(span.span_id));
    const fullIds = new Set(ordered.map((span) => span.span_id).filter(Boolean));
    const parentLinksComplete = ordered.every((span) => !span.parent_span_id || fullIds.has(span.parent_span_id));

    if (!hasAnyParent || !allHaveSpanId) {
        return {
            hierarchy_status: "flat_missing_parent_ids",
            spans: returned.map(compactSpan),
            returned_node_count: returned.length,
            truncated,
        };
    }

    if (!parentLinksComplete) {
        return {
            hierarchy_status: "flat_incomplete_parent_links",
            spans: returned.map(compactSpan),
            returned_node_count: returned.length,
            truncated,
        };
    }

    const nodes = new Map();
    for (const span of returned) {
        nodes.set(span.span_id, treeNode(span));
    }

    const roots = [];
    for (const span of returned) {
        const node = nodes.get(span.span_id);
        const parent = span.parent_span_id ? nodes.get(span.parent_span_id) : null;
        if (parent && node) {
            parent.child_refs.push(node.span_ref);
            parent.children.push(node);
        } else if (node) {
            roots.push(node);
        }
    }

    return {
        hierarchy_status: "complete",
        roots,
        returned_node_count: returned.length,
        truncated,
    };
}

function sanitizeDto(value) {
    if (Array.isArray(value)) {
        return value.map(sanitizeDto);
    }

    if (!value || typeof value !== "object") {
        return value;
    }

    const forbiddenKey = /(raw|payload|prompt|content|argument|result|user|email|credential|secret)/i;
    const sanitized = {};
    for (const [key, child] of Object.entries(value)) {
        if (forbiddenKey.test(key)) {
            continue;
        }

        sanitized[key] = sanitizeDto(child);
    }

    return sanitized;
}

async function handleMonitorHealth(ctx) {
    const monitorUrl = configuredMonitorUrl(ctx);
    validateMonitorUrl(monitorUrl);

    try {
        const health = await fetchReadiness(ctx);
        return sanitizeDto({
            reachable: true,
            ready_status_code: health.statusCode,
            readiness: health.readiness,
            canvas_safe: health.ok && health.readiness?.status === "ready",
            monitor_base_url: health.monitorUrl,
            diagnostic: health.ok
                ? "Local Monitor is reachable. Canvas adapter can be used with the normal raw-default Local Monitor."
                : "Local Monitor is reachable but not ready.",
        });
    } catch (err) {
        if (err instanceof CanvasError) {
            return sanitizeDto({
                reachable: false,
                ready_status_code: null,
                readiness: null,
                canvas_safe: false,
                monitor_base_url: monitorUrl,
                diagnostic: err.message,
            });
        }

        throw err;
    }
}

async function handleListRecentTraces(ctx) {
    const input = ctx.input ?? {};
    const page = await fetchTracePage(ctx, input.limit);
    let items = page.items.map(compactTrace);
    if (input.status) {
        items = items.filter((trace) => trace.status === input.status);
    }

    if (input.model) {
        items = items.filter((trace) => trace.primary_model === input.model);
    }

    return sanitizeDto({
        items,
        count: items.length,
        truncated: false,
    });
}

async function findTraceSummary(ctx, traceId) {
    const page = await fetchTracePage(ctx, MAX_SPAN_PAGE_SIZE);
    return page.items.find((row) => row.trace_id === traceId) ?? null;
}

async function handleGetTraceSummary(ctx) {
    const traceId = ctx.input.traceId;
    const [traceRow, spanPage] = await Promise.all([
        findTraceSummary(ctx, traceId),
        fetchSpanPage(ctx, traceId),
    ]);
    const spans = spanPage.items;
    if (!traceRow && spans.length === 0) {
        throw new CanvasError("trace_not_found", "No sanitized trace data exists for that trace id.");
    }

    const chatTurns = spans.filter(isChatTurn);
    const cacheReadTokens = sumField(chatTurns, "cache_read_tokens");
    const cacheCreationTokens = sumField(chatTurns, "cache_creation_tokens");

    return sanitizeDto({
        trace: traceRow
            ? compactTrace(traceRow)
            : {
                trace_id: traceId,
                status: spans.some(isErrorSpan) ? "error" : "ok",
                span_count: spans.length,
            },
        top_spans: summarizeTopSpans(spans),
        models: uniqueModels(spans),
        cache_totals: {
            cache_read_tokens: cacheReadTokens,
            cache_creation_tokens: cacheCreationTokens,
            input_tokens: sumField(chatTurns, "input_tokens"),
            output_tokens: sumField(chatTurns, "output_tokens"),
            total_tokens: sumField(chatTurns, "total_tokens"),
        },
        span_page_truncated: spanPage.next_cursor !== null && spanPage.next_cursor !== undefined,
    });
}

async function handleGetTraceSpanTree(ctx) {
    const traceId = ctx.input.traceId;
    const spanPage = await fetchSpanPage(ctx, traceId);
    if (spanPage.items.length === 0) {
        throw new CanvasError("trace_not_found", "No sanitized spans exist for that trace id.");
    }

    return sanitizeDto({
        trace_id: traceId,
        span_count: spanPage.items.length,
        ...hierarchyFromSpans(spanPage.items),
    });
}

async function handleGetCacheSummary(ctx) {
    const traceId = ctx.input.traceId;
    const spanPage = await fetchSpanPage(ctx, traceId);
    if (spanPage.items.length === 0) {
        throw new CanvasError("trace_not_found", "No sanitized spans exist for that trace id.");
    }

    const turns = spanPage.items
        .filter(isChatTurn)
        .sort(compareByTimeThenOrdinal);
    const returnedTurns = turns.slice(0, MAX_CACHE_TURNS);
    const inputTokens = sumField(turns, "input_tokens");
    const outputTokens = sumField(turns, "output_tokens");
    const totalTokens = sumField(turns, "total_tokens");
    const cacheReadTokens = sumField(turns, "cache_read_tokens");
    const cacheCreationTokens = sumField(turns, "cache_creation_tokens");

    return sanitizeDto({
        trace_id: traceId,
        turn_count: turns.length,
        returned_turn_count: returnedTurns.length,
        truncated: turns.length > MAX_CACHE_TURNS,
        totals: {
            input_tokens: inputTokens,
            output_tokens: outputTokens,
            total_tokens: totalTokens,
            cache_read_tokens: cacheReadTokens,
            cache_creation_tokens: cacheCreationTokens,
            duration_ms: sumField(turns, "duration_ms"),
        },
        cache_hit_rate: cacheHitRate(cacheReadTokens, inputTokens),
        turns: returnedTurns.map(cacheTurn),
    });
}

// --------------- canvas ---------------

const session = await joinSession({
    canvases: [
        createCanvas({
            id: "otel-monitor",
            displayName: "OTel Monitor",
            description:
                "Local Ingestion Monitor Canvas adapter. Opens Local Monitor pages and provides agent-callable bounded actions over existing /api/monitor/* data.",

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
                {
                    name: "monitor_health",
                    description: "Return Local Monitor readiness and Canvas adapter diagnostics.",
                    inputSchema: {
                        type: "object",
                        additionalProperties: false,
                    },
                    handler: handleMonitorHealth,
                },
                {
                    name: "list_recent_traces",
                    description: "List recent sanitized Local Monitor traces with bounded output.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            limit: {
                                type: "integer",
                                minimum: 1,
                                maximum: MAX_TRACE_LIST_LIMIT,
                            },
                            status: {
                                type: "string",
                                enum: ["ok", "error"],
                            },
                            model: {
                                type: "string",
                                minLength: 1,
                                maxLength: 100,
                            },
                        },
                        required: ["limit"],
                        additionalProperties: false,
                    },
                    handler: handleListRecentTraces,
                },
                {
                    name: "get_trace_summary",
                    description: "Return one bounded sanitized trace summary with top spans and cache totals.",
                    inputSchema: traceIdSchema,
                    handler: handleGetTraceSummary,
                },
                {
                    name: "get_trace_span_tree",
                    description: "Return a bounded sanitized span hierarchy or ordered flat diagnostic for one trace.",
                    inputSchema: traceIdSchema,
                    handler: handleGetTraceSpanTree,
                },
                {
                    name: "get_cache_summary",
                    description: "Return sanitized cache token metrics and a bounded per-turn breakdown for one trace.",
                    inputSchema: traceIdSchema,
                    handler: handleGetCacheSummary,
                },
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
                const healthStatus = health.healthy
                    ? "healthy"
                    : (health.statusCode !== null ? `unhealthy (${health.statusCode})` : "unreachable");

                // Always start the extension-owned helper page (M5). The page
                // shows monitor health, a trace dropdown (proxied from
                // /api/monitor/traces), a focus selector, and the
                // "Analyze selected trace with Copilot" trigger. The trigger is
                // disabled when the monitor is not healthy.
                const token = randomUUID();
                const entry = await startHelperServer({
                    instanceId: ctx.instanceId,
                    monitorUrl,
                    healthStatus,
                    healthBody: health.body,
                    error: health.error,
                    token,
                    session,
                });
                servers.set(ctx.instanceId, entry);
                return {
                    title: health.healthy ? "OTel Monitor" : "OTel Monitor — Offline",
                    status: health.healthy ? "Connected" : "Monitor unavailable",
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
