// Local Ingestion Monitor — cache column renderer (Sprint18 §6.3 right column).
//
// Sanitized boundary: renders only the sanitized span model built by
// monitor-flow.js (cache_read / cache_creation / input / output token counts).
// Never fetches a raw-bearing route; all DOM nodes are built with
// createElement / textContent.
(() => {
  "use strict";

  function compactTokens(value) {
    if (value === null || value === undefined) return "—";
    const abs = Math.abs(value);
    if (abs >= 1_000_000) return `${Math.round((value / 1_000_000) * 10) / 10}M`;
    if (abs >= 1_000) return `${Math.round((value / 1_000) * 10) / 10}K`;
    return String(value);
  }

  function statRow(label, value, options = {}) {
    const row = document.createElement("div");
    row.className = `cache-stat-row${options.emphasis ? " emphasis" : ""}`;
    const labelNode = document.createElement("span");
    labelNode.textContent = label;
    const valueNode = document.createElement("span");
    valueNode.className = `monitor-mono${options.gold ? " stat-gold" : ""}`;
    valueNode.textContent = value;
    row.append(labelNode, valueNode);
    return row;
  }

  function render(model) {
    const overview = document.getElementById("cache-overview");
    const turnsContainer = document.getElementById("cache-turns");
    if (!overview || !turnsContainer) return;
    overview.replaceChildren();
    turnsContainer.replaceChildren();

    const cacheTurns = model.turns.filter((turn) => turn.span.cache_read_tokens !== null && turn.span.cache_read_tokens !== undefined);
    const inputSum = model.turns.reduce((sum, turn) => sum + (turn.span.input_tokens ?? 0), 0);
    const cacheReadSum = cacheTurns.reduce((sum, turn) => sum + (turn.span.cache_read_tokens ?? 0), 0);
    const cacheCreationSum = model.turns.reduce((sum, turn) => sum + (turn.span.cache_creation_tokens ?? 0), 0);

    if (model.turns.length === 0 || inputSum === 0) {
      const empty = document.createElement("p");
      empty.className = "empty-state";
      empty.textContent = "キャッシュ集計に使える LLM ターンがありません。";
      overview.append(empty);
      return;
    }

    const uncachedInput = Math.max(0, inputSum - cacheReadSum);
    const effectiveInput = Math.round(cacheReadSum * 0.1 + uncachedInput);
    const readRate = Math.round((cacheReadSum * 100) / inputSum);
    const compression = inputSum > 0 ? Math.round(((inputSum - effectiveInput) * 100) / inputSum) : 0;

    const rate = document.createElement("div");
    rate.className = "cache-rate-hero";
    const rateValue = document.createElement("span");
    rateValue.className = "cache-rate-value monitor-mono";
    rateValue.textContent = `${readRate}%`;
    const rateLabel = document.createElement("span");
    rateLabel.className = "cache-rate-label";
    rateLabel.textContent = "読取率";
    rate.append(rateValue, rateLabel);
    overview.append(rate);

    overview.append(
      statRow("キャッシュ読取", compactTokens(cacheReadSum)),
      statRow("キャッシュ作成", compactTokens(cacheCreationSum)),
      statRow("未キャッシュ入力", compactTokens(uncachedInput)));
    overview.append(statRow("実効入力換算 (読取=0.1×)", compactTokens(effectiveInput), { emphasis: true, gold: true }));

    if (compression > 0) {
      const remark = document.createElement("p");
      remark.className = "cache-panel-remark";
      remark.textContent = `キャッシュにより入力コストを約 ${compression}% 圧縮できています。`;
      overview.append(remark);
    }

    // Per-turn read-rate bars (70px frame).
    const chart = document.createElement("div");
    chart.className = "cache-turn-chart";
    for (const turn of model.turns) {
      const input = turn.span.input_tokens ?? 0;
      const read = turn.span.cache_read_tokens ?? 0;
      const pct = input > 0 ? Math.round((read * 100) / input) : 0;
      const bar = document.createElement("span");
      bar.className = "cache-turn-bar";
      bar.style.height = `${Math.max(2, pct)}%`;
      bar.title = `ターン${turn.index + 1} 読取率 ${pct}%`;
      chart.append(bar);
    }
    turnsContainer.append(chart);

    const note = document.createElement("p");
    note.className = "cache-panel-note";
    note.textContent = "後半ほど読取率が高いほど、履歴の再送がキャッシュで吸収できています。";
    turnsContainer.append(note);
  }

  window.caoCachePanel = { render };
})();
