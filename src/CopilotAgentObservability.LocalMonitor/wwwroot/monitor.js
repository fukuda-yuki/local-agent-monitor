// Local Ingestion Monitor gap-recovery client.
//
// SSE (/events) is notification-only: it never carries raw payloads or PII. On
// each "projection" notification the client re-reads the sanitized cursor APIs
// (/api/monitor/*) from its last-seen cursor, so a missed notification self-heals
// on the next one. The script never inserts raw payloads into the DOM; it only
// advances internal cursors and emits a local refresh event for the page to use.
(() => {
  "use strict";

  const state = { ingestions: 0, traces: 0 };

  async function refresh(path, key) {
    const response = await fetch(`${path}?after=${state[key]}&limit=50`, { cache: "no-store" });
    if (!response.ok) {
      return;
    }

    const page = await response.json();
    for (const item of page.items) {
      state[key] = Math.max(state[key], item.rawRecordId ?? item.id ?? 0);
    }

    document.dispatchEvent(new CustomEvent("cao-monitor-refresh", {
      detail: { path, count: page.items.length },
    }));
  }

  const events = new EventSource('/events');
  events.addEventListener("projection", () => {
    refresh("/api/monitor/ingestions", "ingestions");
    refresh("/api/monitor/traces", "traces");
  });
})();
