import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

import {
    deriveWorkspaceGates,
    groupWorkspaceSessions,
    instructionDisplay,
    renderWorkspaceHtml,
    workspaceNextActions,
    workspaceSessionLabel,
    workspaceStatusPill,
} from "./canvas-workspace-helpers.mjs";

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
