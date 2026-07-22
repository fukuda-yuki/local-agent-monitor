(() => {
  "use strict";

  const root = document.getElementById("alert-center-root");
  if (!root) return;

  const rows = document.getElementById("alert-rows");
  const content = document.getElementById("alert-content");
  const empty = document.getElementById("alert-empty");
  const error = document.getElementById("alert-error");
  const status = document.getElementById("alert-status");
  const detailBody = document.getElementById("alert-detail-body");
  const detailHeading = document.getElementById("alert-detail-heading");
  const recurring = document.getElementById("alert-recurring");
  const coverage = document.getElementById("alert-coverage");
  const live = document.getElementById("alert-live");
  const form = document.getElementById("alert-filters");
  const previousPage = document.getElementById("alert-page-previous");
  const nextPage = document.getElementById("alert-page-next");
  const pageInfo = document.getElementById("alert-page-info");
  let snapshot = null;
  let selectedAlertId = null;
  let loadGeneration = 0;
  let loadController = null;

  function node(tag, className, text) {
    const value = document.createElement(tag);
    if (className) value.className = className;
    if (text !== undefined && text !== null) value.textContent = String(text);
    return value;
  }

  function shortId(value) {
    if (!value) return "—";
    return value.length > 12 ? `${value.slice(0, 12)}…` : value;
  }

  function formatTime(value) {
    if (!value) return "—";
    const parsed = new Date(value);
    return Number.isNaN(parsed.valueOf()) ? value : parsed.toLocaleString("ja-JP", { timeZone: "UTC" }) + " UTC";
  }

  function announce(message) {
    live.textContent = "";
    window.requestAnimationFrame(() => { live.textContent = message; });
  }

  function currentUrl() {
    return new URL(window.location.href);
  }

  function hydrateFilters() {
    const params = currentUrl().searchParams;
    const mappings = [
      ["alert-filter-severity", "severity"], ["alert-filter-state", "state"],
      ["alert-filter-rule", "rule_id"], ["alert-filter-source", "source_surface"],
      ["alert-filter-completeness", "completeness"], ["alert-filter-repository", "repository"],
      ["alert-filter-workspace", "workspace"],
    ];
    for (const [id, key] of mappings) {
      const control = document.getElementById(id);
      if (control && params.has(key)) control.value = params.get(key);
    }
    const period = document.getElementById("alert-filter-period");
    const from = document.getElementById("alert-filter-from");
    const to = document.getElementById("alert-filter-to");
    if (params.has("from") || params.has("to")) {
      period.value = "custom";
      from.value = params.get("from") ?? "";
      to.value = params.get("to") ?? "";
    } else {
      period.value = params.get("period") ?? "30d";
    }
    toggleCustomDates();
  }

  function toggleCustomDates() {
    const custom = document.getElementById("alert-filter-period").value === "custom";
    for (const label of document.querySelectorAll(".alert-custom-date")) label.hidden = !custom;
  }

  function dateNumber(value) {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return null;
    const [year, month, day] = value.split("-").map(Number);
    const parsed = new Date(`${value}T00:00:00Z`);
    return parsed.getUTCFullYear() === year && parsed.getUTCMonth() === month - 1 && parsed.getUTCDate() === day
      ? parsed.valueOf() / 86_400_000
      : null;
  }

  function validateCustomPeriod(announceFailure = false) {
    const from = document.getElementById("alert-filter-from");
    const to = document.getElementById("alert-filter-to");
    from.setCustomValidity("");
    to.setCustomValidity("");
    if (document.getElementById("alert-filter-period").value !== "custom") return true;

    let message = "";
    const fromDay = dateNumber(from.value);
    const toDay = dateNumber(to.value);
    if (fromDay === null || toDay === null) {
      message = "指定期間には開始日と終了日の両方が必要です。";
    } else if (fromDay > toDay) {
      message = "開始日は終了日以前にしてください。";
    } else if (toDay - fromDay >= 366) {
      message = "指定期間は開始日と終了日を含めて 366 日以内にしてください。";
    }
    if (!message) return true;

    to.setCustomValidity(message);
    if (announceFailure) {
      status.hidden = false;
      status.textContent = message;
      announce(message);
      form.reportValidity();
    }
    return false;
  }

  function updateUrlFromFilters() {
    if (!validateCustomPeriod(true)) return false;
    const url = currentUrl();
    const params = url.searchParams;
    const mappings = [
      ["alert-filter-severity", "severity"], ["alert-filter-state", "state"],
      ["alert-filter-rule", "rule_id"], ["alert-filter-source", "source_surface"],
      ["alert-filter-completeness", "completeness"], ["alert-filter-repository", "repository"],
      ["alert-filter-workspace", "workspace"],
    ];
    for (const [id, key] of mappings) {
      const rawValue = document.getElementById(id).value;
      const value = key === "repository" || key === "workspace" ? rawValue : rawValue.trim();
      if (value) params.set(key, value); else params.delete(key);
    }
    const period = document.getElementById("alert-filter-period").value;
    if (period === "custom") {
      params.delete("period");
      const from = document.getElementById("alert-filter-from").value;
      const to = document.getElementById("alert-filter-to").value;
      if (from) params.set("from", from); else params.delete("from");
      if (to) params.set("to", to); else params.delete("to");
    } else {
      params.set("period", period);
      params.delete("from");
      params.delete("to");
    }
    params.delete("alert");
    params.delete("offset");
    window.history.replaceState(null, "", `${url.pathname}?${params.toString()}`);
    return true;
  }

  function apiParameters() {
    const pageParams = currentUrl().searchParams;
    const api = new URLSearchParams();
    const direct = ["session_id", "trace_id", "severity", "state", "rule_id", "source_surface", "repository", "workspace", "completeness", "period", "from", "to", "offset"];
    for (const key of direct) if (pageParams.has(key)) api.set(key, pageParams.get(key));
    if (pageParams.has("alert")) api.set("alert_id", pageParams.get("alert"));
    if (!api.has("period") && !(api.has("from") && api.has("to"))) api.set("period", "30d");
    api.set("limit", "100");
    return api;
  }

  function setDynamicOptions(id, values, label) {
    const select = document.getElementById(id);
    const selected = select.value || currentUrl().searchParams.get(select.name) || "";
    select.replaceChildren();
    const all = node("option", null, label);
    all.value = "";
    select.append(all);
    const options = new Set(values.filter(Boolean));
    if (selected) options.add(selected);
    for (const value of [...options].sort()) {
      const option = node("option", null, value);
      option.value = value;
      select.append(option);
    }
    select.value = selected;
  }

  function valuesText(alert) {
    const observed = alert.observed_values.map(value => `${value.name} ${value.value} ${value.unit}`).join(" · ");
    const thresholds = alert.effective_thresholds.map(value => `${value.name} ${value.value} ${value.unit}`).join(" · ");
    return `${observed || "観測値なし"} / ${thresholds || "閾値なし"}`;
  }

  function renderRows(alerts) {
    rows.replaceChildren();
    for (const alert of alerts) {
      const row = node("tr", `alert-row severity-${alert.severity}`);
      row.classList.toggle("is-selected", alert.alert_id === selectedAlertId);
      row.dataset.alertId = alert.alert_id;
      const severity = node("td");
      const selectButton = node("button", "alert-row-select");
      selectButton.type = "button";
      selectButton.dataset.alertSelect = alert.alert_id;
      selectButton.setAttribute("aria-pressed", alert.alert_id === selectedAlertId ? "true" : "false");
      selectButton.setAttribute("aria-label", `${alert.rule.title ?? alert.rule.rule_id}、Session ${alert.session_id} を選択`);
      selectButton.append(node("span", `monitor-badge alert-severity ${alert.severity}`, alert.severity), node("span", "alert-state", alert.lifecycle.state));
      selectButton.addEventListener("click", event => {
        event.stopPropagation();
        selectAlert(alert.alert_id, true, true);
      });
      severity.append(selectButton);
      const rule = node("td");
      rule.append(node("strong", null, alert.rule.title ?? alert.rule.rule_id), node("span", "monitor-mono alert-cell-sub", `@${alert.rule.rule_version}`));
      const identity = node("td");
      identity.append(node("span", "monitor-mono", shortId(alert.session_id)), node("span", "monitor-mono alert-cell-sub", shortId(alert.trace_id)));
      const values = node("td", "alert-values", valuesText(alert));
      const source = node("td");
      source.append(node("span", null, `${alert.source.surface}@${alert.source.version}`), node("span", "alert-cell-sub", alert.completeness.state));
      const timing = node("td");
      timing.append(
        node("span", null, `初回 ${formatTime(alert.first_observed_at)}`),
        node("span", "alert-cell-sub", `最終 ${formatTime(alert.last_observed_at)} · ${alert.evidence_count} evidence · ${alert.coverage_note}`));
      row.append(severity, rule, identity, values, source, timing);
      row.addEventListener("click", () => selectAlert(alert.alert_id, true, true));
      rows.append(row);
    }
  }

  function definitionList(entries, className = "alert-definition-list") {
    const list = node("dl", className);
    for (const [term, description] of entries) {
      const wrapper = node("div");
      wrapper.append(node("dt", null, term), node("dd", null, description ?? "—"));
      list.append(wrapper);
    }
    return list;
  }

  function renderDetail(alert) {
    detailHeading.textContent = alert.rule.title ?? alert.rule.rule_id;
    detailBody.replaceChildren();

    const badges = node("div", "alert-detail-badges");
    badges.append(
      node("span", `monitor-badge alert-severity ${alert.severity}`, alert.severity),
      node("span", "monitor-badge", alert.lifecycle.state),
      node("span", "monitor-badge", alert.completeness.state),
      node("span", "monitor-badge", alert.source.capability_state));
    const summary = node("p", "alert-summary", alert.summary);
    const formula = node("section", "alert-detail-section");
    formula.append(node("h4", null, "Rule / formula"), node("p", null, alert.rule.formula ?? "unknown rule version"));
    formula.append(definitionList([
      ["Rule", `${alert.rule.rule_id}@${alert.rule.rule_version}`],
      ["Window", alert.rule.evaluation_window],
      ["Contract", alert.rule.contract_state],
      ["Required capabilities", alert.rule.required_capabilities.join(", ") || "none"],
    ]));

    const measurements = node("section", "alert-detail-section");
    measurements.append(node("h4", null, "Observed / threshold"));
    measurements.append(definitionList([
      ...alert.observed_values.map(value => [`observed · ${value.name}`, `${value.value} ${value.unit}`]),
      ...alert.effective_thresholds.map(value => [`threshold · ${value.name}`, `${value.value} ${value.unit}`]),
    ]));

    const provenance = node("section", "alert-detail-section");
    provenance.append(node("h4", null, "Source / scope / completeness"));
    provenance.append(definitionList([
      ["Source", `${alert.source.surface}@${alert.source.version}`],
      ["Capability", alert.source.capability_state],
      ["Session", alert.session_id],
      ["Trace", alert.trace_id],
      ["Scope state", alert.scope.state],
      ["Effective repository", alert.scope.repository ?? "unknown"],
      ["Effective workspace", alert.scope.workspace ?? "unknown"],
      ["Trace repository", alert.scope.trace_repository ?? "unknown"],
      ["Trace workspace", alert.scope.trace_workspace ?? "unknown"],
      ["Session repository", alert.scope.session_repository ?? "unknown"],
      ["Session workspace", alert.scope.session_workspace ?? "unknown"],
      ["Completeness", `${alert.completeness.state}${alert.completeness.reason_codes.length ? ` · ${alert.completeness.reason_codes.join(", ")}` : ""}`],
      ["First / last", `${formatTime(alert.first_observed_at)} / ${formatTime(alert.last_observed_at)}`],
    ]));

    const evidenceSection = node("section", "alert-detail-section");
    evidenceSection.append(node("h4", null, `Evidence (${alert.evidence_count})`));
    const evidenceList = node("ul", "alert-evidence-list");
    for (const evidence of alert.evidence) {
      const item = node("li");
      const label = `${evidence.kind} · ${shortId(evidence.evidence_id)} · ${evidence.availability_state}${evidence.content_state ? ` / ${evidence.content_state}` : ""}`;
      if (evidence.href) {
        const link = node("a", "alert-evidence-link", label);
        link.href = evidence.href;
        item.append(link);
      } else {
        item.append(node("span", null, label));
      }
      evidenceList.append(item);
    }
    if (alert.evidence.length === 0) evidenceList.append(node("li", "empty-state", "Evidence reference はありません。"));
    evidenceSection.append(evidenceList);

    const relationships = node("section", "alert-detail-section");
    relationships.append(node("h4", null, "Lifecycle relationships"));
    relationships.append(definitionList([
      ["Predecessors", alert.relationships.predecessor_alert_ids.join(", ") || "none"],
      ["Successors", alert.relationships.successor_alert_ids.join(", ") || "none"],
      ["Revision", String(alert.lifecycle.revision)],
    ]));

    const history = node("section", "alert-detail-section");
    history.append(node("h4", null, "Lifecycle history"));
    const historyList = node("ol", "alert-lifecycle-history");
    for (const transition of alert.lifecycle.history ?? []) {
      const relationship = transition.old_alert_id || transition.new_alert_id
        ? ` · old ${transition.old_alert_id ?? "none"} / new ${transition.new_alert_id ?? "none"}`
        : "";
      historyList.append(node(
        "li",
        null,
        `revision ${transition.revision} · ${transition.action} · ${transition.previous_state} → ${transition.state} · ${formatTime(transition.occurred_at)} · ${transition.actor} / ${transition.reason_code} · ${transition.result_code}${relationship}`));
    }
    if (!alert.lifecycle.history?.length) historyList.append(node("li", "empty-state", "lifecycle transition はありません。"));
    history.append(historyList);

    detailBody.append(badges, summary, formula, measurements, provenance, evidenceSection, relationships, history);
    renderActions(alert);
  }

  function renderActions(alert) {
    const section = node("section", "alert-detail-section alert-actions");
    section.append(node("h4", null, "Lifecycle actions"));
    if (alert.lifecycle.allowed_actions.length === 0) {
      section.append(node("p", "monitor-subtle", "この状態で許可された操作はありません。"));
      detailBody.append(section);
      return;
    }
    const reasonLabel = node("label", null, "理由");
    const reason = node("select");
    reason.id = "alert-action-reason";
    for (const value of ["user_reviewed", "expected_behavior", "not_actionable", "manual_resolution", "reopened_for_review"]) {
      const option = node("option", null, value);
      option.value = value;
      reason.append(option);
    }
    reasonLabel.append(reason);
    const commentLabel = node("label", null, "コメント（任意・sanitized）");
    const comment = node("input");
    comment.id = "alert-action-comment";
    comment.type = "text";
    comment.maxLength = 256;
    comment.autocomplete = "off";
    commentLabel.append(comment);
    const buttons = node("div", "alert-action-buttons");
    for (const action of alert.lifecycle.allowed_actions) {
      const button = node("button", "monitor-btn", action);
      button.type = "button";
      button.dataset.alertAction = action;
      button.addEventListener("click", () => mutate(alert, action, reason.value, comment.value));
      buttons.append(button);
    }
    section.append(reasonLabel, commentLabel, buttons);
    detailBody.append(section);
  }

  function selectAlert(alertId, focus, updateUrl) {
    if (!snapshot) return;
    const alert = snapshot.alerts.find(item => item.alert_id === alertId);
    if (!alert) return;
    selectedAlertId = alertId;
    for (const row of rows.querySelectorAll(".alert-row")) {
      const selected = row.dataset.alertId === alertId;
      row.classList.toggle("is-selected", selected);
      row.querySelector("[data-alert-select]")?.setAttribute("aria-pressed", selected ? "true" : "false");
    }
    renderDetail(alert);
    if (updateUrl) {
      const url = currentUrl();
      url.searchParams.set("alert", alertId);
      window.history.replaceState(null, "", `${url.pathname}?${url.searchParams.toString()}`);
    }
    announce(`${alert.rule.title ?? alert.rule.rule_id} を選択しました。`);
    if (focus) detailHeading.focus();
  }

  function renderRecurring(groups, snapshotState) {
    recurring.replaceChildren();
    if (groups.length === 0) {
      recurring.append(node("p", "empty-state", snapshotState === "incomplete"
        ? "取得範囲に recurring group はありません。スナップショットが不完全なため、全体として 0 件とは断定できません。"
        : "この期間の recurring group はありません。"));
      return;
    }
    const list = node("div", "alert-recurring-list");
    for (const group of groups) {
      const aggregationState = snapshotState === "incomplete" ? "incomplete_snapshot" : group.aggregation_state;
      const card = node("article", `alert-recurring-card ${aggregationState}`);
      card.append(node("h4", null, `${group.rule_id}@${group.rule_version}`));
      card.append(node("p", "alert-recurring-state", aggregationState === "incomplete_snapshot"
        ? "incomplete_snapshot · bounded data · recurring support is not asserted"
        : `${aggregationState} · ${group.distinct_session_count} Sessions · ${group.occurrence_count} occurrences`));
      card.append(node("p", "monitor-subtle", `${group.repository ?? "repository unknown"} / ${group.workspace ?? "workspace unknown"} · ${group.source_surface}@${group.source_version}`));
      card.append(node("p", "monitor-subtle", `${group.from} — ${group.to} · observation ${group.observation_date}`));
      const distribution = Object.entries(group.completeness_distribution ?? {})
        .filter(([, count]) => Number.isInteger(count) && count > 0)
        .sort(([left], [right]) => left.localeCompare(right));
      const distributionText = distribution.map(([state, count]) => `${state} ${count}`).join(" · ");
      card.append(node(
        "p",
        "monitor-subtle alert-recurring-completeness",
        distribution.length > 1
          ? `mixed completeness · ${distributionText}`
          : `completeness · ${distributionText || "unknown"}`));
      list.append(card);
    }
    recurring.append(list);
  }

  function renderCoverage(facts, coverageState) {
    coverage.replaceChildren();
    coverage.append(node("p", "monitor-subtle retention-diagnostics-help", "以下の suppression fact はアラートではありません。"));
    if (coverageState === "incomplete") {
      coverage.append(node("p", "monitor-subtle", "coverage の取得上限に達しました。省略件数は不明です。"));
    }
    if (facts.length === 0) {
      coverage.append(node("p", "empty-state", coverageState === "incomplete"
        ? "取得範囲で suppression fact は確認できません。0 件とは断定できません。"
        : "suppression fact はありません。"));
      return;
    }
    const list = node("ul", "alert-coverage-list");
    for (const fact of facts) {
      const item = node("li");
      item.append(node("strong", null, `${fact.rule_id}@${fact.rule_version}`));
      item.append(node("span", "monitor-mono", fact.code));
      item.append(node("span", "monitor-subtle", fact.missing_capabilities.length ? `missing: ${fact.missing_capabilities.join(", ")}` : "missing: none"));
      item.append(node("span", "monitor-subtle", fact.context_state === "unknown"
        ? "context unknown"
        : `${fact.source_surface}@${fact.source_version} · ${fact.session_id} · ${fact.observation_date}`));
      list.append(item);
    }
    coverage.append(list);
  }

  async function mutate(alert, action, reasonCode, comment) {
    const buttons = [...document.querySelectorAll("[data-alert-action]")];
    buttons.forEach(button => { button.disabled = true; });
    try {
      const response = await fetch(`/api/alerts/v1/${encodeURIComponent(alert.alert_id)}/lifecycle/actions`, {
        method: "POST",
        cache: "no-store",
        headers: {
          "Content-Type": "application/json",
          "x-monitor-csrf": "local-monitor",
          "Idempotency-Key": idempotencyKey(),
        },
        body: JSON.stringify({
          schema_version: "alert.lifecycle.v1",
          action,
          expected_revision: alert.lifecycle.revision,
          reason_code: reasonCode,
          comment: comment.trim() || null,
        }),
      });
      if (response.ok) {
        const reloaded = await load(false);
        announce(reloaded
          ? `${action} で更新しました。`
          : `${action} は受理されましたが、最新状態を再読み込みできませんでした。`);
        return;
      }
      let code = "unknown_error";
      try { code = (await response.json()).error ?? code; } catch { /* fixed fallback */ }
      if (response.status === 409 && code === "alert_revision_conflict") {
        const reloaded = await load(false);
        announce(reloaded
          ? "更新が競合しました。最新状態を再読み込みしました。"
          : "更新が競合し、最新状態を再読み込みできませんでした。");
      } else {
        announce(`操作に失敗しました: ${code}`);
      }
    } catch {
      announce("操作 API に接続できませんでした。");
    } finally {
      buttons.forEach(button => { button.disabled = false; });
    }
  }

  function idempotencyKey() {
    const bytes = new Uint8Array(32);
    window.crypto.getRandomValues(bytes);
    let binary = "";
    for (const value of bytes) binary += String.fromCharCode(value);
    return `aid1_${window.btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "")}`;
  }

  function renderPagination(value) {
    const offset = Number.isInteger(value.query?.offset) ? value.query.offset : 0;
    const limit = Number.isInteger(value.query?.limit) ? value.query.limit : 100;
    const total = Number.isInteger(value.total_count) ? value.total_count : 0;
    const first = value.alerts.length === 0 ? 0 : offset + 1;
    const last = Math.min(total, offset + value.alerts.length);
    pageInfo.textContent = `${first}–${last} / ${total} 件`;
    previousPage.disabled = offset === 0;
    previousPage.dataset.offset = String(Math.max(0, offset - limit));
    nextPage.disabled = offset + limit >= total;
    nextPage.dataset.offset = String(offset + limit);
  }

  function setPageOffset(offset) {
    const url = currentUrl();
    if (offset === 0) url.searchParams.delete("offset"); else url.searchParams.set("offset", String(offset));
    url.searchParams.delete("alert");
    selectedAlertId = null;
    window.history.replaceState(null, "", `${url.pathname}?${url.searchParams.toString()}`);
    load(true);
  }

  async function load(announceSelection = false) {
    const generation = ++loadGeneration;
    loadController?.abort();
    const controller = new AbortController();
    loadController = controller;
    status.hidden = false;
    status.textContent = "アラートを読み込んでいます。";
    error.hidden = true;
    empty.hidden = true;
    try {
      const response = await fetch(`/api/alert-center/v1/alerts?${apiParameters().toString()}`, { cache: "no-store", signal: controller.signal });
      if (!response.ok) throw new Error("api");
      const value = await response.json();
      if (generation !== loadGeneration) return false;
      snapshot = value;
      setDynamicOptions("alert-filter-rule", [
        ...snapshot.alerts.map(item => item.rule.rule_id),
        ...snapshot.coverage.map(item => item.rule_id),
      ], "すべて");
      setDynamicOptions("alert-filter-source", [
        ...snapshot.alerts.map(item => item.source.surface),
        ...snapshot.coverage.map(item => item.source_surface),
      ], "すべて");
      renderPagination(snapshot);
      const offset = snapshot.query?.offset ?? 0;
      const first = snapshot.alerts.length === 0 ? 0 : offset + 1;
      const last = Math.min(snapshot.total_count, offset + snapshot.alerts.length);
      const range = `${first}–${last} / ${snapshot.total_count} 件`;
      document.getElementById("alert-count").textContent = snapshot.snapshot_state === "incomplete"
        ? `${range} · incomplete · acquired range · omitted unknown`
        : `${range} · complete snapshot`;
      renderRecurring(snapshot.recurring_groups, snapshot.snapshot_state);
      renderCoverage(snapshot.coverage, snapshot.coverage_state);
      if (snapshot.alerts.length === 0) {
        rows.replaceChildren();
        detailBody.replaceChildren();
        content.hidden = snapshot.total_count === 0;
        empty.hidden = false;
        empty.textContent = snapshot.total_count > 0
          ? "このページにアラートはありません。前のページに戻ってください。"
          : snapshot.snapshot_state === "incomplete"
          ? "取得できた範囲に条件一致のアラートはありません。スナップショットが不完全なため、全体として 0 件とは断定できません。"
          : "この条件のアラートはありません。";
        status.hidden = true;
        return true;
      }
      const requested = currentUrl().searchParams.get("alert");
      const retained = snapshot.alerts.some(item => item.alert_id === selectedAlertId) ? selectedAlertId : null;
      selectedAlertId = snapshot.alerts.some(item => item.alert_id === requested) ? requested : retained ?? snapshot.alerts[0].alert_id;
      renderRows(snapshot.alerts);
      content.hidden = false;
      empty.hidden = true;
      status.hidden = true;
      selectAlert(selectedAlertId, false, false);
      if (announceSelection) announce("フィルター結果を更新しました。");
      return true;
    } catch (caught) {
      if (generation !== loadGeneration || caught?.name === "AbortError") return false;
      snapshot = null;
      rows.replaceChildren();
      detailBody.replaceChildren();
      content.hidden = true;
      empty.hidden = true;
      status.hidden = true;
      error.hidden = false;
      previousPage.disabled = true;
      nextPage.disabled = true;
      pageInfo.textContent = "ページ情報を取得できませんでした。";
      recurring.replaceChildren(node("p", "empty-state", "集計を読み込めませんでした。"));
      coverage.replaceChildren(node("p", "empty-state", "coverage を読み込めませんでした。"));
      return false;
    } finally {
      if (generation === loadGeneration) loadController = null;
    }
  }

  form.addEventListener("submit", event => {
    event.preventDefault();
    if (updateUrlFromFilters()) load(true);
  });
  document.getElementById("alert-filter-period").addEventListener("change", () => {
    toggleCustomDates();
    if (document.getElementById("alert-filter-period").value !== "custom") {
      if (updateUrlFromFilters()) load(true);
    }
  });
  for (const id of ["alert-filter-from", "alert-filter-to"]) {
    document.getElementById(id).addEventListener("input", () => {
      document.getElementById("alert-filter-from").setCustomValidity("");
      document.getElementById("alert-filter-to").setCustomValidity("");
    });
  }
  for (const id of ["alert-filter-severity", "alert-filter-state", "alert-filter-rule", "alert-filter-source", "alert-filter-completeness"]) {
    document.getElementById(id).addEventListener("change", () => {
      if (updateUrlFromFilters()) load(true);
    });
  }
  previousPage.addEventListener("click", () => setPageOffset(Number(previousPage.dataset.offset)));
  nextPage.addEventListener("click", () => setPageOffset(Number(nextPage.dataset.offset)));

  hydrateFilters();
  if (validateCustomPeriod(true)) load(false);
})();
