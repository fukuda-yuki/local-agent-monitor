// Local Ingestion Monitor — span inspector (Sprint18 §6.4).
//
// Raw boundary: the inspector's formatted / raw tabs read the raw-bearing
// GET /traces/{traceId}/spans/{spanId}/detail route (D043) — but only when the
// server rendered the page in the raw-default posture
// (data-raw-available="true"). Under --sanitized-only that route is absent and
// this module renders sanitized span metadata only, without fetching. All DOM
// nodes are built with createElement / textContent; no markup strings are ever
// injected.
(() => {
  "use strict";

  const root = document.getElementById("trace-detail-root");
  const inspector = document.getElementById("span-inspector");
  if (!root || !inspector) return; // Not the trace detail page — no-op.

  const traceId = root.dataset.traceId;
  const rawAvailable = root.dataset.rawAvailable === "true";
  const cacheColumn = document.getElementById("cache-column");
  const errorPanel = document.getElementById("error-panel");

  let currentSpan = null;
  let currentDetail = null;
  let currentDetailState = "idle";
  let selectionVersion = 0;
  let activeTab = "formatted";
  let restorePanel = null; // Which side panel was visible before the inspector opened.

  function compactTokens(value) {
    if (value === null || value === undefined) return "—";
    const abs = Math.abs(value);
    if (abs >= 1_000_000) return `${Math.round((value / 1_000_000) * 10) / 10}M`;
    if (abs >= 1_000) return `${Math.round((value / 1_000) * 10) / 10}K`;
    return String(value);
  }

  function fmtDuration(ms) {
    if (ms === null || ms === undefined) return "—";
    if (ms < 1000) return `${Math.round(ms)}ms`;
    return `${Math.round(ms / 100) / 10}s`;
  }

  function sectionTitle(text) {
    const node = document.createElement("span");
    node.className = "inspector-section-title";
    node.textContent = text;
    return node;
  }

  function monoBlock(text) {
    const pre = document.createElement("pre");
    pre.className = "inspector-mono-block";
    pre.textContent = text;
    return pre;
  }

  function metaRow(label, value) {
    const row = document.createElement("div");
    row.className = "inspector-meta-row";
    const labelNode = document.createElement("span");
    labelNode.textContent = label;
    const valueNode = document.createElement("span");
    valueNode.className = "monitor-mono";
    valueNode.textContent = value;
    row.append(labelNode, valueNode);
    return row;
  }

  /* ── Open / close ── */

  function open(span) {
    selectionVersion += 1;
    const version = selectionVersion;
    currentSpan = span;
    currentDetail = null;
    currentDetailState = rawAvailable && span.span_id ? "loading" : "idle";
    activeTab = "formatted";
    if (restorePanel === null) {
      restorePanel = errorPanel && !errorPanel.hidden ? errorPanel : cacheColumn;
    }
    if (cacheColumn) cacheColumn.hidden = true;
    if (errorPanel) errorPanel.hidden = true;
    inspector.hidden = false;
    render();
    if (rawAvailable && span.span_id) {
      loadDetail(span.span_id, version);
    }
  }

  function close() {
    selectionVersion += 1;
    inspector.hidden = true;
    inspector.replaceChildren();
    currentSpan = null;
    currentDetail = null;
    currentDetailState = "idle";
    if (restorePanel) restorePanel.hidden = false;
    restorePanel = null;
    document.dispatchEvent(new CustomEvent("cao-inspector-closed"));
  }

  async function loadDetail(spanId, version) {
    let detail = null;
    let state = "unavailable";
    try {
      const resp = await fetch(`/traces/${encodeURIComponent(traceId)}/spans/${encodeURIComponent(spanId)}/detail`, { cache: "no-store" });
      if (resp.ok) {
        const parsed = await resp.json();
        if (parsed && typeof parsed === "object") {
          detail = parsed;
          state = "success";
        }
      }
    } catch {
      state = "unavailable";
    }
    if (selectionVersion === version && currentSpan?.span_id === spanId) {
      currentDetail = detail;
      currentDetailState = state;
      render();
    }
  }

  /* ── Rendering ── */

  function render() {
    if (!currentSpan) return;
    inspector.replaceChildren();
    const span = currentSpan;
    const kind = span.kind ?? "other";

    const panel = document.createElement("div");
    panel.className = "inspector-panel";

    // Header: kind mark + name + sub line + tabs + close.
    const head = document.createElement("div");
    head.className = "inspector-head";
    const mark = document.createElement("span");
    mark.className = `inspector-mark mark-${kind}`;
    mark.setAttribute("aria-hidden", "true");
    const name = document.createElement("span");
    name.className = "inspector-name monitor-mono";
    name.textContent = span.label ?? span.span_id ?? "span";
    const closeButton = document.createElement("button");
    closeButton.type = "button";
    closeButton.className = "inspector-close";
    closeButton.setAttribute("aria-label", "インスペクタを閉じる");
    closeButton.textContent = "✕";
    closeButton.addEventListener("click", () => {
      document.dispatchEvent(new CustomEvent("cao-span-deselect"));
    });
    head.append(mark, name, closeButton);

    const sub = document.createElement("p");
    sub.className = "inspector-sub";
    sub.textContent = [
      kind,
      span.status === "error" ? `失敗${span.error_type ? ` · ${span.error_type}` : ""}` : null,
      fmtDuration(span.durationMs),
      span.total_tokens !== null && span.total_tokens !== undefined ? `${compactTokens(span.total_tokens)} tok` : null,
    ].filter(Boolean).join(" · ");

    const tabs = document.createElement("div");
    tabs.className = "inspector-tabs";
    for (const [key, label] of [["formatted", "整形"], ["raw", "raw"]]) {
      const tab = document.createElement("button");
      tab.type = "button";
      tab.className = `inspector-tab${activeTab === key ? " active" : ""}`;
      tab.textContent = label;
      tab.addEventListener("click", () => {
        activeTab = key;
        render();
      });
      tabs.append(tab);
    }

    const body = document.createElement("div");
    body.className = "inspector-body";
    if (activeTab === "raw") {
      renderRawTab(body, span);
    } else {
      renderFormattedTab(body, span);
    }

    panel.append(head, sub, tabs, body);
    inspector.append(panel);
  }

  function renderFormattedTab(body, span) {
    if (span.kind === "agent") {
      renderAgentDetail(body, span);
      return;
    }

    if (!rawAvailable) {
      const note = document.createElement("p");
      note.className = "inspector-note";
      note.textContent = "--sanitized-only のため raw 由来の詳細は表示できません。以下は sanitized なスパン情報です。";
      body.append(note);
      appendMeta(body, span);
      return;
    }

    const tool = currentDetail?.tool;
    const llm = currentDetail?.llm;

    if (tool) {
      if (tool.arguments) {
        body.append(sectionTitle("呼出引数"), monoBlock(tool.arguments));
      }
      if (tool.result_tail) {
        body.append(sectionTitle(tool.exit_code !== null && tool.exit_code !== undefined ? `結果 · exit ${tool.exit_code}` : "結果"));
        body.append(monoBlock(tool.result_tail));
        if (tool.result_token_estimate) {
          const note = document.createElement("p");
          note.className = "inspector-note";
          note.textContent = `全文は raw で確認 — この結果は推定 ${compactTokens(tool.result_token_estimate)} tokens として LLM に渡っています`;
          body.append(note);
        }
      }
    }

    if (llm) {
      const inputTokens = span.input_tokens;
      body.append(sectionTitle(inputTokens !== null && inputTokens !== undefined ? `入力の構成 — ${compactTokens(inputTokens)} tokens` : "入力の構成"));
      const list = document.createElement("div");
      list.className = "inspector-messages";
      for (const message of llm.messages) {
        const row = document.createElement("div");
        row.className = "inspector-message";
        const chip = document.createElement("span");
        chip.className = `role-chip role-${message.role}`;
        chip.textContent = message.role;
        const preview = document.createElement("span");
        preview.className = "message-preview";
        preview.textContent = message.preview;
        const size = document.createElement("span");
        size.className = "message-size monitor-mono";
        size.textContent = `推定 ${compactTokens(message.token_estimate)} tok`;
        row.append(chip, preview, size);
        list.append(row);
      }
      if (llm.messages.length === 0) {
        const empty = document.createElement("p");
        empty.className = "inspector-note";
        empty.textContent = "入力メッセージは抽出できませんでした（raw タブで全文を確認できます）。";
        list.append(empty);
      }
      body.append(list);

      if (llm.response_preview) {
        body.append(sectionTitle(span.output_tokens !== null && span.output_tokens !== undefined
          ? `応答プレビュー — 出力 ${compactTokens(span.output_tokens)} tokens`
          : "応答プレビュー"));
        body.append(monoBlock(llm.response_preview));
      }

      // Token breakdown bar: cache read / uncached input / output.
      const cacheRead = span.cache_read_tokens ?? 0;
      const uncached = Math.max(0, (span.input_tokens ?? 0) - cacheRead);
      if ((span.input_tokens ?? 0) > 0 || (span.output_tokens ?? 0) > 0) {
        body.append(sectionTitle("トークン内訳"));
        const bar = document.createElement("div");
        bar.className = "preview-token-bar";
        for (const [cls, grow] of [["seg-cache", cacheRead], ["seg-input", uncached], ["seg-output", span.output_tokens ?? 0]]) {
          const seg = document.createElement("span");
          seg.className = `bar-seg ${cls}`;
          seg.style.flexGrow = String(grow);
          bar.append(seg);
        }
        body.append(bar);
        const note = document.createElement("p");
        note.className = "inspector-note";
        note.textContent = `キャッシュ ${compactTokens(span.cache_read_tokens)} · 入力 ${compactTokens(uncached)} · 出力 ${compactTokens(span.output_tokens)}`;
        body.append(note);
      }
    }

    if (!tool && !llm) {
      const note = document.createElement("p");
      note.className = "inspector-note";
      note.textContent = currentDetail
        ? "整形表示に使える属性が見つかりませんでした。raw タブで OTLP スパン全文を確認できます。"
        : "詳細を読み込み中…";
      body.append(note);
    }

    appendMeta(body, span);

    const actions = document.createElement("div");
    actions.className = "inspector-actions";
    const ask = document.createElement("button");
    ask.type = "button";
    ask.className = "inspector-ask-copilot";
    ask.textContent = "このスパンを Copilot に聞く";
    ask.addEventListener("click", () => {
      document.dispatchEvent(new CustomEvent("cao-ask-copilot", { detail: { traceId, spanId: span.span_id } }));
    });
    const openRaw = document.createElement("button");
    openRaw.type = "button";
    openRaw.className = "inspector-open-raw";
    openRaw.textContent = "raw を開く";
    openRaw.addEventListener("click", () => {
      activeTab = "raw";
      render();
    });
    actions.append(ask, openRaw);
    body.append(actions);
  }

  function renderAgentDetail(body, span) {
    const agent = span.agent ?? {};
    body.append(sectionTitle("Agent 詳細"));
    const meta = document.createElement("div");
    meta.className = "inspector-meta agent-inspector-meta";
    meta.append(
      metaRow("span id", agent.span_id ?? span.span_id ?? "—"),
      metaRow("Agent名", agent.agent_name ?? span.label ?? "—"),
      metaRow("role", agent.agent_role ?? "unknown"),
      metaRow("caller Agent", agent.caller_agent_span_id ?? "—"),
      metaRow("model", agent.model ?? "—"),
      metaRow("開始", agent.started_at ?? span.start_time ?? "—"),
      metaRow("終了", agent.ended_at ?? span.end_time ?? "—"),
      metaRow("所要", fmtDuration(agent.duration_ms ?? span.durationMs)),
      metaRow("input tokens", compactTokens(agent.input_tokens)),
      metaRow("output tokens", compactTokens(agent.output_tokens)),
      metaRow("total tokens", compactTokens(agent.total_tokens)),
      metaRow("status", agent.status ?? "—"),
      metaRow("子Agent", String(agent.child_agent_count ?? 0)),
      metaRow("Agent depth", agent.agent_depth === null || agent.agent_depth === undefined ? "—" : String(agent.agent_depth)),
      metaRow("relationship source", agent.relationship_source ?? "unresolved"),
      metaRow("relationship confidence", agent.relationship_confidence ?? "unknown"),
      metaRow("所有ターン", String(agent.ownedTurnCount ?? 0)),
      metaRow("所有ツール", String(agent.ownedToolCount ?? 0)));
    body.append(meta);

    if (!rawAvailable) {
      const note = document.createElement("p");
      note.className = "inspector-note";
      note.textContent = "--sanitized-only のため sanitized な Agent 詳細のみ表示しています。";
      body.append(note);
      return;
    }

    const llm = currentDetail?.llm;
    if (currentDetailState === "unavailable") {
      const note = document.createElement("p");
      note.className = "inspector-note";
      note.textContent = "Sub-agent の指示・応答を取得できませんでした。sanitized な Agent 詳細は引き続き確認できます。";
      body.append(note);
    } else if (!llm) {
      const note = document.createElement("p");
      note.className = "inspector-note";
      note.textContent = currentDetailState === "success"
        ? "Sub-agent の指示・応答は抽出できませんでした。raw タブで確認できます。"
        : "Sub-agent の指示・応答を読み込み中…";
      body.append(note);
    } else {
      if (llm.messages.length > 0) {
        body.append(sectionTitle("Sub-agent 指示（best effort）"));
        llm.messages.forEach((message) => body.append(monoBlock(message.preview)));
      }
      if (llm.response_preview) {
        body.append(sectionTitle("Sub-agent 応答（best effort）"), monoBlock(llm.response_preview));
      }
    }

    const actions = document.createElement("div");
    actions.className = "inspector-actions";
    const openRaw = document.createElement("button");
    openRaw.type = "button";
    openRaw.className = "inspector-open-raw";
    openRaw.textContent = "raw を開く";
    openRaw.addEventListener("click", () => {
      activeTab = "raw";
      render();
    });
    actions.append(openRaw);
    body.append(actions);
  }

  function appendMeta(body, span) {
    body.append(sectionTitle("メタ"));
    const meta = document.createElement("div");
    meta.className = "inspector-meta";
    meta.append(
      metaRow("span id", span.span_id ?? "—"),
      metaRow("親スパン", span.parent_span_id ?? "—"),
      metaRow("開始", span.start_time ?? "—"),
      metaRow("終了", span.end_time ?? "—"));
    body.append(meta);
  }

  function renderRawTab(body, span) {
    if (!rawAvailable) {
      const note = document.createElement("p");
      note.className = "inspector-note";
      note.textContent = "--sanitized-only のため raw タブは利用できません。";
      body.append(note);
      return;
    }

    const rawJson = currentDetail?.raw_span_json;
    if (!rawJson) {
      const note = document.createElement("p");
      note.className = "inspector-note";
      note.textContent = currentDetailState === "unavailable" || currentDetail
        ? "raw スパン JSON を取得できませんでした。"
        : "raw スパン JSON を読み込み中…";
      body.append(note);
      return;
    }

    let pretty = rawJson;
    try {
      pretty = JSON.stringify(JSON.parse(rawJson), null, 2);
    } catch {
      // Keep the original text when it is not valid JSON.
    }

    const block = monoBlock(pretty);
    block.classList.add("inspector-raw-json");
    body.append(block);

    const actions = document.createElement("div");
    actions.className = "inspector-actions";
    const copy = document.createElement("button");
    copy.type = "button";
    copy.className = "inspector-copy-json";
    copy.textContent = "JSON をコピー";
    copy.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(pretty);
        copy.textContent = "コピーしました";
        setTimeout(() => { copy.textContent = "JSON をコピー"; }, 1500);
      } catch {
        copy.textContent = "コピーできませんでした";
      }
    });
    actions.append(copy);
    body.append(actions);
  }

  /* ── Wiring: flow selection drives the inspector ── */

  document.addEventListener("cao-span-select", (event) => {
    const { spanId, span } = event.detail ?? {};
    if (spanId && span) {
      open(span);
    } else {
      close();
    }
  });
})();
