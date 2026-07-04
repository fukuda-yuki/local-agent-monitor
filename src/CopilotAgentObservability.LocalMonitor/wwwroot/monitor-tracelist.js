// Local Ingestion Monitor — trace list master-detail (Sprint18 §6.2).
//
// Sanitized boundary: list data comes from the sanitized
// GET /api/monitor/trace-list and the preview's span breakdown from the
// sanitized GET /api/monitor/traces/{id}/spans. The only raw-bearing fetch is
// the per-trace prompt label route (GET /traces/{id}/prompt-label), and only
// when the server rendered the page in the raw-default posture
// (data-raw-available="true"); under --sanitized-only the route is absent and
// this script never calls it. Prompt search is TraceId-substring on the server
// plus a client-side filter over the prompt labels of already-loaded rows
// (D042 C8). All DOM nodes are built with createElement / textContent.
(() => {
  "use strict";

  const root = document.getElementById("tracelist-root");
  if (!root) return; // Not the trace list page — no-op.

  const rawAvailable = root.dataset.rawAvailable === "true";
  const pageSize = Number(root.dataset.pageSize) || 50;
  const rowsBody = document.getElementById("trace-rows");
  const table = document.getElementById("trace-list-table");
  const summary = document.getElementById("tracelist-summary");
  const loadMore = document.getElementById("load-more");
  const previewEmpty = document.getElementById("preview-empty");
  const previewBody = document.getElementById("preview-body");
  const searchInput = document.getElementById("trace-search");
  const modelSelect = document.getElementById("filter-model");
  const statusSelect = document.getElementById("filter-status");
  const periodSelect = document.getElementById("filter-period");

  const state = {
    q: root.dataset.q || "",
    model: root.dataset.model || "",
    status: root.dataset.status || "",
    period: root.dataset.period || "today",
    sort: root.dataset.sort || "tokens",
    offset: 0,
    totalMatched: 0,
    totalTokens: 0,
  };

  const promptCache = new Map(); // trace_id -> prompt label | null
  let loadedItems = []; // accumulated items for the current non-search filter set
  let displayedItems = [];
  let selectedTraceId = null;

  /* ── Formatting (mirrors MonitorViewFormat) ── */

  function compactTokens(value) {
    if (value === null || value === undefined) return "—";
    const abs = Math.abs(value);
    if (abs >= 1_000_000) return `${trim(value / 1_000_000)}M`;
    if (abs >= 1_000) return `${trim(value / 1_000)}K`;
    return String(value);
  }

  function trim(value) {
    return (Math.round(value * 10) / 10).toString();
  }

  function shortTraceId(traceId) {
    if (!traceId) return "—";
    return traceId.length <= 8 ? traceId : `${traceId.slice(0, 8)}…`;
  }

  function duration(ms) {
    if (ms === null || ms === undefined || ms === "" || Number.isNaN(Number(ms))) return "—";
    const value = Number(ms);
    if (value < 1000) return `${Math.round(value)} ms`;
    const seconds = value / 1000;
    if (seconds < 60) return `${trim(seconds)} 秒`;
    const minutes = Math.floor(seconds / 60);
    const rest = Math.round(seconds - minutes * 60);
    return `${minutes}分 ${rest}秒`;
  }

  function relativeTime(timestamp) {
    if (!timestamp) return "—";
    const parsed = Date.parse(timestamp);
    if (Number.isNaN(parsed)) return timestamp;
    const deltaSeconds = Math.max(0, (Date.now() - parsed) / 1000);
    if (deltaSeconds < 60) return "たった今";
    if (deltaSeconds < 3600) return `${Math.floor(deltaSeconds / 60)}分前`;
    if (deltaSeconds < 86400) return `${Math.floor(deltaSeconds / 3600)}時間前`;
    return `${Math.floor(deltaSeconds / 86400)}日前`;
  }

  function statusClass(status) {
    if (status === "ok") return "status-ok";
    if (status === "recovered") return "status-recovered";
    if (status === "unrecovered") return "status-unrecovered";
    return "status-unknown";
  }

  function statusLabel(status) {
    if (status === "ok") return "正常";
    if (status === "recovered") return "エラー · 回復済み";
    if (status === "unrecovered") return "エラー · 異常終了";
    return "状態不明";
  }

  function cachePct(item) {
    if (item.cache_read_tokens === null || item.cache_read_tokens === undefined) return null;
    if (!item.input_tokens) return null;
    return Math.round((item.cache_read_tokens * 100) / item.input_tokens);
  }

  /* ── Items from server-rendered rows (initial page) ── */

  function itemFromRow(row) {
    const d = row.dataset;
    return {
      trace_id: d.traceId,
      total_tokens: Number(d.tokens) || 0,
      input_tokens: Number(d.input) || 0,
      output_tokens: Number(d.output) || 0,
      cache_read_tokens: d.cacheRead === "" ? null : Number(d.cacheRead),
      cache_creation_tokens: d.cacheCreation === "" ? null : Number(d.cacheCreation),
      duration_ms: d.durationMs === "" ? null : Number(d.durationMs),
      turn_count: d.turns === "" ? null : Number(d.turns),
      tool_call_count: d.tools === "" ? null : Number(d.tools),
      trace_status: d.status || null,
      primary_model: d.model || null,
      client_kind: d.client || null,
      last_seen_at: d.lastSeen || null,
    };
  }

  function bootstrapFromServerRows() {
    const rows = [...rowsBody.querySelectorAll(".trace-row")];
    loadedItems = rows.map(itemFromRow);
    displayedItems = loadedItems;
    for (const row of rows) {
      promptCache.set(row.dataset.traceId, row.dataset.label === shortTraceId(row.dataset.traceId) ? null : row.dataset.label);
    }
    state.offset = rows.length;
    if (loadMore) {
      state.totalMatched = rows.length + (Number(loadMore.dataset.remaining) || 0);
    }
  }

  /* ── Prompt labels (raw-default only, D032/D039) ── */

  async function labelFor(traceId) {
    if (promptCache.has(traceId)) {
      return promptCache.get(traceId) ?? shortTraceId(traceId);
    }
    let label = null;
    if (rawAvailable) {
      try {
        const resp = await fetch(`/traces/${encodeURIComponent(traceId)}/prompt-label`, { cache: "no-store" });
        if (resp.ok) {
          label = (await resp.json()).prompt_label ?? null;
        }
      } catch {
        label = null;
      }
    }
    promptCache.set(traceId, label);
    return label ?? shortTraceId(traceId);
  }

  /* ── Table rendering ── */

  function buildRow(item, label, maxTokens) {
    const row = document.createElement("tr");
    row.className = "trace-row";
    row.tabIndex = 0;
    row.dataset.traceId = item.trace_id;

    const prompt = document.createElement("td");
    prompt.className = "col-prompt";
    const dot = document.createElement("span");
    dot.className = `trace-status-dot ${statusClass(item.trace_status)}`;
    dot.setAttribute("aria-hidden", "true");
    const labelSpan = document.createElement("span");
    labelSpan.className = "row-label";
    labelSpan.textContent = label;
    prompt.append(dot, labelSpan);

    const model = document.createElement("td");
    model.className = "col-model monitor-mono";
    model.textContent = item.primary_model ?? "—";

    const tokens = document.createElement("td");
    tokens.className = "col-tokens";
    const heat = document.createElement("span");
    heat.className = "token-heat";
    const fill = document.createElement("span");
    fill.className = "token-heat-fill";
    fill.style.width = `${maxTokens > 0 ? Math.min(100, ((item.total_tokens ?? 0) * 100) / maxTokens) : 0}%`;
    heat.append(fill);
    const tokenValue = document.createElement("span");
    tokenValue.className = "token-value monitor-mono";
    tokenValue.textContent = compactTokens(item.total_tokens);
    tokens.append(heat, tokenValue);

    const cache = document.createElement("td");
    cache.className = "col-cache monitor-mono";
    const pct = cachePct(item);
    cache.textContent = pct === null ? "—" : `${pct}%`;

    const durationCell = document.createElement("td");
    durationCell.className = "col-duration monitor-mono";
    durationCell.textContent = duration(item.duration_ms);

    const time = document.createElement("td");
    time.className = "col-time";
    time.textContent = relativeTime(item.last_seen_at);

    row.append(prompt, model, tokens, cache, durationCell, time);
    return row;
  }

  async function renderRows(items) {
    displayedItems = items;
    const maxTokens = Math.max(1, ...items.map((item) => item.total_tokens ?? 0));
    const labels = await Promise.all(items.map((item) => labelFor(item.trace_id)));
    rowsBody.replaceChildren();
    if (items.length === 0) {
      const row = document.createElement("tr");
      row.className = "empty-row";
      const cell = document.createElement("td");
      cell.colSpan = 6;
      cell.className = "empty-state";
      cell.textContent = "条件に一致するトレースがありません。";
      row.append(cell);
      rowsBody.append(row);
      renderPreviewEmpty();
      return;
    }

    items.forEach((item, index) => rowsBody.append(buildRow(item, labels[index], maxTokens)));
    const stillVisible = items.some((item) => item.trace_id === selectedTraceId);
    if (!stillVisible) {
      selectRow(items[0].trace_id);
    } else {
      highlightSelection();
    }
  }

  function updateSummary() {
    if (!summary) return;
    summary.replaceChildren();
    const count = document.createElement("span");
    count.className = "monitor-mono";
    count.textContent = String(state.totalMatched);
    const middle = document.createTextNode(" 件 · 合計 ");
    const tokens = document.createElement("span");
    tokens.className = "monitor-mono";
    tokens.textContent = compactTokens(state.totalTokens);
    summary.append(count, middle, tokens, document.createTextNode(" tokens"));
  }

  function updateLoadMore() {
    if (!loadMore) return;
    const remaining = Math.max(0, state.totalMatched - state.offset);
    loadMore.dataset.remaining = String(remaining);
    loadMore.hidden = remaining === 0 || state.q !== "";
    loadMore.textContent = `さらに読み込む（残り ${remaining} 件）`;
  }

  /* ── Fetching ── */

  function listUrl(offset) {
    const params = new URLSearchParams();
    if (state.q) params.set("q", state.q);
    if (state.model) params.set("model", state.model);
    if (state.status) params.set("status", state.status);
    params.set("period", state.period);
    params.set("sort", state.sort);
    params.set("offset", String(offset));
    params.set("limit", String(pageSize));
    return `/api/monitor/trace-list?${params}`;
  }

  async function refetch() {
    const resp = await fetch(listUrl(0), { cache: "no-store" });
    if (!resp.ok) return;
    const page = await resp.json();
    state.offset = page.items.length;
    state.totalMatched = page.total_matched;
    state.totalTokens = page.total_matched_tokens;

    let items = page.items;
    if (state.q) {
      // C8: the server matched TraceId substrings; additionally keep loaded rows
      // whose prompt label contains the query (client-side, loaded rows only).
      const needle = state.q.toLowerCase();
      const promptMatches = loadedItems.filter((item) => {
        const label = promptCache.get(item.trace_id);
        return label && label.toLowerCase().includes(needle);
      });
      const seen = new Set(items.map((item) => item.trace_id));
      items = items.concat(promptMatches.filter((item) => !seen.has(item.trace_id)));
      state.totalMatched = items.length;
      state.totalTokens = items.reduce((sum, item) => sum + (item.total_tokens ?? 0), 0);
    } else {
      loadedItems = items;
    }

    await renderRows(items);
    updateSummary();
    updateLoadMore();
    syncUrl();
  }

  async function loadNextPage() {
    const resp = await fetch(listUrl(state.offset), { cache: "no-store" });
    if (!resp.ok) return;
    const page = await resp.json();
    state.offset += page.items.length;
    state.totalMatched = page.total_matched;
    state.totalTokens = page.total_matched_tokens;
    loadedItems = loadedItems.concat(page.items);
    await renderRows(loadedItems);
    updateSummary();
    updateLoadMore();
  }

  function syncUrl() {
    const params = new URLSearchParams();
    if (state.q) params.set("q", state.q);
    if (state.model) params.set("model", state.model);
    if (state.status) params.set("status", state.status);
    if (state.period !== "today") params.set("period", state.period);
    if (state.sort !== "tokens") params.set("sort", state.sort);
    const query = params.toString();
    history.replaceState(null, "", query ? `/traces?${query}` : "/traces");
  }

  /* ── Preview panel (§6.2 right column) ── */

  function renderPreviewEmpty() {
    selectedTraceId = null;
    if (previewEmpty) previewEmpty.hidden = false;
    if (previewBody) {
      previewBody.hidden = true;
      previewBody.replaceChildren();
    }
  }

  function highlightSelection() {
    for (const row of rowsBody.querySelectorAll(".trace-row")) {
      row.classList.toggle("selected", row.dataset.traceId === selectedTraceId);
    }
  }

  function kv(label, value, valueClass) {
    const wrap = document.createElement("div");
    wrap.className = "preview-kpi";
    const labelNode = document.createElement("span");
    labelNode.className = "preview-kpi-label";
    labelNode.textContent = label;
    const valueNode = document.createElement("span");
    valueNode.className = `preview-kpi-value monitor-mono${valueClass ? ` ${valueClass}` : ""}`;
    valueNode.textContent = value;
    wrap.append(labelNode, valueNode);
    return wrap;
  }

  async function selectRow(traceId) {
    selectedTraceId = traceId;
    highlightSelection();
    const item = displayedItems.find((candidate) => candidate.trace_id === traceId);
    if (!item || !previewBody) return;
    if (previewEmpty) previewEmpty.hidden = true;
    previewBody.hidden = false;
    previewBody.replaceChildren();

    const status = document.createElement("div");
    status.className = "preview-status";
    const dot = document.createElement("span");
    dot.className = `trace-status-dot ${statusClass(item.trace_status)}`;
    const statusText = document.createElement("span");
    statusText.textContent = statusLabel(item.trace_status);
    const traceIdSpan = document.createElement("span");
    traceIdSpan.className = "preview-trace-id monitor-mono";
    traceIdSpan.textContent = shortTraceId(item.trace_id);
    status.append(dot, statusText, traceIdSpan);

    const title = document.createElement("h3");
    title.className = "preview-title";
    title.textContent = await labelFor(item.trace_id);

    const meta = document.createElement("p");
    meta.className = "preview-meta";
    meta.textContent = [
      item.primary_model ?? "モデル不明",
      item.client_kind,
      item.turn_count !== null && item.turn_count !== undefined ? `${item.turn_count} ターン` : null,
      item.tool_call_count !== null && item.tool_call_count !== undefined ? `${item.tool_call_count} ツール呼出` : null,
      relativeTime(item.last_seen_at),
    ].filter(Boolean).join(" · ");

    const kpis = document.createElement("div");
    kpis.className = "preview-kpis";
    kpis.append(
      kv("トークン", compactTokens(item.total_tokens), "kpi-gold"),
      kv("所要時間", duration(item.duration_ms)),
      kv("ターン", item.turn_count === null || item.turn_count === undefined ? "—" : String(item.turn_count)));

    const composition = document.createElement("div");
    composition.className = "preview-composition";
    const compTitle = document.createElement("span");
    compTitle.className = "preview-section-title";
    const pct = cachePct(item);
    compTitle.textContent = pct === null ? "トークン構成" : `トークン構成 — cache ${pct}%`;
    const bar = document.createElement("div");
    bar.className = "preview-token-bar";
    const cacheRead = item.cache_read_tokens ?? 0;
    const uncachedInput = Math.max(0, (item.input_tokens ?? 0) - cacheRead);
    for (const [cls, grow] of [["seg-cache", cacheRead], ["seg-input", uncachedInput], ["seg-output", item.output_tokens ?? 0]]) {
      const seg = document.createElement("span");
      seg.className = `bar-seg ${cls}`;
      seg.style.flexGrow = String(grow);
      bar.append(seg);
    }
    const compNote = document.createElement("span");
    compNote.className = "preview-comp-note";
    compNote.textContent = `キャッシュ ${compactTokens(item.cache_read_tokens)} · 入力 ${compactTokens(uncachedInput)} · 出力 ${compactTokens(item.output_tokens)}`;
    composition.append(compTitle, bar, compNote);

    const topSpans = document.createElement("div");
    topSpans.className = "preview-top-spans";
    const topTitle = document.createElement("span");
    topTitle.className = "preview-section-title";
    topTitle.textContent = "コストの大きいスパン TOP3";
    const topList = document.createElement("ul");
    topList.className = "preview-span-list";
    topSpans.append(topTitle, topList);

    const footer = document.createElement("div");
    footer.className = "preview-footer";
    const open = document.createElement("a");
    open.className = "preview-open";
    open.href = `/traces/${item.trace_id}`;
    open.textContent = "詳細を開く";
    footer.append(open);

    previewBody.append(status, title, meta, kpis, composition, topSpans, footer);

    // Sanitized spans API: top-3 token spans + the raw record id for the raw link.
    try {
      const resp = await fetch(`/api/monitor/traces/${encodeURIComponent(item.trace_id)}/spans?limit=200`, { cache: "no-store" });
      if (resp.ok && selectedTraceId === traceId) {
        const spans = (await resp.json()).items;
        const top = spans
          .filter((span) => (span.total_tokens ?? 0) > 0)
          .sort((a, b) => (b.total_tokens ?? 0) - (a.total_tokens ?? 0))
          .slice(0, 3);
        for (const span of top) {
          const li = document.createElement("li");
          const name = document.createElement("span");
          name.className = "span-name monitor-mono";
          name.textContent = span.tool_name ?? span.mcp_tool_name ?? span.operation ?? span.category ?? "span";
          const tokensNode = document.createElement("span");
          tokensNode.className = "span-tokens monitor-mono";
          tokensNode.textContent = compactTokens(span.total_tokens);
          li.append(name, tokensNode);
          topList.append(li);
        }
        if (top.length === 0) {
          const li = document.createElement("li");
          li.className = "empty-state";
          li.textContent = "トークンを持つスパンはありません。";
          topList.append(li);
        }

        if (rawAvailable && spans.length > 0 && spans[0].raw_record_id) {
          const raw = document.createElement("a");
          raw.className = "preview-raw";
          raw.href = `/traces/${spans[0].raw_record_id}/raw`;
          raw.textContent = "raw";
          footer.append(raw);
        }
      }
    } catch {
      // Preview stays without the span section on fetch failure.
    }
  }

  /* ── Events ── */

  rowsBody.addEventListener("click", (event) => {
    const row = event.target.closest(".trace-row");
    if (row) selectRow(row.dataset.traceId);
  });

  rowsBody.addEventListener("keydown", (event) => {
    if (event.key !== "Enter" && event.key !== " ") return;
    const row = event.target.closest(".trace-row");
    if (row) {
      event.preventDefault();
      selectRow(row.dataset.traceId);
    }
  });

  let searchTimer = null;
  searchInput?.addEventListener("input", () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => {
      state.q = searchInput.value.trim();
      refetch();
    }, 250);
  });

  modelSelect?.addEventListener("change", () => { state.model = modelSelect.value; refetch(); });
  statusSelect?.addEventListener("change", () => { state.status = statusSelect.value; refetch(); });
  periodSelect?.addEventListener("change", () => { state.period = periodSelect.value; refetch(); });

  table?.querySelector("thead")?.addEventListener("click", (event) => {
    const header = event.target.closest(".th-sortable");
    if (!header || header.dataset.sortKey === state.sort) return;
    state.sort = header.dataset.sortKey;
    for (const th of table.querySelectorAll(".th-sortable")) {
      const base = th.dataset.sortKey === "tokens" ? "トークン" : th.dataset.sortKey === "duration" ? "所要" : "時刻";
      th.textContent = th.dataset.sortKey === state.sort ? `${base} ▼` : base;
      if (th.dataset.sortKey === state.sort) {
        th.setAttribute("aria-sort", "descending");
      } else {
        th.removeAttribute("aria-sort");
      }
    }
    refetch();
  });

  loadMore?.addEventListener("click", loadNextPage);

  /* ── Init ── */

  bootstrapFromServerRows();
  const firstRow = rowsBody.querySelector(".trace-row");
  if (firstRow) selectRow(firstRow.dataset.traceId);
  updateLoadMore();
})();
