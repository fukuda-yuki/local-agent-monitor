import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

import {
    deriveWorkspaceGates,
    groupWorkspaceSessions,
    instructionDisplay,
    renderWorkspaceHtml,
    workspaceNextActions,
    workspaceSessionLabel,
    workspaceStatusPill,
} from "./canvas-workspace-helpers.mjs";
import { evidenceBrowserScript } from "./canvas-evidence-helpers.mjs";

const bound = {
    session_id: "11111111-1111-7111-8111-111111111111",
    status: "completed",
    completeness: "full",
    repository: "sample-repo",
    started_at: "2026-07-11T01:00:00Z",
    last_seen_at: "2026-07-11T01:05:00Z",
    source_surfaces: ["copilot-sdk"],
};

test("workspace helpers group only the resolved session as this conversation and preserve list order", () => {
    const groups = groupWorkspaceSessions([
        bound,
        { ...bound, session_id: "22222222-2222-7222-8222-222222222222", completeness: "partial" },
        { ...bound, session_id: "33333333-3333-7333-8333-333333333333", completeness: "unbound" },
    ], bound.session_id);

    assert.deepEqual(groups.current.map((item) => item.session_id), [bound.session_id]);
    assert.deepEqual(groups.recent.map((item) => item.session_id), ["22222222-2222-7222-8222-222222222222"]);
    assert.deepEqual(groups.unbound.map((item) => item.session_id), ["33333333-3333-7333-8333-333333333333"]);
});

test("workspace helpers render labels, pills, deterministic gates, next actions, and honest instruction states", () => {
    assert.equal(workspaceSessionLabel(bound, { state: "available", preview: "一行目\n二行目" }), "一行目");
    assert.equal(workspaceSessionLabel(bound, { state: "available", preview: " \n二行目" }), "Copilot セッション");
    assert.equal(workspaceSessionLabel({ ...bound, completeness: "unbound" }, { state: "not_captured" }), "OTel トレースのみ（未紐付け）");
    assert.deepEqual(workspaceStatusPill("active"), { className: "pill-running", text: "実行中", pulsing: true });
    assert.deepEqual(deriveWorkspaceGates({ ...bound, status: "failed", completeness: "rich", events: [{ status: "error" }] }), [
        { label: "終了状態", state: "fail", detail: "失敗" },
        { label: "エラーイベント", state: "fail", detail: "1 件" },
    ]);
    assert.deepEqual(workspaceNextActions({ ...bound, status: "active" }, true), { primary: "Local Monitor を開く", secondary: null, analysis: false });
    assert.deepEqual(workspaceNextActions(bound, true), { primary: "トレース分析を開く", secondary: "Local Monitor を開く", analysis: true });
    for (const state of ["not_captured", "redacted", "unsupported", "expired_pending_deletion", "no_instruction"]) {
        assert.equal(instructionDisplay({ state }).text, `指示は ${state} です。推測では表示しません。`);
    }
});

