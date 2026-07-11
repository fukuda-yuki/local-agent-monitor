import assert from "node:assert/strict";
import test from "node:test";

import {
    collectAllSpanPages,
    createEvidenceLoadCoordinator,
    buildAgentForest,
    composeEvidence,
    evidenceGateLinks,
    evidenceInspector,
    evidenceSelectionKey,
    evidenceTraceIds,
    relationshipLabel,
} from "./canvas-evidence-helpers.mjs";

const graph = {
    summary: { agent_presence: "detected", relationship_quality: "partially_inferred" },
    agents: [
        { span_id: "a", agent_name: "Main", agent_role: "main", caller_agent_span_id: null, agent_depth: 0, relationship_source: "parent_span", relationship_confidence: "exact" },
        { span_id: "b", agent_name: "Sub", agent_role: "sub", caller_agent_span_id: "a", agent_depth: 1, relationship_source: "time_inferred", relationship_confidence: "inferred" },
    ],
    span_ownership: [
        { span_id: "tool", owning_agent_span_id: "b", relationship_source: "parent_span", relationship_confidence: "exact" },
        { span_id: "lost", owning_agent_span_id: null, relationship_source: "unresolved", relationship_confidence: "unknown" },
    ],
    parallel_groups: [{ agent_span_ids: ["b"] }],
    graph_warnings: [],
};

test("trace ids are distinct non-null values in run order", () => {
    assert.deepEqual(evidenceTraceIds({ runs: [
        { trace_id: "trace-b" }, { trace_id: null }, { trace_id: "trace-a" }, { trace_id: "trace-b" },
    ] }), ["trace-b", "trace-a"]);
});

test("span pagination follows production numeric cursors across more than 200 spans", async () => {
    const calls = [];
    const first = Array.from({ length: 200 }, (_, id) => ({ id: id + 1, span_id: `span-${id + 1}`, start_time: `2026-01-01T00:00:${String(id % 60).padStart(2, "0")}Z` }));
    const pages = new Map([
        [null, { items: first, next_cursor: 200 }],
        ["200", { items: [{ id: 201, span_id: "span-201", start_time: "2026-01-01T00:04:00Z" }], next_cursor: null }],
    ]);
    const spans = await collectAllSpanPages(async (cursor) => { calls.push(cursor); return pages.get(cursor); });
    assert.deepEqual(calls, [null, "200"]);
    assert.equal(spans.length, 201);
    await assert.rejects(() => collectAllSpanPages(async () => ({ items: [], next_cursor: 200 })), /repeated_span_cursor/);
    let page = 0;
    await assert.rejects(() => collectAllSpanPages(async () => page++ === 0 ? { items: [], next_cursor: 200 } : { items: [], next_cursor: 199 }), /non_progressing_span_cursor/);
});

test("composition keeps one forest per trace and joins spans only through graph ownership", () => {
    const session = { events: [{ event_id: "event", run_id: "run", occurred_at: "2026-01-01T00:00:01Z", type: "tool.complete", status: "ok", content_state: "not_captured" }] };
    const traces = [
        { traceId: "trace-b", graph, spans: [{ span_id: "tool", parent_span_id: "x", start_time: "2026-01-01T00:00:00Z", category: "tool" }] },
        { traceId: "trace-a", graph: { ...graph, agents: [graph.agents[0]] }, spans: [] },
    ];
    const result = composeEvidence(session, traces);
    assert.deepEqual(result.forests.map((forest) => forest.traceId), ["trace-b", "trace-a"]);
    assert.equal(result.forests[0].spans[0].owningAgentSpanId, "b");
    assert.equal(result.timeline[1].ownership, "session_unowned");
    assert.equal(result.timeline[1].owningAgentSpanId, null);
});

test("timeline is chronological then stable source order and unresolved ownership is not inferred", () => {
    const result = composeEvidence({ events: [
        { event_id: "e2", occurred_at: "2026-01-01T00:00:02Z" },
        { event_id: "e1", occurred_at: "2026-01-01T00:00:01Z" },
    ] }, [{ traceId: "t", graph, spans: [
        { span_id: "lost", start_time: "2026-01-01T00:00:01Z", request_model: "gpt-5.6", tool_type: "function", tool_name: "shell", mcp_server_name: "local" },
        { span_id: "unknown", parent_span_id: "a", start_time: null },
    ] }]);
    assert.deepEqual(result.timeline.map((item) => item.id), ["lost", "e1", "e2", "unknown"]);
    assert.equal(result.timeline[3].ownership, "unresolved");
    const inspected = evidenceInspector({ kind: "span", value: result.forests[0].spans[0] });
    assert.equal(inspected.request_model, "gpt-5.6");
    assert.equal(inspected.tool_type, "function");
    assert.equal(inspected.mcp_server_name, "local");
});

test("forest uses caller ids with out-of-order agents, separate roots, and honest orphans", () => {
    const forest = buildAgentForest([
        { span_id: "child", caller_agent_span_id: "root-a", agent_depth: 99, relationship_source: "parent_span", relationship_confidence: "exact" },
        { span_id: "orphan", caller_agent_span_id: "missing", agent_depth: 1, relationship_source: "unresolved", relationship_confidence: "unknown" },
        { span_id: "root-b", caller_agent_span_id: null, agent_depth: 0 },
        { span_id: "root-a", caller_agent_span_id: null, agent_depth: 0 },
    ], [["child", "root-b"], ["root-a", "child"]]);
    assert.deepEqual(forest.roots.map((node) => node.agent.span_id), ["root-b", "root-a"]);
    assert.deepEqual(forest.roots[1].children.map((node) => node.agent.span_id), ["child"]);
    assert.deepEqual(forest.orphans.map((node) => node.agent.span_id), ["orphan"]);
    assert.deepEqual(forest.parallelGroups, [["child", "root-b"], ["root-a", "child"]]);
});

