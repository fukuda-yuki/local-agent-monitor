(() => {
  "use strict";

  const api = "/api/historical-analysis/v1";
  const root = document.getElementById("historical-analysis-root");
  if (!root) return;

  const byId = id => document.getElementById(id);
  const selectionForm = byId("historical-analysis-selection");
  const previewButton = byId("historical-analysis-preview-button");
  const previewPanel = byId("historical-analysis-preview");
  const previewHeading = byId("historical-analysis-preview-heading");
  const instructionButton = byId("historical-analysis-instruction-start");
  const efficiencyButton = byId("historical-analysis-efficiency-start");
  const instructionResult = byId("historical-analysis-instruction-result");
  const efficiencyResult = byId("historical-analysis-efficiency-result");
  const live = byId("historical-analysis-live");
  const validation = byId("historical-analysis-validation");
  let preview = null;
  let previewGeneration = 0;

  const element = (tag, text, className) => {
    const node = document.createElement(tag);
    if (text !== undefined && text !== null) node.textContent = String(text);
    if (className) node.className = className;
    return node;
  };

  const replace = (parent, ...children) => parent.replaceChildren(...children);
  const list = value => Array.isArray(value) ? value : [];
  const exact = value => value === null || value === undefined ? "unavailable" : String(value);
  const lines = id => byId(id).value.split(/\r?\n/u).filter(value => value.length > 0);
  const utc = value => value ? `${value}:00Z` : null;

  const announce = message => {
    live.textContent = message;
  };

  const showValidation = message => {
    validation.textContent = message;
    validation.hidden = false;
    validation.focus();
    announce(message);
  };

  const clearValidation = () => {
    validation.textContent = "";
    validation.hidden = true;
  };

  const post = async (path, body) => {
    const response = await fetch(`${api}${path}`, {
      method: "POST",
      cache: "no-store",
      credentials: "same-origin",
      headers: {
        "content-type": "application/json",
        "x-monitor-csrf": "local-monitor"
      },
      body: JSON.stringify(body)
    });
    let data;
    try {
      data = await response.json();
    } catch {
      data = { error: "historical_analysis_store_unavailable" };
    }
    if (!response.ok) {
      const error = new Error(exact(data.error));
      error.code = exact(data.error);
      throw error;
    }
    return data;
  };

  const get = async path => {
    const response = await fetch(`${api}${path}`, {
      cache: "no-store",
      credentials: "same-origin"
    });
    let data;
    try {
      data = await response.json();
    } catch {
      data = { error: "historical_analysis_store_unavailable" };
    }
    if (!response.ok) {
      const error = new Error(exact(data.error));
      error.code = exact(data.error);
      throw error;
    }
    return data;
  };

  const cell = value => element("td", exact(value));

  const decisionReasons = session => {
    const reasons = list(session.completeness_reasons);
    return reasons.length === 0 ? "none" : reasons.join(", ");
  };

  const distribution = value => list(value)
    .map(item => `${exact(item.key)}=${exact(item.count)}`)
    .join(", ") || "none";

  const renderPreview = value => {
    const includedBody = byId("historical-analysis-included").querySelector("tbody");
    const excludedBody = byId("historical-analysis-excluded").querySelector("tbody");
    const includedRows = list(value.included).map(session => {
      const row = element("tr");
      row.append(
        cell(session.session_id),
        cell(`${exact(session.source_surface)} / ${exact(session.source_kind)}`),
        cell(exact(session.completeness)),
        cell(`${exact(session.content_state)} / ${exact(session.descriptor_state)}`),
        cell(decisionReasons(session))
      );
      return row;
    });
    const excludedRows = list(value.excluded).map(session => {
      const row = element("tr");
      row.append(
        cell(session.session_id),
        cell(session.reason),
        cell(session.metadata === null ? "metadata_omitted" : "metadata_available")
      );
      return row;
    });
    replace(includedBody, ...includedRows);
    replace(excludedBody, ...excludedRows);
    byId("historical-analysis-preview-count").textContent =
      `${includedRows.length} included · ${excludedRows.length} excluded`;

    const sources = new Set(list(value.included).map(session => `${session.source_surface}/${session.source_kind}`));
    const completeness = new Set(list(value.included).map(session => session.completeness));
    const warnings = [];
    if (sources.size > 1) warnings.push("mixed source cohort");
    if (completeness.size > 1) warnings.push("mixed completeness cohort");
    if (value.truncated_before) {
      warnings.push(`truncated_before · truncated_session_count=${exact(value.truncated_session_count)}`);
    }
    const warning = byId("historical-analysis-warnings");
    warning.textContent = warnings.join(" · ");
    warning.hidden = warnings.length === 0;
    previewPanel.hidden = false;
    instructionButton.disabled = false;
    efficiencyButton.disabled = false;
    byId("historical-analysis-instruction-state").textContent = "ready";
    byId("historical-analysis-efficiency-state").textContent = "ready";
    previewHeading.focus();
    announce("スコープのプレビューが完了しました。Instruction と Efficiency を個別に開始できます。");
  };

  const selection = () => ({
    repository: byId("historical-analysis-repository").value || null,
    workspace: byId("historical-analysis-workspace").value || null,
    from: utc(byId("historical-analysis-from").value),
    to: utc(byId("historical-analysis-to").value),
    explicit_session_ids: lines("historical-analysis-session-ids"),
    source_surfaces: lines("historical-analysis-source-surfaces"),
    task_label: byId("historical-analysis-task-label").value || null,
    experiment_label: byId("historical-analysis-experiment-label").value || null,
    maximum_session_count: Number(byId("historical-analysis-maximum").value),
    sanitized_only: byId("historical-analysis-sanitized-only").checked
  });

  selectionForm.addEventListener("submit", async event => {
    event.preventDefault();
    clearValidation();
    preview = null;
    previewGeneration += 1;
    const generation = previewGeneration;
    instructionButton.disabled = true;
    efficiencyButton.disabled = true;
    previewButton.disabled = true;
    announce("スコープをプレビューしています。");
    try {
      const value = await post("/preview", {
        schema_version: "historical-analysis-preview.request.v1",
        selection: selection()
      });
      if (generation !== previewGeneration) return;
      preview = value;
      renderPreview(value);
    } catch (error) {
      showValidation(exact(error.code));
    } finally {
      previewButton.disabled = false;
    }
  });

  selectionForm.addEventListener("input", () => {
    if (!preview) return;
    preview = null;
    previewGeneration += 1;
    instructionButton.disabled = true;
    efficiencyButton.disabled = true;
    byId("historical-analysis-instruction-state").textContent = "preview_required";
    byId("historical-analysis-efficiency-state").textContent = "preview_required";
    announce("スコープが変更されました。分析を開始する前に再プレビューしてください。");
  });

  const stateText = state => {
    const meanings = {
      queued: "queued",
      running: "running",
      succeeded: "succeeded",
      zero_findings: "zero_findings · provider completed with 0 findings",
      no_eligible_sessions: "no_eligible_sessions",
      content_unavailable: "content_unavailable",
      stale_extraction: "stale_extraction",
      extraction_invalid: "extraction_invalid",
      invalid_citation: "invalid_citation",
      provider_partial: "provider_partial",
      provider_failed: "provider_failed",
      timed_out: "timed_out",
      canceled: "canceled",
      zero_drivers: "zero_drivers · 0 drivers",
      analysis_failed: "analysis_failed"
    };
    return meanings[state] || exact(state);
  };

  const terminalInstructionSuccess = state => state === "succeeded" || state === "zero_findings";
  const terminalInstruction = state => !["queued", "running"].includes(state);
  const terminalEfficiencySuccess = state => state === "succeeded" || state === "zero_drivers";
  const terminalEfficiency = state => !["queued", "running"].includes(state);

  const decodeHandoff = base64 => {
    if (!base64) return null;
    try {
      const bytes = Uint8Array.from(atob(base64), char => char.charCodeAt(0));
      return JSON.parse(new TextDecoder().decode(bytes));
    } catch {
      return null;
    }
  };

  const referenceTokens = reference => {
    const tokens = [reference?.session_id, reference?.trace_id, reference?.span_id]
      .filter(value => typeof value === "string" && /^(session|trace|span)-ref-[0-9a-f]{32}$/u.test(value));
    return [...new Set(tokens)];
  };

  const evidenceControl = (token, binding) => {
    const wrapper = element("div");
    wrapper.dataset.evidenceResolution = token;
    const button = element("button", `Resolve evidence: ${token}`, "monitor-btn");
    button.type = "button";
    button.dataset.evidenceReference = token;
    button.addEventListener("click", async () => {
      button.disabled = true;
      try {
        const response = await post("/evidence/resolve", {
          schema_version: "historical-analysis-evidence-resolve.request.v1",
          extraction_id: binding.extraction_id,
          repository_safe_sha256: binding.repository_safe_sha256,
          references: [token]
        });
        const resolution = response.resolutions?.[0];
        const state = exact(resolution?.resolution_state);
        const content = exact(resolution?.content_state);
        const target = resolution?.target;
        if (state === "resolved"
          && typeof target === "string"
          && (target.startsWith("/traces/") || target.startsWith("/diagnostics?session_id="))) {
          const link = element("a", `Evidence: ${token}`);
          link.href = target;
          link.className = "panel-link";
          replace(wrapper, link, element("span", ` · ${state} · ${content}`));
        } else {
          replace(wrapper, element("span", `${token} · ${state} · ${content}`));
        }
        announce(`Evidence ${token}: ${state} · ${content}`);
      } catch (error) {
        replace(wrapper, element("span", `${token} · ${exact(error.code)}`));
        announce(`Evidence ${token}: ${exact(error.code)}`);
      }
    });
    wrapper.append(button);
    return wrapper;
  };

  const appendEvidence = (parent, references, binding) => {
    const tokens = [...new Set(list(references).flatMap(referenceTokens))];
    if (tokens.length === 0) {
      parent.append(element("p", "exact evidence: none"));
      return;
    }
    const section = element("div");
    section.append(element("h5", "Exact evidence references"));
    tokens.forEach(token => section.append(evidenceControl(token, binding)));
    parent.append(section);
  };

  const renderInstruction = (status, binding) => {
    const body = byId("historical-analysis-instruction-body");
    const content = document.createDocumentFragment();
    content.append(element("p", `state: ${stateText(status.state)}`));
    content.append(element("p",
      `dataset: sanitized_only=${exact(status.dataset_projection?.sanitized_only)} · content_available=${exact(status.dataset_projection?.content_available)} · truncated_before=${exact(status.dataset_projection?.truncated_before)}`));
    const handoff = decodeHandoff(status.handoff_bytes);
    const findings = list(handoff?.findings);
    const supports = new Map(list(status.receipt?.findings).map(item => [item.finding_id, item]));
    if (status.state === "zero_findings") {
      content.append(element("p", "0 findings · provider-complete empty handoff"));
    }
    findings.forEach(finding => {
      const support = supports.get(finding.finding_id);
      const card = element("article", null, "panel");
      card.append(
        element("h4", exact(finding.category)),
        element("p", `finding: ${exact(finding.finding_id)}`),
        element("p", `verdict: ${exact(finding.verdict)}`),
        element("p", `candidate eligibility: ${exact(finding.candidate_eligibility)}`),
        element("p", `support: ${exact(support?.support_kind)} · recurring_count=${exact(support?.recurring_count)}`),
        element("p", `supporting Sessions: ${list(support?.supporting_session_ids).join(", ") || "none"}`),
        element("p", `supporting groups: ${list(support?.supporting_group_ids).join(", ") || "none"}`),
        element("p", `source surfaces: ${distribution(support?.source_surface_distribution)}`),
        element("p", `source versions: ${distribution(support?.source_version_distribution)}`),
        element("p", `source kinds: ${distribution(support?.source_kind_distribution)}`),
        element("p", `completeness: ${distribution(support?.completeness_distribution)}`),
        element("p", `gap: ${exact(finding.gap_summary)}`),
        element("p", `next-time instruction: ${exact(finding.suggested_instruction)}`)
      );
      appendEvidence(card, finding.evidence_refs, binding);
      content.append(card);
    });
    replace(body, content);
    instructionResult.hidden = false;
  };

  instructionButton.addEventListener("click", async () => {
    if (!preview) return;
    const binding = preview;
    const generation = previewGeneration;
    instructionButton.disabled = true;
    byId("historical-analysis-instruction-state").textContent = "starting";
    announce("Instruction 分析を開始しています。");
    try {
      const started = await post("/instruction-runs", {
        schema_version: "historical-analysis-instruction-start.request.v1",
        extraction_id: binding.extraction_id,
        raw_local_sha256: binding.raw_local_sha256,
        model: byId("historical-analysis-model").value,
        provider: byId("historical-analysis-provider").value,
        configuration_sha256: byId("historical-analysis-configuration").value,
        timeout_ms: Number(byId("historical-analysis-timeout").value),
        prompt_template_version: "historical-instruction-analysis.prompt.v1"
      });
      for (let attempt = 0; attempt < 40; attempt += 1) {
        const status = await get(`/instruction-runs/${encodeURIComponent(started.analysis_run_id)}`);
        if (generation !== previewGeneration) return;
        byId("historical-analysis-instruction-state").textContent = stateText(status.state);
        renderInstruction(status, binding);
        announce(`Instruction: ${status.state}`);
        if (terminalInstruction(status.state)) {
          if (terminalInstructionSuccess(status.state)) {
            byId("historical-analysis-instruction-heading").focus();
          } else {
            instructionButton.disabled = false;
            instructionButton.focus();
          }
          return;
        }
        await new Promise(resolve => setTimeout(resolve, 100));
      }
      byId("historical-analysis-instruction-state").textContent = "timed_out";
      if (generation !== previewGeneration) return;
      renderInstruction({ state: "timed_out", dataset_projection: {} }, binding);
      announce("Instruction: timed_out");
      instructionButton.disabled = false;
      instructionButton.focus();
    } catch (error) {
      if (generation !== previewGeneration) return;
      const code = exact(error.code);
      byId("historical-analysis-instruction-state").textContent = code;
      renderInstruction({ state: code, dataset_projection: {} }, binding);
      announce(`Instruction: ${code}`);
      instructionButton.disabled = false;
      instructionButton.focus();
    } finally {
      instructionButton.disabled = preview === null;
    }
  });

  const renderCoverage = coverage => {
    const card = element("article", null, "panel");
    card.append(
      element("h4", exact(coverage.category)),
      element("p", `coverage: ${exact(coverage.state)}`),
      element("p", `rule: ${exact(coverage.rule_source)}`),
      element("p", `formula: ${exact(coverage.formula)}`),
      element("p", `threshold: ${exact(coverage.threshold)}`),
      element("p", `eligible=${exact(coverage.eligible_session_count)} · observed=${exact(coverage.observed_sample_count)} · minimum=${exact(coverage.minimum_sample)}`),
      element("p", `reasons: ${list(coverage.reasons).join(", ") || "none"}`)
    );
    return card;
  };

  const renderDriver = (driver, binding) => {
    const card = element("article", null, "panel");
    card.append(
      element("h4", exact(driver.category)),
      element("p", `verdict: ${exact(driver.verdict)}`),
      element("p", `quality availability: ${exact(driver.quality_availability)}`),
      element("p", `formula: ${exact(driver.formula)}`),
      element("p", `threshold: ${exact(driver.threshold)}`),
      element("p", `observed: ${list(driver.observed_values).map(value => `${exact(value.name)}=${exact(value.value)} ${exact(value.unit)}`).join(", ") || "none"}`),
      element("p", `comparison notes: ${list(driver.comparison_notes).join(", ") || "none"}`),
      element("p", `summary: ${exact(driver.summary)}`),
      element("p", `mitigation: ${exact(driver.mitigation?.code)} · ${exact(driver.mitigation?.summary)}`)
    );
    appendEvidence(card, driver.evidence_refs, binding);
    return card;
  };

  const renderEfficiency = (status, binding) => {
    const body = byId("historical-analysis-efficiency-body");
    const content = document.createDocumentFragment();
    content.append(element("p", `state: ${stateText(status.state)}`));
    if (status.state === "zero_drivers") {
      content.append(element("p", "0 drivers · exact zero receipt"));
    }
    if (status.receipt) {
      const coverage = status.receipt.coverage;
      content.append(
        element("p", `quality availability: ${exact(status.receipt.quality_availability)}`),
        element("p", `comparison notes: ${list(status.receipt.comparison_notes).join(", ") || "none"}`),
        element("p", `coverage: included=${exact(coverage?.included_session_count)} · excluded=${exact(coverage?.excluded_session_count)} · truncated_before=${exact(coverage?.truncated_before)} · truncated_session_count=${exact(coverage?.truncated_session_count)}`),
        element("p", `coverage completeness: ${distribution(coverage?.completeness)}`),
        element("p", `coverage source kinds: ${distribution(coverage?.source_kinds)}`),
        element("p", `coverage capabilities: ${distribution(coverage?.capabilities)}`),
        element("h3", "Category coverage")
      );
      list(status.receipt.category_coverage).forEach(coverage => content.append(renderCoverage(coverage)));
      content.append(element("h3", "Drivers"));
      list(status.receipt.drivers).forEach(driver => content.append(renderDriver(driver, binding)));
    }
    replace(body, content);
    efficiencyResult.hidden = false;
  };

  efficiencyButton.addEventListener("click", async () => {
    if (!preview) return;
    const binding = preview;
    const generation = previewGeneration;
    efficiencyButton.disabled = true;
    byId("historical-analysis-efficiency-state").textContent = "starting";
    announce("Efficiency 分析を開始しています。");
    try {
      const started = await post("/efficiency-runs", {
        schema_version: "historical-analysis-efficiency-start.request.v1",
        extraction_id: binding.extraction_id,
        repository_safe_sha256: binding.repository_safe_sha256
      });
      for (let attempt = 0; attempt < 40; attempt += 1) {
        const status = await get(`/efficiency-runs/${encodeURIComponent(started.analysis_run_id)}`);
        if (generation !== previewGeneration) return;
        byId("historical-analysis-efficiency-state").textContent = stateText(status.state);
        renderEfficiency(status, binding);
        announce(`Efficiency: ${status.state}`);
        if (terminalEfficiency(status.state)) {
          if (terminalEfficiencySuccess(status.state)) {
            byId("historical-analysis-efficiency-heading").focus();
          } else {
            efficiencyButton.disabled = false;
            efficiencyButton.focus();
          }
          return;
        }
        await new Promise(resolve => setTimeout(resolve, 100));
      }
      if (generation !== previewGeneration) return;
      renderEfficiency({ state: "timed_out", receipt: null }, binding);
      announce("Efficiency: timed_out");
      efficiencyButton.disabled = false;
      efficiencyButton.focus();
    } catch (error) {
      if (generation !== previewGeneration) return;
      const code = exact(error.code);
      renderEfficiency({ state: code, receipt: null }, binding);
      byId("historical-analysis-efficiency-state").textContent = code;
      announce(`Efficiency: ${code}`);
      efficiencyButton.disabled = false;
      efficiencyButton.focus();
    } finally {
      efficiencyButton.disabled = preview === null;
    }
  });
})();
