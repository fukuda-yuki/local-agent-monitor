// Local Ingestion Monitor — diagnostics page (ingestion history, Sprint18 C5).
//
// Sanitized boundary: reads only the sanitized GET /api/monitor/ingestions
// cursor API (raw_record_id / received_at / source / trace_id / client_kind /
// span_count — never payloads, prompts, or PII) and never fetches a
// raw-bearing route. All DOM nodes are built with createElement / textContent;
// no markup strings are ever injected.
(() => {
  "use strict";

  const rows = document.getElementById("ingestion-history-rows");
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

  refresh();
  document.addEventListener("cao-monitor-refresh", refresh);

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
