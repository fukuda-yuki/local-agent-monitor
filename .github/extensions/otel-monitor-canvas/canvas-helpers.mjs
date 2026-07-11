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
export const PROMPT_TEMPLATE_VERSION = "canvas-analysis-requested-options-v1";

export const DEFAULT_ANALYSIS_OPTIONS = {
    default_profile: "standard",
    default_model: "gpt-5",
    reasoning_efforts: ["low", "medium", "high"],
    profiles: [
        { id: "fast", display_name: "Fast", timeout_seconds: 60, default_reasoning_effort: "low" },
        { id: "standard", display_name: "Standard", timeout_seconds: 180, default_reasoning_effort: "medium" },
        { id: "deep", display_name: "Deep", timeout_seconds: 600, default_reasoning_effort: "high" },
    ],
    models: [
        { id: "gpt-5", display_name: "GPT-5", provider: "copilot", supports_reasoning_effort: true, is_default: true },
    ],
};

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

export function normalizeAnalysisOptions(options) {
    const source = options && typeof options === "object" ? options : DEFAULT_ANALYSIS_OPTIONS;
    const profiles = Array.isArray(source.profiles) && source.profiles.length > 0
        ? source.profiles
        : DEFAULT_ANALYSIS_OPTIONS.profiles;
    const models = Array.isArray(source.models) && source.models.length > 0
        ? source.models
        : DEFAULT_ANALYSIS_OPTIONS.models;
    const reasoningEfforts = Array.isArray(source.reasoning_efforts) && source.reasoning_efforts.length > 0
        ? source.reasoning_efforts
        : DEFAULT_ANALYSIS_OPTIONS.reasoning_efforts;
    const defaultProfile = profiles.some((profile) => profile.id === source.default_profile)
        ? source.default_profile
        : profiles[0].id;
    const defaultModel = models.some((model) => model.id === source.default_model)
        ? source.default_model
        : models[0].id;

    return {
        default_profile: defaultProfile,
        default_model: defaultModel,
        reasoning_efforts: reasoningEfforts,
        profiles,
        models,
    };
}

export function selectedAnalysisOption(options, selection = {}) {
    const normalized = normalizeAnalysisOptions(options);
    const profile = normalized.profiles.find((candidate) => candidate.id === selection.profile)
        ?? normalized.profiles.find((candidate) => candidate.id === normalized.default_profile)
        ?? normalized.profiles[0];
    const model = normalized.models.find((candidate) => candidate.id === selection.requestedModel)
        ?? normalized.models.find((candidate) => candidate.id === normalized.default_model)
        ?? normalized.models[0];
    const requestedReasoningEffort = model.supports_reasoning_effort === false
        ? null
        : (normalized.reasoning_efforts.includes(selection.requestedReasoningEffort)
            ? selection.requestedReasoningEffort
            : profile.default_reasoning_effort);

    return {
        profile,
        model,
        requested_reasoning_effort: requestedReasoningEffort,
        requested_timeout_seconds: profile.timeout_seconds,
    };
}

export function formatTimeoutHint(seconds) {
    if (typeof seconds !== "number" || !Number.isFinite(seconds) || seconds <= 0) {
        return "—";
    }

    return seconds >= 60 && seconds % 60 === 0
        ? `${seconds / 60}分 (${seconds}s)`
        : `${seconds}s`;
}

export function dispatchPhaseText(phase, elapsedMs, selection) {
    const elapsedSeconds = Math.max(0, Math.round(numberOrZero(elapsedMs) / 1000));
    const timeout = formatTimeoutHint(selection?.requested_timeout_seconds);
    const model = selection?.model?.display_name ?? selection?.model?.id ?? "—";
    const profile = selection?.profile?.display_name ?? selection?.profile?.id ?? "—";
    const reasoning = selection?.requested_reasoning_effort ?? "未指定";
    const labels = {
        options_loaded: "分析オプションを読み込みました。",
        preparing: "bounded Canvas action 用の Copilot 指示を準備しています。",
        sending: "Copilot に分析指示を送信しています。",
        dispatched: "Copilot に分析指示を送信しました。結果は Copilot チャットを確認してください。",
        canceled: "ローカルの送信待機をキャンセルしました。",
    };
    return `${labels[phase] ?? labels.preparing}\nProfile: ${profile}\n希望モデル: ${model}\n推奨 reasoning: ${reasoning}\nTimeout hint: ${timeout}\nElapsed: ${elapsedSeconds}s`;
}

