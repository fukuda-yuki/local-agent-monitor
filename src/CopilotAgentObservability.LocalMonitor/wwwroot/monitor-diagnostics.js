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
      sourceDiagnosticMessage("ソース互換性の観測はまだありません。");
      return;
    }

    sourceDiagnosticRows.replaceChildren();
    for (const item of items) {
      const row = document.createElement("tr");
      row.append(
        lines([item.observation_id, item.observed_at], true),
        lines([item.source_surface, item.source_application_version], false),
        lines([item.source_adapter, item.adapter_version], false),
        cell(item.compatibility_state, true),
        lines(item.reason_codes, true),
        cell(item.next_action, true),
        lines([item.unknown_span_count, item.unknown_event_count, item.unknown_attribute_count], true));
      sourceDiagnosticRows.append(row);
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
  let doctorSelectionGeneration = 0;

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
    currentVerification = verification?.state === "active" && envelope.verification_id
      ? { id: envelope.verification_id, revision: verification.revision }
      : null;

    doctorFields.state.textContent = display(primary?.state_code);
    doctorFields.severity.textContent = display(primary?.severity);
    doctorFields.source.textContent = display(evaluation?.source_surface ?? envelope.source_surface);
    doctorFields.nextAction.textContent = display(primary?.next_action);
    doctorFields.retryability.textContent = display(primary?.retryability);
    doctorFields.lifecycle.textContent = display(verification?.state);
    renderEvidence(primary?.evidence_refs, payload.navigation_targets);
    renderCandidates(envelope.candidates);

    if (verification?.state === "active" && currentVerification) {
      setCancelAction(true);
      if (Array.isArray(envelope.candidates) && envelope.candidates.length > 0) {
        setDoctorAction("選択した証拠で完了", completeVerification, true);
      } else {
        setDoctorAction("状態を確認", refreshVerification);
      }
    } else {
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
      detail.textContent = display(value);
      row.append(term, detail);
      container.append(row);
    }
  }

  async function loadExactEvidenceTarget() {
    const query = new URLSearchParams(window.location.search);
    const sessionId = query.get("session_id");
    const observationId = query.get("observation_id");
    if (sessionId) {
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
          ["adapter", diagnostic.source_adapter], ["互換性", diagnostic.compatibility_state],
          ["次の対応", diagnostic.next_action], ["観測時刻", observation.observed_at],
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
    const generation = doctorSelectionGeneration;
    setDoctorAction(null, null);
    try {
      const payload = await requestDoctor("/api/doctor/ui/v1/verifications", {
        method: "POST",
        headers: { "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" },
        body: JSON.stringify({ source_id: sourceId }),
      });
      if (generation !== doctorSelectionGeneration || doctorSource.value !== sourceId) return;
      renderDoctor(payload);
    } catch (error) {
      if (generation !== doctorSelectionGeneration || doctorSource.value !== sourceId) return;
      renderFailureEnvelope(error);
      if (currentVerification) mutationFailure();
      else doctorFailure(loadDoctorSources);
    }
  }

  async function refreshVerification() {
    if (!currentVerification) return;
    const exact = currentVerification;
    setDoctorAction(null, null);
    try {
      const payload = await requestDoctor(`/api/doctor/ui/v1/verifications/${encodeURIComponent(exact.id)}`);
      renderDoctor(payload);
    } catch (error) {
      renderFailureEnvelope(error);
      if (currentVerification) doctorFailure(refreshVerification);
    }
  }

  async function completeVerification() {
    if (!currentVerification) return;
    const exact = currentVerification;
    const acceptedEvidenceRefs = selectedEvidenceRefs();
    if (acceptedEvidenceRefs.length === 0) return;
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
      if (currentVerification) mutationFailure();
    }
  }

  async function cancelVerification() {
    if (!currentVerification) return;
    const exact = currentVerification;
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
      if (currentVerification) mutationFailure();
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
    doctorSelectionGeneration += 1;
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
