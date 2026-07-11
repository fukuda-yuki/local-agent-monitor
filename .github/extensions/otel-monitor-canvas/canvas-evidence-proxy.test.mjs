import assert from "node:assert/strict";
import { createServer, request } from "node:http";
import test from "node:test";
import { handleEvidenceProxy } from "./canvas-evidence-proxy.mjs";

const graph = {
    summary: { main_agent_name: null, root_agent_count: 0, subagent_invocation_count: 0, unique_subagent_count: 0, max_agent_depth: 0, parallel_agent_group_count: 0, relationship_quality: "exact", agent_presence: "none_detected" },
    agents: [], span_ownership: [], parallel_groups: [], graph_warnings: [],
};
const span = { id: 1, raw_record_id: 1, trace_id: "t", span_id: "s", parent_span_id: null, span_ordinal: 0, operation: null, category: null, tool_name: null, tool_type: null, mcp_tool_name: null, mcp_server_hash: null, agent_name: null, request_model: null, response_model: null, input_tokens: null, output_tokens: null, total_tokens: null, reasoning_tokens: null, cache_read_tokens: null, cache_creation_tokens: null, status: null, error_type: null, finish_reasons: null, conversation_id: null, duration_ms: null, start_time: null, end_time: null, projected_at: "2026-07-11T00:00:00Z" };

async function start(handler) {
    const server = createServer(handler);
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    return { server, url: `http://127.0.0.1:${server.address().port}` };
}

function get(url, token = "token") {
    return new Promise((resolve, reject) => {
        const req = request(url, { headers: token ? { "x-canvas-token": token } : {} }, (res) => {
            let body = ""; res.on("data", (chunk) => body += chunk); res.on("end", () => resolve({ status: res.statusCode, body: JSON.parse(body) }));
        }); req.on("error", reject); req.end();
    });
}

test("executable proxy enforces token and query contract and forwards numeric cursor", async (t) => {
    const upstreamUrls = [];
    const upstream = await start((req, res) => { upstreamUrls.push(req.url); res.setHeader("Content-Type", "application/json"); res.end(JSON.stringify({ items: [], next_cursor: null })); });
    const proxy = await start((req, res) => handleEvidenceProxy(req, res, { token: "token", monitorUrl: upstream.url, fetchImpl: async (url) => { const response = await fetch(url); return { status: response.status, body: await response.text() }; } }));
    t.after(() => { proxy.server.close(); upstream.server.close(); });
    assert.equal((await get(`${proxy.url}/api/session-evidence/traces/t/spans?limit=200`, null)).status, 401);
    for (const path of ["t/spans?limit=199", "t/spans?limit=200&limit=200", "t/spans?limit=200&after=%20", "t/spans?limit=200&raw=1", "%ZZ/spans?limit=200"]) assert.equal((await get(`${proxy.url}/api/session-evidence/traces/${path}`)).status, 400);
    assert.equal((await get(`${proxy.url}/api/session-evidence/traces/t/spans?limit=200&after=200`)).status, 200);
    assert.equal(upstreamUrls.at(-1), "/api/monitor/traces/t/spans?limit=200&after=200");
});

test("proxy preserves pinned sanitized failures and maps invalid responses to fixed 502", async (t) => {
    let response = { status: 503, body: JSON.stringify({ accepted: false, error: "persistence_busy", message: "busy", raw: "secret" }) };
    const proxy = await start((req, res) => handleEvidenceProxy(req, res, { token: "token", monitorUrl: "http://127.0.0.1:4320", fetchImpl: async () => response }));
    t.after(() => proxy.server.close());
    const preserved = await get(`${proxy.url}/api/session-evidence/traces/t/agent-graph`);
    assert.deepEqual(preserved, { status: 503, body: { accepted: false, error: "persistence_busy", message: "busy" } });
    for (const invalid of [{ status: 200, body: "" }, { status: 200, body: "not-json" }, { status: 200, body: "{}" }, { status: 500, body: JSON.stringify({ error: "raw", raw: "secret" }) }]) {
        response = invalid;
        assert.deepEqual(await get(`${proxy.url}/api/session-evidence/traces/t/agent-graph`), { status: 502, body: { error: "monitor_unavailable" } });
    }
    response = { status: 200, body: JSON.stringify({ items: [{ id: 1 }], next_cursor: null }) };
    assert.deepEqual(await get(`${proxy.url}/api/session-evidence/traces/t/spans?limit=200`), { status: 502, body: { error: "monitor_unavailable" } });
    response = { status: 200, body: JSON.stringify(graph) };
    const sanitizedOnly = await get(`${proxy.url}/api/session-evidence/traces/t/agent-graph`);
    assert.equal(sanitizedOnly.status, 200);
    assert.doesNotMatch(JSON.stringify(sanitizedOnly.body), /raw|payload|prompt|response_body/i);
    assert.equal((await get(`${proxy.url}/api/session-evidence/traces/t/raw`)).status, 400);
});

test("near-valid incomplete graph and span shapes map to fixed 502", async (t) => {
    let response = { status: 200, body: "" };
    const proxy = await start((req, res) => handleEvidenceProxy(req, res, { token: "token", monitorUrl: "http://127.0.0.1:4320", fetchImpl: async () => response }));
    t.after(() => proxy.server.close());
    for (const incomplete of [
        { ...graph, summary: { ...graph.summary, relationship_quality: undefined } },
        { ...graph, summary: { ...graph.summary, agent_presence: "maybe" } },
        { ...graph, agents: [{ span_id: "a" }] },
        { ...graph, span_ownership: [{ span_id: "s", owning_agent_span_id: null }] },
    ]) {
        response = { status: 200, body: JSON.stringify(incomplete) };
        assert.equal((await get(`${proxy.url}/api/session-evidence/traces/t/agent-graph`)).status, 502);
    }
    for (const missing of ["start_time", "request_model", "tool_type", "error_type", "end_time"]) {
        const incomplete = { ...span }; delete incomplete[missing];
        response = { status: 200, body: JSON.stringify({ items: [incomplete], next_cursor: null }) };
        assert.equal((await get(`${proxy.url}/api/session-evidence/traces/t/spans?limit=200`)).status, 502);
    }
    response = { status: 200, body: JSON.stringify({ items: [span], next_cursor: null, additive_future_field: true }) };
    assert.equal((await get(`${proxy.url}/api/session-evidence/traces/t/spans?limit=200`)).status, 200);
});
