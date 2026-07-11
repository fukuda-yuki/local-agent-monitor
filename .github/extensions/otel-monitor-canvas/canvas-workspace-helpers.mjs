import { escapeHtml } from "./canvas-helpers.mjs";

const INSTRUCTION_STATES = new Set(["not_captured", "redacted", "unsupported", "expired_pending_deletion", "no_instruction"]);

function scriptValue(value) {
    return JSON.stringify(String(value ?? "")).replace(/[<>&]/g, (character) => ({
        "<": "\\u003c",
        ">": "\\u003e",
        "&": "\\u0026",
    })[character]);
}

export function groupWorkspaceSessions(items, resolvedSessionId) {
    const current = [];
    const recent = [];
    const unbound = [];
    for (const item of Array.isArray(items) ? items : []) {
        if (item?.session_id === resolvedSessionId) current.push(item);
        else if (item?.completeness === "unbound") unbound.push(item);
        else recent.push(item);
    }
    return { current, recent, unbound };
}

export function workspaceSessionLabel(session, instruction) {
    if (session?.completeness === "unbound") return "OTel トレースのみ（未紐付け）";
    if (instruction?.state === "available" && typeof instruction.preview === "string") {
        const firstLine = instruction.preview.split(/\r?\n/, 1)[0].trim();
        if (firstLine) return firstLine.slice(0, 80);
    }
    const surface = Array.isArray(session?.source_surfaces) ? session.source_surfaces[0] : null;
    return ({ "copilot-sdk": "Copilot セッション", "copilot-cli": "Copilot CLI セッション", vscode: "VS Code セッション", "hook-unknown": "Hook セッション" })[surface]
        ?? "指示未取得のセッション";
}

export function workspaceStatusPill(status) {
    return ({
        active: { className: "pill-running", text: "実行中", pulsing: true },
        completed: { className: "pill-completed", text: "完了", pulsing: false },
        failed: { className: "pill-failed", text: "失敗", pulsing: false },
        unknown: { className: "pill-incomplete", text: "未完全", pulsing: false },
    })[status] ?? { className: "pill-incomplete", text: "未完全", pulsing: false };
}

export function deriveWorkspaceGates(detail) {
    const session = detail?.session ?? detail ?? {};
    const errors = Array.isArray(detail?.events) ? detail.events.filter((event) => event?.status === "error").length : 0;
    const terminal = session.status === "completed"
        ? { state: "pass", detail: "完了" }
        : session.status === "failed"
            ? { state: "fail", detail: "失敗" }
            : { state: "pending", detail: "未評価" };
    const eventGate = errors > 0
        ? { state: "fail", detail: `${errors} 件` }
        : (session.completeness === "rich" || session.completeness === "full")
            ? { state: "pass", detail: "0 件" }
            : { state: "pending", detail: "未評価" };
    return [{ label: "終了状態", ...terminal }, { label: "エラーイベント", ...eventGate }];
}

export function workspaceNextActions(session, hasNativeBinding) {
    if (session?.completeness === "unbound") {
        return { primary: "Local Monitor でトレースを開く", secondary: null, analysis: false };
    }
    if (session.status === "active") return { primary: "Local Monitor を開く", secondary: null, analysis: false };
    if (session.status === "completed" || session.status === "failed") {
        return { primary: "トレース分析を開く", secondary: "Local Monitor を開く", analysis: true };
    }
    return { primary: "Local Monitor を開く", secondary: null, analysis: false };
}

export function instructionDisplay(instruction) {
    if (instruction?.state === "available" && typeof instruction.preview === "string") {
        return { available: true, text: instruction.preview };
    }
    const state = INSTRUCTION_STATES.has(instruction?.state) ? instruction.state : "no_instruction";
    return { available: false, text: `指示は ${state} です。推測では表示しません。` };
}