test("selection keys include trace id for duplicate Agent and span ids", () => {
    assert.equal(evidenceSelectionKey("agent", { span_id: "same" }, "trace-a"), "agent:trace-a:same");
    assert.equal(evidenceSelectionKey("span", { span_id: "same" }, "trace-b"), "span:trace-b:same");
    assert.equal(evidenceSelectionKey("event", { event_id: "event" }, null), "event:event");
});

test("exact inferred unresolved none and undeterminable remain distinct", () => {
    assert.equal(relationshipLabel("parent_span", "exact"), "exact");
    assert.equal(relationshipLabel("time_inferred", "inferred"), "推定");
    assert.equal(relationshipLabel("unresolved", "unknown"), "判定不能");
    assert.equal(composeEvidence({}, [{ traceId: "n", graph: { summary: { agent_presence: "none_detected" }, agents: [], span_ownership: [] }, spans: [] }]).forests[0].presence, "none_detected");
    assert.equal(composeEvidence({}, [{ traceId: "u", graph: { summary: { agent_presence: "undeterminable" }, agents: [], span_ownership: [] }, spans: [] }]).forests[0].presence, "undeterminable");
});

test("inspector reports unavailable typed skill test and review fields without deriving names", () => {
    const inspected = evidenceInspector({ kind: "span", value: { span_id: "x", operation: "run npm test", tool_name: "review_skill" } });
    assert.equal(inspected.skill_name, "利用不可");
    assert.equal(inspected.skill_path, "利用不可");
    assert.equal(inspected.skill_version, "利用不可");
    assert.equal(inspected.test_result, "利用不可");
    assert.equal(inspected.review_result, "利用不可");
    assert.deepEqual(evidenceInspector({ kind: "event", value: { event_id: "e", content_state: "redacted", payload: "secret" } }), { event_id: "e", content_state: "redacted" });
});

test("quality links use the canonical terminal family and all exact error events", () => {
    for (const type of ["session.shutdown", "session.task_complete", "SessionEnd", "Stop"]) {
        assert.equal(evidenceGateLinks({ status: "completed" }, [{ event_id: "done", type }]).terminal, "event:done");
    }
    for (const type of ["session.completed", "session.end", "SubagentStop", "StopLater"]) {
        assert.equal(evidenceGateLinks({ status: "completed" }, [{ event_id: "wrong", type }]).terminal, null);
    }
    const links = evidenceGateLinks({ status: "failed" }, [
        { event_id: "done", type: "Stop", status: "error" },
        { event_id: "error", type: "tool.failed", status: "error" },
    ]);
    assert.equal(links.terminal, "event:done");
    assert.deepEqual(links.errors, ["event:done", "event:error"]);
    assert.equal(evidenceGateLinks({ status: "completed" }, []).terminal, null);
});

test("graph and spans retain independent success and error states", () => {
    const graphFailed = composeEvidence({}, [{ traceId: "t", graphError: { status: 503, error: "persistence_busy" }, spans: [{ span_id: "s", start_time: "2026-01-01T00:00:00Z" }] }]);
    assert.equal(graphFailed.forests[0].graphState, "error");
    assert.equal(graphFailed.forests[0].spanState, "available");
    assert.equal(graphFailed.timeline.length, 1);
    const spansFailed = composeEvidence({}, [{ traceId: "t", graph, spansError: { status: 503, error: "persistence_busy" } }]);
    assert.equal(spansFailed.forests[0].graphState, "available");
    assert.equal(spansFailed.forests[0].spanState, "error");
    assert.equal(spansFailed.forests[0].agents.length, 2);
});

test("load coordinator ignores stale completion and exposes immediate loading", async () => {
    const pending = new Map();
    const states = [];
    const coordinator = createEvidenceLoadCoordinator((state) => states.push(state));
    const loader = (id) => new Promise((resolve) => pending.set(id, resolve));
    const a = coordinator.load("A", loader);
    const b = coordinator.load("B", loader);
    assert.deepEqual(states.slice(-2).map((state) => [state.sessionId, state.loading]), [["A", true], ["B", true]]);
    pending.get("A")({ value: "stale" });
    await a;
    assert.notEqual(states.at(-1).data?.value, "stale");
    pending.get("B")({ value: "current" });
    await b;
    assert.equal(states.at(-1).data.value, "current");
});

test("no trace and graph error preserve session timeline with honest graph state", () => {
    const none = composeEvidence({ events: [{ event_id: "e" }] }, []);
    assert.equal(none.graphState, "unavailable");
    assert.deepEqual(none.timeline.map((item) => item.id), ["e"]);
    const failed = composeEvidence({}, [{ traceId: "t", error: { status: 503, error: "monitor_unavailable" } }]);
    assert.equal(failed.forests[0].state, "error");
    assert.equal(failed.forests[0].graphError.status, 503);
    assert.equal(failed.forests[0].spansError.status, 503);
});

test("sanitized composition never copies raw or payload fields", () => {
    const result = composeEvidence({ events: [{ event_id: "e", content_state: "available", payload: "RAW" }] }, [{ traceId: "t", graph, spans: [{ span_id: "tool", raw: "RAW", tool_arguments: "PII" }] }]);
    const json = JSON.stringify(result);
    assert.doesNotMatch(json, /RAW|PII|tool_arguments|payload/);
});
