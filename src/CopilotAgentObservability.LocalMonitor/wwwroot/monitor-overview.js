// Local Ingestion Monitor — overview page (period toggle, Sprint18 §6.1).
//
// Sanitized boundary: aggregates come from the sanitized
// GET /api/monitor/overview and GET /api/monitor/trace-list. The only
// raw-bearing fetch is the per-trace prompt label route
// (GET /traces/{id}/prompt-label), and only when the server rendered the page
// in the raw-default posture (data-raw-available="true"); under
// --sanitized-only the route is absent and this script never calls it. All DOM
// nodes are built with createElement / textContent (no innerHTML).
(() => {
  "use strict";

  const root = document.getElementById("overview-root");
  if (!root) return; // Not the overview page — no-op.

  const rawAvailable = root.dataset.rawAvailable === "true";
  const PERIOD_LABEL = { today: "今日", "7d": "7日", "30d": "30日" };

  function compactTokens(value) {
    if (value === null || value === undefined) return "—";
    const abs = Math.abs(value);
    if (abs >= 1_000_000) return `${trimNumber(value / 1_000_000)}M`;
    if (abs >= 1_000) return `${trimNumber(value / 1_000)}K`;
    return String(value);
  }

  function trimNumber(value) {
    return (Math.round(value * 10) / 10).toString();
  }

  function percent(value) {
    return value === null || value === undefined ? "—" : `${trimNumber(value)}%`;
  }

  function setText(id, text) {
    const element = document.getElementById(id);
    if (element) element.textContent = text;
  }

  function barWidth(value, max) {
    const pct = max > 0 ? Math.min(100, Math.max(0, (value * 100) / max)) : 0;
    return `${Math.round(pct * 10) / 10}%`;
  }

  function shortTraceId(traceId) {
    if (!traceId) return "—";
    return traceId.length <= 8 ? traceId : `${traceId.slice(0, 8)}…`;
  }

  /* ── KPI + mid/low cards from /api/monitor/overview ── */

  function renderKpi(overview, period) {
    setText("kpi-tokens-label", `${PERIOD_LABEL[period]}のトークン`);
    setText("kpi-tokens-value", compactTokens(overview.kpi.tokens_total));
    const change = overview.kpi.tokens_change_pct;
    setText(
      "kpi-tokens-compare",
      `前期間 ${compactTokens(overview.kpi.tokens_previous_period)}${change === null ? "" : ` ${change >= 0 ? "+" : ""}${trimNumber(change)}%`}`);
    setText("kpi-effective-value", compactTokens(overview.kpi.effective_input_tokens));
    const compression = overview.kpi.cache_compression_pct;
    setText(
      "kpi-effective-note",
      compression === null ? "キャッシュ集計はまだありません" : `キャッシュで −${trimNumber(compression)}% 圧縮`);
    setText("kpi-cache-rate-value", percent(overview.kpi.cache_read_rate_pct));
    const rateBar = document.getElementById("kpi-cache-rate-bar");
    if (rateBar) rateBar.style.width = `${Math.round(overview.kpi.cache_read_rate_pct ?? 0)}%`;
    setText("kpi-error-value", String(overview.kpi.error_trace_count));
    setText("kpi-error-note", `/ ${overview.kpi.trace_count} 中 · 確認する →`);

    const tokensCard = document.getElementById("kpi-tokens-card");
    if (tokensCard) tokensCard.href = `/traces?period=${period}`;
    const errorCard = document.getElementById("kpi-error-card");
    if (errorCard) errorCard.href = `/traces?status=error&period=${period}`;
    const topAll = document.getElementById("top-all-link");
    if (topAll) topAll.href = `/traces?period=${period}`;
  }

  function emptyState(text, tag = "p") {
    const node = document.createElement(tag);
    node.className = "empty-state";
    node.textContent = text;
    return node;
  }

  function renderModels(overview) {
    const breakdown = document.getElementById("model-breakdown");
    const efficiency = document.getElementById("cache-efficiency");
    if (!breakdown || !efficiency) return;
    breakdown.replaceChildren();
    efficiency.replaceChildren();

    const models = overview.per_model;
    if (models.length === 0) {
      breakdown.append(emptyState("この期間のトレースはまだありません。"));
      efficiency.append(emptyState("この期間のトレースはまだありません。"));
    }

    const maxTokens = Math.max(1, ...models.map((m) => m.total_tokens));
    for (const m of models) {
      const row = document.createElement("div");
      row.className = "model-row";
      const head = document.createElement("div");
      head.className = "model-row-head";
      const name = document.createElement("span");
      name.className = "model-name monitor-mono";
      name.textContent = m.model;
      const total = document.createElement("span");
      total.className = "model-total monitor-mono";
      total.textContent = `${compactTokens(m.total_tokens)} · ${m.trace_count} traces`;
      head.append(name, total);
      const bar = document.createElement("div");
      bar.className = "model-bar";
      bar.style.width = barWidth(m.total_tokens, maxTokens);
      const uncachedInput = Math.max(0, m.input_tokens - m.cache_read_tokens);
      for (const [cls, grow] of [["seg-cache", m.cache_read_tokens], ["seg-input", uncachedInput], ["seg-output", m.output_tokens]]) {
        const seg = document.createElement("span");
        seg.className = `bar-seg ${cls}`;
        seg.style.flexGrow = String(grow);
        bar.append(seg);
      }
      row.append(head, bar);
      breakdown.append(row);

      const cacheRow = document.createElement("div");
      cacheRow.className = "cache-row";
      const cacheHead = document.createElement("div");
      cacheHead.className = "cache-row-head";
      const cacheName = document.createElement("span");
      cacheName.className = "model-name monitor-mono";
      cacheName.textContent = m.model;
      const rate = document.createElement("span");
      rate.className = "cache-rate";
      rate.textContent = percent(m.cache_read_rate_pct);
      cacheHead.append(cacheName, rate);
      const track = document.createElement("span");
      track.className = "cache-bar-track";
      const fill = document.createElement("span");
      fill.className = "cache-bar-fill";
      fill.style.width = `${Math.round(m.cache_read_rate_pct ?? 0)}%`;
      track.append(fill);
      const note = document.createElement("span");
      note.className = "cache-note";
      note.textContent = `読取 ${compactTokens(m.cache_read_tokens)} / 作成 ${compactTokens(m.cache_creation_tokens)}`;
      cacheRow.append(cacheHead, track, note);
      efficiency.append(cacheRow);
    }

    const existingRemark = document.getElementById("cache-remark");
    if (existingRemark) existingRemark.remove();
    const lowModel = models
      .filter((m) => m.cache_read_rate_pct !== null && m.cache_read_rate_pct < 30)
      .sort((a, b) => a.cache_read_rate_pct - b.cache_read_rate_pct)[0];
    if (lowModel) {
      const remark = document.createElement("p");
      remark.className = "cache-remark";
      remark.id = "cache-remark";
      remark.textContent = `${lowModel.model} はキャッシュ読取率が低く、作成コストを回収できていない可能性があります。`;
      efficiency.parentElement.append(remark);
    }
  }

  function renderHourly(overview) {
    const chart = document.getElementById("hourly-chart");
    if (!chart) return;
    chart.replaceChildren();
    const max = Math.max(1, ...overview.hourly_tokens.map((h) => h.total_tokens));
    for (const hour of overview.hourly_tokens) {
      const bar = document.createElement("span");
      bar.className = "hour-bar";
      bar.style.height = barWidth(hour.total_tokens, max);
      bar.title = `${hour.hour}時 ${compactTokens(hour.total_tokens)} tokens`;
      chart.append(bar);
    }
  }

  /* ── TOP5 from /api/monitor/trace-list (prompt labels only when raw-default) ── */

  async function promptLabel(traceId) {
    if (!rawAvailable) return null;
    try {
      const resp = await fetch(`/traces/${encodeURIComponent(traceId)}/prompt-label`, { cache: "no-store" });
      if (!resp.ok) return null;
      const body = await resp.json();
      return body.prompt_label ?? null;
    } catch {
      return null;
    }
  }

  async function renderTopTraces(period) {
    const list = document.getElementById("top-traces");
    if (!list) return;
    const resp = await fetch(`/api/monitor/trace-list?period=${period}&limit=5`, { cache: "no-store" });
    if (!resp.ok) return;
    const page = await resp.json();
    const items = page.items.filter((item) => item.total_tokens !== null && item.total_tokens > 0);
    list.replaceChildren();
    if (items.length === 0) {
      list.append(emptyState("この期間のトークン集計はまだありません。", "li"));
      return;
    }

    const max = Math.max(1, ...items.map((item) => item.total_tokens));
    const labels = await Promise.all(items.map((item) => promptLabel(item.trace_id)));
    items.forEach((item, index) => {
      const row = document.createElement("li");
      row.className = "top-trace-row";
      const link = document.createElement("a");
      link.href = `/traces/${item.trace_id}`;
      const rank = document.createElement("span");
      rank.className = "top-rank monitor-mono";
      rank.textContent = String(index + 1);
      const body = document.createElement("span");
      body.className = "top-body";
      const label = document.createElement("span");
      label.className = "top-label";
      label.textContent = labels[index] ?? shortTraceId(item.trace_id);
      const track = document.createElement("span");
      track.className = "top-bar-track";
      const fill = document.createElement("span");
      fill.className = "top-bar-fill";
      fill.style.width = barWidth(item.total_tokens, max);
      track.append(fill);
      body.append(label, track);
      const total = document.createElement("span");
      total.className = "top-total monitor-mono";
      total.textContent = compactTokens(item.total_tokens);
      link.append(rank, body, total);
      row.append(link);
      list.append(row);
    });
  }

  /* ── Period toggle ── */

  let currentPeriod = "today";

  async function applyPeriod(period) {
    currentPeriod = period;
    for (const button of document.querySelectorAll("#period-toggle .period-btn")) {
      button.classList.toggle("active", button.dataset.period === period);
    }

    const resp = await fetch(`/api/monitor/overview?period=${period}`, { cache: "no-store" });
    if (!resp.ok) return;
    const overview = await resp.json();
    renderKpi(overview, period);
    renderModels(overview);
    renderHourly(overview);
    await renderTopTraces(period);
  }

  document.getElementById("period-toggle")?.addEventListener("click", (event) => {
    const button = event.target.closest(".period-btn");
    if (button && button.dataset.period !== currentPeriod) {
      applyPeriod(button.dataset.period);
    }
  });

  // New projections arriving over SSE refresh the current period in place.
  document.addEventListener("cao-monitor-refresh", () => applyPeriod(currentPeriod));
})();
