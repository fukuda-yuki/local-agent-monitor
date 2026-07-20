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
    sourceDiagnosticView,
    workspaceContentState,
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
        fetch: async (target, init) => { fetchCalls.push({ target: String(target), init }); if (fetchResponse) return fetchResponse(String(target), init); const path = new URL(String(target)).pathname; const value = path.endsWith("/roots") ? { items: [] } : path.endsWith("/apply") || path.endsWith("/rollback") ? { apply_id: "0197d7c0-0000-7000-8000-000000000001", state: "applied" } : path.endsWith("/selection") || path.endsWith("/approve") ? { draft_id: "0197d7c0-0000-7000-8000-000000000001", proposal_id: "0197d7c0-0000-7000-8000-000000000002", root_id: "0197d7c0-0000-7000-8000-000000000003", selection_revision: 1, approval_digest: "digest", state: "selected", files: [], hunks: [] } : { draft_id: "0197d7c0-0000-7000-8000-000000000001", proposal_id: "0197d7c0-0000-7000-8000-000000000002", root_id: "0197d7c0-0000-7000-8000-000000000003", selection_revision: 1, approval_digest: "digest", state: "draft", files: [], hunks: [] }; return new Response(JSON.stringify(value), { status: 200, headers: { "content-type": "application/json" } }); },
        Response, TextEncoder, console,
        renderWorkspaceHtml: () => "", renderHelperHtml: () => "", handleEvidenceProxy: () => {}, CanvasError: class CanvasError extends Error {},
    });
    new vm.Script(executable).runInContext(context);
    const server = context.createHelperServer({ instanceId: "i", monitorUrl, healthState: "ready", statusCode: 200, healthBody: "", error: null, token: "token", session: {}, extensionScope: "", nativeSessionId: "" });
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    const port = server.address().port;
    const call = (method, path, headers = {}, body) => new Promise((resolve, reject) => { const request = httpRequest({ port, host: "127.0.0.1", method, path, headers }, response => { let text = ""; response.on("data", chunk => text += chunk); response.on("end", () => resolve({ status: response.statusCode, headers: response.headers, text })); }); request.on("error", reject); if (Array.isArray(body)) body.forEach(chunk => request.write(chunk)); else if (body !== undefined) request.write(body); request.end(); });
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

test("Claude session presentation preserves the backend source diagnostic contract and degraded next action", () => {
    const diagnostic = {
        source_surface: "claude-code",
        source_application_version: "3.4.5-synthetic",
        source_adapter: "claude-code-otel",
        adapter_version: "adapter-v1",
        schema_fingerprint: "a".repeat(64),
        compatibility_state: "supported_with_unknown_fields",
        reason_codes: ["unknown_fields_observed"],
        next_action: "review_unknown_fields",
        unknown_field: "must-not-escape",
    };
    const session = {
        ...bound,
        source_surfaces: ["claude-code"],
        source_diagnostic: diagnostic,
        binding_state: "hook_only",
        completeness: "partial",
        completeness_reason_codes: ["hook_only", "missing_trace_context"],
        content_state: null,
    };

    assert.equal(workspaceSessionLabel(session, { state: "available", preview: "prompt must not replace Claude" }), "Claude Code セッション");
    assert.deepEqual(sourceDiagnosticView(diagnostic), {
        source_surface: "claude-code",
        source_application_version: "3.4.5-synthetic",
        source_adapter: "claude-code-otel",
        adapter_version: "adapter-v1",
        schema_fingerprint: "a".repeat(64),
        compatibility_state: "supported_with_unknown_fields",
        reason_codes: ["unknown_fields_observed"],
        next_action: "review_unknown_fields",
    });
    assert.deepEqual(workspaceContentState(session), { state: null, label: "一致するコンテンツ状態なし" });
    assert.deepEqual(workspaceNextActions(session, false), {
        primary: "診断を確認",
        secondary: "Local Monitor を開く",
        analysis: false,
        next_action: "review_unknown_fields",
    });
});

test("Claude content presentation distinguishes an agreed capture state from disagreement without adding fetches", () => {
    assert.deepEqual(workspaceContentState({ content_state: "available" }), { state: "available", label: "available" });
    assert.deepEqual(workspaceContentState({ content_state: "not_captured" }), { state: "not_captured", label: "not_captured" });
    assert.deepEqual(workspaceContentState({ content_state: null }), { state: null, label: "一致するコンテンツ状態なし" });
    const html = renderWorkspaceHtml({ monitorUrl: "http://127.0.0.1:4320", healthState: "ready", token: "synthetic-token" });
    assert.match(html, /source_diagnostic/);
    assert.match(html, /binding_state/);
    assert.match(html, /completeness_reason_codes/);
    assert.match(html, /content_state/);
    assert.doesNotMatch(html, /\/api\/claude/);
    assert.doesNotMatch(html, /\/api\/source-diagnostics/);
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
    assert.ok(html.includes("const surface=Array.isArray(session.source_surfaces)?session.source_surfaces[0]:null;if(surface===\"claude-code\")return \"Claude Code セッション\";if(instruction&&instruction.state===\"available\""));
    assert.match(html, /人間評価を保存できませんでした。接続を確認して再試行してください。/);
    assert.match(html, /\.catch\(showEvaluationError\)/);
    assert.doesNotMatch(html, /innerHTML/);
});

