(() => {
  "use strict";
  const root = document.querySelector("[data-repository-compare]");
  if (!root || !window.LocalMonitorV1FactState) return;

  const UUID_V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const SHA256 = /^[0-9a-f]{64}$/;
  const NODE = /^node-[0-9a-f]{32}$/;
  const SECTIONS = [
    [1, "target", "対象"], [2, "tokens", "トークン"],
    [3, "input_token_breakdown", "入力トークンの内訳"], [4, "time_and_execution", "時間・実行量"],
    [5, "skills", "スキル"], [6, "tools", "ツール"], [7, "subagents", "サブエージェント"],
    [8, "errors_and_retries", "エラー・再試行"], [9, "conditions", "比較条件"],
  ];
  const FAMILY = new Map([["skills", "skill"], ["tools", "tool"], ["subagents", "subagent"]]);
  const EVIDENCE_LABELS = new Map([
    ["value", "保存値"], ["available_count", "利用可能件数"], ["median", "中央値"], ["minimum", "最小値"],
    ["maximum", "最大値"], ["total", "合計"], ["absolute_difference", "絶対差"],
    ["relative_difference_percent", "相対差"], ["condition", "条件"], ["count", "件数"],
    ["duration_ms", "所要時間"], ["input_tokens", "入力トークン"], ["output_tokens", "出力トークン"],
    ["total_tokens", "合計トークン"], ["cache_read", "キャッシュ読み取り"],
    ["cache_creation", "キャッシュ作成"], ["new_input", "新規入力"],
    ["error_count", "エラー件数"], ["retry_count", "再試行件数"],
  ]);
  const state = { generation: 0, controller: null, evidenceGeneration: 0, evidenceController: null, evidenceCursor: null, evidenceResultOrdinal: null, evidenceField: null, invoker: null };
  const sections = root.querySelector("[data-compare-sections]");
  const status = root.querySelector("#repository-compare-status");
  const evidenceDialog = root.querySelector("#repository-compare-evidence-dialog");
  const evidenceStatus = root.querySelector("#repository-compare-evidence-status");
  const evidenceItems = root.querySelector("[data-compare-evidence-items]");
  const evidenceMore = root.querySelector("#repository-compare-evidence-more");
  const evidenceClose = root.querySelector("#repository-compare-evidence-close");
  const repositoryId = root.dataset.repositoryId;
  const comparisonId = root.dataset.comparisonId;
  if (!UUID_V7.test(repositoryId ?? "") || !UUID_V7.test(comparisonId ?? "")) return;
  const base = `/api/local-monitor/v1/repositories/${repositoryId}/comparisons/${comparisonId}`;

  const exact = (value, keys) => value && typeof value === "object" && !Array.isArray(value)
    && Object.keys(value).length === keys.length && Object.keys(value).every((key, index) => key === keys[index]);
  const element = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  };
  const valuePair = value => exact(value, ["key", "value"])
    && typeof value.key === "string" && typeof value.value === "string" && value.key.length > 0 && value.value.length > 0;

  async function get(path, signal) {
    const response = await fetch(path, { method: "GET", credentials: "same-origin", cache: "no-store", signal });
    const text = await response.text();
    if (!response.ok) {
      let code = "local_monitor_ui_unavailable";
      try { code = JSON.parse(text).error ?? code; } catch { /* closed below */ }
      throw Object.assign(new Error(code), { status: response.status, code });
    }
    return JSON.parse(text);
  }

  function validateRead(value) {
    if (!exact(value, ["schema_version", "comparison_id", "repository_id", "receipt_sha256", "created_at", "expires_at", "cohorts", "sections", "results"])
        || value.schema_version !== "local-monitor-comparison-read.response.v1" || value.comparison_id !== comparisonId
        || value.repository_id !== repositoryId || !SHA256.test(value.receipt_sha256)
        || !exact(value.cohorts, ["a", "b"]) || !Array.isArray(value.sections) || !Array.isArray(value.results)
        || value.sections.length !== SECTIONS.length) throw new TypeError("invalid comparison read");
    for (const cohort of ["a", "b"]) {
      const item = value.cohorts[cohort];
      if (!exact(item, ["label", "session_ids", "included_count"])
          || item.label !== (cohort === "a" ? "基準" : "比較対象") || !Array.isArray(item.session_ids)
          || item.session_ids.some(id => !UUID_V7.test(id)) || item.included_count !== item.session_ids.length) throw new TypeError("invalid comparison read");
    }
    value.sections.forEach((item, index) => {
      const expected = SECTIONS[index];
      if (!exact(item, ["ordinal", "key", "label"]) || item.ordinal !== expected[0] || item.key !== expected[1] || item.label !== expected[2])
        throw new TypeError("invalid comparison read");
    });
    value.results.forEach(item => {
      if (!exact(item, ["result_ordinal", "section_key", "row_kind", "row_key", "values"])
          || !Number.isInteger(item.result_ordinal) || item.result_ordinal < 1 || !SECTIONS.some(section => section[1] === item.section_key)
          || typeof item.row_kind !== "string" || typeof item.row_key !== "string" || !Array.isArray(item.values)
          || !item.values.every(valuePair)) throw new TypeError("invalid comparison read");
    });
    return value;
  }

  function renderStoredValue(target, pair) {
    if (pair.value === "not_available") {
      renderFactState(target, "not_observed");
      return;
    }
    if (pair.key.endsWith("_unavailable_states")) {
      if (pair.value === "none") { target.textContent = "なし"; return; }
      const entries = pair.value.split(";");
      for (const [index, entry] of entries.entries()) {
        const match = /^([a-z_]+)=([1-9][0-9]*)$/.exec(entry);
        if (!match) { renderFactState(target, "projection_invalid"); return; }
        if (index > 0) target.append(document.createTextNode("、"));
        const item = element("span"); renderFactState(item, match[1]); item.append(document.createTextNode(`（${match[2]}件）`)); target.append(item);
      }
      return;
    }
    target.textContent = pair.value;
  }

  function renderFactState(target, token) {
    const presentations = {
      recorded: { state: "observed_positive", recordedCount: 1n },
      explicit_zero: { state: "observed_zero", recordedCount: 0n, hasCompleteCoverageProof: true, sourceText: "保存済み比較", reasonText: "保存時点で明示的に 0 です" },
      not_observed: { state: "not_observed", recordedCount: null },
      source_unsupported: { state: "unsupported", recordedCount: null, sourceText: "セッション取得元", reasonText: "この項目は取得元で記録されません" },
      capture_gap: { state: "capture_gap", recordedCount: null, reasonText: "この項目の記録が一部欠けています" },
      certification_pending: { state: "certification_pending", recordedCount: null },
      not_captured: { state: "raw_not_captured", recordedCount: null, reasonText: "この項目は記録されていません" },
      expired: { state: "raw_expired", recordedCount: null, reasonText: "この項目は保存期間を過ぎています" },
      deleted: { state: "raw_not_captured", recordedCount: null, reasonText: "この項目は削除されています" },
      read_denied: { state: "raw_not_captured", recordedCount: null, reasonText: "この項目は読み取れません" },
      inconsistent: { state: "inconsistent", recordedCount: null, reasonText: "この項目の値を確定できません" },
      projection_invalid: { state: "projection_invalid", recordedCount: null, reasonText: "この項目の記録を検証できません" },
      too_large: { state: "projection_invalid", recordedCount: null, reasonText: "記録が表示可能な範囲を超えています" },
      projection_unavailable: { state: "not_observed", recordedCount: null, reasonText: "保存済みの値を利用できません" },
    };
    window.LocalMonitorV1FactState.render(target, presentations[token] ?? presentations.projection_invalid);
  }

  function evidenceFields(result) {
    if (result.section_key === "target") {
      if (["included_session_count", "available_session_count"].includes(result.row_key)) return ["count"];
      if (["period", "archived_inclusion"].includes(result.row_key)) return ["condition"];
      return [];
    }
    if (result.row_kind === "skill") return ["count"];
    if (result.row_kind === "tool") return ["count", "error_count", "retry_count"];
    if (result.row_kind === "subagent") return ["count", "total_tokens"];
    if (result.row_kind === "condition" || result.section_key === "conditions") return ["condition"];
    if (result.row_kind !== "scalar") return [];
    const fields = ["value", "available_count", "median", "minimum", "maximum", "total", "absolute_difference", "relative_difference_percent"];
    const specialized = new Map([
      ["session_duration", "duration_ms"], ["input_tokens", "input_tokens"], ["output_tokens", "output_tokens"],
      ["total_tokens", "total_tokens"], ["cache_read_tokens", "cache_read"], ["cache_creation_tokens", "cache_creation"],
      ["new_input_tokens", "new_input"], ["error_count", "error_count"], ["retry_count", "retry_count"],
    ]).get(result.row_key);
    if (specialized) fields.push(specialized);
    return fields;
  }

  function resultTable(items, captionText) {
    const table = element("table", "local-monitor-compare-table");
    const caption = element("caption", null, captionText);
    const head = element("thead");
    const headerRow = element("tr");
    ["項目", "保存された値", "根拠"].forEach(label => headerRow.append(element("th", null, label)));
    head.append(headerRow);
    const body = element("tbody");
    for (const result of items) {
      const group = element("tr", "local-monitor-compare-result-heading");
      const groupLabel = element("th", null, result.display_name ?? result.row_key);
      groupLabel.colSpan = 2;
      const actions = element("td", "local-monitor-compare-evidence-actions");
      for (const field of evidenceFields(result)) {
        const button = element("button", null, `${EVIDENCE_LABELS.get(field)}の根拠を表示`);
        button.type = "button";
        button.addEventListener("click", () => openEvidence(result.result_ordinal, field, button));
        actions.append(button);
      }
      group.append(groupLabel, actions);
      body.append(group);
      for (const pair of result.values) {
        const row = element("tr");
        row.append(element("th", null, pair.key));
        const value = element("td");
        renderStoredValue(value, pair);
        row.append(value);
        row.append(element("td"));
        body.append(row);
      }
    }
    table.append(caption, head, body);
    return table;
  }

  function renderRead(snapshot) {
    root.querySelector("[data-compare-cohort-count='a']").textContent = `${snapshot.cohorts.a.included_count}件`;
    root.querySelector("[data-compare-cohort-count='b']").textContent = `${snapshot.cohorts.b.included_count}件`;
    const nodes = snapshot.sections.map(section => {
      const region = element("section", "local-monitor-compare-section");
      region.setAttribute("aria-labelledby", `compare-section-${section.ordinal}`);
      const heading = element("h2", null, section.label);
      heading.id = `compare-section-${section.ordinal}`;
      region.append(heading);
      const family = FAMILY.get(section.key);
      if (family) region.append(namedFamily(family, section.label));
      else {
        const results = snapshot.results.filter(result => result.section_key === section.key);
        region.append(results.length ? resultTable(results, `${section.label}の保存済み比較値`) : element("p", null, "保存された結果はありません。"));
      }
      return region;
    });
    sections.replaceChildren(...nodes);
    status.textContent = "保存済みの比較結果を表示しています。";
  }

  function namedFamily(family, label) {
    const owner = element("div", "local-monitor-compare-family");
    const form = element("form", "local-monitor-compare-search");
    const input = element("input"); input.type = "search"; input.maxLength = 200; input.placeholder = `${label}を検索`;
    const submit = element("button", null, "検索"); submit.type = "submit";
    form.append(input, submit);
    const live = element("p"); live.setAttribute("role", "status"); live.setAttribute("aria-live", "polite");
    const content = element("div");
    const next = element("button", null, "次のページ"); next.type = "button"; next.hidden = true;
    let cursor = null; let search = ""; let generation = 0; let controller = null; let table = null; let body = null;
    const load = async (append = false) => {
      const current = ++generation; controller?.abort(); controller = new AbortController();
      live.textContent = "読み込んでいます。"; next.disabled = true;
      const query = new URLSearchParams(); query.append("family", family); if (search) query.append("q", search); if (append && cursor) query.append("after", cursor); query.append("limit", "50");
      try {
        const value = await get(`${base}/rows?${query}`, controller.signal);
        if (current !== generation || !exact(value, ["schema_version", "comparison_id", "family", "items", "next_cursor"])
            || value.schema_version !== "local-monitor-comparison-rows.response.v1" || value.comparison_id !== comparisonId
            || value.family !== family || !Array.isArray(value.items)) throw new TypeError("invalid comparison rows");
        value.items.forEach(item => {
          if (!exact(item, ["result_ordinal", "row_key", "display_name", "values"]) || !Array.isArray(item.values) || !item.values.every(valuePair)) throw new TypeError("invalid comparison rows");
        });
        const pageTable = resultTable(value.items.map(item => ({ ...item, row_kind: family, section_key: `${family}s` })), `${label}の保存済み比較値`);
        if (append) {
          for (const row of [...pageTable.tBodies[0].rows]) body.append(row);
        } else {
          table = pageTable; body = table.tBodies[0]; content.replaceChildren(table);
        }
        cursor = value.next_cursor; next.hidden = cursor === null; next.disabled = false; live.textContent = `${value.items.length}件を読み込みました。`;
      } catch (error) { if (!controller.signal.aborted && current === generation) live.textContent = "読み込めませんでした。"; }
    };
    form.addEventListener("submit", event => { event.preventDefault(); search = input.value.trim(); cursor = null; load(false); });
    next.addEventListener("click", () => load(true));
    const initial = element("button", null, `${label}を読み込む`); initial.type = "button";
    initial.addEventListener("click", () => { initial.remove(); load(false); });
    owner.append(form, initial, live, content, next);
    return owner;
  }

  function evidenceLink(item) {
    const plain = `/sessions/${item.session_id}`;
    if (item.execution_id !== null && !UUID_V7.test(item.execution_id) || item.node_id !== null && !NODE.test(item.node_id)) return null;
    const query = item.execution_id !== null
      ? `?execution=${item.execution_id}${item.node_id === null ? "" : `&node=${item.node_id}`}`
      : item.node_id !== null ? `?node=${item.node_id}` : "";
    return item.session_location === plain + query ? item.session_location : null;
  }

  async function openEvidence(resultOrdinal, fieldKey, invoker) {
    state.evidenceController?.abort(); state.invoker = invoker; state.evidenceCursor = null;
    state.evidenceResultOrdinal = resultOrdinal; state.evidenceField = fieldKey;
    evidenceItems.replaceChildren(); evidenceStatus.textContent = "根拠を読み込んでいます。";
    evidenceMore.hidden = true;
    if (!evidenceDialog.open) evidenceDialog.showModal(); evidenceClose.focus();
    await loadEvidence(false);
  }

  async function loadEvidence(append) {
    const generation = ++state.evidenceGeneration; state.evidenceController?.abort();
    const controller = new AbortController(); state.evidenceController = controller;
    evidenceMore.disabled = true; evidenceStatus.textContent = append ? "続きの根拠を読み込んでいます。" : "根拠を読み込んでいます。";
    const query = new URLSearchParams(); query.append("result_ordinal", String(state.evidenceResultOrdinal)); query.append("field_key", state.evidenceField);
    if (append && state.evidenceCursor) query.append("after", state.evidenceCursor);
    query.append("limit", "100");
    try {
      const value = await get(`${base}/evidence?${query}`, controller.signal);
      if (generation !== state.evidenceGeneration || !exact(value, ["schema_version", "comparison_id", "result_ordinal", "field_key", "items", "next_cursor"])
          || value.schema_version !== "local-monitor-comparison-evidence.response.v1" || value.comparison_id !== comparisonId
          || value.result_ordinal !== state.evidenceResultOrdinal || value.field_key !== state.evidenceField || !Array.isArray(value.items)
          || !(value.next_cursor === null || typeof value.next_cursor === "string" && value.next_cursor.length > 0)) throw new TypeError("invalid comparison evidence");
      const list = append ? evidenceItems.querySelector("ul") : element("ul", "local-monitor-compare-evidence-list");
      if (!list) throw new TypeError("invalid comparison evidence");
      for (const item of value.items) {
        if (!exact(item, ["evidence_ordinal", "cohort", "session_id", "state", "unavailable_reason", "consumed_value", "consumed_revision", "execution_id", "node_id", "session_location"])
            || !UUID_V7.test(item.session_id) || !(item.consumed_revision === null || SHA256.test(item.consumed_revision))) throw new TypeError("invalid comparison evidence");
        const row = element("li");
        row.append(element("span", null, item.consumed_value ?? "利用できません"), document.createTextNode(" / "));
        const factState = element("span");
        if (item.state === "included") factState.textContent = "採用";
        else renderFactState(factState, item.unavailable_reason ?? "projection_unavailable");
        row.append(factState);
        if (item.consumed_revision) row.append(document.createTextNode(` / revision ${item.consumed_revision}`));
        const href = evidenceLink(item); if (href) { const link = element("a", null, "セッションを開く"); link.href = href; row.append(document.createTextNode(" / "), link); }
        list.append(row);
      }
      if (!append) evidenceItems.replaceChildren(list);
      state.evidenceCursor = value.next_cursor; evidenceMore.hidden = value.next_cursor === null; evidenceMore.disabled = false;
      evidenceStatus.textContent = `${list.children.length}件の根拠を表示しています。`;
    } catch (error) { if (!controller.signal.aborted && generation === state.evidenceGeneration) evidenceStatus.textContent = "根拠を読み込めませんでした。"; }
  }

  function closeEvidence() {
    state.evidenceController?.abort(); state.evidenceController = null; state.evidenceCursor = null; evidenceStatus.textContent = ""; evidenceItems.replaceChildren(); evidenceMore.hidden = true;
    if (evidenceDialog.open) evidenceDialog.close(); if (state.invoker?.isConnected) state.invoker.focus(); state.invoker = null;
  }
  evidenceClose.addEventListener("click", closeEvidence);
  evidenceMore.addEventListener("click", () => { if (state.evidenceCursor) loadEvidence(true); });
  evidenceDialog.addEventListener("cancel", event => { event.preventDefault(); closeEvidence(); });
  window.addEventListener("pagehide", () => { state.controller?.abort(); closeEvidence(); });

  (async () => {
    const generation = ++state.generation; state.controller?.abort(); state.controller = new AbortController();
    try { const snapshot = validateRead(await get(base, state.controller.signal)); if (generation === state.generation) renderRead(snapshot); }
    catch (error) {
      if (state.controller.signal.aborted || generation !== state.generation) return;
      status.textContent = error.code === "comparison_expired" ? "比較結果の保存期間が終了しました。" : "比較結果を読み込めませんでした。";
    }
  })();
})();
