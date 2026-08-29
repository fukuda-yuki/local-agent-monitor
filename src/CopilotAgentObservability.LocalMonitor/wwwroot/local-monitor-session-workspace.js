(() => {
  "use strict";

  const root = document.querySelector("[data-session-workspace]");
  if (!root || !window.LocalMonitorV1History || !window.LocalMonitorV1Paths || !window.LocalMonitorV1FactState) return;

  const UUID_V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const NODE = /^node-[0-9a-f]{32}$/;
  const REVISION = /^[0-9a-f]{64}$/;
  const FACT_STATES = new Set(["recorded", "not_observed", "source_unsupported", "capture_gap", "certification_pending", "not_captured", "expired", "redacted", "malformed", "oversized", "inconsistent", "projection_invalid"]);
  const COVERAGE_STATES = new Set(["recorded", "complete_zero", "not_observed", "source_unsupported", "capture_gap", "certification_pending", "inconsistent", "projection_invalid"]);
  const ROOT_KEYS = ["schema_version", "workspace_revision", "session", "executions", "technical_references"];
  const SESSION_KEYS = ["session_id", "status", "completeness", "assignment", "archive", "instruction", "source", "model", "version", "timing", "tokens", "activity", "capture"];
  const TOKEN_KEYS = ["authority", "state", "available_execution_count", "total_execution_count", "input", "output", "total", "reasoning", "cache_read", "cache_creation", "new_input", "cache_read_ratio_basis_points"];
  const ACTIVITY_KEYS = ["skill", "tool", "subagent", "error", "retry"];
  const SOURCES = new Set(["copilot-sdk", "copilot-cli", "vscode", "hook-unknown", "claude-code"]);
  const state = { summary: null, revision: null, selectedNodeId: null, selectedExecutionId: null, executionState: new Map(), staleAttempted: false };
  window.LocalMonitorSessionWorkspace = state;

  const exact = (value, keys) => value && typeof value === "object" && !Array.isArray(value)
    && Object.keys(value).length === keys.length && Object.keys(value).every((key, index) => key === keys[index]);
  const nonnegative = value => Number.isSafeInteger(value) && value >= 0;
  const oneOf = (value, allowed) => allowed.includes(value);
  const distinct = values => new Set(values).size === values.length;
  const sorted = values => values.every((value, index) => index === 0 || values[index - 1] < value);
  const instant = value => typeof value === "string" && /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}\+00:00$/.test(value);
  const scalarFact = (value, key, maximum = null) => exact(value, ["state", key]) && FACT_STATES.has(value.state)
    && (value.state === "recorded" ? nonnegative(value[key]) && (maximum === null || value[key] <= maximum) : value[key] === null);
  const countFact = value => scalarFact(value, "count");

  function tokens(value) {
    if (!(exact(value, TOKEN_KEYS) && oneOf(value.authority, ["session_run", "llm_span", "mixed", "none"])
      && FACT_STATES.has(value.state) && nonnegative(value.available_execution_count) && nonnegative(value.total_execution_count)
      && value.available_execution_count <= value.total_execution_count
      && ["input", "output", "total", "reasoning", "cache_read", "cache_creation", "new_input"].every(key => scalarFact(value[key], "value"))
      && scalarFact(value.cache_read_ratio_basis_points, "value", 10000))) return false;
    const derivedInputsValid = value.input.state === "recorded" && value.cache_read.state === "recorded"
      && value.cache_read.value <= value.input.value;
    const contradictoryInputs = value.input.state === "recorded" && value.cache_read.state === "recorded"
      && value.cache_read.value > value.input.value;
    if (contradictoryInputs && (value.new_input.state !== "inconsistent"
        || value.cache_read_ratio_basis_points.state !== "inconsistent")) return false;
    if (value.new_input.state === "recorded"
        && (!derivedInputsValid || value.new_input.value !== value.input.value - value.cache_read.value)) return false;
    if (value.cache_read_ratio_basis_points.state === "recorded"
        && (!derivedInputsValid || value.input.value === 0)) return false;
    return true;
  }

  const activity = value => exact(value, ACTIVITY_KEYS) && ACTIVITY_KEYS.every(key => countFact(value[key]));
  const valuesFact = (value, maximum = null) => exact(value, ["state", "values"]) && FACT_STATES.has(value.state)
    && Array.isArray(value.values) && (maximum === null || value.values.length <= maximum) && distinct(value.values)
    && sorted(value.values) && value.values.every(item => typeof item === "string" && item.length > 0)
    && (value.state !== "recorded" || value.values.length > 0);

  function timing(value, status, execution = false) {
    const keys = execution ? ["state", "started_at", "ended_at", "duration_ms"] : ["state", "started_at", "ended_at", "last_seen_at", "duration_ms"];
    if (!exact(value, keys)) return false;
    if (execution) {
      if (!oneOf(value.state, ["recorded", "missing", "invalid"])) return false;
      if (value.state !== "recorded") return value.started_at === null && value.ended_at === null && value.duration_ms === null;
      const open = value.ended_at === null && value.duration_ms === null;
      const closed = instant(value.ended_at) && nonnegative(value.duration_ms) && value.ended_at >= value.started_at;
      return instant(value.started_at) && (status === "active" ? open
        : oneOf(status, ["completed", "failed"]) ? closed : open || closed);
    }
    if (!FACT_STATES.has(value.state) || value.last_seen_at !== null && !instant(value.last_seen_at)) return false;
    if (value.state !== "recorded") return (value.started_at === null || instant(value.started_at))
      && (value.ended_at === null || instant(value.ended_at)) && (value.duration_ms === null || nonnegative(value.duration_ms));
    const open = value.ended_at === null && value.duration_ms === null;
    const closed = instant(value.ended_at) && nonnegative(value.duration_ms) && value.ended_at >= value.started_at;
    return instant(value.started_at) && instant(value.last_seen_at) && value.last_seen_at >= value.started_at
      && (status === "active" ? open : oneOf(status, ["completed", "failed"]) ? closed : open || closed);
  }

  function assignment(value) {
    if (!(exact(value, ["state", "authority", "revision", "repository_id", "candidate_repository_ids"])
      && oneOf(value.state, ["assigned", "unassigned", "explicitly_unassigned", "conflict"])
      && oneOf(value.authority, ["automatic", "manual", "none"]) && nonnegative(value.revision)
      && (value.repository_id === null || UUID_V7.test(value.repository_id)) && Array.isArray(value.candidate_repository_ids)
      && value.candidate_repository_ids.length <= 128 && distinct(value.candidate_repository_ids)
      && sorted(value.candidate_repository_ids) && value.candidate_repository_ids.every(id => UUID_V7.test(id)))) return false;
    return value.state === "assigned" && oneOf(value.authority, ["automatic", "manual"])
        && value.repository_id !== null && value.candidate_repository_ids.length === 0
      || value.state === "unassigned" && value.authority === "none" && value.repository_id === null && value.candidate_repository_ids.length === 0
      || value.state === "explicitly_unassigned" && value.authority === "manual" && value.repository_id === null && value.candidate_repository_ids.length === 0
      || value.state === "conflict" && value.authority === "automatic" && value.repository_id === null && value.candidate_repository_ids.length >= 2;
  }

  const archive = value => exact(value, ["state", "revision", "effectively_eligible", "exclusion_reason"])
    && oneOf(value.state, ["active", "archived"]) && nonnegative(value.revision) && typeof value.effectively_eligible === "boolean"
    && oneOf(value.exclusion_reason, [null, "session_archived", "repository_archived"])
    && value.effectively_eligible === (value.exclusion_reason === null)
    && (value.state === "archived") === (value.exclusion_reason === "session_archived");

  const instruction = value => exact(value, ["state", "label", "additional_count", "content_available"])
    && oneOf(value.state, ["recorded", "not_observed", "not_captured", "expired", "invalid"])
    && (value.additional_count === null || nonnegative(value.additional_count)) && typeof value.content_available === "boolean"
    && (value.state === "recorded" ? typeof value.label === "string" && Array.from(value.label).length >= 1 && Array.from(value.label).length <= 160
      : value.label === null && value.content_available === false);

  function capture(value) {
    const families = ["instruction", "source", "model", "version", "timing", "tokens", "cache", "skill", "tool", "subagent", "error", "retry"];
    const notes = ["raw_content_not_captured", "raw_content_expired", "source_unsupported", "capture_gap", "certification_pending", "projection_invalid", "token_inconsistent", "cache_inconsistent"];
    return exact(value, ["state", "notes", "coverage"]) && oneOf(value.state, ["complete", "partial", "not_observed", "invalid"])
      && Array.isArray(value.notes) && value.notes.length <= 16 && distinct(value.notes) && sorted(value.notes) && value.notes.every(note => notes.includes(note))
      && Array.isArray(value.coverage) && value.coverage.length === 12
      && value.coverage.every((item, index) => exact(item, ["signal_family", "state"])
        && item.signal_family === families[index] && COVERAGE_STATES.has(item.state));
  }

  function validate(summary) {
    if (!exact(summary, ROOT_KEYS) || summary.schema_version !== "local-monitor-session-summary.response.v2"
        || !REVISION.test(summary.workspace_revision) || !exact(summary.session, SESSION_KEYS)
        || summary.session.session_id !== root.dataset.sessionId || !UUID_V7.test(summary.session.session_id)
        || !oneOf(summary.session.status, ["active", "completed", "failed", "unknown"])
        || !oneOf(summary.session.completeness, ["unbound", "partial", "rich", "full"])
        || !assignment(summary.session.assignment) || !archive(summary.session.archive) || !instruction(summary.session.instruction)
        || !valuesFact(summary.session.source, 5) || !valuesFact(summary.session.model, 16) || !valuesFact(summary.session.version)
        || summary.session.source.values.some(value => !SOURCES.has(value))
        || summary.session.archive.exclusion_reason === "repository_archived"
          && (summary.session.assignment.state !== "assigned" || summary.session.assignment.repository_id === null)
        || !timing(summary.session.timing, summary.session.status) || !tokens(summary.session.tokens) || !activity(summary.session.activity)
        || !capture(summary.session.capture) || !Array.isArray(summary.executions) || summary.executions.length > 256
        || !exact(summary.technical_references, ["native_session_ids", "trace_ids"])
        || !Array.isArray(summary.technical_references.native_session_ids) || !Array.isArray(summary.technical_references.trace_ids)) throw new TypeError("invalid Session summary");
    if (summary.technical_references.native_session_ids.some(value => typeof value !== "string" || value.length === 0)
        || !distinct(summary.technical_references.native_session_ids) || !sorted(summary.technical_references.native_session_ids)
        || summary.technical_references.trace_ids.some(value => typeof value !== "string" || !/^[0-9a-f]{32}$/.test(value))
        || !distinct(summary.technical_references.trace_ids) || !sorted(summary.technical_references.trace_ids)) throw new TypeError("invalid Session summary");
    let latest = 0;
    for (const execution of summary.executions) {
      if (!exact(execution, ["execution_id", "node_id", "latest", "source", "model", "lifecycle", "status", "timing", "tokens", "activity", "child_count"])
          || !UUID_V7.test(execution.execution_id) || !NODE.test(execution.node_id) || typeof execution.latest !== "boolean"
          || execution.source !== null && (typeof execution.source !== "string" || execution.source.length === 0)
          || execution.model !== null && (typeof execution.model !== "string" || execution.model.length === 0)
          || !oneOf(execution.lifecycle, ["selected", "started", "completed", "failed", "deselected", "unknown"])
          || !oneOf(execution.status, ["active", "completed", "failed", "unknown"]) || !timing(execution.timing, execution.status, true)
          || !tokens(execution.tokens) || !activity(execution.activity) || !nonnegative(execution.child_count) || execution.child_count > 4096) throw new TypeError("invalid Session summary");
      if (execution.latest) latest++;
    }
    if (latest !== (summary.executions.length === 0 ? 0 : 1)) throw new TypeError("invalid Session summary");
    if (summary.executions.some((execution, index) => index > 0
        && compareExecutions(summary.executions[index - 1], execution) > 0)) throw new TypeError("invalid Session summary");
    return summary;
  }

  function compareExecutions(left, right) {
    const group = timing => timing.state === "recorded" ? 0 : timing.state === "missing" ? 1 : 2;
    const groupDifference = group(left.timing) - group(right.timing);
    if (groupDifference !== 0) return groupDifference;
    if (left.timing.state === "recorded" && left.timing.started_at !== right.timing.started_at)
      return left.timing.started_at > right.timing.started_at ? -1 : 1;
    const leftSource = left.source ?? "\uffff";
    const rightSource = right.source ?? "\uffff";
    if (leftSource !== rightSource) return leftSource < rightSource ? -1 : 1;
    return left.execution_id < right.execution_id ? -1 : left.execution_id > right.execution_id ? 1 : 0;
  }

  const el = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  };
  const format = value => value.toLocaleString("ja-JP");
  const renderFact = (target, value) => window.LocalMonitorV1FactState.renderSessionCollection(target,
    { state: value.state, count: value.state === "recorded" ? BigInt(value.count ?? value.value) : null });

  function namedFact(title, value, dataName) {
    const card = tokenMetric(title, value);
    card.dataset[`sessionFixed${dataName}`] = "";
    return card;
  }

  function tokenMetric(title, value) {
    const card = el("div", "local-monitor-session-summary-card");
    card.append(el("h2", null, title));
    if (value.state === "recorded") card.append(el("strong", null, format(value.value)));
    else renderFact(card.appendChild(el("div")), value);
    return card;
  }

  function renderBars(card, first, second, className, firstLabel, secondLabel) {
    if (first.state !== "recorded" || second.state !== "recorded") return;
    const total = first.value + second.value;
    if (total <= 0) return;
    const bar = el("div", `local-monitor-session-bar ${className}`);
    const a = el("span"); a.style.width = `${first.value / total * 100}%`; a.setAttribute("role", "img"); a.setAttribute("aria-label", `${firstLabel} ${format(first.value)}`);
    const b = el("span"); b.style.width = `${second.value / total * 100}%`; b.setAttribute("role", "img"); b.setAttribute("aria-label", `${secondLabel} ${format(second.value)}`);
    bar.append(a, b); card.append(bar);
  }

  const requestUrl = (path, parameters) => `${path}?${new URLSearchParams(parameters)}`;

  async function requestJson(url, exactTarget) {
    const response = await fetch(url, { headers: { Accept: "application/json" } });
    if (response.status === 409 && !state.staleAttempted) {
      state.staleAttempted = true;
      await load(true, false);
      state.executionState.clear();
      return exactTarget();
    }
    if (!response.ok) throw new Error("Session detail unavailable");
    return JSON.parse(await response.text());
  }

  function executionMemory(executionId) {
    if (!state.executionState.has(executionId)) state.executionState.set(executionId, {
      open: false, pages: new Map(), expanded: new Set(), scrollTop: 0,
    });
    return state.executionState.get(executionId);
  }

  async function loadTimeline(executionId, parentNodeId = null, after = null) {
    const parameters = { workspace_revision: state.revision, execution_id: executionId };
    if (parentNodeId) parameters.parent_node_id = parentNodeId;
    if (after) parameters.after = after;
    parameters.limit = "100";
    const exactTarget = () => loadTimeline(executionId, parentNodeId, after);
    const page = await requestJson(requestUrl(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/timeline`, parameters), exactTarget);
    if (page.workspace_revision !== state.revision || page.execution_id !== executionId || page.parent_node_id !== parentNodeId
        || !Array.isArray(page.items)) throw new TypeError("invalid timeline");
    const memory = executionMemory(executionId);
    const key = parentNodeId ?? "root";
    const existing = after ? memory.pages.get(key)?.items ?? [] : [];
    memory.pages.set(key, { items: [...existing, ...page.items], nextCursor: page.next_cursor });
    renderExecutions();
  }

  function timingLabel(node) {
    if (node.timing?.state !== "recorded") return node.timing?.state === "invalid" ? "時刻が無効" : "時刻なし";
    return node.timing.duration_ms === null ? node.timing.started_at : `${format(node.timing.duration_ms)} ms`;
  }

  function renderNodes(execution, memory, parentNodeId, depth) {
    const fragment = document.createDocumentFragment();
    const page = memory.pages.get(parentNodeId ?? "root");
    if (!page) return fragment;
    const known = page.items.filter(item => ["exact", "explicit"].includes(item.relationship_authority));
    const unknown = page.items.filter(item => !["exact", "explicit"].includes(item.relationship_authority));
    const appendRow = (node, target = fragment) => {
      const wrapper = el("div", "local-monitor-session-timeline-entry");
      const row = el("button", "local-monitor-session-timeline-node");
      row.type = "button"; row.dataset.timelineNode = node.node_id; row.style.setProperty("--timeline-depth", depth);
      row.setAttribute("aria-expanded", node.child_count > 0 ? String(memory.expanded.has(node.node_id)) : "false");
      const label = node.name?.state === "recorded" ? node.name.text : node.kind;
      row.append(el("strong", null, label), el("span", null, `${node.kind} · ${node.status} · ${timingLabel(node)}`));
      row.addEventListener("click", async () => {
        if (node.child_count > 0) {
          if (memory.expanded.has(node.node_id)) memory.expanded.delete(node.node_id);
          else { memory.expanded.add(node.node_id); if (!memory.pages.has(node.node_id)) await loadTimeline(execution.execution_id, node.node_id); }
        }
        await selectNode(execution.execution_id, node.node_id, true);
        renderExecutions();
      });
      wrapper.append(row);
      if (memory.expanded.has(node.node_id)) wrapper.append(renderNodes(execution, memory, node.node_id, depth + 1));
      target.append(wrapper);
    };
    known.forEach(node => appendRow(node));
    if (unknown.length) {
      const group = el("div", "local-monitor-session-unknown-group");
      group.append(el("strong", null, "親子関係不明"));
      const holder = el("div"); unknown.forEach(node => appendRow(node, holder));
      group.append(holder); fragment.append(group);
    }
    if (page.nextCursor) {
      const more = el("button", "local-monitor-session-load-more", "さらに読み込む");
      more.type = "button"; more.dataset.timelineLoadMore = parentNodeId ?? "root";
      more.addEventListener("click", () => loadTimeline(execution.execution_id, parentNodeId, page.nextCursor));
      fragment.append(more);
    }
    return fragment;
  }

  function renderExecutions() {
    const executions = root.querySelector("[data-session-executions]"); executions.replaceChildren();
    for (const execution of state.summary.executions) {
      const memory = executionMemory(execution.execution_id);
      const section = el("section", "local-monitor-session-execution"); section.dataset.executionId = execution.execution_id;
      const toggle = el("button", "local-monitor-session-execution-toggle"); toggle.type = "button"; toggle.dataset.executionToggle = "";
      toggle.setAttribute("aria-expanded", String(memory.open));
      toggle.append(el("strong", null, `実行 ${execution.source ?? "不明"} · ${execution.status}`), el("span", null, `${format(execution.child_count)} activity · ${timingLabel(execution)}`));
      toggle.addEventListener("click", async () => {
        memory.open = !memory.open;
        if (memory.open && !memory.pages.has("root")) await loadTimeline(execution.execution_id);
        renderExecutions();
      });
      section.append(toggle);
      if (memory.open) section.append(renderNodes(execution, memory, null, 0));
      executions.append(section);
    }
  }

  async function selectNode(executionId, nodeId, push) {
    const exactTarget = () => selectNode(executionId, nodeId, false);
    const detail = await requestJson(requestUrl(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/nodes/${nodeId}`,
      { workspace_revision: state.revision }), exactTarget);
    if (detail.workspace_revision !== state.revision || detail.node?.node_id !== nodeId
        || detail.node.execution_id !== executionId) return fallbackSelection(push);
    state.selectedExecutionId = executionId; state.selectedNodeId = nodeId;
    const memory = executionMemory(executionId); memory.open = true;
    const path = (detail.parent_path ?? []).filter(parent => ["exact", "explicit"].includes(parent.relationship_authority));
    for (const parent of path) memory.expanded.add(parent.node_id);
    if (!memory.pages.has("root")) await loadTimeline(executionId);
    for (const parent of path.slice(1)) if (!memory.pages.has(parent.node_id)) await loadTimeline(executionId, parent.node_id);
    const inspector = root.querySelector("[data-session-overview]");
    inspector.replaceChildren(el("h2", null, detail.node.name?.state === "recorded" ? detail.node.name.text : detail.node.kind),
      el("p", null, `${detail.node.kind} · ${detail.node.status} · ${timingLabel(detail.node)}`));
    if (push) window.LocalMonitorV1History.push({ execution: executionId, node: nodeId });
    renderExecutions();
  }

  function fallbackSelection(push) {
    state.selectedExecutionId = null; state.selectedNodeId = null;
    if (push) window.LocalMonitorV1History.push({ execution: null, node: null });
  }

  async function applyRoute(route) {
    if (!state.summary) return;
    if (route.execution && route.node) await selectNode(route.execution, route.node, false);
    else fallbackSelection(false);
  }

  function render(summary) {
    const session = summary.session;
    root.querySelector("[data-session-breadcrumb]").textContent = session.instruction.label || "Session";
    const context = root.querySelector("[data-session-context-content]");
    context.replaceChildren(el("strong", null, session.instruction.label || "Session"), el("span", null, ` ${session.status}`));
    const source = el("span"); source.dataset.sessionSource = "";
    if (session.source.state === "recorded") source.textContent = session.source.values.map(window.LocalMonitorV1FactState.sessionSourceLabel).join(" / ");
    else renderFact(source, { state: session.source.state, count: null });
    const time = el("span"); time.dataset.sessionTime = "";
    if (session.timing.state === "recorded") time.textContent = session.timing.ended_at === null
      ? `${session.timing.started_at} から実行中` : `${session.timing.started_at} – ${session.timing.ended_at} · ${format(session.timing.duration_ms)} ms`;
    else renderFact(time, { state: session.timing.state, count: null });
    context.append(source, time);
    if (session.archive.state !== "active") context.append(el("span", null, " アーカイブ済み"));
    if (session.capture.state !== "complete") context.append(el("span", null, " 記録に制限があります"));

    const summaryRoot = root.querySelector("[data-session-summary]");
    summaryRoot.replaceChildren();
    const total = tokenMetric("トークン合計", session.tokens.total);
    if (session.tokens.total.state === "recorded"
        && session.tokens.input.state === "recorded" && session.tokens.output.state === "recorded"
        && session.tokens.total.value === session.tokens.input.value + session.tokens.output.value) {
      renderBars(total, session.tokens.input, session.tokens.output, "local-monitor-token-bar", "入力トークン", "出力トークン");
    }
    const cache = tokenMetric("入力トークンの内訳", session.tokens.cache_read_ratio_basis_points);
    if (session.tokens.input.state === "recorded" && session.tokens.cache_read.state === "recorded"
        && session.tokens.new_input.state === "recorded" && session.tokens.cache_read_ratio_basis_points.state === "recorded"
        && session.tokens.input.value === session.tokens.cache_read.value + session.tokens.new_input.value
        && session.tokens.cache_read_ratio_basis_points.value === Math.round(session.tokens.cache_read.value * 10000 / session.tokens.input.value)) {
      renderBars(cache, session.tokens.cache_read, session.tokens.new_input, "local-monitor-cache-bar", "cache read", "new input");
    }
    if (session.tokens.cache_creation.state === "recorded") cache.append(el("small", null, `cache write ${format(session.tokens.cache_creation.value)}`));
    const input = namedFact("入力", session.tokens.input, "Input");
    const output = namedFact("出力", session.tokens.output, "Output");
    const cacheRead = namedFact("cache read", session.tokens.cache_read, "CacheRead");
    const newInput = namedFact("new input", session.tokens.new_input, "NewInput");
    const coverageCard = el("div", "local-monitor-session-summary-card"); coverageCard.dataset.sessionFixedCoverage = "";
    coverageCard.append(el("h2", null, "取得範囲"));
    const coverageList = el("ul", "local-monitor-session-coverage");
    for (const item of session.capture.coverage) {
      const row = el("li"); row.append(el("span", null, `${item.signal_family}: `));
      const factTarget = row.appendChild(el("span"));
      if (item.state === "recorded") factTarget.textContent = "記録済み";
      else if (item.state === "complete_zero") factTarget.textContent = "0件（完全）";
      else renderFact(factTarget, { state: item.state, count: null });
      coverageList.append(row);
    }
    coverageCard.append(coverageList);
    summaryRoot.append(total, cache, input, output, cacheRead, newInput, coverageCard);
    for (const [key, label] of [["skill", "Skill"], ["tool", "Tool"], ["subagent", "Sub-agent"], ["error", "Error"], ["retry", "Retry"]]) {
      const card = el("div", "local-monitor-session-summary-card"); card.append(el("h2", null, label));
      renderFact(card.appendChild(el("div")), session.activity[key]); summaryRoot.append(card);
    }

    for (const execution of summary.executions) executionMemory(execution.execution_id).open = execution.latest;
    renderExecutions();

    const overview = root.querySelector("[data-session-overview]"); overview.replaceChildren(el("h2", null, "Session overview"));
    overview.append(el("h3", null, "最初の指示"), el("p", null, session.instruction.label || "記録されていません"));
    overview.append(el("p", null, session.instruction.additional_count === null
      ? "追加の指示 今回の記録にはありません" : `追加の指示 ${format(session.instruction.additional_count)}件`));
    overview.append(el("p", null, `状態 ${session.status} · 実行 ${format(summary.executions.length)}件`));
    const coverage = el("ul");
    for (const item of session.capture.coverage) coverage.append(el("li", null, `${item.signal_family}: ${item.state}`));
    overview.append(el("h3", null, "取得範囲"), coverage);
    const technical = el("details"); technical.append(el("summary", null, "技術情報"), el("p", null, `revision ${summary.workspace_revision}`)); overview.append(technical);
  }

  async function load(refresh = false, applyCurrentRoute = true) {
    try {
      const response = await fetch(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/summary`, { headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error("Session summary unavailable");
      const summary = validate(JSON.parse(await response.text()));
      if (refresh && state.revision !== summary.workspace_revision) state.executionState.clear();
      state.summary = summary; state.revision = summary.workspace_revision; render(summary);
      const route = window.LocalMonitorV1History.current();
      if (applyCurrentRoute && route.execution && route.node) await applyRoute(route);
      else {
        const latest = summary.executions.find(execution => execution.latest);
        if (latest && !executionMemory(latest.execution_id).pages.has("root")) await loadTimeline(latest.execution_id);
      }
    } catch {
      root.querySelector("[data-session-context-content]").textContent = "Session を読み込めませんでした";
    }
  }

  document.addEventListener("cao-route-state", event => applyRoute(event.detail));
  load();
})();
