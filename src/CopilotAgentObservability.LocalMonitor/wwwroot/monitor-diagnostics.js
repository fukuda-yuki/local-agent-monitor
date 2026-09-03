// Local Ingestion Monitor — diagnostics page (ingestion history, Sprint18 C5).
//
// Sanitized boundary: reads only the sanitized monitor diagnostics and
// doctor.ui.v1 APIs. It never fetches a raw-bearing route. All DOM nodes are
// built with createElement / textContent; no markup strings are ever injected.
(() => {
  "use strict";

  const rows = document.getElementById("ingestion-history-rows");
  const sourceDiagnosticRows = document.getElementById("source-diagnostics-rows");
  const doctorSource = document.getElementById("doctor-source");
  const doctorSourceState = document.getElementById("doctor-source-state");
  const doctorHeading = document.getElementById("doctor-result-heading");
  const doctorLive = document.getElementById("doctor-live");
  const doctorPrimaryAction = document.getElementById("doctor-primary-action");
  const doctorCancelAction = document.getElementById("doctor-cancel-action");
  const doctorEvidenceList = document.getElementById("doctor-evidence-list");
  const doctorCandidates = document.getElementById("doctor-candidates");
  const doctorCandidateList = document.getElementById("doctor-candidate-list");
  const doctorSessionTarget = document.getElementById("doctor-session-target");
  const doctorSessionTargetSummary = document.getElementById("doctor-session-target-summary");
  const doctorSourceTarget = document.getElementById("doctor-source-target");
  const doctorSourceTargetSummary = document.getElementById("doctor-source-target-summary");
  const sourceDiagnosticsPageSize = 50;
  const maximumSourceDiagnosticsPages = 200;
  const compatibilityCatalog = Object.freeze({
    supported: { reasons: [], action: "none", label: "対応済み" },
    supported_with_unknown_fields: { reasons: ["unknown_fields_observed"], action: "review_unknown_fields", label: "対応済み（未知フィールドあり）" },
    unsupported_source_version: { reasons: ["unsupported_source_version"], action: "use_compatible_source_or_update_adapter", factState: "unsupported", reason: "このバージョンは取得元の互換性契約で対応していません" },
    schema_drift_detected: { reasons: ["schema_drift_detected"], action: "capture_fixture_and_review_mapping", factState: "capture_gap", reason: "取得元のスキーマ変更を検出したため、記録の完全性を確認できません" },
    recognized_record_drop_detected: { reasons: ["recognized_record_drop_detected"], action: "restore_mapping_or_update_versioned_golden", factState: "capture_gap", reason: "認識済みレコードの欠落を検出しました" },
    adapter_failure_parse: { state: "adapter_failure", reasons: ["adapter_parse_failure"], action: "validate_payload_and_protocol", factState: "capture_gap", reason: "送信データを解析できなかったため、記録が一部欠けています" },
    adapter_failure_exception: { state: "adapter_failure", reasons: ["adapter_exception"], action: "inspect_sanitized_adapter_failure", factState: "capture_gap", reason: "アダプター処理に失敗したため、記録が一部欠けています" },
  });
  const compatibilityReasonLabels = {
    unknown_fields_observed: "未知フィールドがあります",
    unsupported_source_version: "送信元バージョンは未対応です",
    schema_drift_detected: "スキーマ変更のため完全性を確認できません",
    recognized_record_drop_detected: "認識済みレコードに欠落があります",
    adapter_parse_failure: "payload を解析できませんでした",
    adapter_exception: "アダプター処理に失敗しました",
  };
  const compatibilityActionLabels = {
    none: "対応は不要です",
    review_unknown_fields: "未知フィールドを確認してください",
    use_compatible_source_or_update_adapter: "対応するバージョンを使用するかアダプターを更新してください",
    capture_fixture_and_review_mapping: "fixture を取得してマッピングを確認してください",
    restore_mapping_or_update_versioned_golden: "マッピングを復元するか versioned golden を更新してください",
    validate_payload_and_protocol: "payload と protocol を確認してください",
    inspect_sanitized_adapter_failure: "sanitized なアダプター診断を確認してください",
  };
  const doctorCatalog = Object.freeze({
    monitor_not_installed: ["error", "after_action", "install_monitor", "Monitor がインストールされていません", "Monitor をインストールしてください"],
    monitor_not_running: ["error", "after_action", "start_monitor", "Monitor が起動していません", "Monitor を起動してください"],
    receiver_not_bound: ["error", "after_action", "restart_monitor", "受信ポートを利用できません", "Monitor を再起動してください"],
    port_owned_by_foreign_process: ["error", "after_action", "free_or_change_port", "受信ポートを別のプロセスが使用しています", "ポートを解放するか変更してください"],
    endpoint_mismatch: ["error", "after_action", "update_source_endpoint", "送信先が Monitor と一致しません", "送信元の接続先を更新してください"],
    protocol_mismatch: ["error", "after_action", "use_http_protobuf", "送信プロトコルが一致しません", "HTTP/Protobuf を使用してください"],
    signal_disabled: ["error", "after_action", "enable_trace_signal", "トレース送信が無効です", "トレース送信を有効にしてください"],
    unsupported_source_version: ["error", "after_action", "use_supported_source_version", "この取得元のバージョンには対応していません", "対応する取得元バージョンを使用してください"],
    feature_unavailable: ["error", "after_action", "use_supported_source_surface", "必要な機能をこの取得元では利用できません", "対応する取得元を使用してください"],
    agent_restart_required: ["warning", "after_action", "restart_source_process", "取得元の再起動が必要です", "取得元のプロセスを再起動してください"],
    endpoint_unreachable: ["error", "after_action", "verify_endpoint_reachability", "Monitor の受信先へ接続できません", "受信先への接続を確認してください"],
    payload_rejected: ["error", "after_action", "inspect_rejected_payload", "送信データを受け付けられませんでした", "拒否された送信データの診断を確認してください"],
    raw_persisted_projection_pending: ["warning", "automatic", "wait_for_projection", "記録済みデータの反映を待っています", "反映の完了を待ってください"],
    projection_failed: ["error", "after_action", "open_projection_diagnostics", "記録済みデータを画面へ反映できませんでした", "反映処理の診断を確認してください"],
    session_unbound: ["error", "after_action", "select_exact_session", "記録を Session に結び付けられません", "対象の Session を選択してください"],
    content_capture_disabled: ["warning", "after_action", "enable_content_capture_if_desired", "内容の記録が無効です", "必要な場合は内容の記録を有効にしてください"],
    sanitized_only_raw_unavailable: ["warning", "after_action", "restart_without_sanitized_only_if_desired", "内容は記録されていません", "必要な場合は通常モードで Monitor を再起動してください"],
    schema_drift_detected: ["warning", "after_action", "review_source_diagnostics", "取得元のスキーマ変更を検出しました", "取得元の診断を確認してください"],
    ready_no_real_trace: ["info", "after_action", "run_bounded_source_interaction", "接続確認のための記録がまだありません", "取得元で確認用の操作を実行してください"],
    first_trace_ready: ["info", "none", "open_verified_trace_or_session", "最初の記録を確認できました", "確認済みの Trace または Session を開いてください"],
  });
  const severityLabels = Object.freeze({ error: "エラー", warning: "注意", info: "情報" });
  const retryabilityLabels = Object.freeze({ after_action: "対応後に再確認できます", automatic: "自動的に再確認します", none: "再確認は不要です" });
  const lifecycleLabels = Object.freeze({ active: "確認中", completed: "完了", cancelled: "キャンセル済み", expired: "有効期限切れ" });
  const detectionLabels = Object.freeze({ detected: "検出済み", not_detected: "未検出", unavailable: "検出状態を確認できません" });
  const setupOwnershipValues = new Set(["managed", "managed_windows", "caller_managed", "managed_cli_caller_managed_agent_sdk"]);
  const sessionStatusLabels = Object.freeze({ active: "実行中", completed: "完了", failed: "失敗", unknown: "状態を確認できません" });
  const completenessLabels = Object.freeze({ unbound: "Session に未接続", partial: "一部を記録", rich: "詳細を記録", full: "完全に記録" });
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

  function valueLine(value, mono) {
    const node = document.createElement("span");
    if (mono) node.className = "monitor-mono";
    node.textContent = value === null || value === undefined ? "—" : String(value);
    return node;
  }

  function lines(values, mono) {
    const node = document.createElement("td");
    for (const value of values) {
      node.append(valueLine(value, mono));
    }
    return node;
  }

  function technicalDisclosure(codes) {
    const technical = document.createElement("details");
    const summary = document.createElement("summary");
    summary.textContent = "技術情報";
    technical.append(summary);
    for (const code of codes) {
      const raw = document.createElement("code");
      raw.textContent = code;
      technical.append(raw);
    }
    return technical;
  }

  function enumPresentation(code, label) {
    if (typeof code !== "string" || typeof label !== "string") throw new Error("unknown diagnostic enum");
    const node = document.createElement("div");
    const primary = document.createElement("span");
    primary.textContent = label;
    node.append(primary, technicalDisclosure([code]));
    return node;
  }

  function enumCell(code, label) {
    const node = document.createElement("td");
    node.append(enumPresentation(code, label));
    return node;
  }

  function enumLines(codes) {
    const node = document.createElement("td");
    if (codes.length === 0) {
      node.append(sharedFactPresentation({
        state: "observed_zero", recordedCount: 0, hasCompleteCoverageProof: true,
        sourceText: "ソース互換性診断", reasonText: "追加の互換性理由はありません",
      }));
      return node;
    }
    for (const code of codes) node.append(enumPresentation(code, compatibilityReasonLabels[code]));
    return node;
  }

  function sharedFactPresentation(fact, codes = []) {
    const node = document.createElement("div");
    window.LocalMonitorV1FactState.render(node, { recordedCount: null, sourceText: null, ...fact });
    if (codes.length > 0) node.append(technicalDisclosure(codes));
    return node;
  }

  function missingFactPresentation(label) {
    return sharedFactPresentation({ state: "not_observed", reasonText: `${label}をこの記録で確認できません` });
  }

  function diagnosticTuple(item) {
    if (!item || !Array.isArray(item.reason_codes)) throw new Error("invalid source diagnostic");
    const key = item.compatibility_state === "adapter_failure"
      ? `adapter_failure_${item.reason_codes[0] === "adapter_parse_failure" ? "parse" : item.reason_codes[0] === "adapter_exception" ? "exception" : "unknown"}`
      : item.compatibility_state;
    const entry = compatibilityCatalog[key];
    if (!entry || (entry.state ?? key) !== item.compatibility_state
        || entry.action !== item.next_action
        || entry.reasons.length !== item.reason_codes.length
        || entry.reasons.some((reason, index) => reason !== item.reason_codes[index])) {
      throw new Error("invalid source diagnostic tuple");
    }
    return entry;
  }

  function diagnosticStatePresentation(item, entry) {
    if (entry.factState === "unsupported") {
      return sharedFactPresentation({
        state: "unsupported",
        sourceText: item.source_surface ?? "取得元を確認できません",
        reasonText: entry.reason,
      }, [item.compatibility_state]);
    }
    if (entry.factState) {
      return sharedFactPresentation({ state: entry.factState, reasonText: entry.reason }, [item.compatibility_state]);
    }
    return enumPresentation(item.compatibility_state, entry.label);
  }

  function factValueLine(value, label, mono) {
    if (value !== null && value !== undefined) return valueLine(value, mono);
    return missingFactPresentation(label);
  }

  function sourceDiagnosticMessage(message) {
    sourceDiagnosticRows.replaceChildren();
    const row = document.createElement("tr");
    const value = document.createElement("td");
    value.colSpan = 7;
    value.className = "empty-state";
    value.textContent = message;
    row.append(value);
    sourceDiagnosticRows.append(row);
  }

  async function loadSourceDiagnostics() {
    const items = [];
    const seenCursors = new Set();
    let after = null;

    for (let page = 0; page < maximumSourceDiagnosticsPages; page += 1) {
      const query = after === null
        ? `?limit=${sourceDiagnosticsPageSize}`
        : `?limit=${sourceDiagnosticsPageSize}&after=${after}`;
      const response = await fetch(`/api/monitor/source-diagnostics${query}`, { cache: "no-store" });
      if (!response.ok) throw new Error("source diagnostics request failed");
      const payload = await response.json();
      if (!Array.isArray(payload.items)) throw new Error("source diagnostics payload is invalid");
      items.push(...payload.items);

      const nextCursor = payload.next_cursor;
      if (nextCursor === null) return items;
      if (!Number.isSafeInteger(nextCursor) || nextCursor < 1 || seenCursors.has(nextCursor)) {
        throw new Error("source diagnostics cursor is invalid");
      }
      seenCursors.add(nextCursor);
      after = nextCursor;
    }

    throw new Error("source diagnostics page limit exceeded");
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

  async function refreshSourceDiagnostics() {
    if (!sourceDiagnosticRows) return;

    let items;
    try {
      items = await loadSourceDiagnostics();
    } catch {
      sourceDiagnosticMessage("ソース互換性の診断を読み込めませんでした。");
      return;
    }

    if (items.length === 0) {
      sourceDiagnosticMessage("今回の記録にはありません。この記録ではソース互換性の診断を確認できませんでした。実際に診断対象がなかったとは断定できません。");
      return;
    }

    try {
      const rendered = [];
      for (const item of items) {
        const entry = diagnosticTuple(item);
        const counts = [item.unknown_span_count, item.unknown_event_count, item.unknown_attribute_count];
        if (counts.some(value => !Number.isSafeInteger(value) || value < 0)) throw new Error("invalid unknown count");
        const row = document.createElement("tr");
        const state = document.createElement("td");
        state.append(diagnosticStatePresentation(item, entry));
        row.append(
          lines([item.observation_id, item.observed_at], true),
          lines([], false), lines([], false), state,
          enumLines(entry.reasons),
          enumCell(item.next_action, compatibilityActionLabels[item.next_action]),
          lines(counts, true));
        row.children[1].append(
          factValueLine(item.source_surface, "取得元", false),
          factValueLine(item.source_application_version, "取得元バージョン", false));
        row.children[2].append(
          factValueLine(item.source_adapter, "Adapter", false),
          factValueLine(item.adapter_version, "Adapter バージョン", false));
        rendered.push(row);
      }
      sourceDiagnosticRows.replaceChildren(...rendered);
    } catch {
      sourceDiagnosticMessage("ソース互換性の診断を読み込めませんでした。");
    }
  }

  const doctorFields = {
    state: document.getElementById("doctor-current-state"),
    severity: document.getElementById("doctor-severity"),
    source: document.getElementById("doctor-result-source"),
    nextAction: document.getElementById("doctor-next-action"),
    retryability: document.getElementById("doctor-retryability"),
    lifecycle: document.getElementById("doctor-lifecycle"),
  };
  let doctorAction = null;
  let currentVerification = null;

  function setDoctorAction(label, action, disabled) {
    doctorAction = action;
    doctorPrimaryAction.hidden = !label;
    doctorPrimaryAction.disabled = Boolean(disabled);
    doctorPrimaryAction.textContent = label || "";
  }

  function setCancelAction(visible) {
    doctorCancelAction.hidden = !visible;
    doctorCancelAction.disabled = false;
  }

  function setSourceLocked(locked) {
    doctorSource.disabled = Boolean(locked);
  }

  function announceDoctor(message, focusHeading) {
    doctorLive.textContent = message;
    if (focusHeading) doctorHeading.focus();
  }

  function doctorFailure(retry) {
    setCancelAction(false);
    setDoctorAction("再試行", retry);
    announceDoctor("Doctor の状態を読み込めませんでした。", true);
  }

  function mutationFailure() {
    setCancelAction(false);
    setDoctorAction("現在の状態を確認", refreshVerification);
    announceDoctor("操作結果を確認できませんでした。現在の状態を確認してください。", true);
  }

  function display(value) {
    return value === null || value === undefined || value === "" ? "—" : String(value);
  }

  function safeNavigationTarget(target, evidenceRef) {
    return target
      && target.evidence_ref === evidenceRef
      && typeof target.href === "string"
      && target.href.startsWith("/")
      && !target.href.startsWith("//");
  }

  function renderEvidence(evidenceRefs, navigationTargets) {
    doctorEvidenceList.replaceChildren();
    if (!Array.isArray(evidenceRefs) || evidenceRefs.length === 0) {
      const empty = document.createElement("li");
      empty.className = "monitor-subtle";
      empty.textContent = "証拠参照はまだありません。";
      doctorEvidenceList.append(empty);
      return;
    }

    for (const evidenceRef of evidenceRefs) {
      const item = document.createElement("li");
      const target = Array.isArray(navigationTargets)
        ? navigationTargets.find(candidate => safeNavigationTarget(candidate, evidenceRef))
        : null;
      if (target) {
        const link = document.createElement("a");
        link.href = target.href;
        link.textContent = String(evidenceRef);
        item.append(link);
      } else {
        item.textContent = String(evidenceRef);
      }
      doctorEvidenceList.append(item);
    }
  }

  function selectedEvidenceRefs() {
    return Array.from(doctorCandidateList.querySelectorAll("input[type=checkbox]:checked"))
      .map(input => input.value);
  }

  function renderCandidates(candidates) {
    doctorCandidateList.replaceChildren();
    doctorCandidates.hidden = !Array.isArray(candidates) || candidates.length === 0;
    if (doctorCandidates.hidden) return;

    for (const candidate of candidates) {
      if (!candidate || typeof candidate.evidence_ref !== "string") continue;
      const label = document.createElement("label");
      label.className = "doctor-candidate-choice";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = candidate.evidence_ref;
      input.setAttribute("aria-label", `候補 ${candidate.evidence_ref} を選択`);
      input.addEventListener("change", () => {
        doctorPrimaryAction.disabled = selectedEvidenceRefs().length === 0;
      });
      const text = document.createElement("span");
      text.textContent = candidate.evidence_ref;
      label.append(input, text);
      doctorCandidateList.append(label);
    }
  }

  function renderDoctor(payload) {
    if (!payload || payload.schema_version !== "doctor.ui.v1" || !payload.envelope) {
      throw new Error("invalid doctor response");
    }

    const envelope = payload.envelope;
    const result = envelope.doctor;
    const evaluation = result?.evaluation;
    const primary = evaluation?.primary_state;
    const verification = result?.verification;
    const catalog = primary ? doctorCatalog[primary.state_code] : null;
    if (primary) {
      if (!catalog
        || !Array.isArray(primary.reason_codes)
        || primary.reason_codes.length !== 1
        || primary.reason_codes[0] !== primary.state_code
        || primary.severity !== catalog[0]
        || primary.retryability !== catalog[1]
        || primary.next_action !== catalog[2]) {
        throw new Error("invalid doctor tuple");
      }
    }
    if (verification && !Object.hasOwn(lifecycleLabels, verification.state)) throw new Error("invalid doctor lifecycle");
    const source = evaluation?.source_surface ?? envelope.source_surface;
    if (source !== null && source !== undefined && typeof source !== "string") throw new Error("invalid doctor source");

    currentVerification = verification && envelope.verification_id
      ? { id: envelope.verification_id, revision: verification.revision, state: verification.state }
      : null;
    doctorFields.state.replaceChildren(primary
      ? enumPresentation(primary.state_code, catalog[3])
      : missingFactPresentation("Doctor の判定状態"));
    doctorFields.nextAction.replaceChildren(primary
      ? enumPresentation(primary.next_action, catalog[4])
      : missingFactPresentation("次の対応"));
    doctorFields.severity.replaceChildren(primary
      ? enumPresentation(primary.severity, severityLabels[primary.severity])
      : missingFactPresentation("重要度"));
    doctorFields.source.replaceChildren(source
      ? valueLine(source, false)
      : missingFactPresentation("取得元"));
    doctorFields.retryability.replaceChildren(primary
      ? enumPresentation(primary.retryability, retryabilityLabels[primary.retryability])
      : missingFactPresentation("再確認条件"));
    doctorFields.lifecycle.replaceChildren(verification
      ? enumPresentation(verification.state, lifecycleLabels[verification.state])
      : missingFactPresentation("確認の進行状態"));
    renderEvidence(primary?.evidence_refs, payload.navigation_targets);
    renderCandidates(envelope.candidates);

    if (verification?.state === "active" && currentVerification) {
      setSourceLocked(true);
      setCancelAction(true);
      if (Array.isArray(envelope.candidates) && envelope.candidates.length > 0) {
        setDoctorAction("選択した証拠で完了", completeVerification, true);
      } else {
        setDoctorAction("状態を確認", refreshVerification);
      }
    } else if (verification?.state === "cancelled" && currentVerification) {
      setSourceLocked(false);
      setCancelAction(false);
      setDoctorAction("ロールバック後の状態を更新", refreshVerification);
    } else {
      setSourceLocked(false);
      setCancelAction(false);
      setDoctorAction(null, null);
    }
    const announcement = verification
      ? lifecycleLabels[verification.state]
      : primary ? catalog[3] : "判定に必要な記録がありません";
    announceDoctor(`Doctor の状態を更新しました: ${announcement}`, true);
  }

  async function requestDoctor(url, options) {
    const response = await fetch(url, { cache: "no-store", ...options });
    const payload = await response.json();
    if (!response.ok) {
      const error = new Error("doctor request failed");
      error.doctorPayload = payload;
      throw error;
    }
    return payload;
  }

  function renderFailureEnvelope(error) {
    if (!error?.doctorPayload?.envelope) return false;
    try {
      renderDoctor(error.doctorPayload);
      return true;
    } catch {
      return false;
    }
  }

  function renderExactSummary(container, values) {
    container.replaceChildren();
    for (const [label, value] of values) {
      const row = document.createElement("div");
      const term = document.createElement("dt");
      const detail = document.createElement("dd");
      term.textContent = label;
      if (value instanceof Node) detail.append(value);
      else detail.textContent = display(value);
      row.append(term, detail);
      container.append(row);
    }
  }

  async function loadExactEvidenceTarget() {
    const query = new URLSearchParams(window.location.search);
    const sessionId = query.get("session_id");
    const observationId = query.get("observation_id");
    if (sessionId) {
      const alertLink = document.getElementById("doctor-session-alert-link");
      if (alertLink) alertLink.href = `/alerts?session_id=${encodeURIComponent(sessionId)}`;
      const costLink = document.getElementById("doctor-session-cost-link");
      if (costLink) costLink.href = `/costs?session_id=${encodeURIComponent(sessionId)}`;
      try {
        const payload = await requestDoctor(`/api/doctor/ui/v1/sessions/${encodeURIComponent(sessionId)}`);
        const session = payload?.session;
        if (!session || !Object.hasOwn(sessionStatusLabels, session.status)
            || !Object.hasOwn(completenessLabels, session.completeness)) throw new Error("invalid session evidence");
        renderExactSummary(doctorSessionTargetSummary, [
          ["Session ID", session.session_id], ["状態", enumPresentation(session.status, sessionStatusLabels[session.status])],
          ["完全性", enumPresentation(session.completeness, completenessLabels[session.completeness])],
          ["最終確認", session.last_seen_at ?? missingFactPresentation("最終確認時刻")],
        ]);
        doctorSessionTarget.hidden = false;
        document.getElementById("doctor-session-target-heading")?.focus();
      } catch (error) {
        const state = error?.doctorPayload?.error === "evidence_not_found"
          ? missingFactPresentation("指定した Session の記録")
          : valueLine("Session の記録を読み込めませんでした。", false);
        renderExactSummary(doctorSessionTargetSummary, [["状態", state]]);
        doctorSessionTarget.hidden = false;
      }
    }
    if (observationId) {
      try {
        const payload = await requestDoctor(`/api/doctor/ui/v1/source-diagnostics/${encodeURIComponent(observationId)}`);
        const observation = payload?.observation;
        const diagnostic = observation?.source_diagnostic;
        if (!diagnostic) throw new Error("invalid source evidence");
        const entry = diagnosticTuple(diagnostic);
        renderExactSummary(doctorSourceTargetSummary, [
          ["Observation ID", observation.observation_id],
          ["ソース", diagnostic.source_surface ?? missingFactPresentation("取得元")],
          ["adapter", diagnostic.source_adapter ?? missingFactPresentation("Adapter")],
          ["互換性", diagnosticStatePresentation(diagnostic, entry)],
          ["理由", entry.reasons.length ? enumPresentation(entry.reasons[0], compatibilityReasonLabels[entry.reasons[0]]) : missingFactPresentation("互換性に関する追加理由")],
          ["次の対応", enumPresentation(diagnostic.next_action, compatibilityActionLabels[diagnostic.next_action])],
          ["観測時刻", observation.observed_at ?? missingFactPresentation("観測時刻")],
        ]);
        doctorSourceTarget.hidden = false;
        document.getElementById("doctor-source-target-heading")?.focus();
      } catch (error) {
        const state = error?.doctorPayload?.error === "evidence_not_found"
          ? missingFactPresentation("指定したソース診断の記録")
          : valueLine("ソース診断の記録を読み込めませんでした。", false);
        renderExactSummary(doctorSourceTargetSummary, [["状態", state]]);
        doctorSourceTarget.hidden = false;
      }
    }
  }

  async function loadDoctorSources() {
    setDoctorAction(null, null);
    doctorSource.disabled = true;
    try {
      const payload = await requestDoctor("/api/doctor/ui/v1/sources");
      if (payload?.schema_version !== "doctor.ui.v1" || !Array.isArray(payload.sources)) {
        throw new Error("invalid sources response");
      }

      const options = [];
      const placeholder = document.createElement("option");
      placeholder.value = "";
      placeholder.textContent = "ソースを選択";
      options.push(placeholder);
      for (const source of payload.sources) {
        if (!source || typeof source.source_id !== "string" || typeof source.display_label !== "string"
            || !Object.hasOwn(detectionLabels, source.detection_state)
            || !setupOwnershipValues.has(source.setup_ownership)) throw new Error("invalid source entry");
        const option = document.createElement("option");
        option.value = source.source_id;
        option.textContent = `${source.display_label} — ${detectionLabels[source.detection_state]}`;
        option.dataset.detectionState = source.detection_state;
        option.dataset.setupOwnership = String(source.setup_ownership);
        options.push(option);
      }
      doctorSource.replaceChildren(...options);
      doctorSource.value = "";
      doctorSource.disabled = false;
      const detected = payload.sources.filter(source => source.detection_state === "detected").length;
      doctorSourceState.textContent = detected === 0
        ? "検出されたソースはありません。ソースを選択して確認できます。"
        : `${detected} 件のソースを検出しました。確認するソースを選択してください。`;
      announceDoctor("Doctor のソース一覧を読み込みました。", false);
    } catch {
      doctorSourceState.textContent = "ソース一覧を読み込めませんでした。";
      doctorFailure(loadDoctorSources);
    }
  }

  async function beginVerification() {
    const sourceId = doctorSource.value;
    if (!sourceId) return;
    setSourceLocked(true);
    setDoctorAction(null, null);
    try {
      const payload = await requestDoctor("/api/doctor/ui/v1/verifications", {
        method: "POST",
        headers: { "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" },
        body: JSON.stringify({ source_id: sourceId }),
      });
      renderDoctor(payload);
    } catch (error) {
      if (renderFailureEnvelope(error) && currentVerification?.state === "active") mutationFailure();
      else {
        setCancelAction(false);
        setDoctorAction(null, null);
        announceDoctor("検証開始の結果を確認できませんでした。ページを再読み込みしてください。", true);
      }
    }
  }

  async function refreshVerification() {
    if (!currentVerification) return;
    const exact = currentVerification;
    setSourceLocked(true);
    setDoctorAction(null, null);
    try {
      const payload = await requestDoctor(`/api/doctor/ui/v1/verifications/${encodeURIComponent(exact.id)}`);
      renderDoctor(payload);
    } catch (error) {
      renderFailureEnvelope(error);
      if (currentVerification?.state === "active") doctorFailure(refreshVerification);
    }
  }

  async function completeVerification() {
    if (!currentVerification) return;
    const exact = currentVerification;
    const acceptedEvidenceRefs = selectedEvidenceRefs();
    if (acceptedEvidenceRefs.length === 0) return;
    setSourceLocked(true);
    setCancelAction(false);
    setDoctorAction(null, null);
    try {
      const payload = await requestDoctor(`/api/doctor/ui/v1/verifications/${encodeURIComponent(exact.id)}/complete`, {
        method: "POST",
        headers: { "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" },
        body: JSON.stringify({ expected_revision: exact.revision, accepted_evidence_refs: acceptedEvidenceRefs }),
      });
      renderDoctor(payload);
    } catch (error) {
      renderFailureEnvelope(error);
      if (currentVerification?.state === "active") mutationFailure();
    }
  }

  async function cancelVerification() {
    if (!currentVerification) return;
    const exact = currentVerification;
    setSourceLocked(true);
    setCancelAction(false);
    setDoctorAction(null, null);
    try {
      const payload = await requestDoctor(`/api/doctor/ui/v1/verifications/${encodeURIComponent(exact.id)}/cancel`, {
        method: "POST",
        headers: { "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" },
        body: JSON.stringify({ expected_revision: exact.revision }),
      });
      renderDoctor(payload);
    } catch (error) {
      renderFailureEnvelope(error);
      if (currentVerification?.state === "active") mutationFailure();
    }
  }

  function resetDoctorResult() {
    currentVerification = null;
    for (const [name, field] of Object.entries(doctorFields)) {
      field.replaceChildren(missingFactPresentation({
        state: "判定状態", severity: "重要度", source: "取得元", nextAction: "次の対応",
        retryability: "再確認条件", lifecycle: "確認の進行状態",
      }[name]));
    }
    renderEvidence([], []);
    renderCandidates([]);
    setCancelAction(false);
  }

  doctorSource?.addEventListener("change", () => {
    const option = doctorSource.selectedOptions[0];
    resetDoctorResult();
    doctorSourceState.textContent = option?.value
      ? `${option.textContent}を確認できます。`
      : "確認するソースを選択してください。";
    setDoctorAction(option?.value ? "検証を開始" : null, option?.value ? beginVerification : null);
  });
  doctorPrimaryAction?.addEventListener("click", () => doctorAction?.());
  doctorCancelAction?.addEventListener("click", cancelVerification);

  refresh();
  refreshSourceDiagnostics();
  if (doctorSource) loadDoctorSources();
  loadExactEvidenceTarget();
  document.addEventListener("cao-monitor-refresh", () => {
    refresh();
    refreshSourceDiagnostics();
  });

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
