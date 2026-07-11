import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createServer, request as httpRequest } from "node:http";
import test from "node:test";
import vm from "node:vm";

import {
    deriveWorkspaceGates,
    groupWorkspaceSessions,
    instructionDisplay,
    renderWorkspaceHtml,
    workspaceNextActions,
    workspaceCandidatePayload,
    workspaceProposalReference,
    workspaceApplyDraftPayload,
    workspaceApplySelectionPayload,
    workspaceApplyApprovalPayload,
    workspaceApplyRequest,
    workspaceApplyView,
    workspaceApplyDraftReview,
    workspaceApplyConfirmation,
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

function workspaceRuntimeHelpers() {
    const html = renderWorkspaceHtml({ monitorUrl: "http://127.0.0.1:4320", healthState: "ready", token: "synthetic-token" });
    const script = html.match(/<script>([\s\S]*)<\/script>/)?.[1];
    const prefix = script.slice(0, script.indexOf("(function(){"));
    const context = vm.createContext({ Set, Map, Date, Number, BigInt, URL, encodeURIComponent, console });
    new vm.Script(prefix).runInContext(context);
    return context;
}

async function proposalApplyHelperServer(fetchCalls) {
    const source = await readFile(new URL("./extension.mjs", import.meta.url), "utf8");
    const boundary = source.indexOf("// --------------- canvas ---------------");
    const executable = source.slice(0, boundary).replace(/^import[\s\S]*?;\r?\n/gm, "") + "\nglobalThis.createHelperServer = createHelperServer;";
    const context = vm.createContext({
        createServer, URL, Buffer, AbortController, setTimeout, clearTimeout,
        fetch: async (target, init) => { fetchCalls.push({ target: String(target), init }); return new Response(JSON.stringify(target.includes("roots") ? { items: [] } : target.includes("apply") || target.includes("rollback") ? { apply_id: "0197d7c0-0000-7000-8000-000000000001", state: "applied" } : { draft_id: "0197d7c0-0000-7000-8000-000000000001", proposal_id: "0197d7c0-0000-7000-8000-000000000002", root_id: "0197d7c0-0000-7000-8000-000000000003", selection_revision: 1, approval_digest: "digest", state: "draft", files: [], hunks: [] }), { status: 200, headers: { "content-type": "application/json" } }); },
        Response, TextEncoder, console,
        renderWorkspaceHtml: () => "", renderHelperHtml: () => "", handleEvidenceProxy: () => {}, CanvasError: class CanvasError extends Error {},
    });
    new vm.Script(executable).runInContext(context);
    const server = context.createHelperServer({ instanceId: "i", monitorUrl: "http://127.0.0.1:4320", healthState: "ready", statusCode: 200, healthBody: "", error: null, token: "token", session: {}, extensionScope: "", nativeSessionId: "" });
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    const port = server.address().port;
    const call = (method, path, headers = {}, body) => new Promise((resolve, reject) => { const request = httpRequest({ port, host: "127.0.0.1", method, path, headers }, response => { let text = ""; response.on("data", chunk => text += chunk); response.on("end", () => resolve({ status: response.statusCode, headers: response.headers, text })); }); request.on("error", reject); if (body !== undefined) request.write(body); request.end(); });
    return { server, call };
}

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
    assert.deepEqual(workspaceNextActions(bound, true, false), { primary: "詳細分析と改善案を作る", secondary: "Local Monitor を開く", improve: true });
    assert.deepEqual(workspaceNextActions(bound, true, true), { primary: "改善案を確認", secondary: "Local Monitor を開く", improve: true });
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

test("Improve shows an honest unavailable state for an unbound session and contains no mutation controls", () => {
    const html = renderWorkspaceHtml({
        monitorUrl: "http://127.0.0.1:4320",
        healthState: "ready",
        token: "synthetic-token",
        nativeSessionId: "native-session",
    });

    assert.match(html, /改善案を作成/);
    assert.match(html, /native binding/);
    assert.match(html, /improvement-proposals/);
    assert.match(html, /textContent/);
    for (const forbidden of ["git", "raw analysis", "sendAndWait"]) {
        assert.doesNotMatch(html, new RegExp(forbidden, "i"));
    }
});

test("Improve limits candidate controls to terminal native-bound sessions and uses safe evidence controls", () => {
    const html = renderWorkspaceHtml({ monitorUrl: "http://127.0.0.1:4320", healthState: "ready", token: "synthetic-token" });

    assert.match(html, /session.status!=="completed"&&session.status!=="failed"/);
    assert.match(html, /終了状態が未確定のため、改善案は作成できません/);
    assert.match(html, /Evidence を開く/);
    assert.match(html, /証拠参照は利用不可/);
    assert.match(html, /selectedTab="evidence"/);
    assert.doesNotMatch(html, /innerHTML/);
});

test("final Improve payload includes each selected session with a resolving sanitized reference and enforces ten-source limit", () => {
    const primary = { session: bound, evidence_refs: [{ kind: "event", reference_id: "event-primary" }] };
    const secondary = { session: { ...bound, session_id: "22222222-2222-7222-8222-222222222222" }, evidence_refs: [{ kind: "gate", reference_id: "terminal" }] };
    const runtime = workspaceRuntimeHelpers();
    const payload = runtime.workspaceCandidatePayload(primary, [secondary], { target_kind: "skill", title: "title" });

    assert.deepEqual(Array.from(payload.source_sessions), [bound.session_id, secondary.session.session_id]);
    assert.deepEqual(JSON.parse(JSON.stringify(payload.evidence_refs)), [{ kind: "event", reference_id: "event-primary" }, { kind: "gate", reference_id: "terminal" }]);
    assert.equal(runtime.workspaceCandidatePayload(primary, Array.from({ length: 10 }, () => secondary), {}), null);
    const html = renderWorkspaceHtml({ monitorUrl: "http://127.0.0.1:4320", healthState: "ready", token: "synthetic-token" });
    assert.match(html, /workspaceCandidatePayload\(primary,others/);
    assert.match(html, /secondary\.length>9/);
});

test("final emitted Improve helper preserves the evidence reference selected by the user", () => {
    const runtime = workspaceRuntimeHelpers();
    const primary = { session: bound, evidence_refs: [{ kind: "trace", reference_id: "trace-user-selected" }] };
    const payload = runtime.workspaceCandidatePayload(primary, [], { target_kind: "skill" });

    assert.deepEqual(JSON.parse(JSON.stringify(payload.evidence_refs)), [{ kind: "trace", reference_id: "trace-user-selected" }]);
    const html = renderWorkspaceHtml({ monitorUrl: "http://127.0.0.1:4320", healthState: "ready", token: "synthetic-token" });
    const script = html.match(/<script>([\s\S]*)<\/script>/)?.[1];
    assert.match(script, /analysisLink\.href="\/analysis\?t="\+encodeURIComponent\(token\)/);
    assert.match(script, /証拠参照を選択してください/);
});

test("final proposal reference resolver opens sanitized Evidence candidates for event run trace and gates", () => {
    const detail = { events: [{ event_id: "event", type: "Stop" }, { event_id: "error", status: "error" }], runs: [{ run_id: "run", trace_id: "trace" }] };
    const runtime = workspaceRuntimeHelpers();
    for (const [reference, expected] of [[{ kind: "event", reference_id: "event" }, "event"], [{ kind: "run", reference_id: "run" }, "run"], [{ kind: "trace", reference_id: "trace" }, "trace"], [{ kind: "gate", reference_id: "terminal" }, "event"], [{ kind: "gate", reference_id: "error" }, "event"]]) {
        assert.equal(runtime.workspaceProposalReference(reference, detail).kind, expected);
    }
    assert.equal(runtime.workspaceProposalReference({ kind: "gate", reference_id: "error" }, { events: [], runs: [] }).value, null);
    const html = renderWorkspaceHtml({ monitorUrl: "http://127.0.0.1:4320", healthState: "ready", token: "synthetic-token" });
    assert.match(html, /workspaceProposalReference\(reference,selectedDetail\)/);
    assert.match(html, /selectedTab="evidence"/);
});

test("proposal apply helpers use the Local Monitor numeric revision and top-level hunks", () => {
    const files = [{ relative_path: "skills/example.md", replacement_text: "complete replacement" }];
    assert.deepEqual(workspaceApplyDraftPayload("proposal-id", "root-id", files), { proposal_id: "proposal-id", root_id: "root-id", files });
    assert.equal(workspaceApplyDraftPayload("proposal-id", "root-id", []), null);
    assert.deepEqual(workspaceApplySelectionPayload(3, ["hunk-a", "hunk-b"]), { selection_revision: 3, selected_hunk_ids: ["hunk-a", "hunk-b"] });
    assert.deepEqual(workspaceApplyApprovalPayload(4, "digest"), { selection_revision: 4, approval_digest: "digest" });
    assert.equal(workspaceApplyRequest(), undefined);
    assert.deepEqual(workspaceApplyDraftReview({ files: [{ relative_path: "one.txt" }], hunks: [{ hunk_id: "hunk-a", relative_path: "one.txt", selected: true }] }), [{ relative_path: "one.txt", hunks: [{ hunk_id: "hunk-a", relative_path: "one.txt", selected: true }] }]);
    assert.deepEqual(workspaceApplyConfirmation({ selection_revision: 4, approval_digest: "digest", files: [{ base_sha256: "base" }] }), { selection_revision: 4, approval_digest: "digest", file_count: 1 });
    assert.deepEqual(workspaceApplyView({ state: "approved", selection_revision: 4, approval_digest: "digest" }), {
        state: "approved", selection_revision: 4, approval_digest: "digest", root: null, canApply: true, canRollback: false,
    });
});

test("proposal apply helper server token-gates canonical routes and forwards empty mutation bodies", async () => {
    const calls = [];
    const helper = await proposalApplyHelperServer(calls);
    try {
        const denied = await helper.call("GET", "/api/session-workspace/proposal-applies/roots");
        assert.equal(denied.status, 401);
        assert.equal(denied.headers["cache-control"], "no-store");
        const id = "0197d7c0-0000-7000-8000-000000000001";
        for (const [method, path, body] of [["GET", "/api/session-workspace/proposal-applies/roots"], ["POST", "/api/session-workspace/proposal-applies/drafts", "{}"], ["GET", `/api/session-workspace/proposal-applies/drafts/${id}`], ["PUT", `/api/session-workspace/proposal-applies/drafts/${id}/selection`, "{}"], ["POST", `/api/session-workspace/proposal-applies/drafts/${id}/approve`, "{}"], ["POST", `/api/session-workspace/proposal-applies/drafts/${id}/apply`, "ignored"], ["POST", `/api/session-workspace/proposal-applies/${id}/rollback`, "ignored"]]) {
            const result = await helper.call(method, `${path}?t=token`, body === undefined ? { authorization: "Bearer ignored" } : { "content-type": "application/json", authorization: "Bearer ignored", "x-monitor-csrf": "browser-value" }, body);
            assert.equal(result.status, 200, `${method} ${path}`);
            assert.equal(result.headers["cache-control"], "no-store");
        }
        const mutationCalls = calls.filter(call => call.target.endsWith("/apply") || call.target.endsWith("/rollback"));
        assert.equal(mutationCalls.length, 2);
        for (const call of mutationCalls) { assert.equal(call.init.body, undefined); assert.equal(call.init.headers["x-monitor-csrf"], "local-monitor"); assert.equal(Object.keys(call.init.headers).length, 1); }
        const malformed = await helper.call("POST", "/api/session-workspace/proposal-applies/drafts/%E0%A4%A/apply?t=token");
        assert.equal(malformed.status, 400);
        assert.equal(malformed.headers["cache-control"], "no-store");
    } finally { await new Promise(resolve => helper.server.close(resolve)); }
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
