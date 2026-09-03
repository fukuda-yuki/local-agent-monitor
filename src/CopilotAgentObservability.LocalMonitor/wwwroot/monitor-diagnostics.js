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
  const compatibilityLabels = {
    supported: "対応済み",
    supported_with_unknown_fields: "対応済み（未知フィールドあり）",
    unsupported_source_version: "未対応のバージョン",
    schema_drift_detected: "スキーマ変更を検出",
    recognized_record_drop_detected: "認識済みレコードの欠落を検出",
    adapter_failure: "アダプターエラー",
  };
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
  const doctorStateLabels = {
    monitor_not_installed: "Monitor がインストールされていません",
    monitor_not_running: "Monitor が起動していません",
    receiver_not_bound: "受信ポートを利用できません",
    port_owned_by_foreign_process: "受信ポートを別のプロセスが使用しています",
    endpoint_mismatch: "送信先が Monitor と一致しません",
    protocol_mismatch: "送信プロトコルが一致しません",
    signal_disabled: "トレース送信が無効です",
    unsupported_source_version: "この取得元のバージョンには対応していません",
    feature_unavailable: "必要な機能をこの取得元では利用できません",
    agent_restart_required: "取得元の再起動が必要です",
    endpoint_unreachable: "Monitor の受信先へ接続できません",
    payload_rejected: "送信データを受け付けられませんでした",
    raw_persisted_projection_pending: "記録済みデータの反映を待っています",
    projection_failed: "記録済みデータを画面へ反映できませんでした",
    session_unbound: "記録を Session に結び付けられません",
    content_capture_disabled: "内容の記録が無効です",
    sanitized_only_raw_unavailable: "内容は記録されていません",
    schema_drift_detected: "取得元のスキーマ変更を検出しました",
    ready_no_real_trace: "接続確認のための記録がまだありません",
    first_trace_ready: "最初の記録を確認できました",
  };
  const doctorActionLabels = {
    install_monitor: "Monitor をインストールしてください",
    start_monitor: "Monitor を起動してください",
    restart_monitor: "Monitor を再起動してください",
    free_or_change_port: "ポートを解放するか変更してください",
    update_source_endpoint: "送信元の接続先を更新してください",
    use_http_protobuf: "HTTP/Protobuf を使用してください",
    enable_trace_signal: "トレース送信を有効にしてください",
    use_supported_source_version: "対応する取得元バージョンを使用してください",
    use_supported_source_surface: "対応する取得元を使用してください",
    restart_source_process: "取得元のプロセスを再起動してください",
    verify_endpoint_reachability: "受信先への接続を確認してください",
    inspect_rejected_payload: "拒否された送信データの診断を確認してください",
    wait_for_projection: "反映の完了を待ってください",
    open_projection_diagnostics: "反映処理の診断を確認してください",
    select_exact_session: "対象の Session を選択してください",
    enable_content_capture_if_desired: "必要な場合は内容の記録を有効にしてください",
    restart_without_sanitized_only_if_desired: "必要な場合は通常モードで Monitor を再起動してください",
    review_source_diagnostics: "取得元の診断を確認してください",
    run_bounded_source_interaction: "取得元で確認用の操作を実行してください",
    open_verified_trace_or_session: "確認済みの Trace または Session を開いてください",
  };
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

  function enumPresentation(code, labels) {
    if (typeof code !== "string" || !Object.hasOwn(labels, code)) {
      throw new Error("unknown diagnostic enum");
    }
    const node = document.createElement("span");
    const primary = document.createElement("span");
    primary.textContent = labels[code];
    const technical = document.createElement("details");
    const summary = document.createElement("summary");
    summary.textContent = "技術情報";
    const raw = document.createElement("code");
    raw.textContent = code;
    technical.append(summary, raw);
    node.append(primary, technical);
    return node;
  }

  function enumCell(code, labels) {
    const node = document.createElement("td");
    node.append(enumPresentation(code, labels));
    return node;
  }

  function enumLines(codes, labels) {
    if (!Array.isArray(codes)) throw new Error("invalid diagnostic reasons");
    const node = document.createElement("td");
    for (const code of codes) node.append(enumPresentation(code, labels));
    return node;
  }

  function setEnumPresentation(container, code, labels) {
    container.replaceChildren(enumPresentation(code, labels));
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
        const row = document.createElement("tr");
        row.append(
          lines([item.observation_id, item.observed_at], true),
          lines([item.source_surface, item.source_application_version], false),
          lines([item.source_adapter, item.adapter_version], false),
          enumCell(item.compatibility_state, compatibilityLabels),
          enumLines(item.reason_codes, compatibilityReasonLabels),
          enumCell(item.next_action, compatibilityActionLabels),
          lines([item.unknown_span_count, item.unknown_event_count, item.unknown_attribute_count], true));
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
    currentVerification = verification && envelope.verification_id
      ? { id: envelope.verification_id, revision: verification.revision, state: verification.state }
      : null;

    if (primary) {
      if (!Array.isArray(primary.reason_codes)
        || primary.reason_codes.length !== 1
        || primary.reason_codes[0] !== primary.state_code) {
        throw new Error("invalid doctor reason");
      }
      setEnumPresentation(doctorFields.state, primary.state_code, doctorStateLabels);
      setEnumPresentation(doctorFields.nextAction, primary.next_action, doctorActionLabels);
    } else {
      doctorFields.state.textContent = "—";
      doctorFields.nextAction.textContent = "—";
    }
    doctorFields.severity.textContent = display(primary?.severity);
    doctorFields.source.textContent = display(evaluation?.source_surface ?? envelope.source_surface);
    doctorFields.retryability.textContent = display(primary?.retryability);
    doctorFields.lifecycle.textContent = display(verification?.state);
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
    announceDoctor(`Doctor の状態を更新しました: ${display(verification?.state ?? primary?.state_code)}`, true);
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
        if (!session) throw new Error("invalid session evidence");
        renderExactSummary(doctorSessionTargetSummary, [
          ["Session ID", session.session_id], ["状態", session.status],
          ["完全性", session.completeness], ["最終確認", session.last_seen_at],
        ]);
        doctorSessionTarget.hidden = false;
        document.getElementById("doctor-session-target-heading")?.focus();
      } catch {
        renderExactSummary(doctorSessionTargetSummary, [["状態", "evidence_not_found"]]);
        doctorSessionTarget.hidden = false;
      }
    }
    if (observationId) {
      try {
        const payload = await requestDoctor(`/api/doctor/ui/v1/source-diagnostics/${encodeURIComponent(observationId)}`);
        const observation = payload?.observation;
        const diagnostic = observation?.source_diagnostic;
        if (!diagnostic) throw new Error("invalid source evidence");
        renderExactSummary(doctorSourceTargetSummary, [
          ["Observation ID", observation.observation_id], ["ソース", diagnostic.source_surface],
          ["adapter", diagnostic.source_adapter],
          ["互換性", enumPresentation(diagnostic.compatibility_state, compatibilityLabels)],
          ["次の対応", enumPresentation(diagnostic.next_action, compatibilityActionLabels)],
          ["観測時刻", observation.observed_at],
        ]);
        doctorSourceTarget.hidden = false;
        document.getElementById("doctor-source-target-heading")?.focus();
      } catch {
        renderExactSummary(doctorSourceTargetSummary, [["状態", "evidence_not_found"]]);
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

      doctorSource.replaceChildren();
      const placeholder = document.createElement("option");
      placeholder.value = "";
      placeholder.textContent = "ソースを選択";
      doctorSource.append(placeholder);
      for (const source of payload.sources) {
        const option = document.createElement("option");
        option.value = String(source.source_id);
        option.textContent = `${display(source.display_label)} — ${display(source.detection_state)}`;
        option.dataset.detectionState = String(source.detection_state);
        option.dataset.setupOwnership = String(source.setup_ownership);
        doctorSource.append(option);
      }
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
    for (const field of Object.values(doctorFields)) field.textContent = "—";
    renderEvidence([], []);
    renderCandidates([]);
    setCancelAction(false);
  }

  doctorSource?.addEventListener("change", () => {
    const option = doctorSource.selectedOptions[0];
    resetDoctorResult();
    doctorSourceState.textContent = option?.value
      ? `${option.textContent} / setup: ${option.dataset.setupOwnership}`
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
