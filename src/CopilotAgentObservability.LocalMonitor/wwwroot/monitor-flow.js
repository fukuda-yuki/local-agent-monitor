// Local Ingestion Monitor — trace detail flow view + view orchestration
// (Sprint18 §6.3). Builds the shared span model from the sanitized spans API,
// renders the vertical flow chart, drives the フロー | waterfall toggle, the
// エラーのみ filter, span selection, and the ?view=&span= URL state.
//
// Sanitized boundary: reads only /api/monitor/traces/{id}/spans and the
// sanitized /agent-graph ownership model. It never
// fetches a raw-bearing route (the span inspector's raw detail is a separate
// module gated on data-raw-available). All DOM nodes are built with
// createElement / textContent; no markup strings are ever injected.
(() => {
  "use strict";

  const root = document.getElementById("trace-detail-root");
  if (!root) return; // Not the trace detail page — no-op.

  const traceId = root.dataset.traceId;
  const flowView = document.getElementById("flow-view");
  const waterfallView = document.getElementById("waterfall-view");
  const statusLine = document.getElementById("flow-status");
  const errorsOnly = document.getElementById("errors-only");
  const viewToggle = document.getElementById("view-toggle");
  const agentSummary = document.getElementById("agent-summary");
  const agentSummaryState = document.getElementById("agent-summary-state");
  const agentSummaryMeta = document.getElementById("agent-summary-meta");

  const state = {
    view: "flow",
    errorsOnly: false,
    selectedSpanId: null,
    expandedRuns: new Set(),
    collapsedAgents: new Set(),
  };

  let model = null;

  /* ── Formatting ── */

  function compactTokens(value) {
    if (value === null || value === undefined) return "—";
    const abs = Math.abs(value);
    if (abs >= 1_000_000) return `${Math.round((value / 1_000_000) * 10) / 10}M`;
    if (abs >= 1_000) return `${Math.round((value / 1_000) * 10) / 10}K`;
    return String(value);
  }

  function fmtDuration(ms) {
    if (ms === null || ms === undefined) return "—";
    if (ms < 1000) return `${Math.round(ms)}ms`;
    if (ms < 60000) return `${Math.round(ms / 100) / 10}s`;
    return `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`;
  }

  function fmtClock(ms) {
    if (ms === null || ms === undefined) return "—";
    const date = new Date(ms);
    return `${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}:${String(date.getSeconds()).padStart(2, "0")}`;
  }

  /* ── Span model ── */

  async function fetchAllSpans() {
    const spans = [];
    let after = 0;
    while (true) {
      const resp = await fetch(`/api/monitor/traces/${encodeURIComponent(traceId)}/spans?after=${after}&limit=200`, { cache: "no-store" });
      if (!resp.ok) throw new Error(`span api returned ${resp.status}`);
      const page = await resp.json();
      spans.push(...page.items);
      if (page.next_cursor === null || page.next_cursor === undefined) return spans;
      after = page.next_cursor;
    }
  }

  async function fetchAgentGraph() {
    try {
      const resp = await fetch(`/api/monitor/traces/${encodeURIComponent(traceId)}/agent-graph`, { cache: "no-store" });
      if (!resp.ok) return null;
      return await resp.json();
    } catch {
      return null;
    }
  }

  function relationshipLabel(confidence) {
    if (confidence === "inferred") return "推定";
    if (confidence === "unknown") return "判定不能";
    return null;
  }

  function relationshipBadge(confidence) {
    const label = relationshipLabel(confidence);
    if (!label) return null;
    const badge = document.createElement("span");
    badge.className = `relationship-badge relationship-${confidence}`;
    badge.textContent = label;
    return badge;
  }

  function renderAgentSummary(graph) {
    if (!agentSummary || !agentSummaryState || !agentSummaryMeta) return;
    if (!graph?.summary) {
      agentSummary.classList.add("agent-summary-unavailable");
      agentSummaryState.textContent = "Sub-agent利用を判定できません";
      agentSummaryMeta.textContent = "Agent実行グラフを取得できませんでした";
      return;
    }

    const summary = graph.summary;
    agentSummary.classList.remove("agent-summary-unavailable");
    agentSummaryState.textContent = summary.agent_presence === "undeterminable"
      ? "Sub-agent利用を判定できません"
      : summary.subagent_invocation_count > 0
        ? `Sub-agent ${summary.subagent_invocation_count}回検出`
        : "Sub-agentは検出されませんでした";
    const main = summary.main_agent_name ? `main ${summary.main_agent_name}` : "main —";
    const rootAgents = (graph.agents ?? []).filter((agent) => agent.agent_role === "root");
    const rootNames = [...new Set(rootAgents.map((agent) => agent.agent_name).filter(Boolean))];
    const shownRootNames = rootNames.slice(0, 3);
    const rootNameLabel = shownRootNames.length === 0
      ? ""
      : ` ${shownRootNames.join(", ")}${rootNames.length > shownRootNames.length ? ` +${rootNames.length - shownRootNames.length}` : ""}`;
    const quality = summary.relationship_quality === "partially_inferred"
      ? "一部推定"
      : summary.relationship_quality === "undeterminable"
        ? "判定不能"
        : "exact";
    const meta = [
      main,
      `root ${summary.root_agent_count}${rootNameLabel}`,
      `呼出 ${summary.subagent_invocation_count}`,
      `ユニーク ${summary.unique_subagent_count}`,
      `最大深度 ${summary.max_agent_depth}`,
      `Agent並行 ${summary.parallel_agent_group_count}`,
      `関係 ${quality}`,
    ];
    if (graph.graph_warnings?.includes("time_range_inconsistent")) meta.push("時刻矛盾あり");
    agentSummaryMeta.textContent = meta.join(" · ");
  }

  function parseMs(timestamp) {
    if (!timestamp) return null;
    const parsed = Date.parse(timestamp);
    return Number.isNaN(parsed) ? null : parsed;
  }

  const INTENT_RULES = [
    { intent: "調査", pattern: /read|grep|glob|search|list|fetch|view|cat/i },
    { intent: "編集", pattern: /str_replace|write|edit|patch|create|insert|replace/i },
    { intent: "実行", pattern: /bash|run|exec|terminal|shell|command/i },
    { intent: "検証", pattern: /test|check|verify|lint|build/i },
  ];

  function intentLabel(tools) {
    const intents = [];
    for (const tool of tools) {
      const rule = INTENT_RULES.find((candidate) => candidate.pattern.test(tool.label));
      if (rule && !intents.includes(rule.intent)) intents.push(rule.intent);
      if (intents.length === 2) break;
    }
    if (intents.length === 0) return "LLM 応答";
    return intents.join("と");
  }

  function normalize(span) {
    const startMs = parseMs(span.start_time);
    const endMs = parseMs(span.end_time);
    const durationMs = span.duration_ms ?? (startMs !== null && endMs !== null ? Math.max(0, endMs - startMs) : null);
    const isLlm = span.category === "llm_call" || span.operation === "chat";
    const isAgent = span.category === "agent_invocation" || span.operation === "invoke_agent";
    const isTool = !isLlm && !isAgent && (span.category === "tool_call" || span.tool_name || span.mcp_tool_name || span.operation === "execute_tool");
    return {
      ...span,
      startMs,
      endMs,
      durationMs,
      isError: span.status === "error",
      kind: isLlm ? "llm" : isAgent ? "agent" : isTool ? "tool" : "other",
      label: isTool
        ? (span.tool_name ?? span.mcp_tool_name ?? span.operation ?? "tool")
        : isAgent
          ? (span.agent_name ?? span.operation ?? "agent")
          : (span.operation ?? span.category ?? "span"),
    };
  }

  function buildModel(rawSpans, agentGraph) {
    const spans = rawSpans.map(normalize);
    const spansById = new Map();
    const byId = new Map();
    for (const span of spans.filter((candidate) => candidate.span_id)) {
      if (!spansById.has(span.span_id)) spansById.set(span.span_id, []);
      spansById.get(span.span_id).push(span);
      if (!byId.has(span.span_id)) byId.set(span.span_id, span);
    }

    const ownershipById = new Map((agentGraph?.span_ownership ?? [])
      .filter((ownership) => ownership.span_id)
      .map((ownership) => [ownership.span_id, ownership]));
    for (const span of spans) {
      const ownership = ownershipById.get(span.span_id);
      if (ownership) {
        span.owningAgentSpanId = ownership.owning_agent_span_id;
        span.relationshipSource = ownership.relationship_source;
        span.relationshipConfidence = ownership.relationship_confidence;
      }
    }

    const startCandidates = spans.map((span) => span.startMs).filter((value) => value !== null);
    const endCandidates = spans.map((span) => span.endMs ?? span.startMs).filter((value) => value !== null);
    const range = {
      startMs: startCandidates.length > 0 ? Math.min(...startCandidates) : 0,
      endMs: endCandidates.length > 0 ? Math.max(...endCandidates) : 1,
    };
    if (range.endMs <= range.startMs) range.endMs = range.startMs + 1;

    const sortKey = (span) => span.startMs ?? range.startMs + span.span_ordinal;
    const llmSpans = spans.filter((span) => span.kind === "llm").sort((a, b) => sortKey(a) - sortKey(b) || a.span_ordinal - b.span_ordinal);
    const toolSpans = spans.filter((span) => span.kind === "tool").sort((a, b) => sortKey(a) - sortKey(b) || a.span_ordinal - b.span_ordinal);
    const graphAgents = agentGraph?.agents ?? [];
    const agentIdCounts = new Map();
    for (const agent of graphAgents) {
      if (agent.span_id) agentIdCounts.set(agent.span_id, (agentIdCounts.get(agent.span_id) ?? 0) + 1);
    }
    const usedAgentCandidates = new Map();
    const agentSpans = [];
    const agentById = new Map();
    let agentIndex = 0;
    for (const agent of graphAgents) {
      const candidates = agent.span_id
        ? (spansById.get(agent.span_id) ?? []).filter((span) => span.kind === "agent")
        : [];
      const candidateIndex = usedAgentCandidates.get(agent.span_id) ?? 0;
      const span = candidates[candidateIndex] ?? normalize({
        span_id: agent.span_id,
        span_ordinal: Number.MAX_SAFE_INTEGER,
        operation: "invoke_agent",
        category: "agent_invocation",
        agent_name: agent.agent_name,
        start_time: agent.started_at,
        end_time: agent.ended_at,
        duration_ms: agent.duration_ms,
        input_tokens: agent.input_tokens,
        output_tokens: agent.output_tokens,
        total_tokens: agent.total_tokens,
        status: agent.status,
      });
      if (agent.span_id) usedAgentCandidates.set(agent.span_id, candidateIndex + 1);
      span.agent = agent;
      span.relationshipSource = agent.relationship_source;
      span.relationshipConfidence = agent.relationship_confidence;
      span.owningAgentSpanId = agent.caller_agent_span_id;
      span.hasUniqueAgentId = Boolean(agent.span_id && agentIdCounts.get(agent.span_id) === 1);
      span.agentUiKey = span.hasUniqueAgentId ? agent.span_id : `agent-${agentIndex}`;
      agentIndex += 1;
      agentSpans.push(span);
      if (span.hasUniqueAgentId) {
        agentById.set(agent.span_id, span);
        byId.set(agent.span_id, span);
      }
    }

    const turns = llmSpans.map((span, index) => ({ span, index, tools: [], groups: [], title: "", matchesFilter: false }));
    const turnBySpanId = new Map(turns.filter((turn) => turn.span.span_id).map((turn) => [turn.span.span_id, turn]));

    const orphanTools = [];
    for (const tool of toolSpans) {
      let turn = tool.parent_span_id ? turnBySpanId.get(tool.parent_span_id) : undefined;
      if (!turn && tool.startMs !== null) {
        // Fall back to the last turn that started at or before the tool.
        for (const candidate of turns) {
          const turnStart = candidate.span.startMs;
          if (turnStart === null || turnStart <= tool.startMs) turn = candidate;
        }
      }
      if (!turn && turns.length > 0) turn = turns[0];
      if (turn) {
        turn.tools.push(tool);
      } else {
        orphanTools.push(tool);
      }
    }

    for (const turn of turns) {
      // Parallel groups: same parent, overlapping [start, end] windows (§9).
      const groups = [];
      let current = null;
      let currentEnd = null;
      for (const tool of turn.tools) {
        const start = tool.startMs;
        const end = tool.endMs ?? tool.startMs;
        if (current && start !== null && currentEnd !== null && start < currentEnd) {
          current.tools.push(tool);
          currentEnd = Math.max(currentEnd, end ?? start);
        } else {
          current = { tools: [tool], parallel: false, maxDurationMs: null };
          groups.push(current);
          currentEnd = end;
        }
      }
      for (const group of groups) {
        group.parallel = group.tools.length > 1;
        const durations = group.tools.map((tool) => tool.durationMs).filter((value) => value !== null);
        group.maxDurationMs = durations.length > 0 ? Math.max(...durations) : null;
      }
      turn.groups = groups;
      turn.title = `ターン${turn.index + 1} · ${intentLabel(turn.tools)}`;
      turn.hasError = turn.span.isError || turn.tools.some((tool) => tool.isError);
      turn.matchesFilter = turn.hasError;
    }

    // §9: an error is recovered when a same-kind operation succeeds later
    // (same tool label for tools; any later successful turn for llm calls).
    const errorSpans = [];
    for (const turn of turns) {
      if (turn.span.isError) {
        const laterTurnOk = turns.some((candidate) => candidate.index > turn.index && !candidate.span.isError);
        errorSpans.push({ span: turn.span, turn, recovered: laterTurnOk });
      }
      for (const tool of turn.tools) {
        if (!tool.isError) continue;
        const retry = toolSpans.find((candidate) => !candidate.isError
          && candidate.label === tool.label
          && (candidate.startMs ?? 0) > (tool.startMs ?? 0));
        tool.recovered = Boolean(retry);
        tool.recoveredBy = retry ?? null;
        errorSpans.push({ span: tool, turn, recovered: tool.recovered });
      }
    }
    errorSpans.sort((a, b) => ((a.span.startMs ?? 0) - (b.span.startMs ?? 0)) || (a.span.span_ordinal - b.span.span_ordinal));

    const ownedSpans = new Map();
    for (const span of spans.filter((candidate) => candidate.kind !== "agent")) {
      const ownerId = span.owningAgentSpanId ?? null;
      if (!ownedSpans.has(ownerId)) ownedSpans.set(ownerId, []);
      ownedSpans.get(ownerId).push(span);
    }

    const childAgents = new Map();
    for (const agent of agentSpans) {
      const callerId = agent.agent?.caller_agent_span_id;
      const ownerId = callerId && agentById.has(callerId) ? callerId : null;
      if (!childAgents.has(ownerId)) childAgents.set(ownerId, []);
      childAgents.get(ownerId).push(agent);
      const direct = agent.hasUniqueAgentId ? ownedSpans.get(agent.span_id) ?? [] : [];
      agent.agent = {
        ...(agent.agent ?? {}),
        ownedTurnCount: direct.filter((span) => span.kind === "llm").length,
        ownedToolCount: direct.filter((span) => span.kind === "tool").length,
      };
    }

    const sortSpans = (items) => items.sort((a, b) => sortKey(a) - sortKey(b) || a.span_ordinal - b.span_ordinal);
    for (const items of ownedSpans.values()) sortSpans(items);
    for (const items of childAgents.values()) sortSpans(items);

    return {
      traceId,
      spans,
      byId,
      range,
      turns,
      agentSpans,
      agentById,
      ownedSpans,
      childAgents,
      agentGraph,
      orphanTools,
      errorSpans,
    };
  }

  /* ── Flow rendering ── */

  function marker(kindClass, symbol) {
    const markerNode = document.createElement("span");
    markerNode.className = `flow-marker ${kindClass}`;
    markerNode.setAttribute("aria-hidden", "true");
    markerNode.textContent = symbol;
    return markerNode;
  }

  function flowRow(markerNode, bodyNode, options = {}) {
    const row = document.createElement("div");
    row.className = `flow-row${options.className ? ` ${options.className}` : ""}`;
    const rail = document.createElement("span");
    rail.className = "flow-rail-cell";
    rail.append(markerNode);
    row.append(rail, bodyNode);
    return row;
  }

  function spanMeta(span) {
    const parts = [];
    if (span.total_tokens !== null && span.total_tokens !== undefined) parts.push(`${compactTokens(span.total_tokens)} tok`);
    if (span.cache_read_tokens !== null && span.cache_read_tokens !== undefined && span.input_tokens) {
      parts.push(`cache ${Math.round((span.cache_read_tokens * 100) / span.input_tokens)}%`);
    }
    if (span.durationMs !== null) parts.push(fmtDuration(span.durationMs));
    return parts.join(" · ");
  }

  function toolCard(tool) {
    const card = document.createElement("button");
    card.type = "button";
    card.className = `tool-card${tool.isError ? " tool-error" : ""}`;
    card.dataset.spanId = tool.span_id ?? "";
    const head = document.createElement("span");
    head.className = "tool-card-head";
    const mark = document.createElement("span");
    mark.className = "tool-mark";
    mark.setAttribute("aria-hidden", "true");
    mark.textContent = "■";
    const name = document.createElement("span");
    name.className = "tool-name monitor-mono";
    name.textContent = tool.label;
    const durationNode = document.createElement("span");
    durationNode.className = "tool-duration monitor-mono";
    durationNode.textContent = fmtDuration(tool.durationMs);
    head.append(mark, name, durationNode);
    card.append(head);

    if (tool.isError && !tool.recovered) card.classList.add("tool-unrecovered");

    const sub = document.createElement("span");
    sub.className = `tool-sub${tool.isError ? " sub-error" : " sub-ok"}`;
    sub.textContent = tool.isError
      ? `✕ 失敗${tool.error_type ? ` · ${tool.error_type}` : ""}`
      : "成功";
    card.append(sub);

    if (tool.isError) {
      const pill = document.createElement("span");
      pill.className = `recovery-pill ${tool.recovered ? "pill-recovered" : "pill-unrecovered"}`;
      pill.textContent = tool.recovered ? "回復済み → 再試行あり" : "未回復";
      card.append(pill);
    }
    const relationship = relationshipBadge(tool.relationshipConfidence);
    if (relationship) card.append(relationship);
    return card;
  }

  function turnCard(turn) {
    const card = document.createElement("button");
    card.type = "button";
    card.className = `turn-card${turn.span.isError ? " turn-error" : ""}`;
    card.dataset.spanId = turn.span.span_id ?? "";
    const title = document.createElement("span");
    title.className = "turn-title";
    title.textContent = turn.title;
    const meta = document.createElement("span");
    meta.className = "turn-meta monitor-mono";
    meta.textContent = spanMeta(turn.span);
    card.append(title, meta);
    const relationship = relationshipBadge(turn.span.relationshipConfidence);
    if (relationship) card.append(relationship);
    return card;
  }

  function collapsedRow(startIndex, endIndex, runId, label) {
    const body = document.createElement("button");
    body.type = "button";
    body.className = "collapsed-turns";
    body.dataset.runId = runId;
    body.textContent = `ターン${startIndex + 1}〜${endIndex + 1} · ${label} — 展開`;
    return flowRow(marker("marker-collapsed", "…"), body, { className: "flow-collapsed-row" });
  }

  function renderLegacyFlow() {
    if (!model) return;
    flowView.replaceChildren();

    const rail = document.createElement("div");
    rail.className = "flow-rail";
    flowView.append(rail);

    // Start marker.
    const startBody = document.createElement("div");
    startBody.className = "flow-terminal flow-start";
    const startLabel = document.createElement("span");
    startLabel.className = "terminal-label";
    startLabel.textContent = "copilot-agent 開始";
    const startTime = document.createElement("span");
    startTime.className = "terminal-time monitor-mono";
    startTime.textContent = fmtClock(model.range.startMs);
    startBody.append(startLabel, startTime);
    rail.append(flowRow(marker("marker-agent", "◆"), startBody));

    // Turns (optionally errors-only; quiet runs collapsed).
    const visibleTurns = model.turns;
    let runStart = null;

    const flushCollapsedRun = (endIndexExclusive) => {
      if (runStart === null) return;
      const endIndex = endIndexExclusive - 1;
      if (endIndex > runStart) {
        const runId = `${runStart}-${endIndex}`;
        if (state.expandedRuns.has(runId)) {
          for (let i = runStart; i <= endIndex; i++) renderTurn(visibleTurns[i]);
        } else {
          const label = state.errorsOnly ? `正常 — ${countSpans(runStart, endIndex)} スパンを折りたたみ` : "変化の少ないターンを折りたたみ";
          rail.append(collapsedRow(runStart, endIndex, runId, label));
        }
      } else {
        renderTurn(visibleTurns[runStart]);
      }
      runStart = null;
    };

    const countSpans = (from, to) => {
      let count = 0;
      for (let i = from; i <= to; i++) count += 1 + visibleTurns[i].tools.length;
      return count;
    };

    const renderTurn = (turn) => {
      rail.append(flowRow(marker("marker-llm", "●"), turnCard(turn), { className: "flow-turn-row" }));
      for (const group of turn.groups) {
        const branch = document.createElement("div");
        branch.className = "tool-branch";
        if (group.parallel) {
          const head = document.createElement("span");
          head.className = "parallel-head";
          const badge = document.createElement("span");
          badge.className = "parallel-badge";
          badge.textContent = `⑂ 並行 ${group.tools.length} 件`;
          const max = document.createElement("span");
          max.className = "parallel-max monitor-mono";
          max.textContent = group.maxDurationMs !== null ? `max ${fmtDuration(group.maxDurationMs)}` : "";
          head.append(badge, max);
          branch.append(head);
          const lane = document.createElement("div");
          lane.className = "parallel-lane";
          for (const tool of group.tools) lane.append(toolCard(tool));
          branch.append(lane);
        } else {
          for (const tool of group.tools) branch.append(toolCard(tool));
        }
        rail.append(branch);
      }
    };

    const isQuiet = (turn) => turn.tools.length === 0 && !turn.hasError;

    for (let i = 0; i < visibleTurns.length; i++) {
      const turn = visibleTurns[i];
      const collapseThis = state.errorsOnly ? !turn.matchesFilter : isQuiet(turn);
      if (collapseThis) {
        if (runStart === null) runStart = i;
        continue;
      }
      flushCollapsedRun(i);
      renderTurn(turn);
    }
    flushCollapsedRun(visibleTurns.length);

    for (const tool of model.orphanTools) {
      const branch = document.createElement("div");
      branch.className = "tool-branch";
      branch.append(toolCard(tool));
      rail.append(branch);
    }

    // Terminal marker from the rollup status.
    const traceStatus = root.dataset.traceStatus;
    const endBody = document.createElement("div");
    endBody.className = "flow-terminal flow-end";
    const endLabel = document.createElement("span");
    endLabel.className = "terminal-label";
    const endTime = document.createElement("span");
    endTime.className = "terminal-time monitor-mono";
    endTime.textContent = `${fmtClock(model.range.endMs)} · ${fmtDuration(model.range.endMs - model.range.startMs)}`;
    if (traceStatus === "unrecovered") {
      endLabel.textContent = "異常終了 — トレースは未完了のまま終了";
      endBody.classList.add("end-error");
      rail.append(flowRow(marker("marker-error", "✕"), endBody));
    } else {
      endLabel.textContent = traceStatus === "recovered" ? "完了 — エラーから回復" : "完了";
      rail.append(flowRow(marker("marker-ok", "✓"), endBody));
    }
    endBody.append(endLabel, endTime);

    applySelection();
  }

  function appendTurnBranch(container, turn, ownerId) {
    container.append(turnCard(turn));
    for (const originalGroup of turn.groups) {
      const tools = originalGroup.tools.filter((tool) => (tool.owningAgentSpanId ?? null) === ownerId);
      if (tools.length === 0) continue;
      const branch = document.createElement("div");
      branch.className = "tool-branch";
      if (tools.length > 1 && originalGroup.parallel) {
        const head = document.createElement("span");
        head.className = "parallel-head";
        const badge = document.createElement("span");
        badge.className = "parallel-badge";
        badge.textContent = `⑂ 並行 ${tools.length} 件`;
        head.append(badge);
        branch.append(head);
        const lane = document.createElement("div");
        lane.className = "parallel-lane";
        tools.forEach((tool) => lane.append(toolCard(tool)));
        branch.append(lane);
      } else {
        tools.forEach((tool) => branch.append(toolCard(tool)));
      }
      container.append(branch);
    }
  }

  function agentHeader(agent) {
    const head = document.createElement("div");
    head.className = "agent-container-head";
    const select = document.createElement("button");
    select.type = "button";
    select.className = "agent-select";
    select.dataset.spanId = agent.hasUniqueAgentId ? agent.span_id : "";
    select.disabled = !agent.hasUniqueAgentId;
    const roleName = document.createElement("strong");
    roleName.className = "agent-role-name";
    roleName.textContent = `${agent.agent?.agent_role ?? "unknown"} · ${agent.agent?.agent_name ?? agent.label}`;
    select.append(roleName);
    const relationship = relationshipBadge(agent.relationshipConfidence ?? "unknown");
    if (relationship) select.append(relationship);

    const collapse = document.createElement("button");
    collapse.type = "button";
    collapse.className = "agent-collapse";
    collapse.dataset.agentToggle = agent.agentUiKey;
    const collapsed = state.collapsedAgents.has(agent.agentUiKey);
    collapse.setAttribute("aria-expanded", String(!collapsed));
    collapse.setAttribute("aria-label", collapsed ? "Agent セクションを展開する" : "Agent セクションを折りたたむ");
    collapse.textContent = collapsed ? "＋" : "−";
    head.append(select, collapse);
    return head;
  }

  function agentMeta(agent) {
    const detail = agent.agent ?? {};
    const caller = detail.caller_agent_span_id
      ? model.agentById.get(detail.caller_agent_span_id)?.agent?.agent_name ?? detail.caller_agent_span_id
      : "—";
    const meta = document.createElement("div");
    meta.className = "agent-container-meta monitor-mono";
    meta.textContent = [
      `caller ${caller}`,
      detail.model ?? "model —",
      `${fmtClock(agent.startMs)} → ${fmtClock(agent.endMs)}`,
      fmtDuration(detail.duration_ms ?? agent.durationMs),
      `${compactTokens(detail.total_tokens)} tok`,
      detail.status ?? "status —",
      `子Agent ${detail.child_agent_count ?? 0}`,
    ].join(" · ");
    return meta;
  }

  function renderAgent(agent) {
    const container = document.createElement("section");
    container.className = `agent-container relationship-${agent.relationshipConfidence ?? "unknown"}`;
    container.dataset.agentId = agent.hasUniqueAgentId ? agent.span_id : "";
    container.append(agentHeader(agent), agentMeta(agent));

    const content = document.createElement("div");
    content.className = "agent-owned-content";
    content.hidden = state.collapsedAgents.has(agent.agentUiKey);
    const start = document.createElement("div");
    start.className = "agent-terminal agent-start";
    start.textContent = `◆ ${agent.agent?.agent_name ?? agent.label} 開始 · ${fmtClock(agent.startMs)}`;
    content.append(start);

    const direct = agent.hasUniqueAgentId ? model.ownedSpans.get(agent.span_id) ?? [] : [];
    const consumedTools = new Set();
    for (const span of direct) {
      if (span.kind === "llm") {
        const turn = model.turns.find((candidate) => candidate.span.span_id === span.span_id);
        if (turn && (!state.errorsOnly || turn.matchesFilter)) {
          appendTurnBranch(content, turn, agent.span_id);
          turn.tools.filter((tool) => tool.owningAgentSpanId === agent.span_id).forEach((tool) => consumedTools.add(tool.span_id));
        }
      } else if (span.kind === "tool" && !consumedTools.has(span.span_id) && (!state.errorsOnly || span.isError)) {
        const branch = document.createElement("div");
        branch.className = "tool-branch";
        branch.append(toolCard(span));
        content.append(branch);
      }
    }

    appendChildAgents(content, agent.span_id);
    const complete = document.createElement("div");
    complete.className = `agent-terminal agent-complete${agent.isError ? " agent-error" : ""}`;
    complete.textContent = `${agent.isError ? "✕" : "✓"} ${agent.agent?.agent_name ?? agent.label} ${agent.isError ? "異常終了" : "完了"} · ${fmtClock(agent.endMs)}`;
    content.append(complete);
    container.append(content);
    return container;
  }

  function appendChildAgents(container, ownerId) {
    const children = model.childAgents.get(ownerId) ?? [];
    const rendered = new Set();
    for (const group of model.agentGraph?.parallel_groups ?? []) {
      const members = children.filter((child) => group.includes(child.span_id));
      if (members.length < 2) continue;
      const wrapper = document.createElement("div");
      wrapper.className = "agent-parallel-group";
      const label = document.createElement("div");
      label.className = "agent-parallel-label";
      label.textContent = `⑂ Agent並行 ${members.length}件`;
      const lanes = document.createElement("div");
      lanes.className = "agent-parallel-lanes";
      members.forEach((member) => {
        rendered.add(member.span_id);
        lanes.append(renderAgent(member));
      });
      wrapper.append(label, lanes);
      container.append(wrapper);
    }
    children.filter((child) => !rendered.has(child.span_id)).forEach((child) => container.append(renderAgent(child)));
  }

  function renderFlow() {
    if (!model) return;
    if (!model.agentGraph || model.agentSpans.length === 0) {
      renderLegacyFlow();
      return;
    }

    flowView.replaceChildren();
    const execution = document.createElement("div");
    execution.className = "agent-execution-flow";
    const start = document.createElement("div");
    start.className = "flow-terminal flow-start";
    start.textContent = `copilot-agent 開始 · ${fmtClock(model.range.startMs)}`;
    execution.append(start);
    appendChildAgents(execution, null);

    const unowned = model.ownedSpans.get(null) ?? [];
    const unownedSection = document.createElement("div");
    unownedSection.className = "unowned-spans";
    const consumed = new Set();
    for (const span of unowned) {
      if (span.kind === "llm") {
        const turn = model.turns.find((candidate) => candidate.span.span_id === span.span_id);
        if (turn && (!state.errorsOnly || turn.matchesFilter)) {
          appendTurnBranch(unownedSection, turn, null);
          turn.tools.filter((tool) => !tool.owningAgentSpanId).forEach((tool) => consumed.add(tool.span_id));
        }
      } else if (span.kind === "tool" && !consumed.has(span.span_id) && (!state.errorsOnly || span.isError)) {
        unownedSection.append(toolCard(span));
      }
    }
    if (unownedSection.childElementCount > 0) execution.append(unownedSection);

    const end = document.createElement("div");
    end.className = "flow-terminal flow-end";
    end.textContent = root.dataset.traceStatus === "unrecovered" ? "異常終了 — トレースは未完了のまま終了" : "完了";
    execution.append(end);
    flowView.append(execution);
    applySelection();
  }

  /* ── View toggle / filter / URL state ── */

  function renderAll() {
    renderFlow();
    if (window.caoWaterfall) {
      model.collapsedAgents = state.collapsedAgents;
      window.caoWaterfall.render(waterfallView, model, state.errorsOnly);
    }
    applySelection();
  }

  function setView(view) {
    state.view = view;
    if (view === "waterfall" && model && window.caoWaterfall) {
      model.collapsedAgents = state.collapsedAgents;
      window.caoWaterfall.render(waterfallView, model, state.errorsOnly);
      applySelection();
    }
    flowView.hidden = view !== "flow";
    waterfallView.hidden = view !== "waterfall";
    for (const button of viewToggle.querySelectorAll(".view-btn")) {
      button.classList.toggle("active", button.dataset.view === view);
    }
    syncUrl();
  }

  function syncUrl() {
    const params = new URLSearchParams();
    if (state.view !== "flow") params.set("view", state.view);
    if (state.selectedSpanId) params.set("span", state.selectedSpanId);
    const query = params.toString();
    history.replaceState(null, "", query ? `/traces/${traceId}?${query}` : `/traces/${traceId}`);
  }

  function applySelection() {
    const containers = [flowView, waterfallView];
    for (const container of containers) {
      container.classList.toggle("has-selection", state.selectedSpanId !== null);
      for (const node of container.querySelectorAll("[data-span-id]")) {
        node.classList.toggle("selected", node.dataset.spanId !== "" && node.dataset.spanId === state.selectedSpanId);
      }
    }
  }

  function selectSpan(spanId) {
    state.selectedSpanId = state.selectedSpanId === spanId ? null : spanId;
    applySelection();
    syncUrl();
    document.dispatchEvent(new CustomEvent("cao-span-select", {
      detail: {
        traceId,
        spanId: state.selectedSpanId,
        span: state.selectedSpanId ? model.byId.get(state.selectedSpanId) ?? null : null,
      },
    }));
  }

  /* ── Events ── */

  viewToggle?.addEventListener("click", (event) => {
    const button = event.target.closest(".view-btn");
    if (button && button.dataset.view !== state.view) setView(button.dataset.view);
  });

  errorsOnly?.addEventListener("change", () => {
    state.errorsOnly = errorsOnly.checked;
    renderAll();
  });

  const onSpanClick = (event) => {
    const agentToggle = event.target.closest("[data-agent-toggle]");
    if (agentToggle) {
      const agentId = agentToggle.dataset.agentToggle;
      if (state.collapsedAgents.has(agentId)) state.collapsedAgents.delete(agentId);
      else state.collapsedAgents.add(agentId);
      renderAll();
      return;
    }
    const collapsed = event.target.closest(".collapsed-turns");
    if (collapsed) {
      state.expandedRuns.add(collapsed.dataset.runId);
      renderFlow();
      return;
    }
    const node = event.target.closest("[data-span-id]");
    if (node && node.dataset.spanId) selectSpan(node.dataset.spanId);
  };
  flowView.addEventListener("click", onSpanClick);
  waterfallView.addEventListener("click", onSpanClick);

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && state.selectedSpanId !== null) {
      selectSpan(state.selectedSpanId); // Toggle off.
    }
  });

  // External modules (inspector ✕) can clear the current selection.
  document.addEventListener("cao-span-deselect", () => {
    if (state.selectedSpanId !== null) selectSpan(state.selectedSpanId);
  });

  // External modules (error list, drawer) can request a span highlight.
  document.addEventListener("cao-span-highlight", (event) => {
    const spanId = event.detail?.spanId;
    if (!spanId || !model) return;
    if (state.selectedSpanId !== spanId) selectSpan(spanId);
    const node = flowView.querySelector(`[data-span-id="${CSS.escape(spanId)}"]`)
      ?? waterfallView.querySelector(`[data-span-id="${CSS.escape(spanId)}"]`);
    node?.scrollIntoView({ behavior: "smooth", block: "center" });
  });

  /* ── Init ── */

  (async () => {
    try {
      const [spans, agentGraph] = await Promise.all([fetchAllSpans(), fetchAgentGraph()]);
      renderAgentSummary(agentGraph);
      model = buildModel(spans, agentGraph);
      statusLine.hidden = true;

      const params = new URLSearchParams(window.location.search);
      const view = params.get("view");
      state.errorsOnly = root.dataset.errorTrace === "true";
      if (errorsOnly) errorsOnly.checked = state.errorsOnly;

      renderAll();
      setView(view === "waterfall" ? "waterfall" : "flow");
      const spanParam = params.get("span");
      if (spanParam && model.byId.has(spanParam)) {
        selectSpan(spanParam);
      }
      if (window.caoCachePanel) window.caoCachePanel.render(model);
      document.dispatchEvent(new CustomEvent("cao-flow-ready", { detail: { traceId, model } }));
    } catch {
      renderAgentSummary(null);
      statusLine.textContent = "実行の流れを読み込めませんでした。/api/monitor が応答しているか確認してください。";
    }
  })();
})();
