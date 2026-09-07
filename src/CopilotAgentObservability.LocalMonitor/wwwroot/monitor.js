// Projection notifications do not replace an investigation snapshot. Diagnostics
// alone consumes the legacy cursor refresh; primary pages offer explicit refresh.
(() => {
  "use strict";

  const state = { ingestions: 0, traces: 0 };
  const diagnostics = document.getElementById("ingestion-history-rows");
  const primary = document.querySelector("[data-session-workspace], #local-monitor-session-explorer");
  let notice = null;
  if (primary) {
    const freshness = document.createElement("div");
    freshness.className = "local-monitor-freshness";
    freshness.dataset.snapshotFreshness = "";
    const observed = document.createElement("span");
    observed.dataset.snapshotObservedAt = "";
    observed.textContent = "表示取得時刻: 読み込み中";
    notice = document.createElement("span");
    notice.dataset.newRecordNotice = "";
    notice.setAttribute("role", "status");
    notice.hidden = true;
    const refreshButton = document.createElement("button");
    refreshButton.type = "button";
    refreshButton.className = "monitor-btn";
    refreshButton.dataset.snapshotRefresh = "";
    refreshButton.textContent = "最新の記録で更新";
    refreshButton.addEventListener("click", () => {
      document.dispatchEvent(new CustomEvent("local-monitor-refresh-requested"));
    });
    freshness.append(observed, notice, refreshButton);
    primary.prepend(freshness);
    document.addEventListener("local-monitor-snapshot-loaded", event => {
      const instant = new Date(event.detail?.observedAt);
      if (!Number.isFinite(instant.getTime())) return;
      observed.textContent = `表示取得時刻: ${instant.toLocaleString("ja-JP")}`;
      observed.title = "このブラウザーが表示用スナップショットを取得した時刻です。取得元での活動時刻とは異なります。";
    });
  }

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
    if (notice) {
      notice.textContent = "新しい記録が届きました。現在の表示は自動では変わりません。";
      notice.hidden = false;
    }
    if (diagnostics) {
      refresh("/api/monitor/ingestions", "ingestions");
      refresh("/api/monitor/traces", "traces");
    }
  });
})();
