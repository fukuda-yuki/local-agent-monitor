// Local Ingestion Monitor — waterfall view renderer (Sprint18 §6.3).
//
// Sanitized boundary: renders only the sanitized span model built by
// monitor-flow.js from /api/monitor/traces/{id}/spans. Never fetches a
// raw-bearing route; all DOM nodes are built with createElement / textContent.
(() => {
  "use strict";

  function compactTokens(value) {
    if (value === null || value === undefined) return "—";
    const abs = Math.abs(value);
    if (abs >= 1_000_000) return `${Math.round((value / 1_000_000) * 10) / 10}M`;
    if (abs >= 1_000) return `${Math.round((value / 1_000) * 10) / 10}K`;
    return String(value);
  }

  function fmtClock(ms) {
    const totalSeconds = Math.max(0, Math.round(ms / 1000));
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}:${String(seconds).padStart(2, "0")}`;
  }

  function fmtDuration(ms) {
    if (ms === null || ms === undefined) return "—";
    if (ms < 1000) return `${Math.round(ms)}ms`;
    return `${Math.round(ms / 100) / 10}s`;
  }

  function barGeometry(span, range) {
    const total = Math.max(1, range.endMs - range.startMs);
    const start = span.startMs ?? range.startMs;
    const end = span.endMs ?? start;
    return {
      left: Math.min(100, Math.max(0, ((start - range.startMs) * 100) / total)),
      width: Math.max(0.2, ((Math.max(end, start) - start) * 100) / total),
    };
  }

  function row(model, span, options) {
    const { label, kind, indent, prefix, parallel, ownerAgentId, relationshipConfidence, inconsistent, collapsible, collapsed, agentToggleKey } = options;
    const line = document.createElement("div");
    line.className = `wf-row wf-${kind}${span.isError ? " wf-error" : ""}${ownerAgentId ? " wf-owned" : ""}${parallel ? ` wf-${kind}-parallel` : ""}${relationshipConfidence === "unknown" ? " wf-unresolved" : ""}${inconsistent ? " wf-inconsistent" : ""}`;
    line.dataset.spanId = options.spanId === undefined ? span.span_id ?? "" : options.spanId;
    if (ownerAgentId) line.dataset.ownerAgentId = ownerAgentId;

    const name = document.createElement("span");
    name.className = "wf-name";
    name.style.paddingLeft = `${indent}px`;
    if (prefix) {
      const prefixNode = document.createElement("span");
      prefixNode.className = "wf-prefix monitor-mono";
      prefixNode.textContent = prefix;
      name.append(prefixNode);
    }
    const mark = document.createElement("span");
    mark.className = `wf-mark mark-${kind}`;
    mark.setAttribute("aria-hidden", "true");
    const text = document.createElement("span");
    text.className = "wf-label";
    text.textContent = label;
    name.append(mark, text);
    if (relationshipConfidence === "inferred" || relationshipConfidence === "unknown") {
      const relationship = document.createElement("span");
      relationship.className = `relationship-badge relationship-${relationshipConfidence}`;
      relationship.textContent = relationshipConfidence === "inferred" ? "推定" : "判定不能";
      name.append(relationship);
    }
    if (inconsistent) {
      const warning = document.createElement("span");
      warning.className = "wf-warning";
      warning.textContent = "時刻矛盾";
      name.append(warning);
    }
    if (collapsible) {
      const toggle = document.createElement("button");
      toggle.type = "button";
      toggle.className = "wf-collapse";
      toggle.dataset.agentToggle = agentToggleKey;
      toggle.setAttribute("aria-expanded", String(!collapsed));
      toggle.setAttribute("aria-label", collapsed ? "Agent セクションを展開する" : "Agent セクションを折りたたむ");
      toggle.textContent = collapsed ? "＋" : "−";
      name.append(toggle);
    }

    const track = document.createElement("span");
    track.className = "wf-track";
    const bar = document.createElement("span");
    bar.className = `wf-bar bar-${kind}`;
    const geometry = barGeometry(span, model.range);
    bar.style.left = `${geometry.left}%`;
    bar.style.width = `${geometry.width}%`;
    if (parallel) bar.classList.add("bar-parallel");
    track.append(bar);

    const tokens = document.createElement("span");
    tokens.className = "wf-tokens monitor-mono";
    tokens.textContent = kind === "llm" ? compactTokens(span.total_tokens) : "—";

    const durationCell = document.createElement("span");
    durationCell.className = "wf-duration monitor-mono";
    durationCell.textContent = fmtDuration(span.durationMs);

    line.append(name, track, tokens, durationCell);
    return line;
  }

  function groupHeader(group) {
    const line = document.createElement("div");
    line.className = "wf-row wf-group-head";
    const name = document.createElement("span");
    name.className = "wf-name wf-group-label";
    name.style.paddingLeft = "32px";
    name.textContent = `⑂ 並行 ${group.tools.length} 件`;
    const meta = document.createElement("span");
    meta.className = "wf-group-max monitor-mono";
    meta.textContent = `max ${fmtDuration(group.maxDurationMs)}`;
    name.append(meta);
    line.append(name, document.createElement("span"), document.createElement("span"), document.createElement("span"));
    return line;
  }

  function render(container, model, filter) {
    container.replaceChildren();

    // Time axis header: 0:00 → trace end, ticks every 25%.
    const head = document.createElement("div");
    head.className = "wf-row wf-head";
    const nameHead = document.createElement("span");
    nameHead.className = "wf-name";
    nameHead.textContent = "スパン";
    const axis = document.createElement("span");
    axis.className = "wf-axis";
    const totalMs = model.range.endMs - model.range.startMs;
    for (let i = 0; i <= 4; i++) {
      const tick = document.createElement("span");
      tick.className = "wf-tick monitor-mono";
      tick.style.left = `${i * 25}%`;
      tick.textContent = fmtClock((totalMs * i) / 4);
      axis.append(tick);
    }
    const tokensHead = document.createElement("span");
    tokensHead.className = "wf-tokens";
    tokensHead.textContent = "tokens";
    const durationHead = document.createElement("span");
    durationHead.className = "wf-duration";
    durationHead.textContent = "所要";
    head.append(nameHead, axis, tokensHead, durationHead);
    container.append(head);

    const isOutsideOwner = (span, owner) => span.startMs !== null && span.endMs !== null
      && owner.startMs !== null && owner.endMs !== null
      && (span.startMs < owner.startMs || span.endMs > owner.endMs);
    const parallelAgents = new Set((model.agentGraph?.parallel_groups ?? []).flat());
    const renderedSpans = new Set();

    const appendTurn = (turn, depth, owner) => {
      if (filter && !turn.matchesFilter) return;
      renderedSpans.add(turn.span.span_id);
      container.append(row(model, turn.span, {
        label: turn.title,
        kind: "llm",
        indent: 32 + depth * 16,
        ownerAgentId: owner.span_id,
        relationshipConfidence: turn.span.relationshipConfidence,
        inconsistent: isOutsideOwner(turn.span, owner),
      }));
      for (const group of turn.groups) {
        const tools = group.tools.filter((tool) => tool.owningAgentSpanId === owner.span_id);
        if (tools.length === 0) continue;
        if (group.parallel && tools.length > 1) container.append(groupHeader({ tools, maxDurationMs: group.maxDurationMs }));
        tools.forEach((tool, index) => {
          renderedSpans.add(tool.span_id);
          container.append(row(model, tool, {
            label: tool.label,
            kind: "tool",
            indent: 48 + depth * 16,
            prefix: group.parallel && tools.length > 1 ? (index === tools.length - 1 ? "└─" : "├─") : null,
            parallel: group.parallel && tools.length > 1,
            ownerAgentId: owner.span_id,
            relationshipConfidence: tool.relationshipConfidence,
            inconsistent: isOutsideOwner(tool, owner),
          }));
        });
      }
    };

    const appendAgent = (agent, depth) => {
      renderedSpans.add(agent.span_id);
      const caller = agent.agent?.caller_agent_span_id ? model.agentById.get(agent.agent.caller_agent_span_id) : null;
      const collapsed = model.collapsedAgents?.has(agent.agentUiKey) ?? false;
      container.append(row(model, agent, {
        label: `${agent.agent?.agent_role ?? "unknown"} · ${agent.agent?.agent_name ?? agent.label}`,
        kind: "agent",
        indent: 16 + depth * 16,
        relationshipConfidence: agent.relationshipConfidence ?? "unknown",
        inconsistent: caller ? isOutsideOwner(agent, caller) : false,
        parallel: parallelAgents.has(agent.span_id),
        collapsible: true,
        collapsed,
        agentToggleKey: agent.agentUiKey,
        spanId: agent.hasUniqueAgentId ? agent.span_id : "",
      }));
      if (collapsed) return;

      const direct = agent.hasUniqueAgentId ? model.ownedSpans.get(agent.span_id) ?? [] : [];
      const children = model.childAgents.get(agent.span_id) ?? [];
      const timeline = [
        ...direct.filter((span) => span.kind === "llm" || span.kind === "tool"),
        ...children,
      ].sort((a, b) => (a.startMs ?? 0) - (b.startMs ?? 0) || (a.span_ordinal ?? 0) - (b.span_ordinal ?? 0));
      for (const item of timeline) {
        if (item.kind === "agent") {
          appendAgent(item, depth + 1);
        } else if (item.kind === "llm") {
          const turn = model.turns.find((candidate) => candidate.span.span_id === item.span_id);
          if (turn) appendTurn(turn, depth, agent);
        } else if (!renderedSpans.has(item.span_id) && (!filter || item.isError)) {
          renderedSpans.add(item.span_id);
          container.append(row(model, item, {
            label: item.label,
            kind: "tool",
            indent: 48 + depth * 16,
            ownerAgentId: agent.span_id,
            relationshipConfidence: item.relationshipConfidence,
            inconsistent: isOutsideOwner(item, agent),
          }));
        }
      }
    };

    model.collapsedAgents = model.collapsedAgents ?? new Set();
    const appendUnownedTurn = (turn) => {
      if (filter && !turn.matchesFilter) return;
      renderedSpans.add(turn.span.span_id);
      container.append(row(model, turn.span, {
        label: turn.title,
        kind: "llm",
        indent: 16,
        relationshipConfidence: turn.span.relationshipConfidence,
      }));
      for (const group of turn.groups) {
        const unownedTools = group.tools.filter((tool) => !tool.owningAgentSpanId);
        if (unownedTools.length === 0) continue;
        if (group.parallel && unownedTools.length > 1) {
          container.append(groupHeader({ tools: unownedTools, maxDurationMs: group.maxDurationMs }));
          unownedTools.forEach((tool, index) => {
            renderedSpans.add(tool.span_id);
            container.append(row(model, tool, {
              label: tool.label,
              kind: "tool",
              indent: 32,
              prefix: index === unownedTools.length - 1 ? "└─" : "├─",
              parallel: true,
            }));
          });
        } else {
          for (const tool of unownedTools) {
            renderedSpans.add(tool.span_id);
            container.append(row(model, tool, { label: tool.label, kind: "tool", indent: 32 }));
          }
        }
      }
    };

    const unownedTurns = model.turns.filter((turn) => !turn.span.owningAgentSpanId);
    const nestedUnownedToolIds = new Set(unownedTurns.flatMap((turn) =>
      turn.groups.flatMap((group) => group.tools.filter((tool) => !tool.owningAgentSpanId).map((tool) => tool.span_id))));
    const topLevel = [
      ...(model.childAgents?.get(null) ?? []).map((agent) => ({ kind: "agent", span: agent, value: agent })),
      ...unownedTurns.map((turn) => ({ kind: "turn", span: turn.span, value: turn })),
      ...model.spans
        .filter((span) => span.kind === "tool" && !span.owningAgentSpanId && !nestedUnownedToolIds.has(span.span_id))
        .map((span) => ({ kind: "tool", span, value: span })),
    ].sort((a, b) => (a.span.startMs ?? 0) - (b.span.startMs ?? 0)
      || (a.span.span_ordinal ?? Number.MAX_SAFE_INTEGER) - (b.span.span_ordinal ?? Number.MAX_SAFE_INTEGER));

    for (const item of topLevel) {
      if (item.kind === "agent") {
        appendAgent(item.value, 0);
      } else if (item.kind === "turn") {
        appendUnownedTurn(item.value);
      } else if (!filter || item.value.isError) {
        renderedSpans.add(item.value.span_id);
        container.append(row(model, item.value, {
          label: item.value.label,
          kind: "tool",
          indent: 16,
          relationshipConfidence: item.value.relationshipConfidence,
        }));
      }
    }
  }

  window.caoWaterfall = { render };
})();
