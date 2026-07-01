// canvas-helpers: pure, side-effect-free helpers for the otel-monitor-canvas
// extension.
//
// This module imports no Copilot SDK and starts no session, so it can be unit
// tested with `node --test` (see canvas-helpers.test.mjs) and syntax checked
// with `node --check`. extension.mjs imports these helpers and keeps the
// session / HTTP-server / fetch wiring.
//
// Sprint15 child A (D036) lives here: the helper-page presentation
// (renderHelperHtml), the decision-supporting trace line formatters, and the
// Japanese UI labels. The display boundary is unchanged: every value rendered
// or compacted here is derived from already-sanitized monitor fields, and the
// bounded-DTO contract is preserved.

// Boundary-invariant note. Kept as an exported constant so the contract test can
// pin the invariant even while the surrounding UI text reads in Japanese.
export const BOUNDARY_NOTE =
    "Canvas action responses and logs must not contain raw telemetry or PII.";

// Output bounds reused by the action handlers and the projection helpers.
export const MAX_TOP_SPANS = 10;
export const MAX_TREE_NODES = 50;
export const MAX_CACHE_TURNS = 50;

// Focus options. The `value` (enum) and action wiring are unchanged; only the
// displayed `label` is Japanese (Sprint15 A2 / Issue §5).
export const FOCUS_OPTIONS = [
    { value: "latency", label: "遅い原因" },
    { value: "tokens", label: "トークン消費" },
    { value: "cache", label: "キャッシュ効率" },
    { value: "errors", label: "エラー原因" },
];

// --------------- generic helpers ---------------

export function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, (char) => {
        if (char === "&") return "&amp;";
        if (char === "<") return "&lt;";
        if (char === ">") return "&gt;";
        if (char === '"') return "&quot;";
        return "&#39;";
    });
}