export function renderWorkspaceHtml({ monitorUrl, healthState, token, nativeSessionId = "" }) {
    const ready = healthState === "ready";
    const connection = ready ? "接続済み（ready）" : healthState === "not_ready" ? "起動中・未 ready" : "未接続";
    const safeToken = scriptValue(token);
    const safeMonitorUrl = scriptValue(String(monitorUrl ?? "").replace(/\/$/, ""));
    const safeNativeSessionId = scriptValue(nativeSessionId);
    return `<!doctype html>
<html lang="ja"><head><meta charset="utf-8"><title>OTel Monitor — Session Workspace</title>
<style>
:root{--bg:var(--background-color-default,#fff);--fg:var(--text-color-default,#1f2328);--muted:var(--text-color-muted,#656d76);--border:var(--border-color-default,#d0d7de);--accent:var(--accent-color-default,#0969da);--danger:#cf222e;--success:#1a7f37;--warn:#9a6700;--font:var(--font-sans,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif)}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 var(--font)}button{font:inherit}.topbar{display:flex;justify-content:space-between;gap:12px;align-items:center;border-bottom:1px solid var(--border);padding:14px 20px}.topbar h1{font-size:16px;margin:0}.connection{color:var(--muted);font-size:12px}.layout{display:grid;grid-template-columns:minmax(240px,300px) minmax(0,1fr);min-height:calc(100vh - 55px)}aside{border-right:1px solid var(--border);padding:16px 12px}.group{margin-bottom:18px}.group h2{font-size:12px;color:var(--muted);margin:0 0 8px}.session-item{width:100%;text-align:left;background:transparent;border:1px solid transparent;border-radius:6px;padding:9px;margin:2px 0;color:var(--fg);cursor:pointer}.session-item[aria-current="true"]{border-color:var(--accent);background:#ddf4ff}.session-title{display:block;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.meta{display:flex;gap:5px;align-items:center;flex-wrap:wrap;margin-top:5px;color:var(--muted);font-size:11px}.pill,.badge{display:inline-flex;gap:4px;align-items:center;border-radius:999px;padding:2px 7px;font-size:11px;font-weight:600}.pill-running{background:#ddf4ff;color:#0969da}.pill-completed{background:#dafbe1;color:#1a7f37}.pill-failed{background:#ffebe9;color:#cf222e}.pill-incomplete{background:#fff8c5;color:#9a6700}.badge{background:#f6f8fa;border:1px solid var(--border);color:var(--muted)}.badge-exact{color:#0969da}.dot{height:6px;width:6px;border-radius:50%;background:currentColor;animation:pulse 1.2s infinite}@keyframes pulse{50%{opacity:.3}}main{min-width:0;padding:20px;max-width:1000px}.tabs{display:flex;gap:4px;border-bottom:1px solid var(--border);margin-bottom:16px}.tab{border:0;border-bottom:2px solid transparent;background:transparent;padding:9px 12px;color:var(--muted);cursor:pointer}.tab[aria-selected="true"]{border-bottom-color:var(--accent);color:var(--fg);font-weight:600}.card,.banner{border:1px solid var(--border);border-radius:6px;padding:14px;margin-bottom:12px}.card h2{font-size:15px;margin:0 0 8px}.banner-warn{background:#fff8c5;border-color:#d4a72c;color:#5c4b00}.empty{color:var(--muted);padding:34px 0;text-align:center}.kv{display:grid;grid-template-columns:160px 1fr;gap:5px 12px}.kv dt{color:var(--muted)}.preview{white-space:pre-wrap;overflow-wrap:anywhere;background:#f6f8fa;border:1px solid var(--border);padding:10px;border-radius:4px}.gate{display:flex;justify-content:space-between;gap:8px;border-top:1px solid var(--border);padding:8px 0}.gate:first-of-type{border-top:0}.gate-state-pass{color:var(--success)}.gate-state-fail{color:var(--danger)}.gate-state-pending{color:var(--warn)}.actions{display:flex;gap:8px;flex-wrap:wrap}.btn{border:1px solid var(--border);background:var(--bg);color:var(--fg);border-radius:6px;padding:7px 10px;cursor:pointer}.btn-primary{background:var(--accent);border-color:var(--accent);color:#fff}.btn[aria-pressed="true"]{outline:2px solid var(--accent);outline-offset:1px}.muted{color:var(--muted)}a{color:var(--accent)}@media(max-width:700px){.layout{grid-template-columns:1fr}aside{border-right:0;border-bottom:1px solid var(--border)}.kv{grid-template-columns:1fr}}
</style></head><body>
<header class="topbar"><h1>Session Workspace</h1><span class="connection">Local Monitor の接続状態: ${escapeHtml(connection)}</span></header>
<div class="layout"><aside><section class="group" id="current-group"><h2>この会話（exact-bound）</h2><div id="current-sessions"></div></section><section class="group"><h2>最近のセッション</h2><div id="recent-sessions"></div></section><section class="group"><h2>未紐付け（OTel のみ）</h2><div id="unbound-sessions"></div></section></aside><main>
<div class="tabs" role="tablist"><button class="tab" data-tab="review" role="tab" aria-selected="true">Review</button><button class="tab" data-tab="evidence" role="tab" aria-selected="false">Evidence</button><button class="tab" data-tab="improve" role="tab" aria-selected="false">Improve</button><button class="tab" data-tab="compare" role="tab" aria-selected="false">Compare</button></div>
<section id="workspace-panel"></section></main></div>
<script>(function(){
const token=${safeToken},monitorUrl=${safeMonitorUrl},nativeSessionId=${safeNativeSessionId};let selectedId=null,selectedDetail=null,selectedTab="review",sessions=[],instructions=new Map(),details=new Map(),evaluationError=null;
const q=(id)=>document.getElementById(id);const esc=(v)=>String(v??"");
function request(path,init){const options=init||{};options.headers=Object.assign({},options.headers||{},{"x-canvas-token":token});return fetch(path+(path.includes("?")?"&":"?")+"t="+encodeURIComponent(token),options).then(async r=>({status:r.status,body:await r.json().catch(()=>({}))}));}
function pill(status){const p={active:["pill-running","実行中",true],completed:["pill-completed","完了"],failed:["pill-failed","失敗"],unknown:["pill-incomplete","未完全"]}[status]||["pill-incomplete","未完全"];const node=document.createElement("span");node.className="pill "+p[0];if(p[2]){const dot=document.createElement("span");dot.className="dot";node.append(dot);}node.append(document.createTextNode(p[1]));return node;}
function hasNative(detail){return Array.isArray(detail&&detail.native_ids)&&detail.native_ids.some(n=>n.binding_kind==="native");}
function label(session){const instruction=instructions.get(session.session_id);if(session.completeness==="unbound")return "OTel トレースのみ（未紐付け）";if(instruction&&instruction.state==="available"&&typeof instruction.preview==="string"){const firstLine=instruction.preview.split(/\\r?\\n/,1)[0].trim();if(firstLine)return firstLine.slice(0,80);}const surface=Array.isArray(session.source_surfaces)?session.source_surfaces[0]:null;return {"copilot-sdk":"Copilot セッション","copilot-cli":"Copilot CLI セッション",vscode:"VS Code セッション","hook-unknown":"Hook セッション"}[surface]||"指示未取得のセッション";}
function renderSidebar(){const groups={current:[],recent:[],unbound:[]};sessions.forEach(s=>{if(s.session_id===selectedExactId)groups.current.push(s);else if(s.completeness==="unbound")groups.unbound.push(s);else groups.recent.push(s);});q("current-group").hidden=!groups.current.length;[["current-sessions",groups.current],["recent-sessions",groups.recent],["unbound-sessions",groups.unbound]].forEach(([id,items])=>{const host=q(id);host.textContent="";items.forEach(s=>{const b=document.createElement("button");b.className="session-item";b.type="button";b.setAttribute("aria-current",String(s.session_id===selectedId));const title=document.createElement("span");title.className="session-title";title.textContent=label(s);const meta=document.createElement("span");meta.className="meta";meta.append(pill(s.status));const completeness=document.createElement("span");completeness.className="badge";completeness.textContent=esc(s.completeness);meta.append(completeness);if(hasNative(details.get(s.session_id))){const exact=document.createElement("span");exact.className="badge badge-exact";exact.textContent="exact";meta.append(exact);}const time=s.started_at||s.last_seen_at||"—";meta.append(document.createTextNode((s.repository||"—")+" · "+time));b.append(title,meta);b.addEventListener("click",()=>select(s.session_id));host.append(b);});});}
function card(title){const el=document.createElement("section");el.className="card";const h=document.createElement("h2");h.textContent=title;el.append(h);return el;}function text(el,value,cls){const p=document.createElement("p");if(cls)p.className=cls;p.textContent=value;el.append(p);return p;}
function bindingText(detail){const kinds=Array.isArray(detail.native_ids)?detail.native_ids.map(n=>n.binding_kind):[];if(kinds.includes("native"))return "exact（native session ID）";return {explicit_resume:"explicit resume",explicit_handoff:"explicit handoff",trace_context:"trace context"}[kinds[0]]||"未紐付け";}
function renderReview(){const host=q("workspace-panel");host.textContent="";if(!selectedDetail){const empty=document.createElement("p");empty.className="empty";empty.textContent="セッションを選択してください";host.append(empty);return;}const detail=selectedDetail,session=detail.session;const native=hasNative(detail);if(!native){const banner=document.createElement("div");banner.className="banner banner-warn";banner.textContent="未紐付けセッションです。native session ID がないため、会話との対応は確認できません。";host.append(banner);}const binding=card("セッションの結合"),dl=document.createElement("dl");dl.className="kv";[["結合方法",bindingText(detail)],["完全性",session.completeness||"—"]].forEach(pair=>{const dt=document.createElement("dt"),dd=document.createElement("dd");dt.textContent=pair[0];dd.textContent=pair[1];dl.append(dt,dd);});binding.append(dl);host.append(binding);
const instruction=card("実際の指示"),value=instructions.get(session.session_id);if(value&&value.state==="available"){const pre=document.createElement("pre");pre.className="preview";pre.textContent=value.preview;instruction.append(pre);}else text(instruction,"指示は "+((value&&value.state)||"no_instruction")+" です。推測では表示しません。","muted");host.append(instruction);
const result=card("結果");const state={completed:"完了",failed:"失敗",active:"未確定",unknown:"不明"}[session.status]||"不明";text(result,state+(session.ended_at?" · "+session.ended_at:session.status==="active"?"（成功・失敗はまだ判定しません。）":""));host.append(result);
const gates=card("品質ゲート"),errors=Array.isArray(detail.events)?detail.events.filter(e=>e.status==="error").length:0;[["終了状態",session.status==="completed"?"pass":session.status==="failed"?"fail":"pending",session.status==="completed"?"完了":session.status==="failed"?"失敗":"未評価"],["エラーイベント",errors>0?"fail":(["rich","full"].includes(session.completeness)?"pass":"pending"),errors>0?errors+" 件":(["rich","full"].includes(session.completeness)?"0 件":"未評価")]].forEach(g=>{const row=document.createElement("div");row.className="gate";const name=document.createElement("span"),status=document.createElement("strong");name.textContent=g[0];status.className="gate-state-"+g[1];status.textContent=(g[1]==="pass"?"PASS":g[1]==="fail"?"FAIL":"未評価")+" · "+g[2];row.append(name,status);gates.append(row);});host.append(gates);
const evaluation=card("人間評価"),evaluationData=detail.human_evaluation,enabled=native&&(session.status==="completed"||session.status==="failed");text(evaluation,enabled?"ワンタップで人間評価を記録します。":"native binding がある完了・失敗セッションでのみ記録できます。","muted");const actions=document.createElement("div");actions.className="actions";[["expected","期待どおり"],["problem","問題あり"]].forEach(pair=>{const btn=document.createElement("button");btn.className="btn";btn.type="button";btn.disabled=!enabled;btn.setAttribute("aria-pressed",String(evaluationData&&evaluationData.verdict===pair[0]));btn.textContent=pair[1];btn.addEventListener("click",()=>saveEvaluation(evaluationData&&evaluationData.verdict===pair[0]?null:pair[0]).catch(showEvaluationError));actions.append(btn);});evaluation.append(actions);if(evaluationData&&evaluationData.recorded_at)text(evaluation,"記録日時: "+evaluationData.recorded_at,"muted");if(evaluationError)text(evaluation,evaluationError,"muted");host.append(evaluation);
const next=card("次の操作"),nextActions=session.completeness==="unbound"?["Local Monitor でトレースを開く",null,false]:session.status==="active"?["Local Monitor を開く",null,false]:(session.status==="completed"||session.status==="failed")?["トレース分析を開く","Local Monitor を開く",true]:["Local Monitor を開く",null,false];const nextButtons=document.createElement("div");nextButtons.className="actions";const primary=document.createElement("a");primary.className="btn btn-primary";primary.textContent=nextActions[0];primary.href=nextActions[2]?"/analysis?t="+encodeURIComponent(token):monitorUrl;primary.target=nextActions[2]?"":"_blank";nextButtons.append(primary);if(nextActions[1]){const secondary=document.createElement("a");secondary.className="btn";secondary.textContent=nextActions[1];secondary.href=monitorUrl;secondary.target="_blank";nextButtons.append(secondary);}next.append(nextButtons);host.append(next);}
function placeholder(tab){const host=q("workspace-panel");host.textContent="";const c=card(tab);text(c,tab==="Evidence"?"Evidence は Issue #49/#53 の Agent 実行グラフとともに提供予定です。":tab==="Improve"?"Improve は後続 Issue で提供予定です。":"Compare は後続 Issue で提供予定です。","muted");host.append(c);}
function render(){renderSidebar();if(selectedTab==="review")renderReview();else placeholder(selectedTab);}function showEvaluationError(){evaluationError="人間評価を保存できませんでした。接続を確認して再試行してください。";render();}async function loadDetail(id){if(details.has(id))return details.get(id);const out=await request("/api/session-workspace/sessions/"+encodeURIComponent(id));const detail=out.status===200?out.body:null;details.set(id,detail);return detail;}async function select(id){selectedId=id;evaluationError=null;selectedDetail=await loadDetail(id);if(selectedDetail){await loadInstruction(id);}render();}async function loadInstruction(id){if(instructions.has(id))return;const out=await request("/api/session-instruction/"+encodeURIComponent(id));instructions.set(id,out.status===200?out.body:{state:"no_instruction"});}async function saveEvaluation(verdict){if(!selectedDetail)return;details.delete(selectedDetail.session.session_id);const out=await request("/api/session-workspace/sessions/"+encodeURIComponent(selectedDetail.session.session_id)+"/human-evaluation",{method:"PUT",headers:{"Content-Type":"application/json"},body:JSON.stringify({verdict})});if(out.status===204)await select(selectedDetail.session.session_id);}
let selectedExactId=null;document.querySelectorAll(".tab").forEach(btn=>btn.addEventListener("click",()=>{selectedTab=btn.dataset.tab;document.querySelectorAll(".tab").forEach(tab=>tab.setAttribute("aria-selected",String(tab===btn)));render();}));const resolvePath="/api/session-workspace/resolve?source_surface=copilot-sdk&native_session_id="+encodeURIComponent(nativeSessionId);Promise.all([request("/api/session-workspace/sessions?limit=50"),request(resolvePath)]).then(async values=>{sessions=values[0].status===200&&Array.isArray(values[0].body.items)?values[0].body.items:[];if(values[1].status===200&&values[1].body.binding_status==="bound"){selectedExactId=values[1].body.session_id;if(!sessions.some(s=>s.session_id===selectedExactId)){const detail=await loadDetail(selectedExactId);if(detail)sessions.unshift(detail.session);}}await Promise.all(sessions.map(s=>Promise.all([loadInstruction(s.session_id),loadDetail(s.session_id)])));if(selectedExactId)await select(selectedExactId);else render();}).catch(render);
})();</script></body></html>`;
}
