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

async function proposalApplyHelperServer(fetchCalls, { monitorUrl = "http://127.0.0.1:4320", fetchResponse } = {}) {
    const source = await readFile(new URL("./extension.mjs", import.meta.url), "utf8");
    const boundary = source.indexOf("// --------------- canvas ---------------");
    const executable = source.slice(0, boundary).replace(/^import[\s\S]*?;\r?\n/gm, "") + "\nglobalThis.createHelperServer = createHelperServer;";
    const context = vm.createContext({
        createServer, URL, Buffer, AbortController, setTimeout, clearTimeout,
        fetch: async (target, init) => { fetchCalls.push({ target: String(target), init }); return fetchResponse ? fetchResponse(String(target), init) : new Response(JSON.stringify(target.includes("roots") ? { items: [] } : target.includes("apply") || target.includes("rollback") ? { apply_id: "0197d7c0-0000-7000-8000-000000000001", state: "applied" } : { draft_id: "0197d7c0-0000-7000-8000-000000000001", proposal_id: "0197d7c0-0000-7000-8000-000000000002", root_id: "0197d7c0-0000-7000-8000-000000000003", selection_revision: 1, approval_digest: "digest", state: "draft", files: [], hunks: [] }), { status: 200, headers: { "content-type": "application/json" } }); },
        Response, TextEncoder, console,
        renderWorkspaceHtml: () => "", renderHelperHtml: () => "", handleEvidenceProxy: () => {}, CanvasError: class CanvasError extends Error {},
    });
    new vm.Script(executable).runInContext(context);
    const server = context.createHelperServer({ instanceId: "i", monitorUrl, healthState: "ready", statusCode: 200, healthBody: "", error: null, token: "token", session: {}, extensionScope: "", nativeSessionId: "" });
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    const port = server.address().port;
    const call = (method, path, headers = {}, body) => new Promise((resolve, reject) => { const request = httpRequest({ port, host: "127.0.0.1", method, path, headers }, response => { let text = ""; response.on("data", chunk => text += chunk); response.on("end", () => resolve({ status: response.statusCode, headers: response.headers, text })); }); request.on("error", reject); if (body !== undefined) request.write(body); request.end(); });
    return { server, call };
}

