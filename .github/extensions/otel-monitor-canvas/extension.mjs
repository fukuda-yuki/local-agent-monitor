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
// Sprint15 (D036) child A: the helper-page presentation (decision-supporting
// trace line, Japanese focus / button labels, concrete health/error guidance,
// collapsed health response) and the pure projection/format helpers live in
// ./canvas-helpers.mjs so they can be unit tested (node --test) and syntax
// checked (node --check) without joining a session. The display boundary is
// unchanged: action responses stay bounded DTOs and no raw / PII is returned.
//
// Canvas id: otel-monitor
// Display name: OTel Monitor

import { createServer } from "node:http";
import { randomUUID } from "node:crypto";
import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";
import {
    renderHelperHtml,
    buildAnalysisPrompt,
    compactTrace,
    summarizeTopSpans,
    uniqueModels,
    sumField,
    isChatTurn,
    isErrorSpan,
    hierarchyFromSpans,
    compareByTimeThenOrdinal,
    cacheHitRate,
    cacheTurn,
    sanitizeDto,
    formatTraceLine,
    formatTokens,
    summaryTraceLine,
    traceDetailSummary,
    extractRawPreviewFragment,
    renderRawPreviewHtml,
    renderRawPreviewMessageHtml,
    MAX_CACHE_TURNS,
} from "./canvas-helpers.mjs";

const DEFAULT_MONITOR_URL = "http://127.0.0.1:4320";
const TRACE_ID_PATTERN = "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$";
const MAX_TRACE_LIST_LIMIT = 50;
const MAX_SPAN_PAGE_SIZE = 200;
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

