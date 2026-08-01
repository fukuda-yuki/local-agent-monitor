// The shared shell reads only readiness. Settings section content and URL state
// remain extension-owned so this host cannot become a second route authority.
(() => {
  "use strict";

  const receiverAction = document.getElementById("receiver-status-action");
  const receiverDot = document.getElementById("receiver-status-dot");
  const receiverText = document.getElementById("receiver-status-text");
  const settingsAction = document.getElementById("settings-action");
  const modal = document.getElementById("settings-modal");
  const closeAction = document.getElementById("settings-modal-close");
  if (!receiverAction || !settingsAction || !modal || !closeAction) return;

  const DISPLAY = {
    ready: { cls: "healthy", label: "正常 · 受信中" },
    degraded: { cls: "degraded", label: "注意 · 受信中" },
    not_ready: { cls: "unhealthy", label: "異常 · 要確認" },
    unreachable: { cls: "unhealthy", label: "未接続" },
  };

  let settingsInvoker = null;

  function setReceiverState(kind) {
    const display = DISPLAY[kind] ?? DISPLAY.unreachable;
    if (receiverDot) {
      receiverDot.classList.remove("healthy", "degraded", "unhealthy");
      receiverDot.classList.add(display.cls);
    }
    receiverAction.classList.remove("degraded", "unhealthy");
    if (display.cls !== "healthy") receiverAction.classList.add(display.cls);
    if (receiverText) receiverText.textContent = display.label;
    receiverAction.setAttribute("aria-label", `受信ステータス: ${display.label}`);
  }

  async function refreshHealth() {
    try {
      const response = await fetch("/health/ready", { cache: "no-store" });
      if (!response.ok && response.status !== 503) throw new Error("unavailable");
      const health = await response.json();
      const status = (health.status || "").toLowerCase();
      setReceiverState(
        status === "ready"
          ? "ready"
          : status === "degraded"
            ? "degraded"
            : "not_ready");
    } catch {
      setReceiverState("unreachable");
    }
  }

  function openSettings(invoker, section) {
    settingsInvoker = invoker;
    if (section === null) {
      delete modal.dataset.requestedSection;
    } else {
      modal.dataset.requestedSection = section;
    }
    modal.showModal();
    document.dispatchEvent(new CustomEvent("cao-settings-open", {
      detail: { section },
    }));
    closeAction.focus({ preventScroll: true });
  }

  function closeSettings() {
    if (modal.open) modal.close();
  }

  receiverAction.addEventListener("click", () => openSettings(receiverAction, "receiver"));
  settingsAction.addEventListener("click", () => openSettings(settingsAction, null));
  closeAction.addEventListener("click", closeSettings);

  modal.addEventListener("cancel", event => {
    if (event.target !== modal) return;
    event.preventDefault();
    closeSettings();
  });

  modal.addEventListener("close", () => {
    delete modal.dataset.requestedSection;
    const returnTarget = settingsInvoker;
    settingsInvoker = null;
    if (returnTarget && returnTarget.isConnected) {
      returnTarget.focus({ preventScroll: true });
    }
  });

  function isSequentialRadio(radio) {
    if (radio.type !== "radio" || !radio.name) return true;
    const group = Array.from(modal.querySelectorAll("input[type='radio']"))
      .filter(candidate => candidate.name === radio.name
        && candidate.form === radio.form
        && !candidate.matches(":disabled")
        && !candidate.closest("[inert]"));
    const checked = group.find(candidate => candidate.checked);
    return checked ? checked === radio : group[0] === radio;
  }

  function sequentialControls() {
    return Array.from(modal.querySelectorAll(
      "a[href], button, input:not([type='hidden']), select, textarea, [contenteditable]:not([contenteditable='false']), [tabindex]"))
      .map((element, index) => ({ element, index, tabIndex: element.tabIndex }))
      .filter(candidate => candidate.tabIndex >= 0
        && !candidate.element.matches(":disabled")
        && !candidate.element.closest("[inert], [hidden]")
        && candidate.element.getClientRects().length > 0
        && getComputedStyle(candidate.element).visibility === "visible"
        && isSequentialRadio(candidate.element))
      .sort((left, right) => {
        const leftOrder = left.tabIndex === 0 ? Number.MAX_SAFE_INTEGER : left.tabIndex;
        const rightOrder = right.tabIndex === 0 ? Number.MAX_SAFE_INTEGER : right.tabIndex;
        return leftOrder - rightOrder || left.index - right.index;
      })
      .map(candidate => candidate.element);
  }

  // Chromium contains modal focus but does not consistently wrap at both
  // boundaries. Intermediate sequential navigation remains browser-owned.
  modal.addEventListener("keydown", event => {
    if (event.key !== "Tab") return;
    const controls = sequentialControls();
    if (controls.length === 0) {
      event.preventDefault();
      modal.focus({ preventScroll: true });
      return;
    }

    const first = controls[0];
    const last = controls[controls.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus({ preventScroll: true });
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus({ preventScroll: true });
    }
  });

  refreshHealth();
  setInterval(refreshHealth, 30000);
})();
