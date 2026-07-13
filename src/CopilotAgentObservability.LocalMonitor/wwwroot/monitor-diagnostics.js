// Local Ingestion Monitor — diagnostics page (ingestion history, Sprint18 C5).
//
// Sanitized boundary: reads only the sanitized GET /api/monitor/ingestions and
// GET /api/monitor/source-diagnostics cursor APIs. It never fetches a
// raw-bearing route. All DOM nodes are built with createElement / textContent;
// no markup strings are ever injected.
(() => {
  "use strict";

  const rows = document.getElementById("ingestion-history-rows");
  const sourceDiagnosticRows = document.getElementById("source-diagnostics-rows");
  const sourceDiagnosticsPageSize = 50;
  const maximumSourceDiagnosticsPages = 200;
  if (!rows) return; // Not the diagnostics page — no-op.

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

  function cell(text, mono) {
    const node = document.createElement("td");
    if (mono) node.className = "monitor-mono";
    node.textContent = text;
    return node;
  }

  function valueLine(value, mono) {
    const node = document.createElement("span");
    if (mono) node.className = "monitor-mono";
    node.textContent = value === null || value === undefined ? "—" : String(value);
    return node;
  }

  function lines(values, mono) {
    const node = document.createElement("td");
    for (const value of values) {
      node.append(valueLine(value, mono));
    }
    return node;
  }

  function sourceDiagnosticMessage(message) {
    sourceDiagnosticRows.replaceChildren();
    const row = document.createElement("tr");
    const value = document.createElement("td");
    value.colSpan = 7;
    value.className = "empty-state";
    value.textContent = message;
    row.append(value);
    sourceDiagnosticRows.append(row);
  }

  async function loadSourceDiagnostics() {
    const items = [];
    const seenCursors = new Set();
    let after = null;

    for (let page = 0; page < maximumSourceDiagnosticsPages; page += 1) {
      const query = after === null
        ? `?limit=${sourceDiagnosticsPageSize}`
        : `?limit=${sourceDiagnosticsPageSize}&after=${after}`;
      const response = await fetch(`/api/monitor/source-diagnostics${query}`, { cache: "no-store" });
      if (!response.ok) throw new Error("source diagnostics request failed");
      const payload = await response.json();
      if (!Array.isArray(payload.items)) throw new Error("source diagnostics payload is invalid");
      items.push(...payload.items);

      const nextCursor = payload.next_cursor;
      if (nextCursor === null) return items;
      if (!Number.isSafeInteger(nextCursor) || nextCursor < 1 || seenCursors.has(nextCursor)) {
        throw new Error("source diagnostics cursor is invalid");
      }
      seenCursors.add(nextCursor);
      after = nextCursor;
    }

    throw new Error("source diagnostics page limit exceeded");
  }

  async function refresh() {
    let items = [];
    try {
      const resp = await fetch("/api/monitor/ingestions?limit=50", { cache: "no-store" });
      if (!resp.ok) return;
      items = (await resp.json()).items;
    } catch {
      return;
    }

    rows.replaceChildren();
    if (items.length === 0) {
      const row = document.createElement("tr");
      const empty = document.createElement("td");
      empty.colSpan = 5;
      empty.className = "empty-state";
      empty.textContent = "まだ取り込みがありません。";
      row.append(empty);
      rows.append(row);
      return;
    }

    // Newest first for the history reading order.
    for (const item of items.slice().reverse()) {
      const row = document.createElement("tr");
      row.append(
        cell(String(item.raw_record_id), true),
        cell(relativeTime(item.received_at), false),
        cell(item.source ?? "—", false),
        cell(item.trace_id ?? "—", true),
        cell(item.span_count === null || item.span_count === undefined ? "—" : String(item.span_count), true));
      rows.append(row);
    }
  }

  async function refreshSourceDiagnostics() {
    if (!sourceDiagnosticRows) return;

    let items;
    try {
      items = await loadSourceDiagnostics();
    } catch {
      sourceDiagnosticMessage("ソース互換性の診断を読み込めませんでした。");
      return;
    }

    if (items.length === 0) {
      sourceDiagnosticMessage("ソース互換性の観測はまだありません。");
      return;
    }

    sourceDiagnosticRows.replaceChildren();
    for (const item of items) {
      const row = document.createElement("tr");
      row.append(
        lines([item.observation_id, item.observed_at], true),
        lines([item.source_surface, item.source_application_version], false),
        lines([item.source_adapter, item.adapter_version], false),
        cell(item.compatibility_state, true),
        lines(item.reason_codes, true),
        cell(item.next_action, true),
        lines([item.unknown_span_count, item.unknown_event_count, item.unknown_attribute_count], true));
      sourceDiagnosticRows.append(row);
    }
  }

  refresh();
  refreshSourceDiagnostics();
  document.addEventListener("cao-monitor-refresh", () => {
    refresh();
    refreshSourceDiagnostics();
  });

  // The popover's 取り込み履歴 link targets #ingestion-history — open it when
  // the fragment points here (both on load and on in-page hash navigation).
  function openWhenTargeted() {
    if (window.location.hash === "#ingestion-history") {
      document.getElementById("ingestion-history")?.setAttribute("open", "");
    }
  }

  openWhenTargeted();
  window.addEventListener("hashchange", openWhenTargeted);
})();