test("workspace HTML contains the sidebar groups, four-tab shell, review cards, and no selected-session guess", () => {
    const html = renderWorkspaceHtml({
        monitorUrl: "http://127.0.0.1:4320",
        healthState: "ready",
        token: "synthetic-token",
    });

    for (const label of ["この会話（exact-bound）", "最近のセッション", "未紐付け（OTel のみ）", "Review", "Evidence", "Improve", "Compare", "セッションの結合", "実際の指示", "結果", "品質ゲート", "人間評価", "次の操作", "セッションを選択してください"]) {
        assert.match(html, new RegExp(label));
    }
    assert.match(html, /\/api\/session-workspace\/resolve/);
    assert.match(html, /\/api\/session-instruction\//);
    assert.match(html, /\/analysis\?t=/);
    assert.ok(html.includes("const firstLine=instruction.preview.split(/\\r?\\n/,1)[0].trim();if(firstLine)return firstLine.slice(0,80);}const surface="));
    assert.match(html, /人間評価を保存できませんでした。接続を確認して再試行してください。/);
    assert.match(html, /\.catch\(showEvaluationError\)/);
    assert.doesNotMatch(html, /innerHTML/);
});

test("helper server routes the legacy analysis view unchanged at /analysis", async () => {
    const extension = await readFile(new URL("./extension.mjs", import.meta.url), "utf8");
    assert.match(extension, /path === "\/analysis"/);
    assert.match(extension, /res\.end\(renderHelperHtml\(\{ instanceId, monitorUrl, healthState, statusCode, healthBody, error, token, extensionScope \}\)\)/);
});

test("workspace Evidence renders exact trace loading, independent forests, timeline, inspector, and unavailable states", () => {
    const html = renderWorkspaceHtml({ monitorUrl: "http://127.0.0.1:4320", healthState: "ready", token: "synthetic-token" });
    for (const expected of [
        "/api/session-evidence/traces/", "agent-graph", "spans?limit=200", "Agent 実行グラフ",
        "リンク済みタイムライン", "Inspector", "Session / unowned", "推定", "判定不能",
        "none_detected", "undeterminable", "exact-linked trace がないため Agent graph は利用できません。",
        "skill_name", "test_result", "review_result", "証拠利用不可",
    ]) assert.ok(html.includes(expected), `missing ${expected}`);
    assert.match(html, /evidenceTraceIdsClient/);
    assert.match(html, /seen\.has\(id\)/);
    assert.match(html, /seen\.has\(next\)/);
    assert.match(html, /selectionGeneration/);
    assert.match(html, /generation!==selectionGeneration/);
    assert.match(html, /data-evidence-key/);
    assert.match(html, /aria-current/);
    assert.match(html, /\.focus\(\{preventScroll:true\}\)/);
    assert.match(html, /選択したセッションを読み込んでいます/);
    for (const field of ["start_time", "end_time", "request_model", "response_model", "tool_type", "mcp_server_name", "error_type"]) assert.ok(html.includes(field));
    for (const terminal of ["session.shutdown", "session.task_complete", "SessionEnd", "Stop"]) assert.ok(html.includes(terminal));
    assert.match(html, /item\.kind==="event"\?"Session · ":"OTel · "/);
    assert.doesNotMatch(html, /event\.run_id.*own/i);
    assert.doesNotMatch(html, /innerHTML/);
    const script = html.match(/<script>([\s\S]*)<\/script>/)?.[1];
    assert.ok(script);
    assert.doesNotThrow(() => new vm.Script(script));
});

test("browser Evidence loader independently keeps graph errors and follows numeric span cursors", async () => {
    const context = vm.createContext({ URL, Number, Set, Map, Date, encodeURIComponent, console });
    new vm.Script(evidenceBrowserScript()).runInContext(context);
    const calls = [];
    const request = async (path) => {
        calls.push(path);
        if (path.endsWith("agent-graph")) return { status: 503, body: { error: "persistence_busy" } };
        if (!path.includes("after=")) return { status: 200, body: { items: [{ span_id: "s", start_time: "2026-07-11T00:00:00Z", request_model: "gpt-5.6" }], next_cursor: 200 } };
        return { status: 200, body: { items: [], next_cursor: null } };
    };
    const traces = await context.loadSessionEvidence({ runs: [{ trace_id: "trace" }] }, request);
    assert.equal(traces[0].graphError.status, 503);
    assert.equal(traces[0].spans.length, 1);
    assert.equal(calls.at(-1), "/api/session-evidence/traces/trace/spans?limit=200&after=200");
    const view = context.evidenceView({ events: [{ event_id: "e", occurred_at: "2026-07-11T00:00:01Z" }] }, traces);
    assert.deepEqual(Array.from(view.timeline, (item) => item.id), ["s", "e"]);
});

test("browser forest preserves API agent_depth and uses separate displayDepth", () => {
    const context = vm.createContext({ URL, Number, Set, Map, Date, BigInt, encodeURIComponent, console });
    new vm.Script(evidenceBrowserScript()).runInContext(context);
    const graphValue = { summary: { agent_presence: "detected" }, agents: [{ span_id: "root", caller_agent_span_id: null, agent_depth: 7 }], span_ownership: [], parallel_groups: [], graph_warnings: [] };
    const view = context.evidenceView({}, [{ traceId: "t", graph: graphValue, spans: [] }]);
    assert.equal(view.forests[0].agents[0].agent_depth, 7);
    assert.equal(view.forests[0].agents[0].displayDepth, 0);
    assert.notEqual(context.evidenceKey("span", { span_id: "same" }, "trace-a"), context.evidenceKey("span", { span_id: "same" }, "trace-b"));
});

test("browser forest renders null-caller unresolved Agent as 判定不能 orphan", () => {
    const context = vm.createContext({ URL, Number, Set, Map, Date, BigInt, encodeURIComponent, console });
    new vm.Script(evidenceBrowserScript()).runInContext(context);
    const agent = { span_id: "unresolved", caller_agent_span_id: null, agent_depth: null, agent_role: "unknown", relationship_source: "unresolved", relationship_confidence: "unknown" };
    const forest = context.evidenceForestClient([agent], []);
    assert.equal(forest.roots.length, 0);
    assert.equal(forest.orphans.length, 1);
    assert.equal(context.evidenceRelationship(forest.orphans[0].agent), "判定不能");
});

test("actual Review gate helper does not apply stale selection after deferred load", async () => {
    const context = vm.createContext({ URL, Number, Set, Map, Date, BigInt, encodeURIComponent, console });
    new vm.Script(evidenceBrowserScript()).runInContext(context);
    let resolveLoad;
    let state = { sessionId: "A", generation: 1 };
    const applied = [];
    const pending = context.runReviewEvidenceLink("A", { event_id: "error" }, () => new Promise((resolve) => resolveLoad = resolve), () => state, (event) => applied.push(event.event_id));
    state = { sessionId: "B", generation: 2 };
    resolveLoad(true);
    assert.equal(await pending, false);
    assert.deepEqual(applied, []);
});
