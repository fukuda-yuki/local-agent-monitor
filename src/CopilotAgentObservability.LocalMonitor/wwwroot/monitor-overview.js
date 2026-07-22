// Local Ingestion Monitor — overview page (period toggle, Sprint18 §6.1).
//
// Sanitized boundary: aggregates come from the sanitized
// GET /api/monitor/overview and GET /api/monitor/trace-list. The only
// raw-bearing fetch is the per-trace prompt label route
// (GET /traces/{id}/prompt-label), and only when the server rendered the page
// in the raw-default posture (data-raw-available="true"); under
// --sanitized-only the route is absent and this script never calls it. All DOM
// nodes are built with createElement / textContent; no markup strings are ever injected.
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

  function formatTime(value) {
    if (!value) return "—";
    const parsed = new Date(value);
    return Number.isNaN(parsed.valueOf()) ? value : `${parsed.toLocaleString("ja-JP", { timeZone: "UTC" })} UTC`;
  }

  /* ── KPI + mid/low cards from /api/monitor/overview ── */

  function renderKpi(overview, period) {
    setText("kpi-tokens-label", `${PERIOD_LABEL[period]}のトークン（実消費）`);
    setText("kpi-tokens-value", compactTokens(overview.kpi.uncached_tokens_total));
    const change = overview.kpi.uncached_tokens_change_pct;
    setText(
      "kpi-tokens-compare",
      `前期間 ${compactTokens(overview.kpi.uncached_tokens_previous_period)}${change === null ? "" : ` ${change >= 0 ? "+" : ""}${trimNumber(change)}%`}`);
    setText(
      "kpi-tokens-breakdown",
      `総量 ${compactTokens(overview.kpi.tokens_total)}（キャッシュ読取 ${compactTokens(overview.kpi.cache_read_tokens_total)} 込み）`);
    setText("kpi-effective-value", compactTokens(overview.kpi.effective_input_tokens));
    const compression = overview.kpi.cache_compression_pct;
    setText(
      "kpi-effective-note",
      compression === null ? "キャッシュ集計はまだありません" : `キャッシュで −${trimNumber(compression)}% 圧縮`);
    setText("kpi-cache-rate-value", percent(overview.kpi.cache_read_rate_pct));
    const rateBar = document.getElementById("kpi-cache-rate-bar");
    if (rateBar) rateBar.style.width = `${Math.round(overview.kpi.cache_read_rate_pct ?? 0)}%`;
    setText(
      "kpi-cache-rate-basis",
      overview.kpi.cache_read_rate_pct === null
        ? "キャッシュ集計はまだありません"
        : `読取 ${compactTokens(overview.kpi.cache_read_tokens_total)} ÷ 入力 ${compactTokens(overview.kpi.cache_aware_input_tokens)}`);
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

  async function promptLabel(traceId, signal) {
    if (!rawAvailable) return null;
    try {
      const resp = await fetch(`/traces/${encodeURIComponent(traceId)}/prompt-label`, { cache: "no-store", signal });
      if (!resp.ok) return null;
      const body = await resp.json();
      return body.prompt_label ?? null;
    } catch {
      return null;
    }
  }

  async function renderTopTraces(period, generation, signal) {
    const list = document.getElementById("top-traces");
    if (!list) return;
    const resp = await fetch(`/api/monitor/trace-list?period=${period}&limit=5`, { cache: "no-store", signal });
    if (!resp.ok) return;
    const page = await resp.json();
    if (generation !== refreshGeneration) return;
    const items = page.items.filter((item) => item.total_tokens !== null && item.total_tokens > 0);
    list.replaceChildren();
    if (items.length === 0) {
      list.append(emptyState("この期間のトークン集計はまだありません。", "li"));
      return;
    }

    const max = Math.max(1, ...items.map((item) => item.total_tokens));
    const labels = await Promise.all(items.map((item) => promptLabel(item.trace_id, signal)));
    if (generation !== refreshGeneration) return;
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

  /* ── Bounded Alert Center summary from sanitized snapshot DTOs ── */

  function countSources(alerts) {
    const counts = new Map();
    for (const alert of alerts) {
      const key = `${alert.source.surface}@${alert.source.version}`;
      counts.set(key, (counts.get(key) ?? 0) + 1);
    }
    return [...counts.entries()].sort((left, right) => right[1] - left[1] || left[0].localeCompare(right[0]));
  }

  async function renderAlertOverview(period, generation, signal) {
    const body = document.getElementById("overview-alert-body");
    const heading = document.getElementById("overview-alert-title");
    const allLink = document.querySelector("#overview-alert-card .panel-link");
    if (!body) return;
    if (heading) heading.textContent = `Alert Center · ${PERIOD_LABEL[period]}`;
    if (allLink) allLink.href = `/alerts?state=open&period=${encodeURIComponent(period)}`;
    body.replaceChildren(emptyState("アラートを読み込んでいます。"));
    try {
      const options = { cache: "no-store", signal };
      const [openResponse, criticalResponse, warningResponse] = await Promise.all([
        fetch(`/api/alert-center/v1/alerts?state=open&period=${encodeURIComponent(period)}&limit=100`, options),
        fetch(`/api/alert-center/v1/alerts?severity=critical&state=open&period=${encodeURIComponent(period)}&limit=1`, options),
        fetch(`/api/alert-center/v1/alerts?severity=warning&state=open&period=${encodeURIComponent(period)}&limit=1`, options),
      ]);
      if (!openResponse.ok || !criticalResponse.ok || !warningResponse.ok) throw new Error("alert-center");
      const [openSnapshot, criticalSnapshot, warningSnapshot] = await Promise.all([
        openResponse.json(), criticalResponse.json(), warningResponse.json(),
      ]);
      if (generation !== refreshGeneration) return;

      const alert = criticalSnapshot.alerts?.[0];
      const incomplete = [openSnapshot, criticalSnapshot, warningSnapshot]
        .some(snapshot => snapshot.snapshot_state === "incomplete");
      const visibleOnly = openSnapshot.alerts.length < openSnapshot.total_count;
      body.replaceChildren();

      const summary = document.createElement("div");
      summary.className = "overview-alert-summary";
      const openCount = document.createElement("p");
      openCount.className = "overview-alert-counts";
      openCount.textContent = incomplete
        ? `取得範囲の open ${openSnapshot.total_count} · critical ${criticalSnapshot.total_count} · warning ${warningSnapshot.total_count}（全体件数は不明）`
        : `open ${openSnapshot.total_count} · critical ${criticalSnapshot.total_count} · warning ${warningSnapshot.total_count}`;
      const sources = document.createElement("p");
      sources.className = "monitor-subtle overview-alert-sources";
      const sourceText = countSources(openSnapshot.alerts).map(([source, count]) => `${source} ${count}`).join(" · ");
      sources.textContent = `${visibleOnly || incomplete ? "表示範囲の source" : "source"} · ${sourceText || "none"}`;
      const recurring = document.createElement("p");
      recurring.className = "monitor-subtle overview-alert-recurring";
      const topRecurring = incomplete
        ? null
        : openSnapshot.recurring_groups?.find(group => group.aggregation_state === "supported");
      recurring.textContent = incomplete
        ? "incomplete snapshot のため top recurring rule は確定できません"
        : topRecurring
          ? `top recurring · ${topRecurring.rule_id}@${topRecurring.rule_version} · ${topRecurring.distinct_session_count} Sessions`
          : "top recurring · none";
      summary.append(openCount, sources, recurring);
      body.append(summary);

      if (!alert) {
        body.append(emptyState(incomplete
          ? "不完全なスナップショットの取得範囲では open の critical alert は見つかりませんでした。全体として 0 件とは断定できません。"
          : "この期間に open の critical alert はありません。"));
        return;
      }

      const link = document.createElement("a");
      link.href = `/alerts?alert=${encodeURIComponent(alert.alert_id)}&period=${encodeURIComponent(period)}`;
      const alertTitle = document.createElement("strong");
      alertTitle.textContent = `${incomplete ? "取得範囲の critical" : "latest critical"} · ${alert.rule.title ?? alert.rule.rule_id}`;
      const alertState = document.createElement("span");
      alertState.className = "monitor-subtle";
      alertState.textContent = `${alert.severity} · ${alert.lifecycle.state} · ${alert.source.surface}@${alert.source.version}`;
      const timing = document.createElement("span");
      timing.className = "monitor-subtle";
      timing.textContent = incomplete
        ? `取得範囲の観測 ${formatTime(alert.last_observed_at)} · 最新とは断定できません`
        : `最終観測 ${formatTime(alert.last_observed_at)}`;
      link.append(alertTitle, alertState, timing);
      body.append(link);
    } catch (caught) {
      if (generation !== refreshGeneration || caught?.name === "AbortError") return;
      body.replaceChildren(emptyState("Alert Center を読み込めませんでした。"));
    }
  }

  /* ── Period toggle ── */

  let currentPeriod = "today";
  let refreshGeneration = 0;
  let refreshController = null;

  async function applyPeriod(period) {
    currentPeriod = period;
    const generation = ++refreshGeneration;
    refreshController?.abort();
    const controller = new AbortController();
    refreshController = controller;
    for (const button of document.querySelectorAll("#period-toggle .period-btn")) {
      button.classList.toggle("active", button.dataset.period === period);
    }

    const alertRefresh = renderAlertOverview(period, generation, controller.signal);
    try {
      const resp = await fetch(`/api/monitor/overview?period=${period}`, { cache: "no-store", signal: controller.signal });
      if (!resp.ok) return;
      const overview = await resp.json();
      if (generation !== refreshGeneration) return;
      renderKpi(overview, period);
      renderModels(overview);
      renderHourly(overview);
      await renderTopTraces(period, generation, controller.signal);
    } catch (caught) {
      if (generation === refreshGeneration && caught?.name !== "AbortError") {
        /* Server-rendered overview remains visible on refresh failure. */
      }
    } finally {
      await alertRefresh;
      if (generation === refreshGeneration) refreshController = null;
    }
  }

  document.getElementById("period-toggle")?.addEventListener("click", (event) => {
    const button = event.target.closest(".period-btn");
    if (button && button.dataset.period !== currentPeriod) {
      applyPeriod(button.dataset.period);
    }
  });

  // New projections arriving over SSE refresh the current period in place.
  document.addEventListener("cao-monitor-refresh", () => applyPeriod(currentPeriod));
  applyPeriod(currentPeriod);
})();
