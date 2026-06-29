// Local Ingestion Monitor — SSE gap-recovery + UI interactivity.
//
// SSE (/events) is notification-only: it never carries raw payloads or PII. On
// each "projection" notification the client re-reads the sanitized cursor APIs
// (/api/monitor/*) from its last-seen cursor, so a missed notification self-heals
// on the next one. The script never inserts raw payloads into the DOM; it only
// advances internal cursors and emits a local refresh event for the page to use.
(() => {
  "use strict";

  /* ========================================================================
   * 1. SSE gap-recovery (preserved from original)
   * ===================================================================== */

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

  /* ========================================================================
   * 2. Status dot — health check via /api/monitor/health
   * ===================================================================== */

  async function updateStatusDot() {
    const dot = document.getElementById("monitor-status-dot");
    if (!dot) return;

    try {
      const resp = await fetch("/api/monitor/health", { cache: "no-store" });
      if (!resp.ok) throw new Error("non-ok");
      const health = await resp.json();

      dot.classList.remove("healthy", "degraded", "unhealthy");
      const status = (health.status || "").toLowerCase();
      if (status === "healthy") {
        dot.classList.add("healthy");
        dot.title = "Healthy";
        dot.setAttribute("aria-label", "Monitor status: healthy");
      } else if (status === "degraded") {
        dot.classList.add("degraded");
        dot.title = "Degraded";
        dot.setAttribute("aria-label", "Monitor status: degraded");
      } else {
        dot.classList.add("unhealthy");
        dot.title = "Unhealthy";
        dot.setAttribute("aria-label", "Monitor status: unhealthy");
      }
    } catch {
      dot.classList.remove("healthy", "degraded", "unhealthy");
      dot.classList.add("unhealthy");
      dot.title = "Unreachable";
      dot.setAttribute("aria-label", "Monitor status: unreachable");
    }
  }

  // Initial check + periodic refresh every 30s
  updateStatusDot();
  setInterval(updateStatusDot, 30000);

  /* ========================================================================
   * 3. Tab switching (TraceDetail page)
   * ===================================================================== */

  function initTabs() {
    const tabContainer = document.getElementById("trace-tabs");
    if (!tabContainer) return;

    const tabs = tabContainer.querySelectorAll(".monitor-tab");
    const panels = document.querySelectorAll("[data-tab-panel]");

    function activateTab(targetId) {
      tabs.forEach(t => t.classList.toggle("active", t.dataset.tab === targetId));
      panels.forEach(p => {
        p.hidden = p.dataset.tabPanel !== targetId;
      });
    }

    tabs.forEach(tab => {
      tab.addEventListener("click", () => {
        activateTab(tab.dataset.tab);
      });
    });

    // Activate first tab by default
    if (tabs.length > 0 && !tabContainer.querySelector(".monitor-tab.active")) {
      tabs[0].classList.add("active");
    }
    if (panels.length > 0) {
      const activeTab = tabContainer.querySelector(".monitor-tab.active");
      const targetId = activeTab ? activeTab.dataset.tab : panels[0].dataset.tabPanel;
      activateTab(targetId);
    }
  }

  /* ========================================================================
   * 4. Row expansion (Traces page — click row to expand hidden columns)
   * ===================================================================== */

  function initRowExpand() {
    document.querySelectorAll("tr[data-expand]").forEach(row => {
      row.addEventListener("click", function(e) {
        // Don't expand if the user clicked a link
        if (e.target.closest("a")) return;

        const hiddenCells = this.querySelectorAll(".monitor-visually-hidden");
        const wasExpanded = this.classList.contains("expanded");

        // Collapse all other rows
        document.querySelectorAll("tr.expanded").forEach(r => {
          r.classList.remove("expanded");
          r.querySelectorAll(".monitor-visually-hidden").forEach(c => c.style.cssText = "");
        });

        if (!wasExpanded) {
          this.classList.add("expanded");
          hiddenCells.forEach(c => {
            c.style.position = "";
            c.style.width = "";
            c.style.height = "";
            c.style.overflow = "";
            c.style.clip = "";
            c.style.whiteSpace = "";
          });
        }
      });
    });
  }

  /* ========================================================================
   * 5. Init on DOM ready
   * ===================================================================== */

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => {
      initTabs();
      initRowExpand();
    });
  } else {
    initTabs();
    initRowExpand();
  }
})();
