(() => {
  "use strict";

  const root = document.querySelector("[data-session-workspace]");
  if (!root || !window.LocalMonitorV1History || !window.LocalMonitorV1Paths || !window.LocalMonitorV1FactState) return;

  const UUID_V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const NODE = /^node-[0-9a-f]{32}$/;
  const REVISION = /^[0-9a-f]{64}$/;
  const STATES = new Set(["recorded", "not_observed", "source_unsupported", "capture_gap", "certification_pending", "not_captured", "expired", "redacted", "malformed", "oversized", "inconsistent", "projection_invalid"]);
  const ROOT_KEYS = ["schema_version", "workspace_revision", "session", "executions", "technical_references"];
  const SESSION_KEYS = ["session_id", "status", "completeness", "assignment", "archive", "instruction", "source", "model", "version", "timing", "tokens", "activity", "capture"];
  const TOKEN_KEYS = ["authority", "state", "available_execution_count", "total_execution_count", "input", "output", "total", "reasoning", "cache_read", "cache_creation", "new_input", "cache_read_ratio_basis_points"];
  const ACTIVITY_KEYS = ["skill", "tool", "subagent", "error", "retry"];
  const state = { summary: null, revision: null, selectedNodeId: null, selectedExecutionId: null };
  window.LocalMonitorSessionWorkspace = state;

  const exact = (value, keys) => value && typeof value === "object" && !Array.isArray(value)
    && Object.keys(value).length === keys.length && Object.keys(value).every((key, index) => key === keys[index]);
  const nonnegative = value => Number.isSafeInteger(value) && value >= 0;
  const fact = value => exact(value, ["state", "value"]) && STATES.has(value.state)
    && (value.state === "recorded" ? nonnegative(value.value) : value.value === null);
  const countFact = value => exact(value, ["state", "count"]) && STATES.has(value.state)
    && (value.state === "recorded" ? nonnegative(value.count) : value.count === null);

  function tokens(value) {
    return exact(value, TOKEN_KEYS) && ["session_run", "execution_sum"].includes(value.authority)
      && STATES.has(value.state) && nonnegative(value.available_execution_count) && nonnegative(value.total_execution_count)
      && value.available_execution_count <= value.total_execution_count
      && ["input", "output", "total", "reasoning", "cache_read", "cache_creation", "new_input", "cache_read_ratio_basis_points"].every(key => fact(value[key]));
  }

  function activity(value) {
    return exact(value, ACTIVITY_KEYS) && ACTIVITY_KEYS.every(key => countFact(value[key]));
  }

  function validate(summary) {
    if (!exact(summary, ROOT_KEYS) || summary.schema_version !== "local-monitor-session-summary.response.v2"
        || !REVISION.test(summary.workspace_revision) || !exact(summary.session, SESSION_KEYS)
        || summary.session.session_id !== root.dataset.sessionId || !UUID_V7.test(summary.session.session_id)
        || !["active", "completed", "failed", "unknown"].includes(summary.session.status)
        || !["full", "partial"].includes(summary.session.completeness)
        || !exact(summary.session.assignment, ["state", "authority", "revision", "repository_id", "candidate_repository_ids"])
        || !nonnegative(summary.session.assignment.revision) || !Array.isArray(summary.session.assignment.candidate_repository_ids)
        || !exact(summary.session.archive, ["state", "revision", "effectively_eligible", "exclusion_reason"])
        || !nonnegative(summary.session.archive.revision) || typeof summary.session.archive.effectively_eligible !== "boolean"
        || !exact(summary.session.instruction, ["state", "label", "additional_count", "content_available"])
        || !STATES.has(summary.session.instruction.state) || typeof summary.session.instruction.label !== "string"
        || !nonnegative(summary.session.instruction.additional_count) || typeof summary.session.instruction.content_available !== "boolean"
        || !tokens(summary.session.tokens) || !activity(summary.session.activity)
        || !exact(summary.session.capture, ["state", "notes", "coverage"]) || !Array.isArray(summary.session.capture.notes)
        || !Array.isArray(summary.session.capture.coverage) || !Array.isArray(summary.executions)
        || !exact(summary.technical_references, ["native_session_ids", "trace_ids"])
        || !Array.isArray(summary.technical_references.native_session_ids) || !Array.isArray(summary.technical_references.trace_ids)) throw new TypeError("invalid Session summary");
    for (const key of ["source", "model", "version"]) {
      const value = summary.session[key];
      if (!exact(value, ["state", "values"]) || !STATES.has(value.state) || !Array.isArray(value.values) || value.values.some(item => typeof item !== "string")) throw new TypeError("invalid Session summary");
    }
    if (summary.session.capture.coverage.some(value => !exact(value, ["signal_family", "state"])
        || typeof value.signal_family !== "string" || !STATES.has(value.state))) throw new TypeError("invalid Session summary");
    if (summary.technical_references.native_session_ids.some(value => typeof value !== "string")
        || summary.technical_references.trace_ids.some(value => typeof value !== "string" || !/^[0-9a-f]{32}$/.test(value))) throw new TypeError("invalid Session summary");
    if (!exact(summary.session.timing, ["state", "started_at", "ended_at", "last_seen_at", "duration_ms"]) || !STATES.has(summary.session.timing.state)) throw new TypeError("invalid Session summary");
    for (const execution of summary.executions) {
      if (!exact(execution, ["execution_id", "node_id", "latest", "source", "model", "lifecycle", "status", "timing", "tokens", "activity", "child_count"])
          || !UUID_V7.test(execution.execution_id) || !NODE.test(execution.node_id) || typeof execution.latest !== "boolean"
          || !tokens(execution.tokens) || !activity(execution.activity) || !nonnegative(execution.child_count)) throw new TypeError("invalid Session summary");
    }
    return summary;
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

  function tokenMetric(title, value) {
    const card = el("div", "local-monitor-session-summary-card");
    card.append(el("h2", null, title));
    if (value.state === "recorded") card.append(el("strong", null, format(value.value)));
    else renderFact(card.appendChild(el("div")), value);
    return card;
  }

  function renderBars(card, first, second, className) {
    if (first.state !== "recorded" || second.state !== "recorded") return;
    const total = first.value + second.value;
    if (total <= 0) return;
    const bar = el("div", `local-monitor-session-bar ${className}`);
    const a = el("span"); a.style.width = `${first.value / total * 100}%`;
    const b = el("span"); b.style.width = `${second.value / total * 100}%`;
    bar.append(a, b); card.append(bar);
  }

  function render(summary) {
    const session = summary.session;
    root.querySelector("[data-session-breadcrumb]").textContent = session.instruction.label || "Session";
    const context = root.querySelector("[data-session-context-content]");
    context.replaceChildren(el("strong", null, session.instruction.label || "Session"), el("span", null, ` ${session.status}`));
    if (session.archive.state !== "active") context.append(el("span", null, " アーカイブ済み"));
    if (session.capture.state !== "complete") context.append(el("span", null, " 記録に制限があります"));

    const summaryRoot = root.querySelector("[data-session-summary]");
    summaryRoot.replaceChildren();
    const total = tokenMetric("トークン合計", session.tokens.total);
    if (session.tokens.total.state === "recorded"
        && session.tokens.input.state === "recorded" && session.tokens.output.state === "recorded"
        && session.tokens.total.value === session.tokens.input.value + session.tokens.output.value) {
      renderBars(total, session.tokens.input, session.tokens.output, "local-monitor-token-bar");
    }
    const cache = tokenMetric("入力トークンの内訳", session.tokens.cache_read_ratio_basis_points);
    if (session.tokens.input.state === "recorded" && session.tokens.cache_read.state === "recorded"
        && session.tokens.new_input.state === "recorded" && session.tokens.cache_read_ratio_basis_points.state === "recorded"
        && session.tokens.input.value === session.tokens.cache_read.value + session.tokens.new_input.value
        && session.tokens.cache_read_ratio_basis_points.value === Math.round(session.tokens.cache_read.value * 10000 / session.tokens.input.value)) {
      renderBars(cache, session.tokens.cache_read, session.tokens.new_input, "local-monitor-cache-bar");
    }
    if (session.tokens.cache_creation.state === "recorded") cache.append(el("small", null, `cache write ${format(session.tokens.cache_creation.value)}`));
    summaryRoot.append(total, cache);
    for (const [key, label] of [["skill", "Skill"], ["tool", "Tool"], ["subagent", "Sub-agent"], ["error", "Error"], ["retry", "Retry"]]) {
      const card = el("div", "local-monitor-session-summary-card"); card.append(el("h2", null, label));
      renderFact(card.appendChild(el("div")), session.activity[key]); summaryRoot.append(card);
    }

    const executions = root.querySelector("[data-session-executions]"); executions.replaceChildren();
    for (const execution of summary.executions) {
      const section = el("section", "local-monitor-session-execution");
      section.dataset.executionId = execution.execution_id;
      section.append(el("h2", null, `実行 ${execution.source} · ${execution.status}`), el("p", null, `${format(execution.child_count)} activity`));
      executions.append(section);
    }

    const overview = root.querySelector("[data-session-overview]"); overview.replaceChildren(el("h2", null, "Session overview"));
    overview.append(el("h3", null, "最初の指示"), el("p", null, session.instruction.label || "記録されていません"));
    overview.append(el("p", null, `追加の指示 ${format(session.instruction.additional_count)}件`));
    overview.append(el("p", null, `状態 ${session.status} · 実行 ${format(summary.executions.length)}件`));
    const coverage = el("ul");
    for (const item of session.capture.coverage) coverage.append(el("li", null, `${item.signal_family}: ${item.state}`));
    overview.append(el("h3", null, "取得範囲"), coverage);
    const technical = el("details"); technical.append(el("summary", null, "技術情報"), el("p", null, `revision ${summary.workspace_revision}`)); overview.append(technical);
  }

  async function load() {
    try {
      const response = await fetch(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/summary`, { headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error("Session summary unavailable");
      const summary = validate(JSON.parse(await response.text()));
      state.summary = summary; state.revision = summary.workspace_revision; render(summary);
    } catch {
      root.querySelector("[data-session-context-content]").textContent = "Session を読み込めませんでした";
    }
  }

  load();
})();