export function numberOrZero(value) {
    return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

// --------------- presentation formatters (Sprint15 A1) ---------------

export function statusLabel(status) {
    return status === "error" ? "エラーあり" : "OK";
}

export function formatTokens(value) {
    if (typeof value !== "number" || !Number.isFinite(value)) {
        return null;
    }

    return String(Math.round(value)).replace(/\B(?=(\d{3})+(?!\d))/g, ",");
}

export function formatDuration(ms) {
    if (typeof ms !== "number" || !Number.isFinite(ms) || ms < 0) {
        return null;
    }

    if (ms < 1000) {
        return `${Math.round(ms)}ms`;
    }

    if (ms < 60000) {
        return `${(ms / 1000).toFixed(1)}s`;
    }

    const minutes = Math.floor(ms / 60000);
    const seconds = Math.floor((ms % 60000) / 1000);
    return `${minutes}:${String(seconds).padStart(2, "0")}`;
}

// Extract HH:MM from an ISO 8601 timestamp without constructing a Date, so the
// label is deterministic (no timezone drift) and unit-testable.
export function formatClock(isoTimestamp) {
    if (typeof isoTimestamp !== "string") {
        return null;
    }

    const match = isoTimestamp.match(/T(\d{2}:\d{2})/);
    return match ? match[1] : null;
}

export function shortTraceId(traceId) {
    if (typeof traceId !== "string" || traceId.length === 0) {
        return null;
    }

    return traceId.length > 8 ? `#${traceId.slice(0, 8)}…` : `#${traceId}`;
}

// One-line, decision-supporting trace label built from sanitized compactTrace
// fields only (Sprint15 A1 / Issue §4). Example:
//   エラーあり / gpt-5 / 12 spans / 3 tools / 8,420 tokens / 14:32 / 18.2s / #abc12345…
export function formatTraceLine(trace) {
    const parts = [statusLabel(trace.status)];

    if (trace.primary_model) {
        parts.push(String(trace.primary_model));
    }

    if (typeof trace.span_count === "number") {
        parts.push(`${trace.span_count} spans`);
    }

    if (typeof trace.tool_call_count === "number") {
        parts.push(`${trace.tool_call_count} tools`);
    }

    const tokens = formatTokens(trace.total_tokens);
    if (tokens !== null) {
        parts.push(`${tokens} tokens`);
    }

    const clock = formatClock(trace.last_seen_at);
    if (clock !== null) {
        parts.push(clock);
    }

    const duration = formatDuration(trace.duration_ms);
    if (duration !== null) {
        parts.push(duration);
    }

    const shortId = shortTraceId(trace.trace_id);
    if (shortId !== null) {
        parts.push(shortId);
    }

    return parts.join(" / ");
}

// Composes a trace's prompt label (D039, helper-page surface only) with its
// existing decision-supporting line for the trace-selection dropdown. Not
// called from extension.mjs — /api/traces keeps `prompt_label` and `line` as
// separate additive fields — but kept as an exported pure function so
// `node --test` can pin the exact composition/separator format; the inline
// (non-module) client script in renderHelperHtml duplicates this same
// one-liner logic since it cannot `import` from this ES module.
export function dropdownOptionLabel(item) {
    const line = item.line ?? "";
    return item.prompt_label ? `${item.prompt_label} — ${line}` : line;
}

// --------------- sanitized projection helpers ---------------

export function statusFromTrace(row) {
    return numberOrZero(row.error_count) > 0 ? "error" : "ok";
}

export function modelForSpan(span) {
    return span.response_model || span.request_model || null;
}

export function spanSubject(span) {
    return span.tool_name || span.mcp_tool_name || span.agent_name || modelForSpan(span) || null;
}

export function isErrorSpan(span) {
    return span.status === "error" || Boolean(span.error_type);
}

export function isChatTurn(span) {
    return span.operation === "chat" || span.category === "llm_call";
}

export function cacheHitRate(cacheReadTokens, inputTokens) {
    return inputTokens > 0 ? cacheReadTokens / inputTokens : null;
}

export function compareByTimeThenOrdinal(a, b) {
    if (a.start_time && b.start_time && a.start_time !== b.start_time) {
        return String(a.start_time).localeCompare(String(b.start_time));
    }

    if (a.start_time && !b.start_time) return -1;
    if (!a.start_time && b.start_time) return 1;
    const ordinal = numberOrZero(a.span_ordinal) - numberOrZero(b.span_ordinal);
    return ordinal !== 0 ? ordinal : numberOrZero(a.id) - numberOrZero(b.id);
}

export function compactTrace(row) {
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

export function compactSpan(span) {
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

export function summarizeTopSpans(spans) {
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

export function sumField(spans, field) {
    return spans.reduce((sum, span) => sum + numberOrZero(span[field]), 0);
}

export function uniqueModels(spans) {
    return [...new Set(spans.map(modelForSpan).filter(Boolean))].sort();
}

export function cacheTurn(span) {
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

export function treeNode(span) {
    return {
        ...compactSpan(span),
        child_refs: [],
        children: [],
    };
}

export function hierarchyFromSpans(spans) {
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

export function sanitizeDto(value) {
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

// --------------- trace detail summary (Sprint15 M3 / child C, D037) ---------------

// Builds the bounded trace-detail summary DTO from a compactTrace row plus a
// separately computed cache_hit_rate. No new field category beyond
// compactTrace fields + cache_hit_rate (D037).
export function traceDetailSummary({ trace, cacheHitRate }) {
    return {
        trace_id: trace.trace_id,
        status: trace.status,
        primary_model: trace.primary_model ?? null,
        span_count: trace.span_count ?? null,
        tool_call_count: trace.tool_call_count ?? null,
        total_tokens: trace.total_tokens ?? null,
        duration_ms: trace.duration_ms ?? null,
        cache_hit_rate: typeof cacheHitRate === "number" ? cacheHitRate : null,
        last_seen_at: trace.last_seen_at ?? null,
    };
}

// --------------- dashboard summary (Sprint15 M4 / child B remainder, D038) ---------------

// Builds the decision-supporting `line` field for a /api/monitor/summary
// highlight trace (latest_trace / top_token_trace / error_trace). These are
// raw per-trace DTOs straight from Local Monitor's own /api/monitor/summary
// response (MonitorHost.ToTraceDto shape): they carry `error_count` but NOT
// a precomputed `status` string. formatTraceLine reads `status` (via
// statusLabel), so it must be given a compactTrace-shaped object — passing
// the raw DTO directly would always read `status` as undefined and render
// "OK" even for the error trace. Route it through compactTrace first, the
// same way /api/traces and /api/trace-detail/:traceId already do for their
// own rows.
export function summaryTraceLine(trace) {
    return trace ? formatTraceLine(compactTrace(trace)) : null;
}

// --------------- raw preview (Sprint15 M5 / child D, D038) ---------------

// Extracts the substring between the FIRST "<pre>" and the LAST "</pre>" in
// the Local Monitor's fixed-format raw route response
// (`<!DOCTYPE html>...<pre>{HtmlEncoder.Default.Encode(payload)}</pre>...`).
// Because the payload is already HTML-encoded server-side, the only literal
// "<"/">" in the whole response belong to the fixed template tags, so this
// substring extraction is unambiguous. The result MUST be re-embedded
// verbatim (no decode, no re-encode) — see D038 / security-data-boundaries.md.
// Returns null if the expected <pre>...</pre> shape is not found (fail loudly
// rather than embed something unexpected).
export function extractRawPreviewFragment(html) {
    if (typeof html !== "string") {
        return null;
    }

    const openTag = "<pre>";
    const closeTag = "</pre>";
    const start = html.indexOf(openTag);
    const end = html.lastIndexOf(closeTag);
    if (start === -1 || end === -1 || end <= start) {
        return null;
    }

    return html.slice(start + openTag.length, end);
}

// Renders the Canvas-owned raw-preview page. `fragment` is embedded verbatim
// (no escapeHtml call) because it is already HTML-encoded by the Local
// Monitor; `traceId`/`spanId`/`token` come from the URL and are escaped. This
// page is reached only via a real page-navigation link click — it introduces
// no client-side script that fetches raw content as JSON.
export function renderRawPreviewHtml({ traceId, spanId, fragment, token }) {
    const escapedTraceId = escapeHtml(traceId);
    const escapedSpanId = escapeHtml(spanId);
    const escapedToken = escapeHtml(token);

    return `<!doctype html>
<html lang="ja">
<head>
  <meta charset="utf-8">
  <title>生データプレビュー — Canvas</title>
  <style>
    body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; padding: 24px; background: #ffffff; color: #1f2328; }
    h1 { font-size: 1.1rem; margin-bottom: 4px; }
    p.meta { color: #656d76; margin-bottom: 16px; }
    pre { background: #f6f8fa; border: 1px solid #d0d7de; border-radius: 4px; padding: 12px; overflow: auto; font-size: 12px; line-height: 1.4; white-space: pre-wrap; word-break: break-all; }
    a { color: #0969da; }
  </style>
</head>
<body>
  <h1>生データプレビュー</h1>
  <p class="meta">trace: <code>${escapedTraceId}</code> / span: <code>${escapedSpanId}</code></p>
  <p><a href="/?t=${escapedToken}">← ヘルパーページに戻る</a></p>
  <pre>${fragment}</pre>
</body>
</html>`;
}

// Small fixed error/status page for the raw-preview route (invalid input,
// span or raw record not found, or raw unavailable). No dynamic content
// besides the (already known-safe, caller-chosen) message text and the token.
export function renderRawPreviewMessageHtml({ heading, message, token }) {
    const escapedHeading = escapeHtml(heading);
    const escapedMessage = escapeHtml(message);
    const escapedToken = escapeHtml(token);

    return `<!doctype html>
<html lang="ja">
<head>
  <meta charset="utf-8">
  <title>${escapedHeading} — Canvas</title>
</head>
<body>
  <h1>${escapedHeading}</h1>
  <p>${escapedMessage}</p>
  <p><a href="/?t=${escapedToken}">← ヘルパーページに戻る</a></p>
</body>
</html>`;
}

// --------------- analysis prompt ---------------

export function buildAnalysisPrompt({ traceId, spanId, focus }) {
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

// --------------- helper page (Sprint15 A1–A6) ---------------

// healthState is one of "ready" | "not_ready" | "unreachable" (Sprint15 A4).
export function renderHelperHtml({ instanceId, monitorUrl, healthState, statusCode, healthBody, error, token }) {
    const ready = healthState === "ready";
    const base = String(monitorUrl ?? "").replace(/\/$/, "");
    const healthUrl = `${base}/health/ready`;

    const escapedUrl = escapeHtml(monitorUrl);
    const escapedInstance = escapeHtml(instanceId);
    const escapedBody = escapeHtml(healthBody ?? "");
    const escapedError = escapeHtml(error ?? "");
    const escapedToken = escapeHtml(token);
    const escapedHealthUrl = escapeHtml(healthUrl);

    const connectionLabel = ready
        ? "接続済み（ready）"
        : healthState === "not_ready"
            ? `起動中・未 ready${statusCode ? `（HTTP ${statusCode}）` : ""}`
            : "未接続";

    const bannerHtml = ready
        ? ""
        : healthState === "not_ready"
            ? `<div class="banner banner-warn">Local Monitor は起動していますが ready ではありません。</div>`
            : `<div class="banner banner-err">Local Monitor が起動していません。</div>`;

    const guidanceHtml = ready
        ? ""
        : `<div class="card">
    <h2>次の操作</h2>
    <ul class="steps">
      <li>接続状態を確認する: <a href="${escapedHealthUrl}" target="_blank" rel="noopener noreferrer"><code>${escapedHealthUrl}</code></a></li>
      <li>Local Monitor を起動する（例: <code>scripts\\local-monitor\\</code> の start スクリプト、または Release ZIP の start 操作）。</li>
      <li>設定を確認する: DB path、port（既定 <code>4320</code>）、URL。</li>
      <li>Canvas が参照している monitor base URL: <code>${escapedUrl}</code></li>
    </ul>
  </div>`;

    const focusOptionsHtml = FOCUS_OPTIONS
        .map((f) => `        <option value="${f.value}">${escapeHtml(f.label)}</option>`)
        .join("\n");

    const healthDetailsHtml = escapedBody
        ? `<div class="card">
    <details>
      <summary>Local Monitor の接続状態（生レスポンス）</summary>
      <pre>${escapedBody}</pre>
    </details>
  </div>`
        : "";

    return `<!doctype html>
<html lang="ja">
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
    .card h3 { font-size: 0.9rem; margin: 12px 0 4px; color: var(--muted); }
    .card ul { margin: 0 0 0 1.2em; }
    .card li { margin-bottom: 4px; }
    .kv { display: grid; grid-template-columns: 200px 1fr; gap: 4px 12px; }
    .kv dt { color: var(--muted); font-weight: 500; }
    .status-ok { color: var(--success); font-weight: 600; }
    .status-err { color: var(--danger); font-weight: 600; }
    .muted { color: var(--muted); }
    .steps { margin: 0 0 0 1.2em; }
    .steps li { margin-bottom: 6px; }
    details summary { cursor: pointer; color: var(--muted); font-weight: 500; }
    pre {
      background: #f6f8fa;
      border: 1px solid var(--border);
      border-radius: 4px;
      padding: 12px;
      margin-top: 8px;
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
  ${bannerHtml}

  <div class="card">
    <h2>接続状態</h2>
    <dl class="kv">
      <dt>Monitor URL</dt><dd><code>${escapedUrl}</code></dd>
      <dt>インスタンス</dt><dd><code>${escapedInstance}</code></dd>
      <dt>状態</dt><dd><span class="${ready ? "status-ok" : "status-err"}">${escapeHtml(connectionLabel)}</span></dd>
    </dl>
  </div>

  ${guidanceHtml}

  ${healthDetailsHtml}

  <div class="card">
    <h2>Local Monitor 概要</h2>
    <p class="muted" id="summary-loading">読み込み中…</p>
    <p class="err" id="summary-error" style="display:none;"></p>
    <div id="summary-content" style="display:none;">
      <p id="summary-scope" class="muted"></p>
      <div>
        <h3>モデル別</h3>
        <ul id="summary-models"></ul>
      </div>
      <div>
        <h3>クライアント種別</h3>
        <ul id="summary-clients"></ul>
      </div>
      <div>
        <h3>注目トレース</h3>
        <ul id="summary-highlights"></ul>
      </div>
    </div>
  </div>

  <div class="card">
    <h2>選択したトレースの要約</h2>
    <p class="muted" id="trace-detail-empty">トレースを選択すると要約が表示されます。</p>
    <dl class="kv" id="trace-detail-kv" style="display:none;">
      <dt>状態</dt><dd id="td-status"></dd>
      <dt>主要モデル</dt><dd id="td-model"></dd>
      <dt>トークン合計</dt><dd id="td-tokens"></dd>
      <dt>所要時間</dt><dd id="td-duration"></dd>
      <dt>キャッシュヒット率</dt><dd id="td-cache"></dd>
    </dl>
    <p id="trace-detail-link" style="display:none;"><a id="td-monitor-link" href="#" target="_blank" rel="noopener noreferrer">Local Monitorで詳細を見る</a></p>
  </div>

  <div class="card">
    <h2>Copilotでこのトレースを分析</h2>
    <p style="margin-bottom:12px;color:var(--muted);">トレースと観点を選んで Copilot 分析を実行します。Copilot は bounded な monitor action を使い、このヘルパーから raw な monitor payload を受け取りません。</p>
    <div class="row">
      <label for="trace">トレース</label>
      <select id="trace" ${ready ? "" : "disabled"}></select>
    </div>
    <div class="row">
      <label for="focus">観点</label>
      <select id="focus" ${ready ? "" : "disabled"}>
${focusOptionsHtml}
      </select>
    </div>
    <div class="row">
      <label for="span">span id（任意）</label>
      <input type="text" id="span" placeholder="任意の span id" ${ready ? "" : "disabled"} />
    </div>
    <button id="analyze" ${ready ? "" : "disabled"}>Copilotでこのトレースを分析</button>
    <div id="result"></div>
    <p class="row"><a id="raw-preview-link" href="#" target="_blank" rel="noopener noreferrer" style="display:none;">生データを表示（新しいタブ）</a></p>
  </div>

  <div class="card">
    <h2>Local Monitor のページ</h2>
    <p>ブラウザで Local Monitor を開く: <a href="${escapedUrl}" target="_blank" rel="noopener noreferrer">Local Monitor をブラウザで開く</a></p>
  </div>

  <div class="card">
    <h2>ローカルモニターの取り扱い</h2>
    <p>このアダプターは通常の raw-default な Local Monitor と併用できます。詳細な実行内容は Local Monitor 側のローカル UI 境界の中に留まります。</p>
    <p class="muted">${escapeHtml(BOUNDARY_NOTE)}</p>
  </div>

  <script>
    (function () {
      var token = ${JSON.stringify(escapedToken)};
      var monitorUrlBase = ${JSON.stringify(String(monitorUrl ?? "").replace(/\/$/, ""))};
      var traceSel = document.getElementById("trace");
      var focusSel = document.getElementById("focus");
      var spanInput = document.getElementById("span");
      var btn = document.getElementById("analyze");
      var result = document.getElementById("result");
      var tdEmpty = document.getElementById("trace-detail-empty");
      var tdKv = document.getElementById("trace-detail-kv");
      var tdLink = document.getElementById("trace-detail-link");
      var tdStatus = document.getElementById("td-status");
      var tdModel = document.getElementById("td-model");
      var tdTokens = document.getElementById("td-tokens");
      var tdDuration = document.getElementById("td-duration");
      var tdCache = document.getElementById("td-cache");
      var tdMonitorLink = document.getElementById("td-monitor-link");

      function setResult(msg, ok) {
        result.textContent = msg;
        result.className = ok ? "ok" : "err";
      }

      function showTraceDetailEmpty(msg) {
        tdEmpty.textContent = msg;
        tdEmpty.style.display = "";
        tdKv.style.display = "none";
        tdLink.style.display = "none";
      }

      function loadTraceDetail(traceId) {
        if (!traceId) { showTraceDetailEmpty("トレースを選択すると要約が表示されます。"); return; }
        showTraceDetailEmpty("読み込み中…");
        fetch("/api/trace-detail/" + encodeURIComponent(traceId) + "?t=" + encodeURIComponent(token), { headers: { "x-canvas-token": token } })
          .then(function (r) { return r.json().then(function (b) { return { status: r.status, body: b }; }); })
          .then(function (out) {
            if (out.status !== 200) {
              showTraceDetailEmpty("要約の取得に失敗しました: " + (out.body && out.body.error || ("HTTP " + out.status)));
              return;
            }
            var d = out.body;
            tdStatus.textContent = d.status === "error" ? "エラーあり" : "OK";
            tdModel.textContent = d.primary_model != null ? String(d.primary_model) : "—";
            tdTokens.textContent = d.total_tokens != null ? String(d.total_tokens) : "—";
            tdDuration.textContent = d.duration_ms != null ? String(d.duration_ms) + "ms" : "—";
            tdCache.textContent = typeof d.cache_hit_rate === "number" ? Math.round(d.cache_hit_rate * 100) + "%" : "—";
            tdMonitorLink.href = monitorUrlBase + "/traces/" + encodeURIComponent(traceId);
            tdEmpty.style.display = "none";
            tdKv.style.display = "";
            tdLink.style.display = "";
          })
          .catch(function (e) { showTraceDetailEmpty("要約の取得に失敗しました: " + e.message); });
      }

      var summaryLoading = document.getElementById("summary-loading");
      var summaryError = document.getElementById("summary-error");
      var summaryContent = document.getElementById("summary-content");
      var summaryScope = document.getElementById("summary-scope");
      var summaryModels = document.getElementById("summary-models");
      var summaryClients = document.getElementById("summary-clients");
      var summaryHighlights = document.getElementById("summary-highlights");

      function showSummaryError(msg) {
        summaryLoading.style.display = "none";
        summaryContent.style.display = "none";
        summaryError.textContent = msg;
        summaryError.style.display = "";
      }

      function appendLine(el, text) {
        var li = document.createElement("li");
        li.textContent = text;
        el.appendChild(li);
      }

      if (!token) { setResult("起動トークンがありません。", false); return; }

      fetch("/api/summary?t=" + encodeURIComponent(token), { headers: { "x-canvas-token": token } })
        .then(function (r) { return r.json().then(function (b) { return { status: r.status, body: b }; }); })
        .then(function (out) {
          if (out.status !== 200) {
            showSummaryError("概要を取得できませんでした: " + (out.body && out.body.error || ("HTTP " + out.status)));
            return;
          }
          var s = out.body;
          var traceCount = s.scope && s.scope.trace_count != null ? s.scope.trace_count : "—";
          summaryScope.textContent = "直近 " + traceCount + " 件のトレースを集計";
          (s.per_model_summary || []).slice(0, 5).forEach(function (m) {
            appendLine(summaryModels, m.model + " / " + m.trace_count + " 件 / " + (m.total_tokens_formatted || "0") + " tokens / エラー " + m.error_count);
          });
          (s.per_client_kind_summary || []).slice(0, 5).forEach(function (c) {
            appendLine(summaryClients, c.client_kind + " / " + c.trace_count + " 件 / " + (c.total_tokens_formatted || "0") + " tokens / エラー " + c.error_count);
          });
          [["最新", s.latest_trace], ["最大トークン", s.top_token_trace], ["エラー", s.error_trace]].forEach(function (pair) {
            if (!pair[1]) { return; }
            appendLine(summaryHighlights, pair[0] + ": " + (pair[1].line || pair[1].trace_id));
          });
          summaryLoading.style.display = "none";
          summaryContent.style.display = "";
        })
        .catch(function (e) { showSummaryError("概要を取得できませんでした: " + e.message); });

      fetch("/api/traces?t=" + encodeURIComponent(token), { headers: { "x-canvas-token": token } })
        .then(function (r) {
          if (!r.ok) { throw new Error("HTTP " + r.status); }
          return r.json();
        })
        .then(function (data) {
          (data.items || []).forEach(function (t) {
            var opt = document.createElement("option");
            opt.value = t.trace_id;
            opt.textContent = (t.prompt_label ? t.prompt_label + " — " : "") + (t.line || t.trace_id);
            traceSel.appendChild(opt);
          });
          if (!traceSel.options.length) { setResult("最近のトレースが見つかりませんでした。", true); }
        })
        .catch(function (e) { setResult("トレースの読み込みに失敗しました: " + e.message, false); });

      var rawPreviewLink = document.getElementById("raw-preview-link");

      function updateRawPreviewLink() {
        var traceId = traceSel.value;
        var spanId = spanInput.value.trim();
        if (traceId && spanId) {
          rawPreviewLink.href = "/raw-preview/" + encodeURIComponent(traceId) + "/" + encodeURIComponent(spanId) + "?t=" + encodeURIComponent(token);
          rawPreviewLink.style.display = "";
        } else {
          rawPreviewLink.removeAttribute("href");
          rawPreviewLink.style.display = "none";
        }
      }

      traceSel.addEventListener("change", function () {
        loadTraceDetail(traceSel.value);
        updateRawPreviewLink();
      });

      spanInput.addEventListener("input", updateRawPreviewLink);

      btn.addEventListener("click", function () {
        var traceId = traceSel.value;
        if (!traceId) { setResult("先にトレースを選択してください。", false); return; }
        var focus = focusSel.value;
        var spanId = spanInput.value.trim();
        btn.disabled = true;
        setResult("送信中…", true);
        fetch("/analyze", {
          method: "POST",
          headers: { "Content-Type": "application/json", "x-canvas-token": token },
          body: JSON.stringify({ traceId: traceId, spanId: spanId || undefined, focus: focus })
        })
          .then(function (r) { return r.json().then(function (b) { return { status: r.status, body: b }; }); })
          .then(function (out) {
            if (out.status === 200) { setResult("Copilot に分析を送信しました。Copilot チャットを確認してください。", true); }
            else { setResult("失敗しました: " + (out.body && out.body.error || ("HTTP " + out.status)), false); }
          })
          .catch(function (e) { setResult("失敗しました: " + e.message, false); })
          .finally(function () { btn.disabled = false; });
      });
    })();
  </script>
</body>
</html>`;
}