test("Session workspace offers an exact navigation-only Local Monitor retention link", () => {
    const html = renderWorkspaceHtml({
        monitorUrl: "http://127.0.0.1:4320",
        healthState: "ready",
        token: "synthetic-token",
    });

    assert.match(html, /data-session-action/);
    assert.match(html, /Manage retention/);
    assert.match(html, /monitorUrl\+"\/retention\/session\/"\+encodeURIComponent\(session\.session_id\)/);
    assert.match(html, /rel="noopener"/);
    assert.doesNotMatch(html, /\/api\/retention/);
    assert.doesNotMatch(html, /retention\/session\/[^"\n]*token/);
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
        for (const [method, path] of [["POST", "/api/session-workspace/proposal-applies/roots"], ["GET", "/api/session-workspace/proposal-applies/drafts"], ["DELETE", `/api/session-workspace/proposal-applies/drafts/${id}`], ["GET", `/api/session-workspace/proposal-applies/drafts/${id}/selection`], ["POST", `/api/session-workspace/proposal-applies/drafts/${id}/selection`], ["GET", `/api/session-workspace/proposal-applies/drafts/${id}/approve`], ["PUT", `/api/session-workspace/proposal-applies/drafts/${id}/approve`], ["GET", `/api/session-workspace/proposal-applies/drafts/${id}/apply`], ["PUT", `/api/session-workspace/proposal-applies/drafts/${id}/apply`], ["PUT", `/api/session-workspace/proposal-applies/${id}/rollback`]]) await reject(method, valid(path), { "x-canvas-token": "token" }, undefined, 404);
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

test("proposal apply helper server streams size limits and returns every canonical route's bounded shape", async () => {
    const id = "0197d7c0-0000-7000-8000-000000000001", calls = [];
    const helper = await proposalApplyHelperServer(calls, { fetchResponse: target => {
        const path = new URL(target).pathname, secret = { path: "secret-path", source: "secret-source", diff: "secret-diff" };
        if (path.endsWith("/roots")) return new Response(JSON.stringify({ items: [{ root_id: "root", kind: "repository", label: "Repo", ...secret }] }));
        if (path.endsWith("/drafts")) return new Response(JSON.stringify({ draft_id: id, full_diff: "helper-only", files: [], hunks: [], path: secret.path, source: secret.source }));
        if (/\/drafts\/[^/]+$/.test(path)) return new Response(JSON.stringify({ draft_id: id, full_diff: "helper-only", files: [], hunks: [], path: secret.path, source: secret.source }));
        if (path.endsWith("/selection") || path.endsWith("/approve")) return new Response(JSON.stringify({ draft_id: id, proposal_id: "proposal", root_id: "root", selection_revision: 3, approval_digest: "digest", state: "approved", files: [{ base_sha256: "base", ...secret }], hunks: [{ hunk_id: "h", selected: true, ...secret }], ...secret }));
        if (path.endsWith("/apply")) return new Response(JSON.stringify({ apply_id: "apply", state: "applied", ...secret }));
        if (path.endsWith("/rollback")) return new Response(JSON.stringify({ apply_id: "apply", state: "rolled_back", ...secret }));
        return new Response(JSON.stringify({ error: "unknown", ...secret }), { status: 500 });
    } });
    try {
        const valid = path => `${path}?t=token`, json = { "x-canvas-token": "token", "content-type": "application/json", authorization: "Bearer browser", "x-monitor-csrf": "browser" };
        const huge = ["x".repeat(524289), "x".repeat(524289)];
        for (const [method, path, headers] of [["POST", "/api/session-workspace/proposal-applies/drafts", json], ["POST", `/api/session-workspace/proposal-applies/${id}/rollback`, { "x-canvas-token": "token" }]]) { const result = await helper.call(method, valid(path), headers, huge); assert.equal(result.status, 413); assert.equal(result.headers["cache-control"], "no-store"); }
        const routes = [["GET", "/api/session-workspace/proposal-applies/roots", undefined, "items"], ["POST", "/api/session-workspace/proposal-applies/drafts", "{}", "draft_id"], ["GET", `/api/session-workspace/proposal-applies/drafts/${id}`, undefined, "full_diff"], ["PUT", `/api/session-workspace/proposal-applies/drafts/${id}/selection`, "{}", "selection_revision"], ["POST", `/api/session-workspace/proposal-applies/drafts/${id}/approve`, "{}", "approval_digest"], ["POST", `/api/session-workspace/proposal-applies/drafts/${id}/apply`, "discarded", "apply_id"], ["POST", `/api/session-workspace/proposal-applies/${id}/rollback`, "discarded", "apply_id"]];
        for (const [method, path, body, key] of routes) { const result = await helper.call(method, valid(path), body === undefined || path.endsWith("/apply") || path.endsWith("/rollback") ? { "x-canvas-token": "token", authorization: "Bearer browser", "x-monitor-csrf": "browser" } : json, body); assert.equal(result.status, 200, `${method} ${path}`); assert.ok(Object.hasOwn(JSON.parse(result.text), key)); assert.doesNotMatch(result.text, /secret-/); }
        const forwarded = calls.filter(call => call.init?.method);
        for (const suffix of ["/drafts", "/approve", "/rollback"]) { const call = forwarded.find(item => new URL(item.target).pathname.endsWith(suffix)); assert.equal(call.init.headers.authorization, undefined); assert.equal(call.init.headers["x-monitor-csrf"], "local-monitor"); }
        const original = calls.length;
        const unknown = await proposalApplyHelperServer([], { fetchResponse: () => new Response(JSON.stringify({ error: "unexpected", path: "secret", source: "secret", diff: "secret" }), { status: 500 }) });
        try { const result = await unknown.call("GET", valid("/api/session-workspace/proposal-applies/roots"), { "x-canvas-token": "token" }); assert.equal(result.status, 500); assert.equal(result.text, '{"error":"monitor_unavailable"}'); } finally { await new Promise(resolve => unknown.server.close(resolve)); }
        assert.ok(calls.length >= original);
    } finally { await new Promise(resolve => helper.server.close(resolve)); }
});

test("effect comparison helper proxy accepts only the six token-gated canonical routes", async () => {
    const calls = [], proposalId = "0197d7c0-0000-7000-8000-000000000001", applyId = "0197d7c0-0000-7000-8000-000000000002", comparisonId = "0197d7c0-0000-7000-8000-000000000003";
    const sessionId = "11111111-1111-7111-8111-111111111111", runId = "22222222-2222-7222-8222-222222222222";
    const objective = JSON.stringify({ session_id: sessionId, run_id: runId, trace_id: "trace-1", result: "pass", severity: "normal", evaluator_id: "eval", evaluator_version: "v1", criterion_id: "quality", case_key: "case-a", evidence_refs: [{ kind: "run", reference_id: runId }] });
    const comparison = JSON.stringify({ proposal_id: proposalId, proposal_revision: 2, apply_id: applyId, sessions: [{ session_id: sessionId, classification: "pre", case_key: "case-a", exclusion_reason: null }] });
    const helper = await proposalApplyHelperServer(calls, { fetchResponse: () => new Response(JSON.stringify({ comparison_id: comparisonId, path: "secret-path", source: "secret-source", diff: "secret-diff", hash: "secret-hash", log: "secret-log" }), { status: 200 }) });
    try {
        const routes = [
            ["POST", "/api/session-workspace/objective-evaluations", objective],
            ["GET", `/api/session-workspace/objective-evaluations?session_id=${proposalId}`],
            ["GET", `/api/session-workspace/proposal-applies/receipts?proposal_id=${proposalId}`],
            ["GET", `/api/session-workspace/effect-comparisons/candidates?proposal_id=${proposalId}&apply_id=${applyId}`],
            ["POST", "/api/session-workspace/effect-comparisons", comparison],
            ["GET", `/api/session-workspace/effect-comparisons/${comparisonId}`],
        ];
        for (const [method, path, body] of routes) {
            const result = await helper.call(method, `${path}${path.includes("?") ? "&" : "?"}t=token`, body === undefined ? { authorization: "Bearer browser" } : { "content-type": "application/json", authorization: "Bearer browser", "x-monitor-csrf": "browser" }, body);
            assert.equal(result.status, 200, `${method} ${path}`);
            assert.equal(result.headers["cache-control"], "no-store");
            assert.doesNotMatch(result.text, /secret-(path|source|diff|hash|log)/);
        }
        assert.equal(calls.length, 6);
        for (const call of calls) {
            assert.equal(call.init?.headers?.authorization, undefined);
            assert.equal(call.init?.headers?.["x-monitor-csrf"], call.init?.method === "POST" ? "local-monitor" : undefined);
        }
        const before = calls.length;
        for (const [method, path] of [["DELETE", "/api/session-workspace/effect-comparisons"], ["GET", "/api/session-workspace/effect-comparisons/candidates"], ["GET", `/api/session-workspace/effect-comparisons/${comparisonId}?extra=1`], ["GET", "/api/session-workspace/effect-comparisons/not-allowed"]]) {
            const result = await helper.call(method, `${path}${path.includes("?") ? "&" : "?"}t=token`, { "x-canvas-token": "token" });
            assert.ok([400, 404].includes(result.status), `${method} ${path}`);
            assert.equal(result.headers["cache-control"], "no-store");
        }
        assert.equal(calls.length, before, "denied routes never reach upstream");
    } finally { await new Promise(resolve => helper.server.close(resolve)); }
});

test("effect comparison helper projects stale and unavailable backend errors without echoing rejected details", async () => {
    const errors = ["application_not_active", "proposal_revision_stale", "cohort_not_confirmed", "comparison_evidence_stale", "objective_store_unavailable"];
    let index = 0;
    const helper = await proposalApplyHelperServer([], { fetchResponse: async () => new Response(JSON.stringify({ error: errors[index++], path: "C:\\secret\\source.diff", raw: "do-not-echo" }), { status: index === errors.length ? 503 : 400, headers: { "content-type": "application/json" } }) });
    const body = JSON.stringify({ proposal_id: "0197d7c0-0000-7000-8000-000000000001", proposal_revision: 2, apply_id: "0197d7c0-0000-7000-8000-000000000002", sessions: [{ session_id: "11111111-1111-7111-8111-111111111111", classification: "pre", case_key: "case-a", exclusion_reason: null }] });
    try {
        for (const error of errors) {
            const result = await helper.call("POST", "/api/session-workspace/effect-comparisons?t=token", { "content-type": "application/json", "x-canvas-token": "token" }, body);
            assert.equal(result.status, error === "objective_store_unavailable" ? 503 : 400);
            assert.equal(result.headers["cache-control"], "no-store");
            assert.equal(result.text, JSON.stringify({ error }));
            assert.doesNotMatch(result.text, /secret|source|raw/i);
        }
    } finally { await new Promise(resolve => helper.server.close(resolve)); }
});

test("effect comparison helper rejects unknown nested write fields before upstream dispatch", async () => {
    const calls = [], proposalId = "0197d7c0-0000-7000-8000-000000000001", applyId = "0197d7c0-0000-7000-8000-000000000002", sessionId = "11111111-1111-7111-8111-111111111111";
    const helper = await proposalApplyHelperServer(calls);
    const payload = { proposal_id: proposalId, proposal_revision: 2, apply_id: applyId, sessions: [{ session_id: sessionId, classification: "pre", case_key: "case-a", exclusion_reason: null, unapproved_field: "must-not-forward" }] };
    try {
        const result = await helper.call("POST", "/api/session-workspace/effect-comparisons?t=token", { "content-type": "application/json", "x-canvas-token": "token" }, JSON.stringify(payload));
        assert.equal(result.status, 400);
        assert.equal(result.text, '{"error":"invalid_comparison_request"}');
        assert.equal(calls.length, 0);
    } finally { await new Promise(resolve => helper.server.close(resolve)); }
});

test("Compare workspace is explicit, inert, and never confirms a candidate automatically", () => {
    const html = renderWorkspaceHtml({ monitorUrl: "http://127.0.0.1:4320", healthState: "ready", token: "synthetic-token" });
    for (const value of ["比較を確定", "not_comparable", "wrong_case", "missing_evidence", "overlaps_application", "user_excluded", "insufficient_evidence", "no_change", "improved", "regressed", "case_key", "evidence_refs", "rollback", "textContent"]) assert.match(html, new RegExp(value));
    assert.doesNotMatch(html, /Compare は後続 Issue/);
    assert.doesNotMatch(html, /innerHTML/);
    assert.doesNotMatch(html, /session\.send/);
});

test("emitted Compare IIFE waits for manual cohort confirmation and renders the reloaded historical detail inertly", async () => {
    const calls = [], proposalId = "0197d7c0-0000-7000-8000-000000000001", applyId = "0197d7c0-0000-7000-8000-000000000002", comparisonId = "0197d7c0-0000-7000-8000-000000000003";
    const candidates = [{ session_id: "11111111-1111-7111-8111-111111111111", suggestion_reasons: [] }, { session_id: "22222222-2222-7222-8222-222222222222", suggestion_reasons: ["not_exact_bound", "unapproved_reason"] }, { session_id: "33333333-3333-7333-8333-333333333333", suggestion_reasons: ["not_comparable"] }];
    const detail = { receipt: { comparison_id: comparisonId, verdict: "improved", verification_state: "invalidated" }, summary: { verdict: "improved", reasons: ["missing_evidence"], duration_delta: -0.2, token_delta: -0.1 }, evidence: [{ kind: "event", reference_id: "same-ref" }], case_key_groups: [{ case_key: "case-a", evidence: [{ kind: "event", reference_id: "same-ref" }] }] };
    const fetch = async (path, init = {}) => {
        calls.push({ path: String(path), init });
        const url = String(path);
        if (url.includes("/sessions?")) return response({ items: [bound] });
        if (url.includes("/resolve?")) return response({ binding_status: "bound", session_id: bound.session_id });
        if (url.includes("/sessions/")) return response({ session: bound, native_ids: [{ binding_kind: "native" }], events: [], runs: [] });
        if (url.includes("/session-instruction/")) return response({ state: "no_instruction" });
        if (url.includes("improvement-proposals?")) return response({ items: [{ proposal_id: proposalId, status: "recommended" }] });
        if (url.includes("proposal-applies/receipts")) return response({ items: [{ apply_id: applyId, state: "applied" }] });
        if (url.includes("effect-comparisons/candidates")) return response({ proposal_id: proposalId, proposal_revision: 2, apply_id: applyId, items: candidates });
        if (url.includes(`effect-comparisons/${comparisonId}`)) return response(detail);
        if (url.includes("effect-comparisons")) return response({ comparison_id: comparisonId }, 201);
        return response({});
    };
    const settle = async () => { for (let i = 0; i < 5; i++) await new Promise(resolve => setImmediate(resolve)); };
    const { document } = await runWorkspaceIife(fetch); await settle();
    const tab = document.querySelectorAll("button").find(item => item.dataset.tab === "improve"); await tab.click(); await settle();
    const compare = document.querySelectorAll("button").find(item => item.dataset.tab === "compare"); await compare.click(); await settle();
    assert.match(textIn(document.body), /適用記録を読み込む/);
    assert.equal(calls.filter(call => call.init.method === "POST" && call.path.includes("effect-comparisons")).length, 0);
    await document.querySelectorAll("button").find(item => item.textContent === "適用記録を読み込む").click(); await settle();
    await document.querySelectorAll("button").find(item => item.textContent.startsWith("適用記録 ·")).click(); await settle();
    assert.match(textIn(document.body), /証拠不足/);
    assert.match(textIn(document.body), /比較不可/);
    assert.equal(calls.filter(call => call.init.method === "POST" && call.path.includes("effect-comparisons")).length, 0);
    const selects = document.querySelectorAll("select"), inputs = document.querySelectorAll("input");
    selects[0].value = "pre"; inputs[0].value = "case-a";
    selects[2].value = "post"; inputs[1].value = "case-a";
    selects[4].value = "excluded"; selects[5].value = "user_excluded";
    await document.querySelectorAll("button").find(item => item.textContent === "比較を確定").click(); await settle();
    const post = calls.filter(call => call.init.method === "POST" && call.path.includes("effect-comparisons"));
    assert.equal(post.length, 1);
    assert.deepEqual(JSON.parse(post[0].init.body), { proposal_id: proposalId, proposal_revision: 2, apply_id: applyId, sessions: [{ session_id: candidates[0].session_id, classification: "pre", case_key: "case-a", exclusion_reason: null }, { session_id: candidates[1].session_id, classification: "post", case_key: "case-a", exclusion_reason: null }, { session_id: candidates[2].session_id, classification: "excluded", case_key: "", exclusion_reason: "user_excluded" }] });
    assert.equal(calls.filter(call => call.path.includes(`effect-comparisons/${comparisonId}`)).length, 1);
    assert.match(textIn(document.body), /改善/); assert.match(textIn(document.body), /現在の適用状態が無効のため無効化済み/); assert.doesNotMatch(textIn(document.body), /ロールバックにより/); assert.doesNotMatch(textIn(document.body), /有効な改善/);
    assert.equal((textIn(document.body).match(/same-ref/g) || []).length, 2);
});

test("emitted Compare IIFE crosses the real helper with an exact upstream contract and strips nested extras", async () => {
    const proposalId = "0197d7c0-0000-7000-8000-000000000001", applyId = "0197d7c0-0000-7000-8000-000000000002", comparisonId = "0197d7c0-0000-7000-8000-000000000003";
    const candidates = [
        { session_id: "11111111-1111-7111-8111-111111111111", status: "completed", completeness: "full", started_at: "2026-07-11T00:00:00+00:00", ended_at: "2026-07-11T00:01:00+00:00", exact_bound: true, evidence_available: true, boundary_eligibility: "pre", suggestion_reasons: [] },
        { session_id: "22222222-2222-7222-8222-222222222222", status: "completed", completeness: "full", started_at: "2026-07-11T00:02:00+00:00", ended_at: "2026-07-11T00:03:00+00:00", exact_bound: false, evidence_available: true, boundary_eligibility: "post", suggestion_reasons: ["not_exact_bound", "unapproved_reason"] },
        { session_id: "33333333-3333-7333-8333-333333333333", status: "failed", completeness: "partial", started_at: "2026-07-11T00:04:00+00:00", ended_at: "2026-07-11T00:05:00+00:00", exact_bound: true, evidence_available: false, boundary_eligibility: "not_eligible", suggestion_reasons: ["not_comparable", "missing_evidence", "overlaps_application"] },
    ];
    const upstream = [], helper = await proposalApplyHelperServer(upstream, { fetchResponse: (target, init = {}) => {
        const path = new URL(target).pathname;
        if (path.endsWith("/sessions")) return new Response(JSON.stringify({ items: [bound] }));
        if (path.endsWith("/resolve")) return new Response(JSON.stringify({ binding_status: "bound", session_id: bound.session_id }));
        if (path.endsWith(`/sessions/${bound.session_id}`)) return new Response(JSON.stringify({ session: bound, native_ids: [{ binding_kind: "native" }], events: [], runs: [] }));
        if (path.includes("session-instruction")) return new Response(JSON.stringify({ state: "no_instruction" }));
        if (path.includes("improvement-proposals")) return new Response(JSON.stringify({ items: [{ proposal_id: proposalId, status: "recommended" }] }));
        if (path.endsWith("/receipts")) return new Response(JSON.stringify({ items: [{ apply_id: applyId, state: "applied" }] }));
        if (path.endsWith("/candidates")) return new Response(JSON.stringify({ proposal_id: proposalId, apply_id: applyId, proposal_revision: 2, items: candidates }));
        if (path.endsWith(`/effect-comparisons/${comparisonId}`)) return new Response(JSON.stringify({ receipt: { comparison_id: comparisonId, verdict: "improved", verification_state: "invalidated", extra: "hidden" }, summary: { verdict: "improved", reasons: ["missing_evidence", "hidden"], nested: { unapproved: "hidden" } }, evidence: [{ kind: "event", reference_id: "same-ref", unapproved: "hidden" }], case_key_groups: [{ case_key: "case-a", sessions: [candidates[0].session_id], evidence: [{ kind: "event", reference_id: "same-ref", unapproved: "hidden" }], unapproved: "hidden" }] }));
        if (path.endsWith("/effect-comparisons")) {
            const value = JSON.parse(init.body);
            assert.deepEqual(value, { proposal_id: proposalId, proposal_revision: 2, apply_id: applyId, sessions: [{ session_id: candidates[0].session_id, classification: "pre", case_key: "case-a", exclusion_reason: null }, { session_id: candidates[1].session_id, classification: "post", case_key: "case-a", exclusion_reason: null }, { session_id: candidates[2].session_id, classification: "excluded", case_key: "", exclusion_reason: "user_excluded" }] });
            return new Response(JSON.stringify({ comparison_id: comparisonId }), { status: 201 });
        }
        return new Response(JSON.stringify({ error: "unexpected" }), { status: 404 });
    } });
    try {
        const fetch = async (path, init = {}) => {
            const route = String(path);
            if (route.includes("effect-comparisons")) { const clean = route.replace(/([?&])t=[^&]*/, "$1").replace(/[?&]$/, ""); const value = await helper.call(init.method ?? "GET", `${clean}${clean.includes("?") ? "&" : "?"}t=token`, { ...(init.headers ?? {}), "x-canvas-token": "token" }, init.body); return { status: value.status, json: async () => JSON.parse(value.text) }; }
            if (route.includes("/sessions?")) return response({ items: [bound] });
            if (route.includes("/resolve?")) return response({ binding_status: "bound", session_id: bound.session_id });
            if (route.includes("/sessions/")) return response({ session: bound, native_ids: [{ binding_kind: "native" }], events: [], runs: [] });
            if (route.includes("session-instruction")) return response({ state: "no_instruction" });
            if (route.includes("improvement-proposals")) return response({ items: [{ proposal_id: proposalId, status: "recommended" }] });
            if (route.includes("proposal-applies/receipts")) return response({ items: [{ apply_id: applyId, state: "applied" }] });
            return response({ error: "unexpected" }, 404);
        };
        const settle = async () => { for (let index = 0; index < 20; index++) await new Promise(resolve => setImmediate(resolve)); };
        const { document } = await runWorkspaceIife(fetch); await settle();
        await document.querySelectorAll("button").find(item => item.dataset.tab === "improve").click(); await settle();
        await document.querySelectorAll("button").find(item => item.dataset.tab === "compare").click(); await settle();
        await document.querySelectorAll("button").find(item => item.textContent === "適用記録を読み込む").click(); await settle();
        await document.querySelectorAll("button").find(item => item.textContent.startsWith("適用記録 ·")).click(); await settle();
        assert.match(textIn(document.body), /正確な紐付けなし/); assert.match(textIn(document.body), /比較不可/); assert.doesNotMatch(textIn(document.body), /unapproved_reason/);
        const selects = document.querySelectorAll("select"), inputs = document.querySelectorAll("input"); selects[0].value = "pre"; inputs[0].value = "case-a"; selects[2].value = "post"; inputs[1].value = "case-a"; selects[4].value = "excluded"; selects[5].value = "user_excluded";
        await document.querySelectorAll("button").find(item => item.textContent === "比較を確定").click(); await settle();
        assert.equal(upstream.filter(call => new URL(call.target).pathname.endsWith("/effect-comparisons") && call.init.method === "POST").length, 1);
        const text = textIn(document.body); assert.match(text, /改善/); assert.equal((text.match(/same-ref/g) || []).length, 2); assert.doesNotMatch(text, /hidden|unapproved/i);
    } finally { await new Promise(resolve => helper.server.close(resolve)); }
});

test("emitted Compare IIFE renders every canonical engine reason through the proxy and drops arbitrary reasons", async () => {
    const proposalId = "0197d7c0-0000-7000-8000-000000000001", applyId = "0197d7c0-0000-7000-8000-000000000002", comparisonId = "0197d7c0-0000-7000-8000-000000000003";
    const candidates = Array.from({ length: 6 }, (_, index) => ({ session_id: `00000000-0000-7000-8000-00000000000${index + 1}`, suggestion_reasons: [] }));
    const settle = async () => { for (let i = 0; i < 5; i++) await new Promise(resolve => setImmediate(resolve)); };
    let comparisonDetail = {}, candidateResponse = null, receiptsResponse = null;
    const helper = await proposalApplyHelperServer([], { fetchResponse: (target, init = {}) => {
        const path = new URL(target).pathname;
        if (path.endsWith("/receipts")) return new Response(JSON.stringify(receiptsResponse ?? { items: [{ apply_id: applyId, state: "applied" }] }));
        if (path.endsWith("/candidates")) return candidateResponse?.error
            ? new Response(JSON.stringify(candidateResponse), { status: candidateResponse.error === "objective_store_unavailable" ? 503 : 400 })
            : new Response(JSON.stringify(candidateResponse ?? { proposal_id: proposalId, proposal_revision: 2, apply_id: applyId, items: candidates }));
        if (path.endsWith(`/effect-comparisons/${comparisonId}`)) return new Response(JSON.stringify(comparisonDetail));
        if (path.endsWith("/effect-comparisons")) return new Response(JSON.stringify({ comparison_id: comparisonId }), { status: 201 });
        return new Response(JSON.stringify({ error: "unexpected" }), { status: 404 });
    } });
    const drive = async () => {
        const calls = [];
        const fetch = async (path, init = {}) => {
            calls.push({ path: String(path), init }); const url = String(path);
            if (url.includes("/sessions?")) return response({ items: [bound] });
            if (url.includes("/resolve?")) return response({ binding_status: "bound", session_id: bound.session_id });
            if (url.includes("/sessions/")) return response({ session: bound, native_ids: [{ binding_kind: "native" }], events: [], runs: [] });
            if (url.includes("/session-instruction/")) return response({ state: "no_instruction" });
            if (url.includes("improvement-proposals?")) return response({ items: [{ proposal_id: proposalId, status: "recommended" }] });
            if (url.includes("effect-comparisons") || url.includes("proposal-applies/receipts")) { const clean = url.replace(/([?&])t=[^&]*/, "$1").replace(/[?&]$/, ""); const value = await helper.call(init.method ?? "GET", `${clean}${clean.includes("?") ? "&" : "?"}t=token`, { ...(init.headers ?? {}), "x-canvas-token": "token" }, init.body); return { status: value.status, json: async () => JSON.parse(value.text) }; }
            return response({ error: "unexpected" }, 404);
        };
        const { document } = await runWorkspaceIife(fetch); await settle();
        await document.querySelectorAll("button").find(item => item.dataset.tab === "improve").click(); await settle();
        await document.querySelectorAll("button").find(item => item.dataset.tab === "compare").click(); await settle();
        return { document, calls, settle };
    };
    try {
      for (const [verdict, reason, label] of [["insufficient_evidence", "invalid_linkage", "比較の紐付けが無効"], ["insufficient_evidence", "insufficient_cohort", "比較対象セッション数が不足"], ["insufficient_evidence", "missing_quality_evidence", "品質評価の証拠が不足"], ["regressed", "post_severe_failure", "変更後に重大な失敗"], ["improved", "quality_improved", "品質が改善"], ["regressed", "quality_regressed", "品質が悪化"], ["insufficient_evidence", "missing_efficiency_evidence", "効率評価の証拠が不足"], ["improved", "duration_improved", "所要時間が改善"], ["regressed", "duration_regressed", "所要時間が悪化"], ["improved", "tokens_improved", "総トークンが改善"], ["regressed", "tokens_regressed", "総トークンが悪化"]]) {
        comparisonDetail = { receipt: { comparison_id: comparisonId, verdict }, summary: { verdict, reasons: [reason, "unapproved_reason"], pre_pass: 3, pre_count: 3, post_pass: 3, post_count: 3, pre_duration_median: 100, post_duration_median: 90, duration_delta: 0.1, pre_token_median: 200, post_token_median: 180, token_delta: 0.1 }, evidence: [{ kind: "event", reference_id: "<same-ref>" }], case_key_groups: [{ case_key: "case-a", evidence: [{ kind: "event", reference_id: "<same-ref>" }] }] };
        candidateResponse = null; receiptsResponse = null;
        const run = await drive(); await run.document.querySelectorAll("button").find(item => item.textContent === "適用記録を読み込む").click(); await run.settle(); await run.document.querySelectorAll("button").find(item => item.textContent.startsWith("適用記録 ·")).click(); await run.settle();
        const selects = run.document.querySelectorAll("select"), inputs = run.document.querySelectorAll("input");
        for (let index = 0; index < candidates.length; index++) { selects[index * 2].value = index < 3 ? "pre" : "post"; inputs[index].value = "case-a"; }
        await run.document.querySelectorAll("button").find(item => item.textContent === "比較を確定").click(); await run.settle();
        const panel = textIn(run.document.getElementById("workspace-panel"));
        assert.match(panel, new RegExp(comparisonTextForTest(verdict))); assert.match(panel, new RegExp(label)); assert.doesNotMatch(panel, /unapproved_reason/);
        for (const metric of ["3 / 3", "100", "90", "200", "180", "0.1"]) assert.match(panel, new RegExp(metric));
        assert.equal((panel.match(/same-ref/g) || []).length, 2, "summary and matching case drill-down preserve the same evidence identity");
        assert.equal(run.calls.filter(call => call.init.method === "POST" && call.path.includes("effect-comparisons")).length, 1);
        assert.equal(run.calls.filter(call => /session\.send|canvas/i.test(call.path)).length, 0);
      }
    receiptsResponse = { items: [] }; const absent = await drive(); await absent.document.querySelectorAll("button").find(item => item.textContent === "適用記録を読み込む").click(); await absent.settle();
    assert.equal(absent.document.querySelectorAll("button").find(item => item.textContent === "比較を確定"), undefined);
    assert.equal(absent.calls.filter(call => call.init.method === "POST" && call.path.includes("effect-comparisons")).length, 0);
    for (const error of ["application_not_active", "proposal_revision_stale", "cohort_not_confirmed", "comparison_evidence_stale", "objective_store_unavailable"]) {
        receiptsResponse = null; candidateResponse = { error, secret_path: "C:\\secret\\raw" }; const failed = await drive(); await failed.document.querySelectorAll("button").find(item => item.textContent === "適用記録を読み込む").click(); await failed.settle(); await failed.document.querySelectorAll("button").find(item => item.textContent.startsWith("適用記録 ·")).click(); await failed.settle();
        const panel = textIn(failed.document.getElementById("workspace-panel")); assert.match(panel, new RegExp(error)); assert.doesNotMatch(panel, /secret|raw/i); assert.equal(failed.calls.filter(call => call.init.method === "POST" && call.path.includes("effect-comparisons")).length, 0);
    }
    } finally { await new Promise(resolve => helper.server.close(resolve)); }
});

function comparisonTextForTest(value) { return ({ improved: "改善", no_change: "変化なし", regressed: "悪化", insufficient_evidence: "証拠不足" })[value]; }

test("emitted workspace IIFE requires explicit approval, sends zero-byte apply, and gives rollback failures terminal precedence", async () => {
    const calls = [];
    const id = "0197d7c0-0000-7000-8000-000000000001";
    const proposal = { proposal_id: "proposal-1", status: "recommended", title: "proposal", evidence_refs: [{ kind: "event", reference_id: "stop" }] };
    const draft = { draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 1, approval_digest: "digest-1", state: "draft", diff: "<script>inert</script>\n@@ -1 +1 @@", files: [{ relative_path: "safe.txt", base_sha256: "base" }], hunks: [{ hunk_id: "hunk-a", relative_path: "safe.txt", selected: true }, { hunk_id: "hunk-b", relative_path: "safe.txt", selected: true }] };
    const selected = { draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 2, approval_digest: "digest-2", state: "selected", files: [{ relative_path: "safe.txt", base_sha256: "base" }], hunks: [{ hunk_id: "hunk-a", relative_path: "safe.txt", selected: true }, { hunk_id: "hunk-b", relative_path: "safe.txt", selected: false }] };
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
        if (route === `/api/session-workspace/proposal-applies/drafts/${id}`) return response({ ...selected, diff: "selected hunk only" });
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
    const draftCall = calls.find(call => call.path.split("?")[0].endsWith("/proposal-applies/drafts") && call.init.method === "POST");
    assert.deepEqual(JSON.parse(draftCall.init.body), { proposal_id: "proposal-1", root_id: "root-1", files: [{ relative_path: "safe.txt", replacement_text: "complete replacement" }] });
    assert.match(textIn(document.getElementById("workspace-panel")), /<script>inert<\/script>/, "diff is rendered as text, never parsed markup");
    const fileToggle = document.querySelectorAll("input").find(item => item.type === "checkbox");
    fileToggle.checked = false;
    await fileToggle.dispatch("change");
    assert.ok(document.querySelectorAll("input").filter(item => item.type === "checkbox").every(item => !item.checked));
    const hunk = document.querySelectorAll("input").filter(item => item.type === "checkbox")[2];
    hunk.checked = true;
    await button("選択を更新").click();
    await settle();
    const selectionCall = calls.find(call => call.path.split("?")[0].endsWith("/selection"));
    assert.deepEqual(JSON.parse(selectionCall.init.body), { selection_revision: 1, selected_hunk_ids: ["hunk-b"] });
    assert.doesNotMatch(textIn(document.getElementById("workspace-panel")), /<script>inert<\/script>/, "confirmation is source/diff-free");
    assert.equal(calls.filter(call => /\/(apply|rollback)(?:\?|$)/.test(call.path)).length, 0, "no mutation occurs before explicit approval and apply");
    assert.equal(button("適用する"), undefined, "selection invalidates approval and cannot mutate early");
    await button("確認へ進む").click();
    await settle();
    await button("承認する").click();
    await settle();
    const apply = button("適用する");
    assert.ok(apply);
    await apply.click();
    await settle();
    assert.match(textIn(document.getElementById("workspace-panel")), /状態: applied/);
    const applyCall = calls.find(call => call.path.split("?")[0].endsWith("/apply"));
    assert.equal(applyCall.init.body, undefined, "apply has no editable browser body");
    await button("ロールバック").click();
    await settle();
    const panel = textIn(document.getElementById("workspace-panel"));
    assert.match(panel, /rollback_stale/);
    assert.doesNotMatch(panel, /状態: applied/);
    assert.equal(button("ロールバック"), undefined, "a failed rollback remains permanently disabled");
});

test("emitted workspace IIFE refetches and displays the selected diff before confirmation can enable approval", async () => {
    const calls = [], id = "0197d7c0-0000-7000-8000-000000000001";
    const proposal = { proposal_id: "proposal-1", status: "recommended", evidence_refs: [{ kind: "event", reference_id: "stop" }] };
    const detail = { session: bound, native_ids: [{ binding_kind: "native" }], events: [{ event_id: "stop", type: "Stop" }], runs: [] };
    let resolveSelectedDraft;
    const fetch = async (path, init = {}) => {
        calls.push({ path: String(path), init });
        const route = String(path).split("?")[0];
        if (route === "/api/session-workspace/sessions") return response({ items: [bound] });
        if (route.startsWith("/api/session-workspace/resolve")) return response({ binding_status: "bound", session_id: bound.session_id });
        if (route === `/api/session-workspace/sessions/${bound.session_id}`) return response(detail);
        if (route.startsWith("/api/session-instruction/")) return response({ state: "no_instruction" });
        if (route.startsWith("/api/session-workspace/improvement-proposals")) return response({ items: [proposal] });
        if (route.endsWith("/roots")) return response({ items: [{ root_id: "root-1", kind: "repository", label: "Repository" }] });
        if (route.endsWith("/drafts") && init.method === "POST") return response({ draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 1, approval_digest: "digest-1", state: "draft", diff: "all hunks\nremoved hunk", files: [{ relative_path: "safe.txt" }], hunks: [{ hunk_id: "keep", relative_path: "safe.txt", selected: true }, { hunk_id: "remove", relative_path: "safe.txt", selected: true }] }, 201);
        if (route.endsWith("/selection")) return response({ draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 2, approval_digest: "digest-2", state: "selected", files: [{}], hunks: [{ hunk_id: "keep", selected: true }] });
        if (route === `/api/session-workspace/proposal-applies/drafts/${id}`) return new Promise(resolve => { resolveSelectedDraft = resolve; });
        return response({ error: "unexpected" }, 404);
    };
    const { document } = await runWorkspaceIife(fetch);
    const button = text => document.querySelectorAll("button").find(item => item.textContent === text);
    const settle = async () => { await new Promise(resolve => setImmediate(resolve)); await new Promise(resolve => setImmediate(resolve)); };
    await button("Improve").click(); await settle(); await button("Apply locally").click(); await settle();
    const pathInput = document.querySelectorAll("input").find(item => item.placeholder === "relative path"), replacement = document.querySelectorAll("textarea").find(item => item.placeholder === "complete replacement text");
    pathInput.value = "safe.txt"; replacement.value = "replacement";
    let form = pathInput; while (form.tagName !== "form") form = form.parentNode;
    await form.dispatch("submit"); await settle();
    const hunkChecks = document.querySelectorAll("input").filter(item => item.type === "checkbox");
    hunkChecks.find(item => item.value === "remove").checked = false;
    const selectionClick = button("選択を更新").click(); await settle();
    assert.ok(calls.some(call => call.path.split("?")[0] === `/api/session-workspace/proposal-applies/drafts/${id}` && !call.init.method), "selection fetches the current helper-only draft");
    assert.equal(button("承認する"), undefined, "approval remains unavailable while selected-diff fetch is pending");
    resolveSelectedDraft(response({ draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 2, approval_digest: "digest-2", state: "selected", diff: "selected hunk only", files: [{ relative_path: "safe.txt" }], hunks: [{ hunk_id: "keep", relative_path: "safe.txt", selected: true }] }));
    await selectionClick;
    await settle();
    assert.match(textIn(document.getElementById("workspace-panel")), /selected hunk only/);
    assert.doesNotMatch(textIn(document.getElementById("workspace-panel")), /removed hunk/);
    assert.equal(button("承認する"), undefined, "reviewing the selected diff is separate from confirmation");
    await button("確認へ進む").click(); await settle();
    assert.doesNotMatch(textIn(document.getElementById("workspace-panel")), /selected hunk only/, "confirmation is source/diff-free");
    assert.ok(button("承認する"));
});

test("emitted workspace IIFE fail-closes malformed, stale, and failed selected-draft reads", async () => {
    const id = "0197d7c0-0000-7000-8000-000000000001", proposal = { proposal_id: "proposal-1", status: "recommended", evidence_refs: [{ kind: "event", reference_id: "stop" }] }, detail = { session: bound, native_ids: [{ binding_kind: "native" }], events: [{ event_id: "stop", type: "Stop" }], runs: [] };
    const valid = { draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 2, approval_digest: "digest-2", state: "selected", diff: "selected", files: [{ relative_path: "safe.txt" }], hunks: [{ hunk_id: "keep", relative_path: "safe.txt", selected: true }, { hunk_id: "removed", relative_path: "safe.txt", selected: false }] };
    for (const selectedDraft of [response({ error: "draft_not_found", path: "secret", diff: "secret" }, 404), response({ ...valid, diff: undefined, path: "secret" }), response({ ...valid, proposal_id: "wrong", path: "secret" }), response({ ...valid, root_id: "wrong", path: "secret" }), response({ ...valid, files: [{}], path: "secret" }), response({ ...valid, hunks: [{ hunk_id: "keep", selected: true }], path: "secret" }), response({ draft_id: id, selection_revision: 3, approval_digest: "wrong", diff: "secret" })]) {
        const calls = [], fetch = async (path, init = {}) => { calls.push({ path: String(path), init }); const route = String(path).split("?")[0]; if (route === "/api/session-workspace/sessions") return response({ items: [bound] }); if (route.startsWith("/api/session-workspace/resolve")) return response({ binding_status: "bound", session_id: bound.session_id }); if (route === `/api/session-workspace/sessions/${bound.session_id}`) return response(detail); if (route.startsWith("/api/session-instruction/")) return response({ state: "no_instruction" }); if (route.startsWith("/api/session-workspace/improvement-proposals")) return response({ items: [proposal] }); if (route.endsWith("/roots")) return response({ items: [{ root_id: "root-1", kind: "repository", label: "Repository" }] }); if (route.endsWith("/drafts") && init.method === "POST") return response({ draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 1, approval_digest: "digest-1", state: "draft", diff: "full", files: [{ relative_path: "safe.txt" }], hunks: [{ hunk_id: "keep", relative_path: "safe.txt", selected: true }] }, 201); if (route.endsWith("/selection")) return response({ draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 2, approval_digest: "digest-2", state: "selected", files: [], hunks: [] }); if (route === `/api/session-workspace/proposal-applies/drafts/${id}`) return selectedDraft; return response({}, 404); };
        const { document } = await runWorkspaceIife(fetch), button = text => document.querySelectorAll("button").find(item => item.textContent === text), settle = async () => { await new Promise(resolve => setImmediate(resolve)); await new Promise(resolve => setImmediate(resolve)); };
        await button("Improve").click(); await settle(); await button("Apply locally").click(); await settle(); const path = document.querySelectorAll("input").find(item => item.placeholder === "relative path"), replacement = document.querySelectorAll("textarea").find(item => item.placeholder === "complete replacement text"); path.value = "safe.txt"; replacement.value = "replacement"; let form = path; while (form.tagName !== "form") form = form.parentNode; await form.dispatch("submit"); await settle(); await button("選択を更新").click(); await settle(); const panel = textIn(document.getElementById("workspace-panel")); assert.equal(button("承認する"), undefined); assert.equal(button("適用する"), undefined); assert.equal(calls.filter(call => /\/(approve|apply)(?:\?|$)/.test(call.path)).length, 0); assert.match(panel, /draft_not_found|selection_stale/); assert.doesNotMatch(panel, /secret/);
    }
});

test("emitted workspace IIFE renders unavailable roots, ten-file limit, stale selection, and every apply/rollback terminal", async () => {
    const id = "0197d7c0-0000-7000-8000-000000000001", proposal = { proposal_id: "proposal-1", status: "recommended", evidence_refs: [{ kind: "event", reference_id: "stop" }] }, detail = { session: bound, native_ids: [{ binding_kind: "native" }], events: [{ event_id: "stop", type: "Stop" }], runs: [] };
    const settle = async () => { await new Promise(resolve => setImmediate(resolve)); await new Promise(resolve => setImmediate(resolve)); };
    const drive = async ({ roots = 200, selection = response({ draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 2, approval_digest: "digest-2", state: "selected", files: [{ relative_path: "safe.txt" }], hunks: [{ hunk_id: "hunk-a", relative_path: "safe.txt", selected: true }] }), apply = response({ apply_id: "apply-1", state: "applied" }), rollback = response({ apply_id: "apply-1", state: "rolled_back" }) } = {}) => {
        const calls = [], fetch = async (path, init = {}) => { calls.push({ path: String(path), init }); const route = String(path).split("?")[0]; if (route === "/api/session-workspace/sessions") return response({ items: [bound] }); if (route.startsWith("/api/session-workspace/resolve")) return response({ binding_status: "bound", session_id: bound.session_id }); if (route === `/api/session-workspace/sessions/${bound.session_id}`) return response(detail); if (route.startsWith("/api/session-instruction/")) return response({ state: "no_instruction" }); if (route.startsWith("/api/session-workspace/improvement-proposals")) return response({ items: [proposal] }); if (route.endsWith("/roots")) return roots === 200 ? response({ items: [{ root_id: "root-1", kind: "repository", label: "Repository" }] }) : response({ error: "apply_not_configured" }, roots); if (route.endsWith("/drafts") && init.method === "POST") return response({ draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 1, approval_digest: "digest-1", state: "draft", diff: "source-only", files: [{ relative_path: "safe.txt" }], hunks: [{ hunk_id: "hunk-a", relative_path: "safe.txt", selected: true }] }, 201); if (route.endsWith("/selection")) return selection; if (route === `/api/session-workspace/proposal-applies/drafts/${id}`) return response({ draft_id: id, proposal_id: "proposal-1", root_id: "root-1", selection_revision: 2, approval_digest: "digest-2", state: "selected", diff: "selected hunk only", files: [{ relative_path: "safe.txt" }], hunks: [{ hunk_id: "hunk-a", relative_path: "safe.txt", selected: true }] }); if (route.endsWith("/approve")) return response({ draft_id: id, selection_revision: 2, approval_digest: "digest-2", state: "approved" }); if (route.endsWith("/apply")) return apply; if (route.endsWith("/rollback")) return rollback; return response({}, 404); };
        const { document } = await runWorkspaceIife(fetch), button = text => document.querySelectorAll("button").find(item => item.textContent === text);
        await button("Improve").click(); await settle(); await button("Apply locally").click(); await settle();
        return { calls, document, button, settle };
    };
    const unavailable = await drive({ roots: 503 });
    assert.match(textIn(unavailable.document.getElementById("workspace-panel")), /apply_not_configured/);
    const ten = await drive();
    for (let index = 0; index < 10; index++) await ten.button("ファイルを追加").click();
    const tenPaths = ten.document.querySelectorAll("input").filter(item => item.placeholder === "relative path"), tenTexts = ten.document.querySelectorAll("textarea").filter(item => item.placeholder === "complete replacement text");
    assert.equal(tenPaths.length, 10, "the eleventh file control is rejected");
    tenPaths.forEach((item, index) => item.value = `file-${index}.txt`); tenTexts.forEach((item, index) => item.value = `replacement-${index}`); let tenForm = tenPaths[0]; while (tenForm.tagName !== "form") tenForm = tenForm.parentNode; await tenForm.dispatch("submit"); await ten.settle();
    assert.equal(JSON.parse(ten.calls.find(call => call.path.split("?")[0].endsWith("/proposal-applies/drafts") && call.init.method === "POST").init.body).files.length, 10, "ten complete files form a valid draft request");
    const stale = await drive({ selection: response({ error: "selection_stale" }, 409) });
    const stalePath = stale.document.querySelectorAll("input").find(item => item.placeholder === "relative path"), staleText = stale.document.querySelectorAll("textarea").find(item => item.placeholder === "complete replacement text"); stalePath.value = "safe.txt"; staleText.value = "text"; let form = stalePath; while (form.tagName !== "form") form = form.parentNode; await form.dispatch("submit"); await stale.settle(); await stale.button("選択を更新").click(); await stale.settle(); assert.match(textIn(stale.document.getElementById("workspace-panel")), /selection_stale/);
    for (const [apply, expected] of [[response({ error: "apply_failed" }, 409), "apply_failed"], [response({ apply_id: "apply-1", state: "recovered" }), "recovered"]]) { const run = await drive({ apply }); const path = run.document.querySelectorAll("input").find(item => item.placeholder === "relative path"), text = run.document.querySelectorAll("textarea").find(item => item.placeholder === "complete replacement text"); path.value = "safe.txt"; text.value = "text"; let form = path; while (form.tagName !== "form") form = form.parentNode; await form.dispatch("submit"); await run.settle(); await run.button("選択を更新").click(); await run.settle(); await run.button("確認へ進む").click(); await run.settle(); await run.button("承認する").click(); await run.settle(); await run.button("適用する").click(); await run.settle(); assert.match(textIn(run.document.getElementById("workspace-panel")), new RegExp(expected)); }
    for (const [rollback, expected] of [[response({ apply_id: "apply-1", state: "rolled_back" }), "rolled_back"], [response({ error: "rollback_not_available" }, 409), "rollback_not_available"]]) { const run = await drive({ rollback }); const path = run.document.querySelectorAll("input").find(item => item.placeholder === "relative path"), text = run.document.querySelectorAll("textarea").find(item => item.placeholder === "complete replacement text"); path.value = "safe.txt"; text.value = "text"; let form = path; while (form.tagName !== "form") form = form.parentNode; await form.dispatch("submit"); await run.settle(); await run.button("選択を更新").click(); await run.settle(); await run.button("確認へ進む").click(); await run.settle(); await run.button("承認する").click(); await run.settle(); await run.button("適用する").click(); await run.settle(); await run.button("ロールバック").click(); await run.settle(); assert.match(textIn(run.document.getElementById("workspace-panel")), new RegExp(expected)); assert.equal(run.button("ロールバック"), undefined, "rollback remains unavailable after its first terminal attempt"); }
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
