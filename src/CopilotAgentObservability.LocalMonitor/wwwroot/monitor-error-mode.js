// Local Ingestion Monitor — error analysis mode (Sprint18 §6.5).
//
// For traces containing error spans, populates the error summary strip and
// replaces the cache column with the 3-card error panel (error list / error
// detail / input-token trend with the 128K limit line). Sanitized boundary:
// the list/trend use the sanitized span model from monitor-flow.js; the error
// detail's exception message comes from the raw span-detail route (D043) only
// in the raw-default posture (data-raw-available="true") — under
// --sanitized-only it shows the sanitized error_type only and never fetches.
// All DOM nodes are built with createElement / textContent.
(() => {
  "use strict";

  const root = document.getElementById("trace-detail-root");
  const errorPanel = document.getElementById("error-panel");
  if (!root || !errorPanel) return; // Not the trace detail page — no-op.

  const traceId = root.dataset.traceId;
  const rawAvailable = root.dataset.rawAvailable === "true";
  const cacheColumn = document.getElementById("cache-column");
  const strip = document.getElementById("error-strip");
  const stripText = document.getElementById("error-strip-text");
  const firstErrorButton = document.getElementById("error-first-btn");
  const errorsOnlyStripButton = document.getElementById("errors-only-strip-btn");
  const errorsOnlyCheckbox = document.getElementById("errors-only");

  const TOKEN_LIMIT = 128_000;

  let model = null;
  let selectedIndex = 0;

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
    return `${Math.round(ms / 100) / 10}s`;
  }

  /* ── Summary strip ── */

  function renderStrip() {
    if (!strip || !stripText) return;
    const total = model.errorSpans.length;
    const recovered = model.errorSpans.filter((entry) => entry.recovered).length;
    const unrecovered = total - recovered;
    const parts = [`エラー ${total}件`];
    if (recovered > 0) parts.push(`${recovered}件は回復済み`);
    if (unrecovered > 0) parts.push(`${unrecovered}件が原因でトレースが異常終了`);
    stripText.textContent = parts.join(" — ");
    strip.hidden = false;
  }

  /* ── Error panel (3 cards) ── */

  function card(title) {
    const panel = document.createElement("div");
    panel.className = "panel error-card";
    const head = document.createElement("div");
    head.className = "panel-head";
    const titleNode = document.createElement("span");
    titleNode.className = "panel-title";
    titleNode.textContent = title;
    head.append(titleNode);
    panel.append(head);
    return panel;
  }

  function renderPanel() {
    errorPanel.replaceChildren();

    // Card 1: error list.
    const listCard = card(`エラー一覧（${model.errorSpans.length}件）`);
    const list = document.createElement("ul");
    list.className = "error-list";
    model.errorSpans.forEach((entry, index) => {
      const item = document.createElement("li");
      const button = document.createElement("button");
      button.type = "button";
      button.className = `error-row${index === selectedIndex ? " selected" : ""}`;
      const dot = document.createElement("span");
      dot.className = `trace-status-dot ${entry.recovered ? "status-recovered" : "status-unrecovered"}`;
      dot.setAttribute("aria-hidden", "true");
      const name = document.createElement("span");
      name.className = "error-name monitor-mono";
      name.textContent = `${entry.span.label}${entry.span.error_type ? ` · ${entry.span.error_type}` : ""}`;
      const sub = document.createElement("span");
      sub.className = "error-sub";
      sub.textContent = `ターン${entry.turn.index + 1} · ${fmtDuration(entry.span.durationMs)}`;
      const pill = document.createElement("span");
      pill.className = `recovery-pill ${entry.recovered ? "pill-recovered" : "pill-unrecovered"}`;
      pill.textContent = entry.recovered ? "回復済み" : "未回復";
      button.append(dot, name, sub, pill);
      button.addEventListener("click", () => {
        selectedIndex = index;
        renderPanel();
        document.dispatchEvent(new CustomEvent("cao-span-highlight", { detail: { spanId: entry.span.span_id } }));
      });
      item.append(button);
      list.append(item);
    });
    listCard.append(list);

    // Card 2: selected error detail.
    const selected = model.errorSpans[selectedIndex];
    const detailCard = card("エラー詳細");
    if (selected) {
      const meta = document.createElement("div");
      meta.className = "error-detail-meta";
      const rows = [
        ["span id", selected.span.span_id ?? "—"],
        ["種別", selected.span.error_type ?? "—"],
        ["発生", `ターン${selected.turn.index + 1}`],
        ["モデル", selected.turn.span.response_model ?? selected.turn.span.request_model ?? "—"],
      ];
      for (const [label, value] of rows) {
        const row = document.createElement("div");
        row.className = "inspector-meta-row";
        const labelNode = document.createElement("span");
        labelNode.textContent = label;
        const valueNode = document.createElement("span");
        valueNode.className = "monitor-mono";
        valueNode.textContent = value;
        row.append(labelNode, valueNode);
        meta.append(row);
      }
      detailCard.append(meta);

      const message = document.createElement("pre");
      message.className = "error-message-block";
      message.id = "error-message-block";
      message.textContent = rawAvailable ? "例外メッセージを読み込み中…" : "例外メッセージは --sanitized-only では表示できません。";
      detailCard.append(message);
      if (rawAvailable && selected.span.span_id) {
        loadErrorMessage(selected.span.span_id, message);
      }

      const actions = document.createElement("div");
      actions.className = "inspector-actions";
      const show = document.createElement("button");
      show.type = "button";
      show.className = "inspector-open-raw";
      show.textContent = "該当スパンを見る";
      show.addEventListener("click", () => {
        document.dispatchEvent(new CustomEvent("cao-span-highlight", { detail: { spanId: selected.span.span_id } }));
      });
      const ask = document.createElement("button");
      ask.type = "button";
      ask.className = "inspector-ask-copilot";
      ask.textContent = "Copilot で解析";
      ask.addEventListener("click", () => {
        document.dispatchEvent(new CustomEvent("cao-ask-copilot", { detail: { traceId, spanId: selected.span.span_id, focus: "errors" } }));
      });
      actions.append(show);
      if (rawAvailable) actions.append(ask);
      detailCard.append(actions);
    }

    // Card 3: input-token trend with the 128K limit line.
    const trendCard = card("原因の手がかり — 入力トークンの推移");
    const chart = document.createElement("div");
    chart.className = "token-trend-chart";
    const limitLine = document.createElement("span");
    limitLine.className = "token-limit-line";
    limitLine.title = `上限 ${compactTokens(TOKEN_LIMIT)}`;
    const scaleMax = Math.max(TOKEN_LIMIT, ...model.turns.map((turn) => turn.span.input_tokens ?? 0)) * 1.1;
    limitLine.style.bottom = `${(TOKEN_LIMIT * 100) / scaleMax}%`;
    chart.append(limitLine);
    for (const turn of model.turns) {
      const input = turn.span.input_tokens ?? 0;
      const bar = document.createElement("span");
      bar.className = `token-trend-bar${input > TOKEN_LIMIT ? " over-limit" : ""}`;
      bar.style.height = `${Math.max(2, (input * 100) / scaleMax)}%`;
      bar.title = `ターン${turn.index + 1} 入力 ${compactTokens(input)}`;
      chart.append(bar);
    }
    trendCard.append(chart);
    const note = document.createElement("p");
    note.className = "cache-panel-note";
    note.textContent = `赤破線は入力上限 ${compactTokens(TOKEN_LIMIT)} の目安。超過したターンの棒は赤になります。`;
    trendCard.append(note);

    errorPanel.append(listCard, detailCard, trendCard);
  }

  async function loadErrorMessage(spanId, target) {
    try {
      const detail = await window.caoLoadSpanDetail(spanId);
      target.textContent = detail
        ? detail.error_message ?? "例外メッセージは記録されていません。"
        : "例外メッセージを取得できませんでした。";
    } catch {
      target.textContent = "例外メッセージを取得できませんでした。";
    }
  }

  /* ── Strip buttons ── */

  firstErrorButton?.addEventListener("click", () => {
    const first = model?.errorSpans[0];
    if (first) {
      document.dispatchEvent(new CustomEvent("cao-span-highlight", { detail: { spanId: first.span.span_id } }));
    }
  });

  errorsOnlyStripButton?.addEventListener("click", () => {
    if (!errorsOnlyCheckbox) return;
    errorsOnlyCheckbox.checked = !errorsOnlyCheckbox.checked;
    errorsOnlyCheckbox.dispatchEvent(new Event("change"));
    errorsOnlyStripButton.textContent = errorsOnlyCheckbox.checked ? "エラーのみ表示中" : "すべて表示中";
  });

  /* ── Activation ── */

  document.addEventListener("cao-flow-ready", (event) => {
    model = event.detail?.model;
    if (!model || model.errorSpans.length === 0) return; // Normal trace — cache column stays.
    renderStrip();
    if (cacheColumn) cacheColumn.hidden = true;
    errorPanel.hidden = false;
    renderPanel();
  });

  // When the inspector closes on an error trace, it restores whichever panel
  // was visible before it opened (the error panel), so no extra wiring needed.
})();