export function longRunningDispatchNotice(elapsedMs, timeoutSeconds) {
    const thresholdMs = Math.min(Math.max(numberOrZero(timeoutSeconds) * 1000, 15000), 60000);
    return numberOrZero(elapsedMs) >= thresholdMs
        ? "送信待機が長くなっています。これは Local Monitor raw analysis runner の実行待ちではありません。分析結果は Copilot チャット側に表示されます。"
        : null;
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

export function repositoryLabel(trace) {
    const repository = typeof trace?.repository_name === "string" && trace.repository_name.trim()
        ? trace.repository_name.trim()
        : null;
    if (!repository) {
        return "unknown repository";
    }

    const snapshot = typeof trace?.repo_snapshot === "string" && trace.repo_snapshot.trim()
        ? trace.repo_snapshot.trim()
        : null;
    return snapshot ? `${repository} @ ${snapshot}` : repository;
}

export function workspaceLabel(trace) {
    return typeof trace?.workspace_label === "string" && trace.workspace_label.trim()
        ? trace.workspace_label.trim()
        : null;
}

export function repositoryFilterKey(trace) {
    return `${repositoryLabel(trace)}\u001f${workspaceLabel(trace) ?? ""}`;
}

export function repositoryFilterOptions(traces) {
    const seen = new Map();
    for (const trace of traces ?? []) {
        const key = repositoryFilterKey(trace);
        if (seen.has(key)) {
            continue;
        }

        const workspace = workspaceLabel(trace);
        const label = workspace ? `${repositoryLabel(trace)} / ${workspace}` : repositoryLabel(trace);
        seen.set(key, { key, label });
    }

    return [...seen.values()].sort((a, b) => a.label.localeCompare(b.label));
}

export function extensionScopeFromModuleUrl(moduleUrl) {
    const value = String(moduleUrl ?? "").toLowerCase();
    if (value.includes("/.github/extensions/otel-monitor-canvas/")) {
        return "project";
    }

    if (value.includes("/extensions/otel-monitor-canvas/")) {
        return "user";
    }

    return "unknown";
}

// One-line, decision-supporting trace label built from sanitized compactTrace
// fields only (Sprint15 A1 / Issue §4). Example:
//   エラーあり / gpt-5 / 12 spans / 3 tools / 8,420 tokens / 14:32 / 18.2s / #abc12345…
export function formatTraceLine(trace) {
    const parts = [statusLabel(trace.status)];
    parts.push(repositoryLabel(trace));

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
        repository_name: row.repository_name ?? null,
        workspace_label: row.workspace_label ?? null,
        repo_snapshot: row.repo_snapshot ?? null,
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
  <p><a href="/analysis?t=${escapedToken}">← 分析ページに戻る</a></p>
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
  <p><a href="/analysis?t=${escapedToken}">← 分析ページに戻る</a></p>
</body>
</html>`;
}

// --------------- analysis prompt ---------------

export function buildAnalysisPrompt({
    traceId,
    spanId,
    focus,
    profile = null,
    requestedModel = null,
    requestedReasoningEffort = null,
    requestedTimeoutSeconds = null,
}) {
    const focusActions = {
        latency: ["get_trace_summary", "get_trace_span_tree"],
        tokens: ["get_trace_summary", "get_cache_summary"],
        cache: ["get_cache_summary"],
        errors: ["get_trace_span_tree"],
    };
    const actions = focusActions[focus] ?? ["get_trace_summary"];
    const spanLine = spanId ? `Selected span id: ${spanId}\n` : "";
    const profileLine = profile ? `Requested analysis profile: ${profile}.` : "Requested analysis profile: not specified.";
    const modelLine = requestedModel ? `Requested model / Copilot instruction: ${requestedModel}.` : "Requested model / Copilot instruction: not specified.";
    const reasoningLine = requestedReasoningEffort
        ? `Requested reasoning depth: ${requestedReasoningEffort}. Use deeper reasoning if the current Copilot session and selected model support it.`
        : "Requested reasoning depth: not specified or unsupported by the selected model.";
    const timeoutLine = requestedTimeoutSeconds
        ? `Requested timeout hint: ${requestedTimeoutSeconds} seconds. This is a helper display/wait hint, not a model execution abort.`
        : "Requested timeout hint: not specified.";
    return [
        `Analyze the selected Local Ingestion Monitor trace using bounded Canvas actions.`,
        `Prompt template version: ${PROMPT_TEMPLATE_VERSION}.`,
        `Trace id: ${traceId}`,
        spanLine,
        `Analysis focus: ${focus}.`,
        profileLine,
        modelLine,
        reasoningLine,
        timeoutLine,
        `Do not claim the requested model, reasoning depth, or timeout was enforced as a per-message execution setting.`,
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
export function renderHelperHtml({ instanceId, monitorUrl, healthState, statusCode, healthBody, error, token, extensionScope = "unknown" }) {
    const ready = healthState === "ready";
    const base = String(monitorUrl ?? "").replace(/\/$/, "");
    const healthUrl = `${base}/health/ready`;

    const escapedUrl = escapeHtml(monitorUrl);
    const escapedInstance = escapeHtml(instanceId);
    const escapedBody = escapeHtml(healthBody ?? "");
    const escapedError = escapeHtml(error ?? "");
    const escapedToken = escapeHtml(token);
    const escapedHealthUrl = escapeHtml(healthUrl);
    const normalizedScope = ["project", "user", "unknown"].includes(extensionScope) ? extensionScope : "unknown";

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
    .preview-block { margin-top: 12px; }
    .preview-block h3 { color: var(--fg); }
    .preview-block label { color: var(--muted); margin-top: 10px; }
    .preview-block pre { max-height: 180px; white-space: pre-wrap; }
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
      <dt>拡張スコープ</dt><dd><code>${escapeHtml(normalizedScope)}</code></dd>
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
    <div class="preview-block" id="trace-content-panel" style="display:none;">
      <h3>プロンプト / 応答</h3>
      <p class="muted" id="trace-content-empty">本文プレビューを読み込み中…</p>
      <div id="trace-content-values" style="display:none;">
        <label for="trace-prompt-preview">プロンプト</label>
        <pre id="trace-prompt-preview"></pre>
        <label for="trace-response-preview">応答</label>
        <pre id="trace-response-preview"></pre>
      </div>
    </div>
  </div>

  <div class="card">
    <h2>Copilotでこのトレースを分析</h2>
    <p style="margin-bottom:12px;color:var(--muted);">トレースと観点を選んで Copilot 分析を実行します。Copilot は bounded な monitor action を使い、このヘルパーから raw な monitor payload を受け取りません。</p>
    <p class="muted" id="analysis-option-status">分析オプションを読み込み中…</p>
    <div class="row">
      <label for="repository-filter">リポジトリ / ワークスペース</label>
      <select id="repository-filter" ${ready ? "" : "disabled"}>
        <option value="">すべて</option>
      </select>
    </div>
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
      <label for="analysis-profile">分析プロファイル</label>
      <select id="analysis-profile" ${ready ? "" : "disabled"}></select>
    </div>
    <div class="row">
      <label for="requested-model">希望モデル</label>
      <select id="requested-model" ${ready ? "" : "disabled"}></select>
      <p class="muted">実際の実行モデルは現在の Copilot セッション設定に依存します。</p>
    </div>
    <div class="row">
      <label for="requested-reasoning">推奨 reasoning</label>
      <select id="requested-reasoning" ${ready ? "" : "disabled"}></select>
      <p class="muted">Copilot セッション / モデルが対応する場合のみ反映されます。</p>
    </div>
    <div class="row">
      <label>Timeout hint</label>
      <p id="timeout-hint" class="muted">—</p>
    </div>
    <div class="row">
      <label for="span">span id（任意）</label>
      <input type="text" id="span" placeholder="任意の span id" ${ready ? "" : "disabled"} />
    </div>
    <button id="analyze" ${ready ? "" : "disabled"}>Copilotでこのトレースを分析</button>
    <button id="cancel-dispatch" type="button" style="display:none;background:#6e7781;">送信待機をキャンセル</button>
    <div id="result"></div>
    <pre id="dispatch-progress" style="display:none;"></pre>
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
      var repositoryFilter = document.getElementById("repository-filter");
      var traceSel = document.getElementById("trace");
      var focusSel = document.getElementById("focus");
      var profileSel = document.getElementById("analysis-profile");
      var modelSel = document.getElementById("requested-model");
      var reasoningSel = document.getElementById("requested-reasoning");
      var timeoutHint = document.getElementById("timeout-hint");
      var analysisOptionStatus = document.getElementById("analysis-option-status");
      var spanInput = document.getElementById("span");
      var btn = document.getElementById("analyze");
      var cancelDispatch = document.getElementById("cancel-dispatch");
      var result = document.getElementById("result");
      var dispatchProgress = document.getElementById("dispatch-progress");
      var dispatchAbortController = null;
      var dispatchStartedAt = 0;
      var dispatchTimer = null;
      var analysisOptions = null;
      var tdEmpty = document.getElementById("trace-detail-empty");
      var tdKv = document.getElementById("trace-detail-kv");
      var tdLink = document.getElementById("trace-detail-link");
      var tdStatus = document.getElementById("td-status");
      var tdModel = document.getElementById("td-model");
      var tdTokens = document.getElementById("td-tokens");
      var tdDuration = document.getElementById("td-duration");
      var tdCache = document.getElementById("td-cache");
      var tdMonitorLink = document.getElementById("td-monitor-link");
      var traceContentPanel = document.getElementById("trace-content-panel");
      var traceContentEmpty = document.getElementById("trace-content-empty");
      var traceContentValues = document.getElementById("trace-content-values");
      var tracePromptPreview = document.getElementById("trace-prompt-preview");
      var traceResponsePreview = document.getElementById("trace-response-preview");

      function setResult(msg, ok) {
        result.textContent = msg;
        result.className = ok ? "ok" : "err";
      }

      function formatTimeoutHint(seconds) {
        if (typeof seconds !== "number" || !isFinite(seconds) || seconds <= 0) { return "—"; }
        return seconds >= 60 && seconds % 60 === 0 ? String(seconds / 60) + "分 (" + seconds + "s)" : seconds + "s";
      }

      function currentProfile() {
        if (!analysisOptions || !analysisOptions.profiles) { return null; }
        return analysisOptions.profiles.find(function (p) { return p.id === profileSel.value; }) || analysisOptions.profiles[0] || null;
      }

      function currentModel() {
        if (!analysisOptions || !analysisOptions.models) { return null; }
        return analysisOptions.models.find(function (m) { return m.id === modelSel.value; }) || analysisOptions.models[0] || null;
      }

      function updateReasoningState() {
        var model = currentModel();
        var profile = currentProfile();
        var supported = !model || model.supports_reasoning_effort !== false;
        reasoningSel.disabled = !supported || ${ready ? "false" : "true"};
        if (!supported) {
          reasoningSel.value = "";
        } else if (profile && !reasoningSel.value) {
          reasoningSel.value = profile.default_reasoning_effort || "medium";
        }
        timeoutHint.textContent = profile ? formatTimeoutHint(profile.timeout_seconds) : "—";
      }

      function currentAnalysisSelection() {
        var profile = currentProfile();
        var model = currentModel();
        return {
          profile: profile ? profile.id : null,
          requestedModel: model ? model.id : null,
          requestedReasoningEffort: model && model.supports_reasoning_effort === false ? null : (reasoningSel.value || null),
          requestedTimeoutSeconds: profile ? profile.timeout_seconds : null,
          profileLabel: profile ? profile.display_name || profile.id : "—",
          modelLabel: model ? model.display_name || model.id : "—"
        };
      }

      function setDispatchProgress(phase) {
        var selected = currentAnalysisSelection();
        var elapsedMs = dispatchStartedAt ? Date.now() - dispatchStartedAt : 0;
        var lines = [];
        var phaseText = {
          options_loaded: "分析オプションを読み込みました。",
          preparing: "bounded Canvas action 用の Copilot 指示を準備しています。",
          sending: "Copilot に分析指示を送信しています。",
          dispatched: "Copilot に分析指示を送信しました。結果は Copilot チャットを確認してください。",
          canceled: "ローカルの送信待機をキャンセルしました。"
        }[phase] || "bounded Canvas action 用の Copilot 指示を準備しています。";
        lines.push(phaseText);
        lines.push("Profile: " + selected.profileLabel);
        lines.push("希望モデル: " + selected.modelLabel);
        lines.push("推奨 reasoning: " + (selected.requestedReasoningEffort || "未指定"));
        lines.push("Timeout hint: " + formatTimeoutHint(selected.requestedTimeoutSeconds));
        lines.push("Elapsed: " + Math.round(elapsedMs / 1000) + "s");
        if (elapsedMs >= Math.min(Math.max((selected.requestedTimeoutSeconds || 60) * 1000, 15000), 60000)) {
          lines.push("");
          lines.push("送信待機が長くなっています。これは Local Monitor raw analysis runner の実行待ちではありません。分析結果は Copilot チャット側に表示されます。");
        }
        dispatchProgress.textContent = lines.join("\\n");
        dispatchProgress.style.display = "";
      }

      function stopDispatchTimer() {
        if (dispatchTimer) {
          clearInterval(dispatchTimer);
          dispatchTimer = null;
        }
      }

      function showTraceDetailEmpty(msg) {
        tdEmpty.textContent = msg;
        tdEmpty.style.display = "";
        tdKv.style.display = "none";
        tdLink.style.display = "none";
      }

      function hideTraceContent() {
        traceContentPanel.style.display = "none";
        traceContentEmpty.style.display = "none";
        traceContentValues.style.display = "none";
        tracePromptPreview.textContent = "";
        traceResponsePreview.textContent = "";
      }

      function showTraceContentEmpty(msg) {
        traceContentPanel.style.display = "";
        traceContentEmpty.textContent = msg;
        traceContentEmpty.style.display = "";
        traceContentValues.style.display = "none";
        tracePromptPreview.textContent = "";
        traceResponsePreview.textContent = "";
      }

      function showTraceContentValues(promptPreview, responsePreview) {
        traceContentPanel.style.display = "";
        traceContentEmpty.style.display = "none";
        traceContentValues.style.display = "";
        tracePromptPreview.textContent = promptPreview || "—";
        traceResponsePreview.textContent = responsePreview || "—";
      }

      function loadTraceContent(traceId) {
        if (!traceId) { hideTraceContent(); return; }
        showTraceContentEmpty("本文プレビューを読み込み中…");
        fetch("/api/trace-content/" + encodeURIComponent(traceId) + "?t=" + encodeURIComponent(token), { headers: { "x-canvas-token": token } })
          .then(function (r) { return r.json().then(function (b) { return { status: r.status, body: b }; }); })
          .then(function (out) {
            if (out.status !== 200) {
              showTraceContentEmpty("本文プレビューを取得できませんでした: " + (out.body && out.body.error || ("HTTP " + out.status)));
              return;
            }
            var promptPreview = out.body && typeof out.body.prompt_preview === "string" ? out.body.prompt_preview : "";
            var responsePreview = out.body && typeof out.body.response_preview === "string" ? out.body.response_preview : "";
            if (!promptPreview && !responsePreview) {
              showTraceContentEmpty("本文プレビューはありません（sanitized-only または対応する LLM span がありません）。");
              return;
            }
            showTraceContentValues(promptPreview, responsePreview);
          })
          .catch(function (e) { showTraceContentEmpty("本文プレビューを取得できませんでした: " + e.message); });
      }

      function loadTraceDetail(traceId) {
        loadTraceContent(traceId);
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

      function repositoryLabel(trace) {
        var repository = trace && typeof trace.repository_name === "string" && trace.repository_name.trim() ? trace.repository_name.trim() : null;
        if (!repository) { return "unknown repository"; }
        var snapshot = trace && typeof trace.repo_snapshot === "string" && trace.repo_snapshot.trim() ? trace.repo_snapshot.trim() : null;
        return snapshot ? repository + " @ " + snapshot : repository;
      }

      function workspaceLabel(trace) {
        return trace && typeof trace.workspace_label === "string" && trace.workspace_label.trim() ? trace.workspace_label.trim() : null;
      }

      function repositoryFilterKey(trace) {
        return repositoryLabel(trace) + "\\u001f" + (workspaceLabel(trace) || "");
      }

      function repositoryFilterOptionLabel(trace) {
        var workspace = workspaceLabel(trace);
        return workspace ? repositoryLabel(trace) + " / " + workspace : repositoryLabel(trace);
      }

      function dropdownOptionLabel(item) {
        var line = item.line || item.trace_id;
        return (item.prompt_label ? item.prompt_label + " — " : "") + line;
      }

      function renderTraceOptions(items) {
        traceSel.textContent = "";
        items.forEach(function (t) {
          var opt = document.createElement("option");
          opt.value = t.trace_id;
          opt.textContent = dropdownOptionLabel(t);
          traceSel.appendChild(opt);
        });
        if (traceSel.value) {
          loadTraceDetail(traceSel.value);
        } else {
          showTraceDetailEmpty("トレースを選択すると要約が表示されます。");
        }
        updateRawPreviewLink();
      }

      function renderRepositoryFilterOptions(items) {
        var seen = new Map();
        items.forEach(function (t) {
          var key = repositoryFilterKey(t);
          if (seen.has(key)) { return; }
          seen.set(key, repositoryFilterOptionLabel(t));
        });
        Array.from(seen.keys()).sort(function (a, b) { return seen.get(a).localeCompare(seen.get(b)); }).forEach(function (key) {
          var opt = document.createElement("option");
          opt.value = key;
          opt.textContent = seen.get(key);
          repositoryFilter.appendChild(opt);
        });
      }

      if (!token) { setResult("起動トークンがありません。", false); return; }

      function renderAnalysisOptions(options) {
        analysisOptions = options || {};
        var profiles = Array.isArray(analysisOptions.profiles) && analysisOptions.profiles.length ? analysisOptions.profiles : [];
        var models = Array.isArray(analysisOptions.models) && analysisOptions.models.length ? analysisOptions.models : [];
        var reasoningEfforts = Array.isArray(analysisOptions.reasoning_efforts) && analysisOptions.reasoning_efforts.length ? analysisOptions.reasoning_efforts : ["low", "medium", "high"];
        profileSel.textContent = "";
        profiles.forEach(function (profile) {
          var opt = document.createElement("option");
          opt.value = profile.id;
          opt.textContent = (profile.display_name || profile.id) + " / " + formatTimeoutHint(profile.timeout_seconds);
          profileSel.appendChild(opt);
        });
        modelSel.textContent = "";
        models.forEach(function (model) {
          var opt = document.createElement("option");
          opt.value = model.id;
          opt.textContent = (model.display_name || model.id) + " / " + (model.provider || "provider unknown") + (model.supports_reasoning_effort === false ? " / reasoning 非対応" : "");
          modelSel.appendChild(opt);
        });
        reasoningSel.textContent = "";
        var emptyOpt = document.createElement("option");
        emptyOpt.value = "";
        emptyOpt.textContent = "未指定";
        reasoningSel.appendChild(emptyOpt);
        reasoningEfforts.forEach(function (effort) {
          var opt = document.createElement("option");
          opt.value = effort;
          opt.textContent = effort;
          reasoningSel.appendChild(opt);
        });
        profileSel.value = analysisOptions.default_profile || (profiles[0] && profiles[0].id) || "";
        modelSel.value = analysisOptions.default_model || (models[0] && models[0].id) || "";
        var profile = currentProfile();
        reasoningSel.value = profile ? profile.default_reasoning_effort || "medium" : "";
        updateReasoningState();
        analysisOptionStatus.textContent = "分析オプションを読み込みました。選択値は Copilot への指示・dispatch metadata として扱われます。";
        setDispatchProgress("options_loaded");
      }

      profileSel.addEventListener("change", function () {
        var profile = currentProfile();
        reasoningSel.value = profile ? profile.default_reasoning_effort || "medium" : "";
        updateReasoningState();
      });
      modelSel.addEventListener("change", updateReasoningState);

      fetch("/api/analysis/options?t=" + encodeURIComponent(token), { headers: { "x-canvas-token": token } })
        .then(function (r) {
          if (!r.ok) { throw new Error("HTTP " + r.status); }
          return r.json();
        })
        .then(renderAnalysisOptions)
        .catch(function (e) {
          analysisOptionStatus.textContent = "分析オプションの取得に失敗しました: " + e.message;
          analysisOptionStatus.className = "err";
        });

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
            appendLine(summaryHighlights, pair[0] + ": " + dropdownOptionLabel(pair[1]));
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
          var allTraces = data.items || [];
          renderRepositoryFilterOptions(allTraces);
          renderTraceOptions(allTraces);
          repositoryFilter.addEventListener("change", function () {
            var selected = repositoryFilter.value;
            renderTraceOptions(selected ? allTraces.filter(function (t) { return repositoryFilterKey(t) === selected; }) : allTraces);
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
      cancelDispatch.addEventListener("click", function () {
        if (dispatchAbortController) {
          dispatchAbortController.abort();
        }
        stopDispatchTimer();
        setDispatchProgress("canceled");
        cancelDispatch.style.display = "none";
        btn.disabled = false;
      });

      btn.addEventListener("click", function () {
        var traceId = traceSel.value;
        if (!traceId) { setResult("先にトレースを選択してください。", false); return; }
        var focus = focusSel.value;
        var spanId = spanInput.value.trim();
        var selected = currentAnalysisSelection();
        if (!selected.profile || !selected.requestedModel) {
          setResult("分析オプションを読み込めていません。", false);
          return;
        }
        btn.disabled = true;
        cancelDispatch.style.display = "";
        dispatchAbortController = new AbortController();
        dispatchStartedAt = Date.now();
        setResult("Copilot に分析指示を送信しています。", true);
        setDispatchProgress("preparing");
        stopDispatchTimer();
        dispatchTimer = setInterval(function () { setDispatchProgress("sending"); }, 1000);
        fetch("/analyze", {
          method: "POST",
          headers: { "Content-Type": "application/json", "x-canvas-token": token },
          signal: dispatchAbortController.signal,
          body: JSON.stringify({
            traceId: traceId,
            spanId: spanId || undefined,
            focus: focus,
            profile: selected.profile,
            requestedModel: selected.requestedModel,
            requestedReasoningEffort: selected.requestedReasoningEffort || undefined,
            requestedTimeoutSeconds: selected.requestedTimeoutSeconds
          })
        })
          .then(function (r) { return r.json().then(function (b) { return { status: r.status, body: b }; }); })
          .then(function (out) {
            if (out.status === 200) {
              setDispatchProgress("dispatched");
              setResult("Copilot に分析指示を送信しました。Copilot チャットを確認してください。", true);
            }
            else { setResult("失敗しました: " + (out.body && out.body.error || ("HTTP " + out.status)), false); }
          })
          .catch(function (e) {
            if (e.name === "AbortError") {
              setResult("ローカルの送信待機をキャンセルしました。", false);
            } else {
              setResult("失敗しました: " + e.message, false);
            }
          })
          .finally(function () {
            stopDispatchTimer();
            dispatchAbortController = null;
            cancelDispatch.style.display = "none";
            btn.disabled = false;
          });
      });
    })();
  </script>
</body>
</html>`;
}
