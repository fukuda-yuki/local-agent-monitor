import { canonicalCursor } from "./canvas-evidence-helpers.mjs";

const TRACE_ID = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;
const PINNED_FAILURES = new Set([400, 404, 503]);
const RELATIONSHIP_SOURCES = new Set(["parent_span", "time_inferred", "unresolved"]);
const RELATIONSHIP_CONFIDENCES = new Set(["exact", "inferred", "unknown"]);
const AGENT_ROLES = new Set(["main", "sub", "root", "unknown"]);
const WARNINGS = new Set(["cycle_detected", "duplicate_span_id", "unknown_parent", "time_range_inconsistent"]);
const SUMMARY_FIELDS = ["main_agent_name", "root_agent_count", "subagent_invocation_count", "unique_subagent_count", "max_agent_depth", "parallel_agent_group_count", "relationship_quality", "agent_presence"];
const AGENT_FIELDS = ["span_id", "agent_name", "agent_role", "caller_agent_span_id", "model", "started_at", "ended_at", "duration_ms", "input_tokens", "output_tokens", "total_tokens", "status", "child_agent_count", "agent_depth", "relationship_source", "relationship_confidence"];
const OWNERSHIP_FIELDS = ["span_id", "owning_agent_span_id", "relationship_source", "relationship_confidence"];
const SPAN_FIELDS = ["id", "raw_record_id", "trace_id", "span_id", "parent_span_id", "span_ordinal", "operation", "category", "tool_name", "tool_type", "mcp_tool_name", "mcp_server_hash", "agent_name", "request_model", "response_model", "input_tokens", "output_tokens", "total_tokens", "reasoning_tokens", "cache_read_tokens", "cache_creation_tokens", "status", "error_type", "finish_reasons", "conversation_id", "duration_ms", "start_time", "end_time", "projected_at"];

const has = (value, field) => Object.prototype.hasOwnProperty.call(value, field);
const hasAll = (value, fields) => fields.every((field) => has(value, field));
const nullableString = (value) => value === null || typeof value === "string";
const nullableNumber = (value) => value === null || typeof value === "number";

function sendJson(res, status, value) {
    res.statusCode = status;
    res.setHeader("Content-Type", "application/json; charset=utf-8");
    res.end(JSON.stringify(value));
}

function fixed502(res) {
    sendJson(res, 502, { error: "monitor_unavailable" });
}

function safeFailure(value) {
    if (!value || typeof value !== "object" || Array.isArray(value)) return null;
    const result = {};
    if (typeof value.accepted === "boolean") result.accepted = value.accepted;
    if (typeof value.error === "string") result.error = value.error;
    if (typeof value.message === "string") result.message = value.message;
    return typeof result.error === "string" ? result : null;
}

export function isAgentGraph(value) {
    if (!(value && typeof value === "object" && !Array.isArray(value)
        && value.summary && typeof value.summary === "object" && !Array.isArray(value.summary) && hasAll(value.summary, SUMMARY_FIELDS)
        && nullableString(value.summary.main_agent_name)
        && ["root_agent_count", "subagent_invocation_count", "unique_subagent_count", "max_agent_depth", "parallel_agent_group_count"].every((field) => Number.isInteger(value.summary[field]) && value.summary[field] >= 0)
        && new Set(["exact", "partially_inferred", "undeterminable"]).has(value.summary.relationship_quality)
        && new Set(["detected", "none_detected", "undeterminable"]).has(value.summary.agent_presence)
        && Array.isArray(value.agents) && value.agents.every((agent) => agent && typeof agent === "object" && hasAll(agent, AGENT_FIELDS)
            && nullableString(agent.span_id) && nullableString(agent.agent_name) && AGENT_ROLES.has(agent.agent_role)
            && nullableString(agent.caller_agent_span_id) && nullableString(agent.model) && nullableString(agent.started_at) && nullableString(agent.ended_at)
            && nullableNumber(agent.duration_ms) && nullableNumber(agent.input_tokens) && nullableNumber(agent.output_tokens) && nullableNumber(agent.total_tokens)
            && nullableString(agent.status) && Number.isInteger(agent.child_agent_count) && agent.child_agent_count >= 0
            && nullableNumber(agent.agent_depth) && RELATIONSHIP_SOURCES.has(agent.relationship_source) && RELATIONSHIP_CONFIDENCES.has(agent.relationship_confidence))
        && Array.isArray(value.span_ownership) && value.span_ownership.every((item) => item && typeof item === "object" && hasAll(item, OWNERSHIP_FIELDS)
            && nullableString(item.span_id) && nullableString(item.owning_agent_span_id)
            && RELATIONSHIP_SOURCES.has(item.relationship_source) && RELATIONSHIP_CONFIDENCES.has(item.relationship_confidence))
        && Array.isArray(value.parallel_groups) && value.parallel_groups.every((group) => Array.isArray(group) && group.every((id) => typeof id === "string"))
        && Array.isArray(value.graph_warnings) && value.graph_warnings.every((warning) => WARNINGS.has(warning)))) return false;
    return true;
}

