// Local Ingestion Monitor — Copilot analysis drawer (Sprint18 §6.6, D045).
//
// Raw boundary: the drawer starts local raw analysis runs via the raw-default
// analysis routes (POST /traces/{id}/analysis with the CSRF header, result
// polling with no-store). The whole drawer exists only in the raw-default
// posture (the server omits its markup under --sanitized-only). Follow-up chat
// is history resend (D045): the client holds the transcript and each follow-up
// creates a new run whose payload carries question + prior Q&A; nothing is
// persisted server-side. Analysis results are local runtime data rendered as
// text via createElement / textContent; no markup strings are ever injected.
(() => {
  "use strict";

  const root = document.getElementById("trace-detail-root");
  const drawer = document.getElementById("copilot-drawer");
  if (!root || !drawer) return; // Not the trace detail page or sanitized posture — no-op.

  const traceId = root.dataset.traceId;
  const flowCard = document.getElementById("flow-card");
  const log = document.getElementById("drawer-log");
  const focusSelect = document.getElementById("drawer-focus");
  const runButton = document.getElementById("drawer-run");
  const closeButton = document.getElementById("drawer-close");
  const questionInput = document.getElementById("drawer-question");
  const sendButton = document.getElementById("drawer-send");
  const suggests = document.getElementById("drawer-suggests");

  const transcript = []; // {question, answer} — client-held, per page load (D045).
  let contextSpanId = null;
  let busy = false;

  /* ── Open / close ── */

  function setOpen(open) {
    drawer.hidden = !open;
    if (flowCard) flowCard.classList.toggle("dimmed-behind-drawer", open);
    if (open) questionInput?.focus();
  }

  document.getElementById("copilot-open")?.addEventListener("click", () => {
    contextSpanId = null;
    setOpen(true);
  });

  document.addEventListener("cao-ask-copilot", (event) => {
    contextSpanId = event.detail?.spanId ?? null;
    if (event.detail?.focus && focusSelect) focusSelect.value = event.detail.focus;
    setOpen(true);
  });

  closeButton?.addEventListener("click", () => setOpen(false));

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && !drawer.hidden) {
      // The drawer owns Esc while open (the flow's deselect must not also fire).
      event.stopImmediatePropagation();
      setOpen(false);
    }
  }, { capture: true });

  /* ── Log rendering ── */

  function appendChip(text) {
    const chip = document.createElement("div");
    chip.className = "drawer-run-chip";
    chip.textContent = text;
    log.append(chip);
    log.scrollTop = log.scrollHeight;
    return chip;
  }

  function appendQuestion(text) {
    const bubble = document.createElement("div");
    bubble.className = "drawer-question-bubble";
    bubble.textContent = text;
    log.append(bubble);
    log.scrollTop = log.scrollHeight;
  }

  function appendAnswer(text, spanId) {
    const block = document.createElement("div");
    block.className = "drawer-answer";
    const body = document.createElement("pre");
    body.className = "drawer-answer-text";
    body.textContent = text;
    block.append(body);
    if (spanId) {
      const show = document.createElement("button");
      show.type = "button";
      show.className = "drawer-show-span";
      show.textContent = "該当スパンを表示";
      show.addEventListener("click", () => {
        document.dispatchEvent(new CustomEvent("cao-span-highlight", { detail: { spanId } }));
      });
      block.append(show);
    }
    log.append(block);
    log.scrollTop = log.scrollHeight;
  }

  /* ── Analysis runs (raw-default local analysis routes, D035/D045) ── */

  async function startRun(payload) {
    const resp = await fetch(`/traces/${encodeURIComponent(traceId)}/analysis`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "x-monitor-csrf": "local-monitor",
      },
      body: JSON.stringify(payload),
    });
    const body = await resp.json();
    if (!resp.ok) {
      throw new Error(body.error ?? String(resp.status));
    }
    return body.run_id;
  }

  function pollRun(runId) {
    return new Promise((resolve, reject) => {
      const tick = async () => {
        try {
          const resp = await fetch(`/traces/${encodeURIComponent(traceId)}/analysis/runs/${encodeURIComponent(runId)}`, { cache: "no-store" });
          const body = await resp.json();
          if (!resp.ok) {
            reject(new Error(body.error ?? String(resp.status)));
            return;
          }
          if (body.status === "queued" || body.status === "running") {
            setTimeout(tick, 1000);
            return;
          }
          resolve(body);
        } catch (error) {
          reject(error);
        }
      };
      tick();
    });
  }

  async function execute(question) {
    if (busy) return;
    busy = true;
    if (runButton) runButton.disabled = true;
    if (sendButton) sendButton.disabled = true;
    const focus = focusSelect?.value ?? "tokens";
    const focusLabel = focusSelect?.selectedOptions?.[0]?.textContent ?? focus;
    if (question) appendQuestion(question);
    const chip = appendChip(question
      ? "追い質問を解析中… (履歴を再送)"
      : `観点「${focusLabel}」で解析を実行中…`);
    const startedAt = performance.now();

    try {
      const runId = await startRun({
        focus,
        spanId: contextSpanId ?? undefined,
        question: question ?? undefined,
        history: question && transcript.length > 0 ? transcript.slice() : undefined,
      });
      const run = await pollRun(runId);
      const seconds = Math.round((performance.now() - startedAt) / 100) / 10;
      chip.textContent = question
        ? `追い質問を解析 — ${seconds}s`
        : `観点「${focusLabel}」で解析を実行 — ${seconds}s`;
      const answer = run.result_markdown ?? run.error_message ?? `解析は ${run.status} で終了しました。`;
      appendAnswer(answer, contextSpanId);
      transcript.push({ question: question ?? `観点「${focusLabel}」での解析`, answer });
    } catch (error) {
      chip.textContent = `解析に失敗しました: ${error.message}`;
    } finally {
      busy = false;
      if (runButton) runButton.disabled = false;
      if (sendButton) sendButton.disabled = false;
    }
  }

  runButton?.addEventListener("click", () => execute(null));

  function sendQuestion(text) {
    const question = (text ?? "").trim();
    if (!question) return;
    if (questionInput) questionInput.value = "";
    execute(question);
  }

  sendButton?.addEventListener("click", () => sendQuestion(questionInput?.value));
  questionInput?.addEventListener("keydown", (event) => {
    if (event.key === "Enter") sendQuestion(questionInput.value);
  });

  suggests?.addEventListener("click", (event) => {
    const chip = event.target.closest(".suggest-chip");
    if (chip) sendQuestion(chip.textContent);
  });
})();
