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
    const { label, kind, indent, prefix, parallel } = options;
    const line = document.createElement("div");
    line.className = `wf-row wf-${kind}${span.isError ? " wf-error" : ""}`;
    line.dataset.spanId = span.span_id ?? "";

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

    for (const agent of model.agentSpans) {
      container.append(row(model, agent, { label: agent.label, kind: "agent", indent: 0 }));
    }

    for (const turn of model.turns) {
      if (filter && !turn.matchesFilter) continue;
      container.append(row(model, turn.span, { label: turn.title, kind: "llm", indent: 16 }));
      for (const group of turn.groups) {
        if (group.parallel) {
          container.append(groupHeader(group));
          group.tools.forEach((tool, index) => {
            container.append(row(model, tool, {
              label: tool.label,
              kind: "tool",
              indent: 32,
              prefix: index === group.tools.length - 1 ? "└─" : "├─",
              parallel: true,
            }));
          });
        } else {
          for (const tool of group.tools) {
            container.append(row(model, tool, { label: tool.label, kind: "tool", indent: 32 }));
          }
        }
      }
    }
  }

  window.caoWaterfall = { render };
})();