export function isSpanPage(value) {
    if (!value || typeof value !== "object" || Array.isArray(value) || !Array.isArray(value.items)
        || !value.items.every((item) => item && typeof item === "object" && !Array.isArray(item) && hasAll(item, SPAN_FIELDS)
            && nullableString(item.trace_id) && nullableString(item.span_id) && nullableString(item.parent_span_id)
            && nullableString(item.operation) && nullableString(item.category) && nullableString(item.tool_name) && nullableString(item.tool_type)
            && nullableString(item.request_model) && nullableString(item.response_model) && nullableString(item.status)
            && nullableString(item.error_type) && nullableString(item.start_time) && nullableString(item.end_time))
        || !("next_cursor" in value)) return false;
    try { canonicalCursor(value.next_cursor); return true; } catch { return false; }
}

export function evidenceProxyTarget(requestUrl) {
    const url = new URL(requestUrl, "http://127.0.0.1");
    const prefix = "/api/session-evidence/traces/";
    if (!url.pathname.startsWith(prefix)) return { error: "invalid_session_evidence_request" };
    const suffix = url.pathname.slice(prefix.length);
    const separator = suffix.lastIndexOf("/");
    let traceId;
    try { traceId = separator > 0 ? decodeURIComponent(suffix.slice(0, separator)) : ""; } catch { return { error: "invalid_session_evidence_request" }; }
    const resource = separator > 0 ? suffix.slice(separator + 1) : "";
    if (!TRACE_ID.test(traceId) || !["agent-graph", "spans"].includes(resource)) return { error: "invalid_session_evidence_request" };
    const allowed = resource === "spans" ? new Set(["t", "limit", "after"]) : new Set(["t"]);
    if ([...url.searchParams.keys()].some((key) => !allowed.has(key))) return { error: "invalid_session_evidence_query" };
    for (const key of allowed) if (url.searchParams.getAll(key).length > 1) return { error: "invalid_session_evidence_query" };
    if (resource === "agent-graph") return { traceId, resource, monitorPath: `/api/monitor/traces/${encodeURIComponent(traceId)}/agent-graph` };
    if (url.searchParams.get("limit") !== "200") return { error: "invalid_session_evidence_query" };
    let after = null;
    try { after = url.searchParams.has("after") ? canonicalCursor(url.searchParams.get("after")) : null; } catch { return { error: "invalid_session_evidence_query" }; }
    return { traceId, resource, monitorPath: `/api/monitor/traces/${encodeURIComponent(traceId)}/spans?limit=200${after === null ? "" : `&after=${encodeURIComponent(after)}`}` };
}

export async function handleEvidenceProxy(req, res, options) {
    const url = new URL(req.url, "http://127.0.0.1");
    if ((req.headers["x-canvas-token"] || url.searchParams.get("t")) !== options.token) {
        sendJson(res, 401, { error: "unauthorized" });
        return true;
    }
    if (req.method !== "GET") { sendJson(res, 405, { error: "method_not_allowed" }); return true; }
    let monitor;
    try { monitor = new URL(options.monitorUrl); } catch { sendJson(res, 400, { error: "invalid_monitor_url" }); return true; }
    if (!(["127.0.0.1", "localhost", "::1"].includes(monitor.hostname))) { sendJson(res, 400, { error: "invalid_monitor_url" }); return true; }
    const target = evidenceProxyTarget(req.url);
    if (target.error) { sendJson(res, 400, { error: target.error }); return true; }
    let upstream;
    try { upstream = await options.fetchImpl(new URL(target.monitorPath, monitor).toString()); } catch { fixed502(res); return true; }
    const status = upstream.status;
    let parsed;
    try { parsed = upstream.body ? JSON.parse(upstream.body) : null; } catch { fixed502(res); return true; }
    if (PINNED_FAILURES.has(status)) {
        const failure = safeFailure(parsed);
        if (!failure) fixed502(res); else sendJson(res, status, failure);
        return true;
    }
    if (status !== 200 || !(target.resource === "agent-graph" ? isAgentGraph(parsed) : isSpanPage(parsed))) { fixed502(res); return true; }
    sendJson(res, 200, parsed);
    return true;
}
