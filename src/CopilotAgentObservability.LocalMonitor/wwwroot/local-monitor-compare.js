(() => {
  "use strict";
  const root = document.querySelector("[data-repository-compare]");
  if (!root || !window.LocalMonitorV1FactState) return;

  const UUID_V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const RESULT_MAXIMUM_BYTES = 1_048_576;
  const RUN_MAXIMUM_BYTES = RESULT_MAXIMUM_BYTES + 4_096;
  const SMALL_RESPONSE_MAXIMUM_BYTES = 4_096;
  const SHA256 = /^[0-9a-f]{64}$/;
  const NODE = /^node-[0-9a-f]{32}$/;
  const SECTIONS = [
    [1, "target", "対象"], [2, "tokens", "トークン"],
    [3, "input_token_breakdown", "入力トークンの内訳"], [4, "time_and_execution", "時間・実行量"],
    [5, "skills", "スキル"], [6, "tools", "ツール"], [7, "subagents", "サブエージェント"],
    [8, "errors_and_retries", "エラー・再試行"], [9, "conditions", "比較条件"],
  ];
  const FAMILY = new Map([["skills", "skill"], ["tools", "tool"], ["subagents", "subagent"]]);
  const STATIC_CONDITION_ROWS = new Set(["sources", "models", "source_versions", "adapter_versions", "completeness"]);
  const EVIDENCE_LABELS = new Map([
    ["value", "保存値"], ["available_count", "利用可能件数"], ["median", "中央値"], ["minimum", "最小値"],
    ["maximum", "最大値"], ["total", "合計"], ["absolute_difference", "絶対差"],
    ["relative_difference_percent", "相対差"], ["condition", "条件"], ["count", "件数"],
    ["duration_ms", "所要時間"], ["input_tokens", "入力トークン"], ["output_tokens", "出力トークン"],
    ["total_tokens", "トークン合計"], ["cache_read", "キャッシュから読み込み"],
    ["cache_creation", "キャッシュ書き込み"], ["new_input", "新規入力"],
    ["error_count", "エラー件数"], ["retry_count", "再試行件数"],
  ]);
  const ROW_LABELS = new Map([
    ["included_session_count", "対象セッション数"], ["excluded_session_count", "除外セッション数"],
    ["available_session_count", "利用可能なセッション数"], ["period", "期間"], ["archived_inclusion", "アーカイブ済みの対象"],
    ["input_tokens", "入力トークン"], ["output_tokens", "出力トークン"], ["total_tokens", "トークン合計"],
    ["cache_read_tokens", "キャッシュから読み込み"], ["new_input_tokens", "新規入力"],
    ["cache_creation_tokens", "キャッシュ書き込み"], ["cache_read_ratio", "キャッシュ読み込み比率"],
    ["session_duration", "セッションの所要時間"], ["execution_count", "実行数"], ["model_turn_count", "モデル応答数"],
    ["tool_call_count", "ツール呼び出し数"], ["skill_invocation_count", "スキル呼び出し数"],
    ["subagent_start_count", "サブエージェント開始数"], ["error_count", "エラー件数"], ["retry_count", "再試行件数"],
    ["subagent_aggregate_start_count", "サブエージェント開始数"], ["subagent_aggregate_completed_count", "サブエージェント完了数"],
    ["subagent_aggregate_failed_count", "サブエージェント失敗数"], ["subagent_aggregate_recorded_tokens", "サブエージェントのトークン合計"],
    ["error_session_count", "エラーのあるセッション数"], ["retry_session_count", "再試行のあるセッション数"],
    ["recovery_relation_count", "復旧関係数"], ["sources", "取得元"], ["models", "モデル"],
    ["source_versions", "取得元のバージョン"], ["adapter_versions", "アダプターのバージョン"],
    ["completeness", "記録の完全性"], ["metric_availability", "指標の利用可能件数"],
  ]);
  const VALUE_LABELS = new Map([
    ["invocation_count", "呼び出し回数"], ["call_count", "呼び出し回数"], ["failure_count", "失敗回数"],
    ["start_count", "開始回数"], ["completed_count", "完了回数"], ["failed_count", "失敗回数"], ["recorded_tokens", "トークン合計"],
    ["session_count", "セッション数"], ["available_session_count", "利用可能件数"], ["available_count", "利用可能件数"],
    ["invoked_session_count", "呼び出しあり"], ["called_session_count", "呼び出しあり"], ["started_session_count", "開始あり"],
    ["median", "中央値"], ["minimum", "最小値"], ["maximum", "最大値"], ["total", "合計"],
    ["unavailable_states", "利用できない状態"], ["absolute_difference", "絶対差"], ["relative_difference", "相対差"],
    ["relative_difference_percent", "相対差"], ["count", "件数"], ["start", "開始"], ["end", "終了"],
    ["distribution", "内訳"], ["included_count", "対象件数"], ["direct_session_archived_count", "アーカイブ済みセッション数"],
    ["assigned_repository_archived_count", "アーカイブ済みリポジトリのセッション数"], ["includes_archived", "アーカイブ済みを含む"],
    ["display_name", "表示名"], ["sort_key", "並び順"],
  ]);
  const state = { generation: 0, controller: null, evidenceGeneration: 0, evidenceController: null, evidenceCursor: null, evidenceResultOrdinal: null, evidenceField: null, invoker: null };
  const sections = root.querySelector("[data-compare-sections]");
  const status = root.querySelector("#repository-compare-status");
  const evidenceDialog = root.querySelector("#repository-compare-evidence-dialog");
  const evidenceStatus = root.querySelector("#repository-compare-evidence-status");
  const evidenceItems = root.querySelector("[data-compare-evidence-items]");
  const evidenceMore = root.querySelector("#repository-compare-evidence-more");
  const evidenceClose = root.querySelector("#repository-compare-evidence-close");
  const aiSurface = root.querySelector("[data-compare-ai]");
  const aiStart = root.querySelector("[data-compare-ai-start]");
  const aiCancel = root.querySelector("[data-compare-ai-cancel]");
  const aiStatus = root.querySelector("[data-compare-ai-status]");
  const aiResult = root.querySelector("[data-compare-ai-result]");
  const repositoryId = root.dataset.repositoryId;
  const comparisonId = root.dataset.comparisonId;
  if (!UUID_V7.test(repositoryId ?? "") || !UUID_V7.test(comparisonId ?? "")) return;
  const base = `/api/local-monitor/v1/repositories/${repositoryId}/comparisons/${comparisonId}`;
  let activeAiRun = null;
  let aiGeneration = 0;
  let restoredAiRun = null;
  let aiCancelFailed = false;

  const clearAiStatusOutline = () => { aiStatus.style.outline = ""; aiStatus.style.outlineOffset = ""; };
  aiStatus.addEventListener("focus", () => {
    if (aiStatus.dataset.terminalFailure !== "true") return;
    aiStatus.style.outline = "2px solid currentColor";
    aiStatus.style.outlineOffset = "2px";
  });
  aiStatus.addEventListener("blur", clearAiStatusOutline);

  const exact = (value, keys) => value && typeof value === "object" && !Array.isArray(value)
    && Object.keys(value).length === keys.length && Object.keys(value).every((key, index) => key === keys[index]);
  const exactSet = (value, keys) => value && typeof value === "object" && !Array.isArray(value)
    && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
  function validateJsonWire(text) {
    const stack = []; let quoted = false; let escaped = false; let start = -1;
    for (let index = 0; index < text.length; index++) {
      const character = text[index];
      if (quoted) {
        if (escaped) { escaped = false; continue; }
        if (character === "\\") { escaped = true; continue; }
        if (character !== "\"") continue;
        quoted = false;
        if (start >= 0) {
          let next = index + 1; while (/\s/.test(text[next] ?? "")) next++;
          if (text[next] === ":") { const owner = stack.at(-1); if (!owner || owner.type !== "object") throw new TypeError("invalid json"); const key = JSON.parse(text.slice(start, index + 1)); if (owner.keys.has(key)) throw new TypeError("duplicate json key"); owner.keys.add(key); }
        }
        continue;
      }
      if (character === "\"") { quoted = true; start = index; }
      else if (character === "{" || character === "[") { stack.push({ type: character === "{" ? "object" : "array", keys: new Set() }); if (stack.length > 16) throw new TypeError("json too deep"); }
      else if (character === "}" || character === "]") { const owner = stack.pop(); if (!owner || owner.type !== (character === "}" ? "object" : "array")) throw new TypeError("invalid json"); }
    }
    if (quoted || stack.length) throw new TypeError("invalid json");
  }
  function topLevelValueSpan(text, propertyName) {
    let index = 0; while (/\s/.test(text[index] ?? "")) index++;
    if (text[index++] !== "{") return null;
    while (index < text.length) {
      while (/\s/.test(text[index] ?? "")) index++;
      if (text[index] === "}") return null;
      if (text[index] !== "\"") return null;
      const keyStart = index++;
      let escaped = false;
      while (index < text.length) {
        const character = text[index++];
        if (escaped) { escaped = false; continue; }
        if (character === "\\") { escaped = true; continue; }
        if (character === "\"") break;
      }
      const key = JSON.parse(text.slice(keyStart, index));
      while (/\s/.test(text[index] ?? "")) index++;
      if (text[index++] !== ":") return null;
      while (/\s/.test(text[index] ?? "")) index++;
      const valueStart = index;
      let quoted = false; escaped = false; let depth = 0;
      for (; index < text.length; index++) {
        const character = text[index];
        if (quoted) {
          if (escaped) { escaped = false; continue; }
          if (character === "\\") { escaped = true; continue; }
          if (character === "\"") quoted = false;
          continue;
        }
        if (character === "\"") quoted = true;
        else if (character === "{" || character === "[") depth++;
        else if (character === "}" || character === "]") {
          if (depth > 0) depth--;
          else if (character === "}") break;
        } else if (character === "," && depth === 0) break;
      }
      let valueEnd = index; while (valueEnd > valueStart && /\s/.test(text[valueEnd - 1])) valueEnd--;
      if (key === propertyName) return { start: valueStart, end: valueEnd };
      if (text[index] === ",") index++;
    }
    return null;
  }
  async function readStrictJson(response, maximumBytes, includeText = false) {
    const bytes = new Uint8Array(await response.arrayBuffer()); if (bytes.length > maximumBytes) throw new TypeError("json too large");
    const text = new TextDecoder("utf-8", { fatal: true }).decode(bytes); validateJsonWire(text); const value = JSON.parse(text);
    return includeText ? { value, text, resultSpan: topLevelValueSpan(text, "result") } : value;
  }
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
      if (pair.value === "none") { target.textContent = "今回の記録にはありません"; return; }
      const entries = pair.value.split(";");
      for (const [index, entry] of entries.entries()) {
        const match = /^([a-z_]+)=([1-9][0-9]*)$/.exec(entry);
        if (!match) { renderFactState(target, "projection_invalid"); return; }
        if (index > 0) target.append(document.createTextNode("、"));
        const item = element("span"); renderFactState(item, match[1]); item.append(document.createTextNode(`（${match[2]}件）`)); target.append(item);
      }
      return;
    }
    const archivedBoolean = pair.key === "a_includes_archived" || pair.key === "b_includes_archived";
    target.textContent = archivedBoolean && pair.value === "true" ? "はい"
      : archivedBoolean && pair.value === "false" ? "いいえ"
        : pair.value;
  }

  function rowLabel(key) {
    return ROW_LABELS.get(key) ?? "比較項目";
  }

  function valueLabel(key) {
    const cohort = key.startsWith("a_") ? "基準" : key.startsWith("b_") ? "比較対象" : null;
    const structuralKey = cohort === null ? key : key.slice(2);
    const metric = /^s[1-9][0-9]*_(.+)$/.exec(structuralKey);
    if (metric) {
      const label = ROW_LABELS.get(metric[1]);
      return label ? [cohort, label].filter(Boolean).join("・") : "記録項目";
    }
    const direct = VALUE_LABELS.get(structuralKey) ?? ROW_LABELS.get(structuralKey);
    if (direct) return cohort === null ? direct : `${cohort}・${direct}`;
    for (const [suffix, label] of VALUE_LABELS) {
      if (!structuralKey.endsWith(`_${suffix}`)) continue;
      const coreKey = structuralKey.slice(0, -(suffix.length + 1));
      const core = VALUE_LABELS.get(coreKey) ?? ROW_LABELS.get(coreKey);
      if (core) return [cohort, core, label].filter(Boolean).join("・");
    }
    return "記録項目";
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
    if (result.section_key === "conditions") return STATIC_CONDITION_ROWS.has(result.row_key) ? ["condition"] : [];
    if (result.row_kind === "skill") return ["count"];
    if (result.row_kind === "tool") return ["count", "error_count", "retry_count"];
    if (result.row_kind === "subagent") return ["count", "total_tokens"];
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
    ["指標", "基準", "比較対象", "差"].forEach(label => headerRow.append(element("th", null, label)));
    head.append(headerRow);
    const body = element("tbody");
    for (const result of items) {
      const row = element("tr", "local-monitor-compare-result-heading");
      const indicator = element("th");
      indicator.append(element("div", null, result.display_name ?? rowLabel(result.row_key)));
      const baseline = element("td");
      const comparison = element("td");
      const difference = element("td");
      const actions = element("div", "local-monitor-compare-evidence-actions");
      actions.style.display = "grid";
      for (const field of evidenceFields(result)) {
        const button = element("button", null, `${EVIDENCE_LABELS.get(field)}の根拠を表示`);
        button.type = "button";
        button.style.whiteSpace = "normal";
        button.addEventListener("click", () => openEvidence(result.result_ordinal, field, button));
        actions.append(button);
      }
      indicator.append(actions);
      for (const pair of result.values) {
        if (pair.key === "display_name" || pair.key === "sort_key") continue;
        const cohort = pair.key.startsWith("a_") ? "a" : pair.key.startsWith("b_") ? "b" : null;
        const structuralKey = cohort === null ? pair.key : pair.key.slice(2);
        const isDifference = structuralKey === "absolute_difference" || structuralKey === "relative_difference"
          || structuralKey === "relative_difference_percent" || structuralKey.endsWith("_absolute_difference")
          || structuralKey.endsWith("_relative_difference") || structuralKey.endsWith("_relative_difference_percent");
        const owner = cohort === "a" ? baseline : cohort === "b" ? comparison : isDifference ? difference : indicator;
        const fact = element("div", "local-monitor-compare-fact");
        fact.append(element("span", null, valueLabel(structuralKey)));
        const value = element("span");
        renderStoredValue(value, pair);
        fact.append(value);
        owner.append(fact);
      }
      row.append(indicator, baseline, comparison, difference);
      body.append(row);
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

  const AI_STATES = Object.freeze({
    queued: "AI解釈を待っています。", running: "AIが比較を解釈しています。",
    succeeded: "AI解釈が完了しました。", zero_findings: "AIからの指摘はありませんでした。",
    provider_failed: "AIで解釈できませんでした。", provider_partial: "不完全なAI結果のため表示できません。",
    timed_out: "AI解釈がタイムアウトしました。", canceled: "AI解釈をキャンセルしました。",
    stale_snapshot: "比較の保存期間またはスナップショットが更新されたため表示できません。",
    invalid_result: "AI結果を安全に表示できません。", invalid_evidence: "AIの根拠を確認できないため表示できません。",
    scope_too_large: "比較がAI解釈の上限を超えています。",
  });

  function finishAiFailure(message) {
    activeAiRun = null; aiCancelFailed = false; aiCancel.hidden = true; aiCancel.disabled = false; aiResult.replaceChildren(); aiStatus.textContent = message;
    aiStatus.tabIndex = -1; aiStatus.dataset.terminalFailure = "true"; aiStatus.focus();
  }

  function resetAiFailureFocus(restoreInitiator) {
    const ownedFocus = document.activeElement === aiStatus && aiStatus.dataset.terminalFailure === "true";
    delete aiStatus.dataset.terminalFailure; clearAiStatusOutline();
    if (restoreInitiator && ownedFocus && aiStart.isConnected && !aiStart.hidden && !aiStart.disabled) aiStart.focus();
  }

  function appendAiField(target, label, value) {
    if (value === null || value === undefined || value === "") return;
    const row = element("p"); row.append(element("strong", null, `${label}: `), document.createTextNode(String(value))); target.append(row);
  }

  function aiEvidenceLink(reference) {
    if (typeof reference !== "string") return null;
    const match = /^\/sessions\/([0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})(?:\?execution=([0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})(?:&node=(node-[0-9a-f]{32}))?|\?node=(node-[0-9a-f]{32}))?$/.exec(reference);
    return match ? reference : null;
  }

  const RESULT_KEYS = ["scope", "snapshot", "summary", "findings", "improvement_suggestions", "limitations", "provenance"];
  const FINDING_KEYS = ["finding_id", "title", "explanation", "evidence_state", "evidence_refs", "limitation"];
  const SUGGESTION_KEYS = ["suggestion_id", "target_kind", "target_label", "concrete_change", "rationale", "expected_effect", "risks_or_limitations", "evidence_refs"];
  const PROVENANCE_KEYS = ["provider", "model", "configuration_sha256", "prompt_template_version", "requested_at", "started_at", "completed_at", "snapshot_id", "snapshot_sha256", "coverage"];
  const TARGET_KINDS = new Set(["instructions", "skill", "agent", "subagent_input", "tool_configuration"]);
  const HASH = /^[0-9a-f]{64}$/;
  const nonblank = value => typeof value === "string" && value.trim().length > 0;
  const timestamp = value => typeof value === "string" && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}(?:Z|[+-]\d{2}:\d{2})$/.test(value) && !Number.isNaN(Date.parse(value));

  function validateAiRefs(value, accepted) {
    if (!Array.isArray(value) || value.length < 1 || value.length > 16) return false;
    const local = new Set();
    for (const reference of value) {
      const href = aiEvidenceLink(reference);
      if (!href || local.has(href)) return false;
      local.add(href);
      accepted.add(href);
    }
    return true;
  }

  function validateAiResult(result) {
    if (!exactSet(result, RESULT_KEYS) || new TextEncoder().encode(JSON.stringify(result)).length > RESULT_MAXIMUM_BYTES
        || !exactSet(result.scope, ["kind", "repository_id", "comparison_id", "anchor_id"])
        || result.scope.kind !== "comparison" || result.scope.repository_id !== repositoryId
        || result.scope.comparison_id !== comparisonId || result.scope.anchor_id !== comparisonId
        || !exactSet(result.snapshot, ["snapshot_id", "payload_sha256"])
        || !UUID_V7.test(result.snapshot.snapshot_id ?? "") || !HASH.test(result.snapshot.payload_sha256 ?? "")
        || typeof result.summary !== "string" || !Array.isArray(result.findings)
        || !Array.isArray(result.improvement_suggestions) || !Array.isArray(result.limitations)
        || result.limitations.some(item => typeof item !== "string") || !exactSet(result.provenance, PROVENANCE_KEYS)) return null;
    const provenance = result.provenance;
    if (!["provider", "model", "configuration_sha256", "prompt_template_version", "requested_at", "started_at", "completed_at", "snapshot_id", "snapshot_sha256"].every(key => nonblank(provenance[key]))
        || !HASH.test(provenance.configuration_sha256) || !HASH.test(provenance.snapshot_sha256)
        || !UUID_V7.test(provenance.snapshot_id) || provenance.snapshot_id !== result.snapshot.snapshot_id
        || !timestamp(provenance.requested_at) || !timestamp(provenance.started_at) || !timestamp(provenance.completed_at)
        || Date.parse(provenance.requested_at) > Date.parse(provenance.started_at) || Date.parse(provenance.started_at) > Date.parse(provenance.completed_at)
        || !exactSet(provenance.coverage, ["included", "excluded", "content_available"])
        || !Number.isInteger(provenance.coverage.included) || provenance.coverage.included < 0
        || !Number.isInteger(provenance.coverage.excluded) || provenance.coverage.excluded < 0
        || typeof provenance.coverage.content_available !== "boolean") return null;
    const accepted = new Set();
    for (const finding of result.findings) {
      if (!exactSet(finding, FINDING_KEYS) || !["finding_id", "title", "explanation", "limitation"].every(key => nonblank(finding[key]))
          || !["supported", "limited"].includes(finding.evidence_state) || !validateAiRefs(finding.evidence_refs, accepted)) return null;
    }
    for (const suggestion of result.improvement_suggestions) {
      if (!exactSet(suggestion, SUGGESTION_KEYS) || !["suggestion_id", "target_kind", "target_label", "concrete_change", "rationale", "expected_effect", "risks_or_limitations"].every(key => nonblank(suggestion[key]))
          || !TARGET_KINDS.has(suggestion.target_kind) || !validateAiRefs(suggestion.evidence_refs, accepted)) return null;
    }
    return [...accepted];
  }

  function appendAiEvidence(target, references) {
    for (const reference of Array.isArray(references) ? references : []) {
      const href = aiEvidenceLink(reference);
      if (!href) continue;
      const link = element("a", null, "正確な根拠を開く"); link.href = href; target.append(link);
    }
  }

  function renderAiResult(result) {
    aiResult.replaceChildren();
    const acceptedEvidence = validateAiResult(result);
    if (!acceptedEvidence) {
      finishAiFailure(AI_STATES.invalid_result); return false;
    }
    resetAiFailureFocus(false);
    const resultHeading = element("h3", null, "AIによる解釈"); resultHeading.tabIndex = -1; aiResult.append(resultHeading);
    if (result.scope && typeof result.scope === "object") {
      const scope = element("section"); scope.append(element("h4", null, "分析対象の技術情報"));
      for (const [key, label] of [["kind", "種類"], ["repository_id", "リポジトリID"], ["comparison_id", "比較ID"], ["anchor_id", "基準ID"]]) appendAiField(scope, label, key === "kind" && result.scope[key] === "comparison" ? "セッション比較" : result.scope[key]);
      aiResult.append(scope);
    }
    if (result.snapshot && typeof result.snapshot === "object") {
      const snapshot = element("section"); snapshot.append(element("h4", null, "記録時点の技術情報"));
      appendAiField(snapshot, "スナップショットID", result.snapshot.snapshot_id); appendAiField(snapshot, "内容のSHA-256", result.snapshot.payload_sha256); aiResult.append(snapshot);
    }
    aiResult.append(element("h4", null, "要約"), element("p", null, result.summary));
    if (result.findings.length) aiResult.append(element("h4", null, "指摘（AIによる解釈）"));
    for (const finding of result.findings) {
      const article = element("article"); article.append(element("h5", null, String(finding.title ?? "指摘")));
      appendAiField(article, "解釈", finding.explanation); appendAiField(article, "根拠の状態", ({ supported: "根拠あり", limited: "根拠に制約あり" })[finding.evidence_state] ?? finding.evidence_state); appendAiField(article, "制約", finding.limitation);
      appendAiEvidence(article, finding.evidence_refs); aiResult.append(article);
    }
    if (result.improvement_suggestions.length) aiResult.append(element("h4", null, "改善案（AIによる提案）"));
    for (const suggestion of result.improvement_suggestions) {
      const article = element("article");
      for (const [key, label] of [["target_label", "対象"], ["rationale", "理由"], ["concrete_change", "変更案"], ["expected_effect", "期待される効果（AIによる提案）"], ["risks_or_limitations", "リスク・制約"]]) appendAiField(article, label, suggestion[key]);
      appendAiEvidence(article, suggestion.evidence_refs); aiResult.append(article);
    }
    if (result.limitations.length) { aiResult.append(element("h4", null, "制約")); for (const limitation of result.limitations) aiResult.append(element("p", null, String(limitation))); }
    const evidence = element("section"); evidence.append(element("h4", null, "正確な根拠"));
    const evidenceList = element("ul"); for (const href of acceptedEvidence) { const item = element("li"); const link = element("a", null, href); link.href = href; item.append(link); evidenceList.append(item); }
    evidence.append(evidenceList); aiResult.append(evidence);
    if (result.provenance && typeof result.provenance === "object") {
      const provenance = element("section"); provenance.append(element("h4", null, "分析の技術情報"));
      for (const [key, label] of [["provider", "プロバイダー"], ["model", "モデル"], ["configuration_sha256", "設定のSHA-256"], ["prompt_template_version", "テンプレート"], ["snapshot_id", "スナップショットID"], ["snapshot_sha256", "内容のSHA-256"]]) appendAiField(provenance, label, result.provenance[key]);
      aiResult.append(provenance);
    }
    resultHeading.focus(); return true;
  }

  function ownsComparisonRun(run, runId) {
    if (!exactSet(run, ["run_id", "state", "scope_kind", "session_id", "node_id", "repository_id", "comparison_id", "error", "result"])
        || run.run_id !== runId || !UUID_V7.test(run.run_id ?? "") || run.scope_kind !== "comparison"
        || run.repository_id !== repositoryId || run.comparison_id !== comparisonId || run.session_id !== null || run.node_id !== null) return false;
    const states = ["queued", "running", "succeeded", "zero_findings", "provider_failed", "provider_partial", "invalid_result", "invalid_evidence", "stale_snapshot", "scope_too_large", "timed_out", "canceled"];
    if (!states.includes(run.state)) return false;
    if (["queued", "running"].includes(run.state)) return run.error === null && run.result === null;
    if (["succeeded", "zero_findings"].includes(run.state)) return run.error === null && run.result !== null && typeof run.result === "object";
    return run.error === run.state && run.result === null;
  }

  async function pollAiRun(runId, generation) {
    while (generation === aiGeneration && activeAiRun === runId) {
      try {
        const response = await fetch(`/api/local-monitor/v1/ai/runs/${runId}`, { method: "GET", credentials: "same-origin", cache: "no-store" });
        if (!response.ok) throw new Error("poll_failed");
        const wire = await readStrictJson(response, RUN_MAXIMUM_BYTES, true); const run = wire.value;
        if (generation !== aiGeneration || activeAiRun !== runId) return;
        if (!ownsComparisonRun(run, runId)) { finishAiFailure("この比較に属するAI解釈を表示できません。"); return; }
        if (["succeeded", "zero_findings"].includes(run.state)) {
          const span = wire.resultSpan;
          if (!span || new TextEncoder().encode(wire.text.slice(span.start, span.end)).length > RESULT_MAXIMUM_BYTES) {
            finishAiFailure(AI_STATES.invalid_result); return;
          }
        }
        if (["succeeded", "zero_findings"].includes(run.state)
            && (!validateAiResult(run.result) || (run.state === "zero_findings") !== (run.result.findings.length === 0))) {
          finishAiFailure(AI_STATES.invalid_result); return;
        }
        if (!aiCancelFailed || !["queued", "running"].includes(run.state)) aiStatus.textContent = AI_STATES[run.state] ?? "AI解釈を表示できません。";
        if (!["queued", "running"].includes(run.state)) {
          activeAiRun = null; aiCancelFailed = false; aiCancel.hidden = true;
          if (["succeeded", "zero_findings"].includes(run.state) && !renderAiResult(run.result)) return;
          if (!["succeeded", "zero_findings"].includes(run.state)) {
            finishAiFailure(aiStatus.textContent);
          }
          return;
        }
      } catch { if (generation !== aiGeneration || activeAiRun !== runId) return; aiStatus.textContent = "AI解釈の状態を確認できません。再試行しています。"; }
      await new Promise(resolve => setTimeout(resolve, 250));
    }
  }

  async function startAi() {
    resetAiFailureFocus(false); aiResult.replaceChildren(); aiCancelFailed = false; aiCancel.disabled = false; aiStatus.textContent = "AI解釈を開始しています。"; aiStart.disabled = true;
    try {
      const response = await fetch("/api/local-monitor/v1/ai/comparison-runs", {
        method: "POST", credentials: "same-origin", cache: "no-store",
        headers: { Accept: "application/json", "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" },
        body: JSON.stringify({ schema_version: "local-ai-comparison-run.request.v1", repository_id: repositoryId, comparison_id: comparisonId, timeout_seconds: 60 }),
      });
      if (!response.ok) { const failure = await readStrictJson(response, SMALL_RESPONSE_MAXIMUM_BYTES).catch(() => null); finishAiFailure(failure?.error === "comparison_expired" ? "比較の保存期間が終了したためAI解釈を開始できません。" : failure?.error === "provider_unavailable" ? "AIを利用できません。" : failure?.error === "persistence_busy" ? "AI解釈の保存先が使用中です。" : "AI解釈を開始できませんでした。"); return; }
      const started = await readStrictJson(response, SMALL_RESPONSE_MAXIMUM_BYTES); if (!exactSet(started, ["run_id"]) || !UUID_V7.test(started.run_id ?? "")) { finishAiFailure("AI解釈を開始できませんでした。"); return; }
      activeAiRun = started.run_id; restoredAiRun = started.run_id; const generation = ++aiGeneration; aiCancel.hidden = false;
      try { window.LocalMonitorV1History?.push({ analysis: activeAiRun }); } catch { /* shared Compare analysis query plumbing is optional */ }
      await pollAiRun(activeAiRun, generation);
    } catch { finishAiFailure("AI解釈を開始できませんでした。"); }
    finally { aiStart.disabled = false; }
  }

  async function restoreAi(runId) {
    if (!UUID_V7.test(runId ?? "") || restoredAiRun === runId || activeAiRun === runId) return;
    resetAiFailureFocus(true);
    restoredAiRun = runId;
    activeAiRun = runId; const generation = ++aiGeneration; aiCancel.hidden = false; await pollAiRun(runId, generation);
  }

  function validReadiness(value) {
    const readinessStates = ["unconfigured", "configured_not_checked", "ready", "authentication_required", "unavailable", "check_failed"];
    const checkResults = ["not_checked", "ready", "authentication_required", "unavailable", "check_failed"];
    return exactSet(value, ["provider", "selected_model", "selected_configuration", "readiness_state", "last_check_result", "provider_egress_notice"])
      && value.provider === "github_copilot" && (value.selected_model === null || nonblank(value.selected_model))
      && (value.selected_configuration === null || nonblank(value.selected_configuration))
      && readinessStates.includes(value.readiness_state) && checkResults.includes(value.last_check_result)
      && value.provider_egress_notice === "selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action"
      && (value.readiness_state !== "ready" || value.last_check_result === "ready" && nonblank(value.selected_model) && nonblank(value.selected_configuration));
  }

  async function checkAiReadiness() {
    try {
      const response = await fetch("/api/local-monitor/v1/settings/ai-readiness", { method: "GET", credentials: "same-origin", cache: "no-store" });
      if (!response.ok) return;
      const readiness = await readStrictJson(response, SMALL_RESPONSE_MAXIMUM_BYTES);
      if (!validReadiness(readiness) || readiness.readiness_state !== "ready") return;
      aiSurface.hidden = false;
      const route = window.LocalMonitorV1History?.current(); if (route?.analysis) await restoreAi(route.analysis);
    } catch { /* deterministic Compare remains available without AI */ }
  }

  aiStart.addEventListener("click", startAi);
  aiCancel.addEventListener("click", async () => {
    if (!activeAiRun || aiCancel.disabled) return;
    aiCancel.disabled = true;
    const runId = activeAiRun;
    const generation = aiGeneration;
    try {
      const response = await fetch(`/api/local-monitor/v1/ai/runs/${runId}/cancel`, { method: "POST", credentials: "same-origin", headers: { Accept: "application/json", "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" }, body: "{}" });
      if (generation !== aiGeneration || activeAiRun !== runId) return;
      const value = response.ok ? await readStrictJson(response, SMALL_RESPONSE_MAXIMUM_BYTES) : null;
      if (generation !== aiGeneration || activeAiRun !== runId) return;
      if (!response.ok || !exactSet(value, ["run_id", "state"]) || value.run_id !== runId || value.state !== "canceled") { aiCancelFailed = true; aiCancel.disabled = false; aiStatus.textContent = "キャンセルできませんでした。AI解釈の状態を確認しています。"; return; }
      aiGeneration++; finishAiFailure(AI_STATES.canceled);
    } catch { if (generation !== aiGeneration || activeAiRun !== runId) return; aiCancelFailed = true; aiCancel.disabled = false; aiStatus.textContent = "キャンセルできませんでした。AI解釈の状態を確認しています。"; }
  });
  document.addEventListener("cao-route-state", event => {
    if (aiSurface.hidden) return;
    const runId = event.detail?.analysis;
    if (UUID_V7.test(runId ?? "")) { restoreAi(runId); return; }
    resetAiFailureFocus(true);
    if (restoredAiRun) { aiGeneration++; activeAiRun = null; restoredAiRun = null; aiCancel.hidden = true; aiCancel.disabled = false; aiStatus.textContent = ""; aiResult.replaceChildren(); }
  });
  window.addEventListener("pagehide", () => { state.controller?.abort(); aiGeneration++; closeEvidence(); });

  (async () => {
    const generation = ++state.generation; state.controller?.abort(); state.controller = new AbortController();
    try { const snapshot = validateRead(await get(base, state.controller.signal)); if (generation === state.generation) { renderRead(snapshot); await checkAiReadiness(); } }
    catch (error) {
      if (state.controller.signal.aborted || generation !== state.generation) return;
      status.textContent = error.code === "comparison_expired" ? "比較結果の保存期間が終了しました。" : "比較結果を読み込めませんでした。";
    }
  })();
})();