class DomElement {
    constructor(tagName, ownerDocument) { this.tagName = tagName.toLowerCase(); this.ownerDocument = ownerDocument; this.children = []; this.parentNode = null; this.listeners = new Map(); this.attributes = new Map(); this.dataset = {}; this.className = ""; this.value = ""; this.checked = false; this.disabled = false; this.hidden = false; }
    append(...nodes) { for (const node of nodes) { if (node === null || node === undefined) continue; const child = typeof node === "string" ? this.ownerDocument.createTextNode(node) : node; child.parentNode = this; this.children.push(child); if (this.tagName === "select" && child.tagName === "option" && !this.value) this.value = child.value; } }
    appendChild(node) { this.append(node); return node; }
    insertBefore(node, before) { const index = this.children.indexOf(before); if (index < 0) return this.append(node); node.parentNode = this; this.children.splice(index, 0, node); return node; }
    set textContent(value) { this.children = [this.ownerDocument.createTextNode(String(value))]; }
    get textContent() { return this.children.map(child => child.textContent).join(""); }
    setAttribute(name, value) { const text = String(value); this.attributes.set(name, text); if (name === "id") this.ownerDocument.ids.set(text, this); if (name === "class") this.className = text; if (name.startsWith("data-")) this.dataset[name.slice(5).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = text; }
    getAttribute(name) { return this.attributes.get(name) ?? null; }
    removeAttribute(name) { this.attributes.delete(name); }
    addEventListener(type, listener) { const items = this.listeners.get(type) ?? []; items.push(listener); this.listeners.set(type, items); }
    async dispatch(type) { for (const listener of this.listeners.get(type) ?? []) await listener({ preventDefault() {} }); }
    async click() { await this.dispatch("click"); }
    matches(selector) {
        if (selector === "input" || selector === "textarea" || selector === "button" || selector === "form") return this.tagName === selector;
        if (selector.startsWith(".")) return this.className.split(/\s+/).includes(selector.slice(1));
        const typed = /^(\w+)\[([^=]+)=([^\]]+)\]$/.exec(selector);
        if (typed) return this.tagName === typed[1] && this[typed[2]] === typed[3].replace(/["']/g, "");
        return this.tagName === selector;
    }
    querySelectorAll(selector) { const found = []; const visit = node => { for (const child of node.children ?? []) { if (child.matches?.(selector)) found.push(child); visit(child); } }; visit(this); return found; }
    querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
}

class DomDocument {
    constructor() { this.ids = new Map(); this.body = new DomElement("body", this); }
    createElement(tag) { return new DomElement(tag, this); }
    createTextNode(value) { return { textContent: String(value), parentNode: null }; }
    getElementById(id) { return this.ids.get(id) ?? null; }
    querySelectorAll(selector) { return this.body.querySelectorAll(selector); }
}

function workspaceDom() {
    const document = new DomDocument();
    for (const id of ["current-group", "current-sessions", "recent-sessions", "unbound-sessions", "workspace-panel"]) { const element = document.createElement("div"); element.setAttribute("id", id); document.body.append(element); }
    for (const tab of ["review", "evidence", "improve", "compare"]) { const element = document.createElement("button"); element.className = "tab"; element.dataset.tab = tab; element.textContent = tab[0].toUpperCase() + tab.slice(1); document.body.append(element); }
    return document;
}

function response(payload, status = 200) { return { status, json: async () => payload }; }

async function runWorkspaceIife(fetch) {
    const html = renderWorkspaceHtml({ monitorUrl: "http://127.0.0.1:4320", healthState: "ready", token: "synthetic-token", nativeSessionId: "native" });
    const script = html.match(/<script>([\s\S]*)<\/script>/)?.[1];
    const document = workspaceDom();
    const context = vm.createContext({ document, fetch, Set, Map, Date, Number, BigInt, URL, encodeURIComponent, console, Promise, setTimeout, clearTimeout });
    new vm.Script(script).runInContext(context);
    await new Promise(resolve => setImmediate(resolve));
    await new Promise(resolve => setImmediate(resolve));
    return { document, context };
}

function textIn(element) { return element.textContent; }

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

test("proposal apply helper server rejects every unsafe boundary and sanitizes route-specific upstream responses", async () => {
    const calls = [], id = "0197d7c0-0000-7000-8000-000000000001";
    const helper = await proposalApplyHelperServer(calls, { fetchResponse: target => {
        const path = new URL(target).pathname;
        if (path.endsWith("/roots")) return new Response(JSON.stringify({ items: [{ root_id: "root", kind: "repository", label: "Repo", path: "secret" }] }), { status: 200 });
        if (path.endsWith("/selection") || path.endsWith("/approve")) return new Response(JSON.stringify({ draft_id: id, proposal_id: "proposal", root_id: "root", selection_revision: 2, approval_digest: "digest", state: "selected", path: "secret", source: "secret", diff: "secret", files: [{ base_sha256: "b", path: "secret" }], hunks: [{ hunk_id: "h", selected: true, diff: "secret" }] }), { status: 200 });
        if (path.endsWith("/apply") || path.endsWith("/rollback")) return new Response(JSON.stringify({ apply_id: "apply", state: "applied", path: "secret" }), { status: 200 });
        return new Response(JSON.stringify({ draft_id: id, full_diff: "helper-only", files: [], hunks: [] }), { status: 200 });
    } });
    try {
        const valid = path => `${path}?t=token`;
        const reject = async (method, path, headers, body, status) => { const result = await helper.call(method, path, headers, body); assert.equal(result.status, status); assert.equal(result.headers["cache-control"], "no-store"); return result; };
        await reject("GET", "/api/session-workspace/proposal-applies/roots?t=wrong", { "x-canvas-token": "token" }, undefined, 401);
        await reject("GET", valid("/api/session-workspace/proposal-applies/roots"), { "x-canvas-token": "wrong" }, undefined, 401);
        assert.equal(calls.length, 0, "token rejection never reaches upstream");
        for (const [method, path] of [["POST", "/api/session-workspace/proposal-applies/roots"], ["GET", "/api/session-workspace/proposal-applies/drafts"], ["DELETE", `/api/session-workspace/proposal-applies/drafts/${id}`], ["PUT", `/api/session-workspace/proposal-applies/${id}/rollback`]]) await reject(method, valid(path), { "x-canvas-token": "token" }, undefined, 404);
        await reject("GET", valid("/api/session-workspace/proposal-applies/drafts/%E0%A4%A"), { "x-canvas-token": "token" }, undefined, 400);
        for (const contentType of [undefined, "text/plain"]) await reject("POST", valid("/api/session-workspace/proposal-applies/drafts"), { "x-canvas-token": "token", ...(contentType ? { "content-type": contentType } : {}) }, "{}", 415);
        await reject("POST", valid("/api/session-workspace/proposal-applies/drafts"), { "x-canvas-token": "token", "content-type": "application/json" }, "{", 400);
        await reject("POST", valid("/api/session-workspace/proposal-applies/drafts"), { "x-canvas-token": "token", "content-type": "application/json", "content-length": "1048577" }, "x".repeat(1048577), 413);
        await reject("POST", valid(`/api/session-workspace/proposal-applies/${id}/rollback`), { "x-canvas-token": "token", "content-length": "1048577" }, "x".repeat(1048577), 413);
        const before = calls.length;
        const selected = await helper.call("PUT", valid(`/api/session-workspace/proposal-applies/drafts/${id}/selection`), { "x-canvas-token": "token", "content-type": "application/json", authorization: "Bearer browser", "x-monitor-csrf": "browser" }, "{}");
        assert.equal(selected.status, 200); assert.doesNotMatch(selected.text, /secret/);
        const apply = await helper.call("POST", valid(`/api/session-workspace/proposal-applies/drafts/${id}/apply`), { "x-canvas-token": "token", authorization: "Bearer browser", "x-monitor-csrf": "browser" }, "discarded");
        assert.equal(apply.status, 200);
        const forwarded = calls.slice(before);
        assert.equal(forwarded[0].init.headers.authorization, undefined); assert.equal(forwarded[0].init.headers["x-monitor-csrf"], "local-monitor");
        assert.equal(forwarded[1].init.body, undefined); assert.deepEqual(Object.keys(forwarded[1].init.headers), ["x-monitor-csrf"]);
    } finally { await new Promise(resolve => helper.server.close(resolve)); }
    const remoteCalls = [], remote = await proposalApplyHelperServer(remoteCalls, { monitorUrl: "http://example.test:4320" });
    try { const result = await remote.call("GET", "/api/session-workspace/proposal-applies/roots?t=token", { "x-canvas-token": "token" }); assert.equal(result.status, 400); assert.equal(result.headers["cache-control"], "no-store"); assert.equal(remoteCalls.length, 0); } finally { await new Promise(resolve => remote.server.close(resolve)); }
});

test("emitted workspace IIFE requires explicit approval, sends zero-byte apply, and gives rollback failures terminal precedence", async () => {
    const calls = [];
    const id = "0197d7c0-0000-7000-8000-000000000001";
    const proposal = { proposal_id: "proposal-1", status: "recommended", title: "proposal", evidence_refs: [{ kind: "event", reference_id: "stop" }] };
    const draft = { draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 1, approval_digest: "digest-1", state: "draft", diff: "<script>inert</script>\n@@ -1 +1 @@", files: [{ relative_path: "safe.txt", base_sha256: "base" }], hunks: [{ hunk_id: "hunk-a", relative_path: "safe.txt", selected: true }, { hunk_id: "hunk-b", relative_path: "safe.txt", selected: true }] };
    const selected = { draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 2, approval_digest: "digest-2", state: "selected", files: [{ base_sha256: "base" }], hunks: [{ hunk_id: "hunk-a", selected: true }] };
    const detail = { session: bound, native_ids: [{ binding_kind: "native" }], events: [{ event_id: "stop", type: "Stop" }], runs: [] };
    const fetch = async (path, init = {}) => {
        calls.push({ path: String(path), init });
        const route = String(path).split("?")[0];
        if (route === "/api/session-workspace/sessions") return response({ items: [bound] });
        if (route.startsWith("/api/session-workspace/resolve")) return response({ binding_status: "bound", session_id: bound.session_id });
        if (route === `/api/session-workspace/sessions/${bound.session_id}`) return response(detail);
        if (route.startsWith("/api/session-instruction/")) return response({ state: "no_instruction" });
        if (route.startsWith("/api/session-workspace/improvement-proposals")) return response({ items: [proposal] });
        if (route.endsWith("/proposal-applies/roots")) return response({ items: [{ root_id: "root-1", kind: "repository", label: "Repository" }] });
        if (route.endsWith("/proposal-applies/drafts") && init.method === "POST") return response(draft, 201);
        if (route.endsWith("/selection")) return response(selected);
        if (route.endsWith("/approve")) return response({ ...selected, state: "approved" });
        if (route.endsWith("/apply")) return response({ apply_id: "apply-1", state: "applied" });
        if (route.endsWith("/rollback")) return response({ error: "rollback_stale" }, 409);
        return response({ error: "unexpected" }, 404);
    };
    const { document } = await runWorkspaceIife(fetch);
    const button = text => document.querySelectorAll("button").find(item => item.textContent === text);
    const settle = async () => { await new Promise(resolve => setImmediate(resolve)); await new Promise(resolve => setImmediate(resolve)); };
    await button("Improve").click();
    await settle();
    await button("Apply locally").click();
    const pathInput = document.querySelectorAll("input").find(item => item.placeholder === "relative path");
    const replacement = document.querySelectorAll("textarea").find(item => item.placeholder === "complete replacement text");
    pathInput.value = "safe.txt";
    replacement.value = "complete replacement";
    let draftForm = pathInput;
    while (draftForm.tagName !== "form") draftForm = draftForm.parentNode;
    await draftForm.dispatch("submit");
    await settle();
    assert.match(textIn(document.getElementById("workspace-panel")), /<script>inert<\/script>/, "diff is rendered as text, never parsed markup");
    const fileToggle = document.querySelectorAll("input").find(item => item.type === "checkbox");
    fileToggle.checked = false;
    await fileToggle.dispatch("change");
    assert.ok(document.querySelectorAll("input").filter(item => item.type === "checkbox").every(item => !item.checked));
    const hunk = document.querySelectorAll("input").filter(item => item.type === "checkbox")[2];
    hunk.checked = true;
    await button("選択を更新").click();
    await settle();
    assert.equal(button("適用する"), undefined, "selection invalidates approval and cannot mutate early");
    await button("承認する").click();
    await settle();
    const apply = button("適用する");
    assert.ok(apply);
    await apply.click();
    await settle();
    const applyCall = calls.find(call => call.path.split("?")[0].endsWith("/apply"));
    assert.equal(applyCall.init.body, undefined, "apply has no editable browser body");
    await button("ロールバック").click();
    await settle();
    const panel = textIn(document.getElementById("workspace-panel"));
    assert.match(panel, /rollback_stale/);
    assert.doesNotMatch(panel, /状態: applied/);
    assert.equal(button("ロールバック"), undefined, "a failed rollback remains permanently disabled");
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
