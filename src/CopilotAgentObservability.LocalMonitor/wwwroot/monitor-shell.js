// Local Ingestion Monitor — shell (sidebar status badge + diagnostics popover).
//
// Sanitized boundary: this script reads only /health/ready and the sanitized
// /api/monitor/* JSON. It never fetches a raw-bearing route and never inserts
// raw payloads or PII into the DOM. All DOM nodes are built with
// createElement / textContent (no innerHTML).
(() => {
  "use strict";

  const badge = document.getElementById("status-badge");
  if (!badge) return; // Shell absent (non-layout page) — no-op.

  const badgeDot = document.getElementById("status-badge-dot");
  const badgeText = document.getElementById("status-badge-text");
  const popover = document.getElementById("status-popover");
  const popoverDot = document.getElementById("popover-dot");
  const popoverTitle = document.getElementById("popover-title");
  const popoverEndpoint = document.getElementById("popover-endpoint");
  const pipelineList = document.getElementById("popover-pipeline");
  const reasonLine = document.getElementById("popover-reason");
  const endpointLabel = document.getElementById("sidebar-endpoint");
  const traceCount = document.getElementById("sidebar-trace-count");

  if (endpointLabel) endpointLabel.textContent = window.location.host;

  /* ── Receive-status badge (/health/ready) ── */

  const DISPLAY = {
    ready: { cls: "healthy", badge: "正常 · 受信中", title: "受信できます — ready" },
    degraded: { cls: "degraded", badge: "注意 · 受信中", title: "注意 — degraded" },
    not_ready: { cls: "unhealthy", badge: "異常 · 要確認", title: "受信できません — not_ready" },
    unreachable: { cls: "unhealthy", badge: "未接続", title: "モニターに到達できません" },
  };

  let lastHealth = null;
  let lastHttpStatus = null;

  function setStates(kind) {
    const display = DISPLAY[kind] ?? DISPLAY.unreachable;
    for (const dot of [badgeDot, popoverDot]) {
      if (!dot) continue;
      dot.classList.remove("healthy", "degraded", "unhealthy");
      dot.classList.add(display.cls);
    }
    badge.classList.remove("degraded", "unhealthy");
    if (display.cls !== "healthy") badge.classList.add(display.cls);
    if (badgeText) badgeText.textContent = display.badge;
    if (popoverTitle) popoverTitle.textContent = display.title;
    badge.setAttribute("aria-label", `受信ステータス: ${display.badge}`);
  }

  async function refreshHealth() {
    try {
      const resp = await fetch("/health/ready", { cache: "no-store" });
      lastHttpStatus = resp.status;
      if (!resp.ok && resp.status !== 503) throw new Error("non-ok");
      lastHealth = await resp.json();
      const status = (lastHealth.status || "").toLowerCase();
      setStates(status === "ready" ? "ready" : status === "degraded" ? "degraded" : "not_ready");
    } catch {
      lastHealth = null;
      lastHttpStatus = null;
      setStates("unreachable");
    }
    if (popoverEndpoint) {
      popoverEndpoint.textContent = lastHttpStatus === null
        ? "/health/ready → ?"
        : `/health/ready → ${lastHttpStatus}`;
    }
    renderPipeline();
  }

  /* ── Pipeline summary rows (handoff §5: 受信 / 書き込みキュー / Projection / DB) ── */

  function stageState(ok, detail) {
    return { ok: ok === true, detail };
  }

  function pipelineStages(health) {
    const checks = (health && health.checks) || {};
    const projectionDetail = [
      checks.projection_backlog != null ? `残 ${checks.projection_backlog}` : null,
      checks.projection_lag_seconds != null ? `遅延 ${Math.round(checks.projection_lag_seconds)}s` : null,
    ].filter(Boolean).join(" · ");
    return [
      { label: "① 受信 (OTLP)", state: stageState(checks.ingestion_accepting && checks.loopback_bound, "") },
      { label: "② 書き込みキュー", state: stageState(checks.writer_running, "") },
      { label: "③ Projection", state: stageState(checks.projection_worker_running, projectionDetail) },
      { label: "④ DB / migration", state: stageState(checks.db_open && checks.migration_complete, "") },
    ];
  }

  function renderPipeline() {
    if (!pipelineList) return;
    pipelineList.replaceChildren();
    if (!lastHealth) {
      const row = document.createElement("li");
      row.textContent = "モニターの状態を取得できません。プロセスが起動しているか確認してください。";
      pipelineList.append(row);
      if (reasonLine) reasonLine.hidden = true;
      return;
    }

    for (const stage of pipelineStages(lastHealth)) {
      const row = document.createElement("li");
      const label = document.createElement("span");
      label.textContent = stage.label;
      const state = document.createElement("span");
      state.className = "pipeline-state" + (stage.state.ok ? "" : " unhealthy");
      state.textContent = stage.state.ok
        ? (stage.state.detail ? `OK · ${stage.state.detail}` : "OK")
        : "NG";
      row.append(label, state);
      pipelineList.append(row);
    }

    if (reasonLine) {
      const reasons = Array.isArray(lastHealth.degraded_reasons) ? lastHealth.degraded_reasons : [];
      if (reasons.length > 0) {
        reasonLine.textContent = `理由: ${reasons.join(", ")} — 詳細診断で対処を確認してください。`;
        reasonLine.hidden = false;
      } else {
        reasonLine.hidden = true;
      }
    }
  }

  /* ── Popover open / close ── */

  function setPopoverOpen(open) {
    if (!popover) return;
    popover.hidden = !open;
    badge.setAttribute("aria-expanded", open ? "true" : "false");
  }

  badge.addEventListener("click", () => {
    const opening = popover ? popover.hidden : false;
    setPopoverOpen(opening);
    if (opening) refreshHealth();
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && popover && !popover.hidden) {
      setPopoverOpen(false);
      badge.focus();
    }
  });

  document.addEventListener("click", (event) => {
    if (!popover || popover.hidden) return;
    if (event.target instanceof Node
      && !popover.contains(event.target)
      && !badge.contains(event.target)) {
      setPopoverOpen(false);
    }
  });

  /* ── Sidebar trace-count badge (sanitized total from /api/monitor/trace-list) ── */

  async function refreshTraceCount() {
    if (!traceCount) return;
    try {
      const resp = await fetch("/api/monitor/trace-list?limit=1", { cache: "no-store" });
      if (!resp.ok) return;
      const page = await resp.json();
      if (typeof page.total_matched === "number") {
        traceCount.textContent = String(page.total_matched);
      }
    } catch {
      // Leave the count empty when the API is unavailable.
    }
  }

  refreshHealth();
  refreshTraceCount();
  setInterval(refreshHealth, 30000);
  document.addEventListener("cao-monitor-refresh", refreshTraceCount);
})();