function matchesTraceId(value) {
    return typeof value === "string" && new RegExp(TRACE_ID_PATTERN).test(value);
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

// Shared "fetch sanitized trace rows" used by both the /api/traces helper
// route and the /api/trace-detail/:traceId route (Sprint15 M3), so both share
// one underlying fetch+parse path rather than duplicating it. Operates on the
// route's own `monitorUrl` closure variable, mirroring fetchTracePage's ctx-
// based equivalent used by the Canvas action handlers.
async function fetchHelperTraceRows(monitorUrl) {
    const { response, body } = await fetchTextWithTimeout(monitorApiUrl(monitorUrl, `/api/monitor/traces?limit=${MAX_TRACE_LIST_LIMIT}`));
    if (!response.ok) {
        throw new CanvasError("monitor_unavailable", `The Local Monitor returned HTTP ${response.status}.`);
    }
    const page = body ? parseJsonBody(body) : { items: [] };
    return Array.isArray(page.items) ? page.items : [];
}

// Shared "fetch sanitized spans for one trace" used by /api/trace-detail/:traceId
// (Sprint15 M3), mirroring fetchSpanPage's ctx-based equivalent used by the
// Canvas action handlers.
async function fetchHelperSpans(monitorUrl, traceId) {
    const encodedTraceId = encodeURIComponent(traceId);
    const { response, body } = await fetchTextWithTimeout(monitorApiUrl(monitorUrl, `/api/monitor/traces/${encodedTraceId}/spans?limit=${MAX_SPAN_PAGE_SIZE}`));
    if (!response.ok) {
        throw new CanvasError("monitor_unavailable", `The Local Monitor returned HTTP ${response.status}.`);
    }
    const page = body ? parseJsonBody(body) : { items: [] };
    return Array.isArray(page.items) ? page.items : [];
}

// Fetch the sanitized dashboard summary (Sprint15 M4 / child B remainder,
// D038), proxied as-is from GET /api/monitor/summary (already the bounded
// D037 contract — no reshaping here). Unlike fetchHelperTraceRows/
// fetchHelperSpans, this does NOT throw on a non-OK response: an out-of-
// range `?limit=` is a legitimate client-input 400 from the Local Monitor,
// and the route passes that status/body straight through rather than
// masking it as a generic monitor_unavailable 502 (only a genuine network
// failure — a thrown CanvasError from fetchTextWithTimeout itself — reaches
// that generic path).
async function fetchHelperSummary(monitorUrl, limitQuery) {
    const path = limitQuery ? `/api/monitor/summary?${limitQuery}` : "/api/monitor/summary";
    return fetchTextWithTimeout(monitorApiUrl(monitorUrl, path));
}

// Fetch one trace's prompt label (D039), proxied server-to-server from the
// Local Monitor's own /traces/{traceId}/prompt-label. Unlike
// fetchHelperTraceRows/fetchHelperSpans, this never throws: both a non-OK
// response (e.g. a 404 when the Local Monitor route is absent) and any
// thrown error (network failure, timeout) resolve to null. This is
// deliberate — the /api/traces route below fetches a label per trace in
// parallel via Promise.all, and a single trace's label lookup failing must
// not take down the whole trace list (that would defeat D039's "additive,
// gracefully degrading" intent).
async function fetchHelperPromptLabel(monitorUrl, traceId) {
    try {
        const encodedTraceId = encodeURIComponent(traceId);
        const { response, body } = await fetchTextWithTimeout(monitorApiUrl(monitorUrl, `/traces/${encodedTraceId}/prompt-label`));
        if (!response.ok) {
            return null;
        }
        const parsed = body ? parseJsonBody(body) : null;
        return parsed && typeof parsed.prompt_label === "string" ? parsed.prompt_label : null;
    } catch {
        return null;
    }
}

function createHelperServer({ instanceId, monitorUrl, healthState, statusCode, healthBody, error, token, session }) {
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
            res.end(renderHelperHtml({ instanceId, monitorUrl, healthState, statusCode, healthBody, error, token }));
            return;
        }

        if (req.method === "GET" && path === "/api/traces") {
            try {
                if (!isLoopbackUrl(monitorUrl)) {
                    sendJson(res, 400, { error: "invalid_monitor_url" });
                    return;
                }
                const rows = await fetchHelperTraceRows(monitorUrl);
                const items = rows.map((row) => {
                    const trace = compactTrace(row);
                    // `line` is a sanitized, decision-supporting label built
                    // only from compactTrace fields (Sprint15 A1).
                    return { ...trace, line: formatTraceLine(trace) };
                });
                // `prompt_label` (D039) is fetched per trace in parallel and
                // merged in additively — `line` above is unchanged.
                const promptLabels = await Promise.all(items.map((item) => fetchHelperPromptLabel(monitorUrl, item.trace_id)));
                const itemsWithPromptLabel = items.map((item, i) => ({ ...item, prompt_label: promptLabels[i] }));
                sendJson(res, 200, { items: itemsWithPromptLabel, count: itemsWithPromptLabel.length });
            } catch (err) {
                const code = err instanceof CanvasError ? err.code : "monitor_unavailable";
                sendJson(res, 502, { error: code, message: err.message });
            }
            return;
        }

        if (req.method === "GET" && path === "/api/summary") {
            try {
                if (!isLoopbackUrl(monitorUrl)) {
                    sendJson(res, 400, { error: "invalid_monitor_url" });
                    return;
                }
                const limitQuery = url.searchParams.has("limit")
                    ? `limit=${encodeURIComponent(url.searchParams.get("limit"))}`
                    : "";
                const { response, body } = await fetchHelperSummary(monitorUrl, limitQuery);
                if (!response.ok) {
                    // Pass the Local Monitor's own status/body through (e.g.
                    // its 400 for an out-of-range `limit`) instead of masking
                    // every non-OK response as a generic monitor_unavailable
                    // 502 — only a thrown network-level failure (caught
                    // below) gets that generic treatment.
                    let parsedError = null;
                    try {
                        parsedError = body ? JSON.parse(body) : null;
                    } catch {
                        parsedError = null;
                    }
                    sendJson(res, response.status, parsedError ?? { error: "monitor_unavailable", message: `The Local Monitor returned HTTP ${response.status}.` });
                    return;
                }
                const summary = body ? parseJsonBody(body) : {};
                // Additive-only enrichment mirroring /api/traces' own `line`
                // field: every existing D037/D038 field is preserved
                // unchanged, only a derived, already-sanitized display string
                // is appended (Sprint15 M4 / D038). The highlight traces are
                // raw MonitorHost.ToTraceDto shapes (no `status` field), so
                // `summaryTraceLine` routes them through compactTrace first —
                // calling formatTraceLine on them directly would always read
                // `status` as undefined and mislabel error traces as "OK".
                const withLine = (trace) => (trace ? { ...trace, line: summaryTraceLine(trace) } : null);
                const withTokensFormatted = (rows) => (Array.isArray(rows)
                    ? rows.map((row) => ({ ...row, total_tokens_formatted: formatTokens(row.total_tokens) }))
                    : rows);
                sendJson(res, 200, {
                    ...summary,
                    latest_trace: withLine(summary.latest_trace),
                    top_token_trace: withLine(summary.top_token_trace),
                    error_trace: withLine(summary.error_trace),
                    per_model_summary: withTokensFormatted(summary.per_model_summary),
                    per_client_kind_summary: withTokensFormatted(summary.per_client_kind_summary),
                });
            } catch (err) {
                const code = err instanceof CanvasError ? err.code : "monitor_unavailable";
                sendJson(res, 502, { error: code, message: err.message });
            }
            return;
        }

        if (req.method === "GET" && path.startsWith("/api/trace-detail/")) {
            const traceId = decodeURIComponent(path.slice("/api/trace-detail/".length));
            if (!matchesTraceId(traceId)) {
                sendJson(res, 400, { error: "invalid_trace_id" });
                return;
            }
            try {
                if (!isLoopbackUrl(monitorUrl)) {
                    sendJson(res, 400, { error: "invalid_monitor_url" });
                    return;
                }
                const [rows, spans] = await Promise.all([
                    fetchHelperTraceRows(monitorUrl),
                    fetchHelperSpans(monitorUrl, traceId),
                ]);
                const traceRow = rows.find((row) => row.trace_id === traceId) ?? null;
                if (!traceRow && spans.length === 0) {
                    sendJson(res, 404, { error: "trace_not_found" });
                    return;
                }

                const trace = traceRow
                    ? compactTrace(traceRow)
                    : {
                        trace_id: traceId,
                        status: spans.some(isErrorSpan) ? "error" : "ok",
                        span_count: spans.length,
                    };
                const chatTurns = spans.filter(isChatTurn);
                const cacheReadTokens = sumField(chatTurns, "cache_read_tokens");
                const inputTokens = sumField(chatTurns, "input_tokens");
                const summary = traceDetailSummary({
                    trace,
                    cacheHitRate: cacheHitRate(cacheReadTokens, inputTokens),
                });
                sendJson(res, 200, summary);
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

        // Sprint15 M5 (child D, D038): page-navigation-only raw preview.
        // HTML end to end — no JSON error branch here, this route is reached
        // only by a real <a href> link click, never by client-side fetch().
        if (req.method === "GET" && path.startsWith("/raw-preview/")) {
            res.setHeader("Content-Type", "text/html; charset=utf-8");
            res.setHeader("Cache-Control", "no-store");

            const segments = path.slice("/raw-preview/".length).split("/");
            if (segments.length !== 2) {
                res.statusCode = 400;
                res.end(renderRawPreviewMessageHtml({ heading: "不正なリクエスト", message: "trace id と span id を指定してください。", token }));
                return;
            }

            const previewTraceId = decodeURIComponent(segments[0]);
            const previewSpanId = decodeURIComponent(segments[1]);
            if (!matchesTraceId(previewTraceId) || !matchesTraceId(previewSpanId)) {
                res.statusCode = 400;
                res.end(renderRawPreviewMessageHtml({ heading: "不正なリクエスト", message: "trace id または span id の形式が正しくありません。", token }));
                return;
            }

            try {
                if (!isLoopbackUrl(monitorUrl)) {
                    res.statusCode = 400;
                    res.end(renderRawPreviewMessageHtml({ heading: "設定エラー", message: "Monitor URL がループバックではありません。", token }));
                    return;
                }

                const spans = await fetchHelperSpans(monitorUrl, previewTraceId);
                const span = spans.find((s) => s.span_id === previewSpanId);
                if (!span || span.raw_record_id === undefined || span.raw_record_id === null) {
                    res.statusCode = 404;
                    res.end(renderRawPreviewMessageHtml({ heading: "見つかりません", message: "指定した span が見つかりません。", token }));
                    return;
                }

                const { response, body } = await fetchTextWithTimeout(monitorApiUrl(monitorUrl, `/traces/${span.raw_record_id}/raw`));
                if (!response.ok) {
                    if (typeof body === "string" && body.includes("raw_record_not_found")) {
                        res.statusCode = 404;
                        res.end(renderRawPreviewMessageHtml({ heading: "見つかりません", message: "指定した raw レコードが見つかりません。", token }));
                        return;
                    }
                    res.statusCode = 502;
                    res.end(renderRawPreviewMessageHtml({
                        heading: "raw を取得できません",
                        message: "raw を取得できませんでした（Local Monitor 側で raw が無効になっているか、一時的に利用できません）。",
                        token,
                    }));
                    return;
                }

                const fragment = extractRawPreviewFragment(body);
                if (fragment === null) {
                    res.statusCode = 502;
                    res.end(renderRawPreviewMessageHtml({ heading: "表示できません", message: "Local Monitor の raw レスポンス形式を認識できませんでした。", token }));
                    return;
                }

                res.statusCode = 200;
                res.end(renderRawPreviewHtml({ traceId: previewTraceId, spanId: previewSpanId, fragment, token }));
            } catch {
                res.statusCode = 502;
                res.end(renderRawPreviewMessageHtml({
                    heading: "raw を取得できません",
                    message: "raw を取得できませんでした（Local Monitor 側で raw が無効になっているか、一時的に利用できません）。",
                    token,
                }));
            }
            return;
        }

        sendJson(res, 404, { error: "not_found" });
    });
    return server;
}

async function startHelperServer({ instanceId, monitorUrl, healthState, statusCode, healthBody, error, token, session }) {
    const server = createHelperServer({ instanceId, monitorUrl, healthState, statusCode, healthBody, error, token, session });
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

                // Check monitor health and derive a structured state for the
                // helper page (Sprint15 A4): ready / not_ready / unreachable.
                const health = await checkMonitorHealth(monitorUrl);
                const healthState = health.healthy
                    ? "ready"
                    : (health.statusCode !== null ? "not_ready" : "unreachable");

                // Always start the extension-owned helper page (M5). The page
                // shows monitor connection state, a decision-supporting trace
                // dropdown (proxied from /api/monitor/traces), a focus selector,
                // and the "Copilotでこのトレースを分析" trigger. The trigger is
                // disabled when the monitor is not ready.
                const token = randomUUID();
                const entry = await startHelperServer({
                    instanceId: ctx.instanceId,
                    monitorUrl,
                    healthState,
                    statusCode: health.statusCode,
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
