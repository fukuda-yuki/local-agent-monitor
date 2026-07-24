(() => {
  "use strict";

  const root = document.getElementById("cost-root");
  if (!root) return;

  const byId = id => document.getElementById(id);
  const sessionId = root.dataset.sessionId || null;
  const estimateId = root.dataset.estimateId || null;
  const status = byId("cost-status");
  const live = byId("cost-live");
  const incomplete = byId("cost-incomplete");
  const error = byId("cost-error");
  class HttpResponseError extends Error {}
  const state = {
    generation: 0,
    controller: null,
    configuration: null,
    catalog: null,
    analytics: null,
    estimateHistory: null,
    attemptHistory: null,
    exactEstimate: null,
    preview: null,
    mutation: Promise.resolve(),
    hydratedConfigurationId: null,
    contextFocused: false,
  };

  function node(tag, className, text) {
    const result = document.createElement(tag);
    if (className) result.className = className;
    if (text !== undefined && text !== null) result.textContent = String(text);
    return result;
  }

  function announce(text) {
    live.textContent = text;
  }

  function dateText(value) {
    return value ? String(value).replace("T", " ").replace(".0000000Z", " UTC") : "—";
  }

  function amount(value, currency) {
    return value === null || value === undefined ? "—" : `${value} ${currency ?? "currency unknown"}`;
  }

  function percent(basisPoints) {
    return basisPoints === null || basisPoints === undefined
      ? "coverage unavailable"
      : `${(basisPoints / 100).toFixed(2)}%`;
  }

  function definitionList(entries) {
    const list = node("dl", "cost-definition-list");
    for (const [term, value] of entries) {
      const row = node("div");
      row.append(node("dt", null, term), node("dd", null, value ?? "—"));
      list.append(row);
    }
    return list;
  }

  function nextGeneration() {
    state.generation += 1;
    state.controller?.abort();
    state.controller = new AbortController();
    return { generation: state.generation, signal: state.controller.signal };
  }

  async function getJson(url, request) {
    const response = await fetch(url, {
      cache: "no-store",
      credentials: "same-origin",
      signal: request.signal,
      headers: { Accept: "application/json" },
    });
    if (!response.ok) throw await responseError(response);
    const value = await response.json();
    if (request.generation !== state.generation) throw new DOMException("Superseded", "AbortError");
    return value;
  }

  async function responseError(response) {
    try {
      const body = await response.json();
      return new HttpResponseError(body.error || `cost_http_${response.status}`);
    } catch {
      return new HttpResponseError(`cost_http_${response.status}`);
    }
  }

  function currentRange() {
    const now = new Date();
    const to = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1));
    const from = new Date(to);
    from.setUTCDate(from.getUTCDate() - 30);
    return {
      from: byId("cost-filter-from").value || from.toISOString().slice(0, 10),
      to: byId("cost-filter-to").value || to.toISOString().slice(0, 10),
    };
  }

  function exactUtc(date) {
    return `${date}T00:00:00.0000000Z`;
  }

  function analyticsUrl(after = null) {
    const range = currentRange();
    const query = new URLSearchParams({
      from: exactUtc(range.from),
      to: exactUtc(range.to),
      limit: "50",
    });
    const fields = [
      ["source_surface", "cost-filter-source"],
      ["model", "cost-filter-model"],
      ["billing_mode", "cost-filter-mode"],
      ["status", "cost-filter-status"],
      ["registry_version", "cost-filter-registry"],
    ];
    for (const [name, id] of fields) {
      const value = byId(id).value;
      if (value) query.set(name, value);
    }
    if (after) query.set("after", after);
    return `/api/costs/v1/analytics?${query}`;
  }

  function initializeDates() {
    const range = currentRange();
    byId("cost-filter-from").value = range.from;
    byId("cost-filter-to").value = range.to;
  }

  async function loadAll() {
    const request = nextGeneration();
    root.setAttribute("aria-busy", "true");
    root.dataset.readState = "loading";
    status.textContent = "Cost analytics を読み込んでいます。";
    error.hidden = true;
    incomplete.hidden = true;
    try {
      const reads = [
        getJson("/api/costs/v1/configuration", request),
        getJson(catalogUrl(), request),
        getJson(analyticsUrl(), request),
      ];
      if (sessionId) {
        reads.push(getJson(`/api/costs/v1/sessions/${encodeURIComponent(sessionId)}/estimates?limit=100`, request));
        reads.push(getJson(`/api/costs/v1/sessions/${encodeURIComponent(sessionId)}/recalculations?limit=100`, request));
        if (estimateId) reads.push(getJson(`/api/costs/v1/sessions/${encodeURIComponent(sessionId)}/estimates/${encodeURIComponent(estimateId)}`, request));
      }
      const values = await Promise.all(reads);
      if (request.generation !== state.generation) return;
      [state.configuration, state.catalog, state.analytics] = values;
      if (sessionId) {
        state.estimateHistory = values[3];
        state.attemptHistory = values[4];
        state.exactEstimate = estimateId ? values[5] : null;
      }
      renderConfiguration();
      renderCatalog();
      renderAnalytics();
      renderSession();
      status.textContent = "Cost analytics を更新しました。";
      root.setAttribute("aria-busy", "false");
      root.dataset.readState = "fresh";
      if (estimateId && !state.contextFocused) {
        state.contextFocused = true;
        byId("cost-context-heading").focus();
        announce("exact estimate を読み込みました。");
      } else {
        announce(state.analytics.state === "incomplete"
          ? "incomplete snapshot を読み込みました。完全な totals は保留しています。"
          : "Cost analytics を読み込みました。");
      }
    } catch (failure) {
      if (failure?.name === "AbortError") return;
      root.setAttribute("aria-busy", "false");
      root.dataset.readState = "stale";
      error.hidden = false;
      status.textContent = "前回表示した facts は stale の可能性があります。";
      error.textContent = `Cost analytics を読み込めませんでした · ${failure.message}`;
      announce("Cost analytics の読み込みに失敗しました。");
    }
  }

  function renderConfiguration() {
    const value = state.configuration;
    if (!value) return;
    state.preview = null;
    byId("cost-commit").disabled = true;
    byId("cost-config-state").textContent =
      `head ${value.head_revision ?? "none"} · catalog ${value.catalog_state ?? "unknown"}`;
    const budgets = value.configuration?.budget_entries ?? [];
    const budgetState = byId("cost-budget-state");
    budgetState.replaceChildren(node("strong", null, "Active budget states"));
    for (const ruleId of [
      "session-estimated-cost-threshold",
      "daily-estimated-cost-threshold",
      "period-estimated-cost-threshold",
    ]) {
      const budget = budgets.find(item => item.rule_id === ruleId);
      budgetState.append(node("p", "monitor-subtle", budget
        ? `${ruleId} · ${budget.enabled ? "enabled" : "disabled"} · ${budget.currency} · warning ${budget.warning_threshold} / critical ${budget.critical_threshold} · coverage ${budget.minimum_coverage_basis_points} bps · ${budget.scope_kind}${budget.window_days ? ` ${budget.window_days} days` : ""}`
        : `${ruleId} · disabled (not configured)`));
    }
    const configurationId = value.configuration?.configuration_id ?? "none";
    if (configurationId !== state.hydratedConfigurationId) {
      state.hydratedConfigurationId = configurationId;
      hydrateConfigurationForm(value.configuration);
    }
    updateRecalculationAvailability();
  }

  function hydrateConfigurationForm(configuration) {
    const sources = configuration?.source_entries ?? [];
    const source = sources[0] ?? null;
    byId("cost-config-surface").value = source?.source_surface ?? "";
    byId("cost-config-version").value = source?.application_version ?? "";
    byId("cost-config-adapter").value = source?.adapter_capability_version ?? "";
    byId("cost-config-provider").value = source?.provider ?? "";
    byId("cost-config-mode").value = source?.billing_mode ?? "";
    byId("cost-config-route").value = source?.pricing_route ?? "";
    const budgetMap = new Map((configuration?.budget_entries ?? []).map(item => [item.rule_id, item]));
    hydrateBudget("cost-budget", budgetMap.get("session-estimated-cost-threshold"));
    hydrateBudget("cost-budget-daily", budgetMap.get("daily-estimated-cost-threshold"));
    hydrateBudget("cost-budget-period", budgetMap.get("period-estimated-cost-threshold"));
  }

  function hydrateBudget(prefix, budget) {
    byId(`${prefix}-enabled`).checked = Boolean(budget);
    byId(`${prefix}-active`).checked = budget?.enabled ?? true;
    if (!budget) return;
    byId(`${prefix}-warning`).value = budget.warning_threshold;
    byId(`${prefix}-critical`).value = budget.critical_threshold;
    byId(`${prefix}-coverage`).value = String(budget.minimum_coverage_basis_points);
    if (prefix === "cost-budget-period" && budget.window_days) {
      byId("cost-budget-period-days").value = String(budget.window_days);
    }
  }

  function renderCatalog() {
    const container = byId("cost-catalog");
    container.replaceChildren();
    const sources = state.catalog?.sources ?? [];
    const entries = state.catalog?.entries ?? [];
    if (!sources.length && !entries.length) {
      container.append(node("p", "empty-state", "安全な catalog projection はありません。"));
    }
    for (const item of sources.slice(0, 64)) {
      const card = node("article", "cost-catalog-item");
      card.append(
        node("strong", null, `${item.source_kind} · ${item.source_label}`),
        node("span", "monitor-mono", item.registry_version),
        node("span", null, `reviewed ${item.last_reviewed_date ?? "—"} · stale after ${item.stale_after_date ?? "—"}`),
      );
      container.append(card);
    }
    const selector = byId("cost-config-entry");
    const previous = selector.value;
    selector.replaceChildren(node("option", null, "既存 mapping を維持"));
    selector.firstElementChild.value = "";
    for (const item of entries.slice(0, 100)) {
      const card = node("article", "cost-catalog-item");
      card.append(
        node("strong", null, `${item.source_kind} · ${item.source_label}`),
        node("span", "monitor-mono", `${item.registry_version} · ${item.provider} · ${item.model}`),
        node("span", null, `${item.billing_mode} / ${item.pricing_route} · ${item.currency} · ${item.selection_state}`),
        node("span", null, `${dateText(item.effective_from_utc)} — ${dateText(item.effective_to_utc)}`),
        node("span", null, `source reference (inert text) · ${item.source_reference ?? "not exposed"}`),
      );
      container.append(card);
      const option = node("option", null,
        `${item.source_label} · ${item.model} · ${item.billing_mode} / ${item.pricing_route}`);
      option.value = item.entry_key;
      selector.append(option);
    }
    if ([...selector.options].some(option => option.value === previous)) selector.value = previous;
    if (!selector.value) {
      const source = state.configuration?.configuration?.source_entries?.[0];
      const matching = source && entries.find(item =>
        item.provider === source.provider
        && item.billing_mode === source.billing_mode
        && item.pricing_route === source.pricing_route);
      if (matching) selector.value = matching.entry_key;
    }
    const next = byId("cost-catalog-next");
    next.hidden = !state.catalog?.next_after;
    next.dataset.cursor = state.catalog?.next_after ?? "";
  }

  function renderAnalytics() {
    const value = state.analytics;
    if (!value) return;
    if (value.state === "incomplete") {
      byId("cost-range-total-list").replaceChildren();
      byId("cost-daily-list").replaceChildren();
      const next = byId("cost-groups-next");
      next.hidden = true;
      next.dataset.cursor = "";
      incomplete.hidden = false;
      const lower = value.eligible_session_lower_bound ?? value.group_lower_bound;
      incomplete.textContent =
        `incomplete · ${value.cap_reason} · 少なくとも ${Number(lower).toLocaleString("ja-JP")} 件。`
        + " 取得範囲外を含むため、global total・0 件・latest・top は断定しません。";
      byId("cost-overall").replaceChildren(
        node("div", "panel-head", null),
        node("p", "empty-state", "Coverage と全体件数は取得上限のため保留しています。"),
      );
      byId("cost-overall").firstElementChild.append(
        Object.assign(node("h3", "panel-title", "Coverage"), { id: "cost-overall-heading" }));
      byId("cost-range-totals").hidden = true;
      byId("cost-daily-trend").hidden = true;
      byId("cost-group-rows").replaceChildren();
      return;
    }

    incomplete.hidden = true;
    byId("cost-range-totals").hidden = false;
    byId("cost-daily-trend").hidden = false;
    const overall = value.overall;
    const overallCard = byId("cost-overall");
    overallCard.replaceChildren();
    const head = node("div", "panel-head");
    head.append(
      Object.assign(node("h3", "panel-title", "Coverage"), { id: "cost-overall-heading" }),
      node("span", "panel-meta", "estimated / eligible Sessions"));
    overallCard.append(
      head,
      node("p", "cost-coverage-value", `${overall.coverage_numerator} / ${overall.coverage_denominator}`),
      node("p", "monitor-subtle", percent(overall.coverage_basis_points)),
      definitionList([
        ["estimated", overall.estimated_session_count],
        ["partial", overall.partial_session_count],
        ["not-estimable", overall.not_estimable_session_count],
        ["missing", overall.missing_session_count],
        ["failed", overall.failed_session_count],
        ["unavailable", overall.unavailable_session_count],
        ["stale", overall.stale_session_count],
      ]),
    );

    const totals = byId("cost-range-total-list");
    totals.replaceChildren();
    for (const total of value.range_totals ?? []) {
      const card = node("article", "cost-total-row");
      card.append(
        node("strong", null, `${total.registry_version ?? "registry unknown"} · ${total.currency ?? "currency unknown"}`),
        node("span", null, `estimated · ${amount(total.estimated_amount, total.currency)} · ${total.estimated_amount_state}`),
        node("span", "cost-provisional", `partial provisional · ${amount(total.partial_known_component_amount, total.currency)} · ${total.partial_known_component_amount_state}`),
      );
      const reasons = (total.partial_reason_counts ?? []).map(item => `${item.reason}: ${item.session_count}`).join(", ");
      if (reasons) card.append(node("span", "monitor-subtle", reasons));
      totals.append(card);
    }
    if (!totals.children.length) totals.append(node("p", "empty-state", "この範囲に monetary total はありません。"));

    const daily = byId("cost-daily-list");
    daily.replaceChildren();
    for (const item of value.daily_totals ?? []) {
      const row = node("article", "cost-trend-row");
      row.append(
        node("strong", null, `${item.utc_date} UTC`),
        node("span", null, `${item.registry_version ?? "registry unknown"} · ${item.currency ?? "currency unknown"}`),
        node("span", null, `estimated ${amount(item.estimated_amount, item.currency)}`),
        node("span", "cost-provisional", `partial provisional ${amount(item.partial_known_component_amount, item.currency)}`),
      );
      daily.append(row);
    }
    if (!daily.children.length) daily.append(node("p", "empty-state", "daily monetary trend はありません。"));
    renderGroups(value.groups ?? []);
    const next = byId("cost-groups-next");
    next.hidden = !value.next_cursor;
    next.dataset.cursor = value.next_cursor ?? "";
  }

  function renderGroups(groups) {
    const rows = byId("cost-group-rows");
    rows.replaceChildren();
    for (const group of groups.slice(0, 100)) {
      const row = node("tr");
      const cells = [
        `${group.utc_date} UTC\n${group.source_surface}`,
        `${group.provider ?? "unknown"} / ${group.model ?? "unknown"} / ${group.billing_mode ?? "unknown"}`,
        `${group.registry_version ?? "unknown"} / ${group.component_category ?? "unknown"}`,
        `${group.estimated_session_count}/${group.eligible_session_count} · ${percent(group.coverage_basis_points)}`,
        `${amount(group.estimated_amount, group.currency)} · ${group.estimated_amount_state}`,
        `${amount(group.partial_known_component_amount, group.currency)} · ${group.partial_known_component_amount_state}\n${(group.partial_reason_counts ?? []).map(item => item.reason).join(", ")}`,
      ];
      for (const text of cells) row.append(node("td", null, text));
      rows.append(row);
    }
    if (!groups.length) {
      const row = node("tr");
      const cell = node("td", "empty-state", "この条件の component group はありません。");
      cell.colSpan = 6;
      row.append(cell);
      rows.append(row);
    }
  }

  function renderSession() {
    const section = byId("cost-session");
    if (!sessionId) {
      section.hidden = true;
      return;
    }
    section.hidden = false;
    byId("cost-context-id").textContent = sessionId;
    byId("cost-session-alerts").href = `/alerts?session_id=${encodeURIComponent(sessionId)}`;
    renderEstimate(state.exactEstimate?.item ?? state.estimateHistory?.items?.[0] ?? null);
    renderEstimateHistory();
    renderAttempts();
  }

  function renderEstimate(item) {
    const container = byId("cost-exact-estimate");
    container.replaceChildren();
    const history = state.estimateHistory;
    if (!item) {
      container.append(node("p", "empty-state", `${history?.calculation_state ?? "not_calculated"} · monetary zero ではありません。`));
      return;
    }
    const registry = item.registry;
    container.append(
      definitionList([
        ["status / freshness", `${item.estimate_status} / ${item.freshness}`],
        ["amount", `${item.amount_kind} · ${amount(item.amount, item.currency)}`],
        ["provider / model", `${item.provider ?? "unknown"} / ${item.model ?? "unknown"}`],
        ["billing / route", `${item.billing_mode ?? "unknown"} / ${item.pricing_route ?? "unknown"}`],
        ["registry", registry ? `${registry.registry_version} · ${registry.source_kind} · ${registry.source_label}` : "not selected"],
        ["effective", registry ? `${dateText(registry.effective_from_utc)} — ${dateText(registry.effective_to_utc)}` : "—"],
        ["source reference (inert text)", registry?.source_reference ?? "not exposed"],
        ["coverage", `${item.coverage?.estimated_categories?.length ?? 0}/${item.coverage?.required_categories?.length ?? 0} categories`],
        ["missing", (item.coverage?.missing_categories ?? []).join(", ") || "none"],
        ["reasons", (item.reasons ?? []).join(", ") || "none"],
        ["delta", `${item.delta?.state ?? "not_applicable"} · ${amount(item.delta?.amount, item.delta?.currency)} · ${(item.delta?.changed_fields ?? []).join(", ")}`],
        ["disclaimer", item.disclaimer],
      ]),
    );
    const components = node("ul", "cost-component-list");
    for (const component of item.components ?? []) {
      components.append(node("li", null,
        `${component.category} · ${component.state} · ${amount(component.amount, item.currency)} · ${component.missing_reason ?? "complete"}`));
    }
    container.append(components);
  }

  function renderEstimateHistory() {
    const container = byId("cost-estimate-history");
    container.replaceChildren();
    const items = state.estimateHistory?.items ?? [];
    for (const item of items.slice(0, 100)) {
      const card = node("article", "cost-history-item");
      card.append(
        node("strong", null, `revision ${item.head_revision} · ${item.estimate_status} · ${item.freshness}`),
        node("span", null, `${item.amount_kind} · ${amount(item.amount, item.currency)}`),
        node("span", null, `delta ${item.delta?.state ?? "not_applicable"} · ${amount(item.delta?.amount, item.delta?.currency)}`),
      );
      container.append(card);
    }
    if (!items.length) container.append(node("p", "empty-state", "immutable estimate history はありません。"));
    const next = byId("cost-estimates-next");
    next.hidden = !state.estimateHistory?.next_after;
    next.dataset.cursor = state.estimateHistory?.next_after ?? "";
  }

  function renderAttempts() {
    const container = byId("cost-attempt-history");
    container.replaceChildren();
    const active = state.attemptHistory?.active;
    const attempts = state.attemptHistory?.attempts ?? [];
    const values = active ? [active, ...attempts] : attempts;
    for (const item of values.slice(0, 100)) {
      const card = node("article", "cost-history-item");
      const link = node("a", "panel-link", `run ${item.run_id}`);
      link.href = item.recalculation_href;
      card.append(
        node("strong", null, `${item.state ?? item.kind} · ${item.freshness}`),
        node("span", null, `${item.estimate_status ?? item.code ?? "pending"}`),
        link,
      );
      container.append(card);
    }
    const latest = state.estimateHistory?.latest_attempt;
    if (latest) container.append(node("p", "monitor-subtle", `latest attempt · ${latest.kind} · ${latest.freshness} · ${latest.code ?? latest.estimate_status ?? "pending"}`));
    if (!values.length && !latest) container.append(node("p", "empty-state", "recalculation attempt はありません。"));
    const next = byId("cost-attempts-next");
    next.hidden = !state.attemptHistory?.next_after;
    next.dataset.cursor = state.attemptHistory?.next_after ?? "";
  }

  function catalogUrl(after = null) {
    const query = new URLSearchParams({ limit: "100" });
    if (after) query.set("after", after);
    return `/api/costs/v1/catalog?${query}`;
  }

  async function replacePage(url, assign, render, announcement, focusId) {
    const request = nextGeneration();
    try {
      assign(await getJson(url, request));
      render();
      byId(focusId).focus();
      announce(announcement);
    } catch (failure) {
      if (failure?.name !== "AbortError") announce(`page transition failed · ${failure.message}`);
    }
  }

  function loadNextCatalog() {
    const after = byId("cost-catalog-next").dataset.cursor;
    if (!after) return;
    return replacePage(
      catalogUrl(after),
      value => { state.catalog = value; },
      renderCatalog,
      "次の catalog page を読み込みました。",
      "cost-config-heading");
  }

  function loadNextEstimates() {
    const after = byId("cost-estimates-next").dataset.cursor;
    if (!after || !sessionId) return;
    return replacePage(
      `/api/costs/v1/sessions/${encodeURIComponent(sessionId)}/estimates?limit=100&after=${encodeURIComponent(after)}`,
      value => { state.estimateHistory = value; },
      renderSession,
      "次の estimate history page を読み込みました。",
      "cost-estimate-history-heading");
  }

  function loadNextAttempts() {
    const after = byId("cost-attempts-next").dataset.cursor;
    if (!after || !sessionId) return;
    return replacePage(
      `/api/costs/v1/sessions/${encodeURIComponent(sessionId)}/recalculations?limit=100&after=${encodeURIComponent(after)}`,
      value => { state.attemptHistory = value; },
      renderSession,
      "次の recalculation history page を読み込みました。",
      "cost-attempt-history-heading");
  }

  async function loadNextGroups() {
    const cursor = byId("cost-groups-next").dataset.cursor;
    if (!cursor) return;
    const request = nextGeneration();
    try {
      state.analytics = await getJson(analyticsUrl(cursor), request);
      renderAnalytics();
      byId("cost-groups-heading").focus();
      announce("次の component group ページを読み込みました。");
    } catch (failure) {
      if (failure?.name !== "AbortError") announce(`group page の読み込み失敗 · ${failure.message}`);
    }
  }

  function mutation(task) {
    state.mutation = state.mutation.then(task, task);
    return state.mutation;
  }

  async function postExact(url, body, replayOnTransport = false) {
    const send = async () => {
      const response = await fetch(url, {
        method: "POST",
        cache: "no-store",
        credentials: "same-origin",
        headers: {
          "Content-Type": "application/json",
          "x-monitor-csrf": "local-monitor",
          Accept: "application/json",
        },
        body,
      });
      if (!response.ok) throw await responseError(response);
      return response.json();
    };
    try {
      return await send();
    } catch (failure) {
      if (!replayOnTransport || failure instanceof HttpResponseError) throw failure;
      return send();
    }
  }

  function sourceEntries() {
    if (byId("cost-config-clear-sources").checked) return [];
    const current = (state.configuration?.configuration?.source_entries ?? [])
      .map(item => ({ ...item }));
    const sourceSurface = byId("cost-config-surface").value;
    const applicationVersion = byId("cost-config-version").value;
    const adapter = byId("cost-config-adapter").value;
    const provider = byId("cost-config-provider").value;
    const mode = byId("cost-config-mode").value;
    const route = byId("cost-config-route").value;
    const editor = [sourceSurface, applicationVersion, adapter, provider, mode, route];
    if (editor.some(Boolean) && !editor.every(Boolean)) {
      throw new Error("source mapping は surface/version/adapter/catalog entry をすべて指定してください。");
    }
    if (!editor.every(Boolean)) {
      return current;
    }
    const edited = {
      source_surface: byId("cost-config-surface").value,
      application_version: byId("cost-config-version").value,
      adapter_capability_version: byId("cost-config-adapter").value,
      provider: byId("cost-config-provider").value,
      billing_mode: byId("cost-config-mode").value,
      pricing_route: byId("cost-config-route").value,
    };
    return current
      .filter(item =>
        item.source_surface !== edited.source_surface
        || item.application_version !== edited.application_version)
      .concat(edited)
      .sort((left, right) =>
        compareOrdinal(left.source_surface, right.source_surface)
        || compareOrdinal(left.application_version, right.application_version));
  }

  function compareOrdinal(left, right) {
    return left < right ? -1 : left > right ? 1 : 0;
  }

  function budgetEntries() {
    const values = [];
    if (byId("cost-budget-enabled").checked) {
      values.push({
        rule_id: "session-estimated-cost-threshold",
        rule_version: "1",
        enabled: byId("cost-budget-active").checked,
        currency: "USD",
        warning_threshold: byId("cost-budget-warning").value,
        critical_threshold: byId("cost-budget-critical").value,
        minimum_coverage_basis_points: Number(byId("cost-budget-coverage").value),
        scope_kind: "session",
        window_days: null,
      });
    }
    if (byId("cost-budget-daily-enabled").checked) {
      values.push({
        rule_id: "daily-estimated-cost-threshold",
        rule_version: "1",
        enabled: byId("cost-budget-daily-active").checked,
        currency: "USD",
        warning_threshold: byId("cost-budget-daily-warning").value,
        critical_threshold: byId("cost-budget-daily-critical").value,
        minimum_coverage_basis_points: Number(byId("cost-budget-daily-coverage").value),
        scope_kind: "utc_day",
        window_days: null,
      });
    }
    if (byId("cost-budget-period-enabled").checked) {
      values.push({
        rule_id: "period-estimated-cost-threshold",
        rule_version: "1",
        enabled: byId("cost-budget-period-active").checked,
        currency: "USD",
        warning_threshold: byId("cost-budget-period-warning").value,
        critical_threshold: byId("cost-budget-period-critical").value,
        minimum_coverage_basis_points: Number(byId("cost-budget-period-coverage").value),
        scope_kind: "rolling_period",
        window_days: Number(byId("cost-budget-period-days").value),
      });
    }
    return values;
  }

  async function previewConfiguration(event) {
    event.preventDefault();
    await mutation(async () => {
      setMutationDisabled(true);
      const requestedGeneration = state.generation;
      try {
        const request = {
          schema_version: "cost.configuration-preview-request.v1",
          source_entries: sourceEntries(),
          budget_entries: budgetEntries(),
        };
        const preview = await postExact(
          "/api/costs/v1/configuration/preview",
          JSON.stringify(request));
        if (requestedGeneration !== state.generation) {
          state.preview = null;
          byId("cost-preview-result").replaceChildren();
          announce("configuration preview response was superseded by a newer read.");
          return;
        }
        state.preview = preview;
        byId("cost-preview-result").replaceChildren(
          definitionList([
            ["Preview", state.preview.preview_digest],
            ["Proposed match", `${state.preview.proposed_match_count} Sessions`],
            ["Current match", `${state.preview.current_match_count} · ${state.preview.current_match_count_state}`],
            ["Overlap", `${state.preview.overlap_count} · ${state.preview.overlap_count_state}`],
            ["Expected head", `${state.preview.expected_head_revision} · ${state.preview.expected_configuration_id ?? "none"}`],
            ["Created", dateText(state.preview.configuration?.created_at_utc)],
          ]));
        byId("cost-commit").disabled = false;
        announce("configuration preview を作成しました。commit 前です。");
      } catch (failure) {
        state.preview = null;
        if (requestedGeneration === state.generation) {
          byId("cost-preview-result").textContent = `preview failed · ${failure.message}`;
          announce("configuration preview に失敗しました。");
        }
      } finally {
        setMutationDisabled(false);
        byId("cost-commit").disabled = !state.preview;
      }
    });
  }

  async function commitConfiguration() {
    if (!state.preview) return;
    await mutation(async () => {
      setMutationDisabled(true);
      const requestedGeneration = state.generation;
      const preview = state.preview;
      const commit = { schema_version: "cost.configuration-commit.v1" };
      for (const [key, value] of Object.entries(preview)) {
        if (key !== "schema_version") commit[key] = value;
      }
      try {
        await postExact("/api/costs/v1/configurations", JSON.stringify(commit), true);
        state.preview = null;
        if (requestedGeneration !== state.generation) {
          await refreshDerivedState();
          return;
        }
        byId("cost-preview-result").textContent = "committed · configuration/history/analytics を再読込します。";
        await refreshDerivedState();
        announce("configuration committed。最新状態を読み込みました。");
      } catch (failure) {
        if (requestedGeneration === state.generation) {
          announce(`configuration commit の結果を確認できません · ${failure.message}`);
        }
      } finally {
        setMutationDisabled(false);
        byId("cost-commit").disabled = !state.preview;
      }
    });
  }

  function recalculationRequest() {
    const configuration = state.configuration?.configuration;
    const scopes = [{ scope_kind: "session", session_id: sessionId }];
    const effective = state.exactEstimate?.item?.session_effective_at_utc
      ?? state.estimateHistory?.items?.[0]?.session_effective_at_utc;
    if (byId("cost-scope-utc-day").checked && effective) {
      scopes.push({ scope_kind: "utc_day", utc_date: effective.slice(0, 10) });
    }
    if (byId("cost-scope-period").checked) {
      scopes.push({
        scope_kind: "rolling_period",
        cutoff_utc: exactUtc(currentRange().to),
        window_days: Number(byId("cost-budget-period-days").value),
      });
    }
    return {
      schema_version: "cost.recalculation-request.v1",
      configuration_id: configuration?.configuration_id,
      expected_head_revision: state.configuration?.head_revision,
      catalog_sha256: state.configuration?.provider_catalog_sha256 ?? configuration?.catalog_sha256,
      session_ids: [sessionId],
      budget_scopes: scopes,
      idempotency_key: `browser-${crypto.randomUUID()}`,
    };
  }

  async function startRecalculation() {
    if (!sessionId) return;
    await mutation(async () => {
      setMutationDisabled(true);
      const requestedGeneration = state.generation;
      const request = recalculationRequest();
      const body = JSON.stringify(request);
      try {
        let result = await postExact("/api/costs/v1/recalculations", body, true);
        if (requestedGeneration === state.generation) renderRecalculation(result);
        while (result.state === "requested" || result.state === "running") {
          await new Promise(resolve => setTimeout(resolve, 100));
          const pollGeneration = state.generation;
          try {
            result = await getJson(
              `/api/costs/v1/recalculations/${encodeURIComponent(result.run_id)}`,
              { generation: pollGeneration, signal: state.controller.signal });
          } catch (failure) {
            if (failure?.name === "AbortError" && pollGeneration !== state.generation) continue;
            throw failure;
          }
          if (requestedGeneration === state.generation) renderRecalculation(result);
        }
        if (requestedGeneration === state.generation) {
          announce(`recalculation ${result.state}。history と analytics を更新します。`);
        }
        if (result.state === "succeeded") await refreshDerivedState();
      } catch (failure) {
        if (failure?.name !== "AbortError" && requestedGeneration === state.generation) {
          byId("cost-recalculation").textContent = `recalculation failed · ${failure.message}`;
          announce("recalculation の結果を確認できません。");
        }
      } finally {
        setMutationDisabled(false);
      }
    });
  }

  async function refreshDerivedState() {
    state.configuration = null;
    state.catalog = null;
    state.analytics = null;
    state.estimateHistory = null;
    state.attemptHistory = null;
    state.exactEstimate = null;
    state.hydratedConfigurationId = null;
    await loadAll();
  }

  function renderRecalculation(value) {
    const container = byId("cost-recalculation");
    container.replaceChildren(
      node("h3", null, `Recalculation · ${value.state}`),
      node("p", "monitor-mono", value.run_id));
    for (const target of value.targets ?? []) {
      container.append(node("p", null,
        `${target.session_id} · ${target.result?.kind ?? "pending"} · ${target.result?.status ?? target.result?.code ?? "running"}`));
    }
    for (const budget of value.budget_results ?? []) {
      container.append(node("p", null,
        `${budget.rule_id} · ${budget.outcome?.kind ?? "unknown"} · ${budget.outcome?.code ?? "evaluated"}`));
    }
  }

  function setMutationDisabled(disabled) {
    for (const control of root.querySelectorAll(
      "#cost-config-form input, #cost-config-form select, #cost-preview, #cost-commit")) {
      control.disabled = disabled;
    }
    if (!disabled) updateSourceEditorAvailability();
    updateRecalculationAvailability(disabled);
  }

  function updateSourceEditorAvailability() {
    const disabled = byId("cost-config-clear-sources").checked;
    for (const id of [
      "cost-config-surface",
      "cost-config-version",
      "cost-config-adapter",
      "cost-config-entry",
      "cost-config-provider",
      "cost-config-mode",
      "cost-config-route",
    ]) {
      byId(id).disabled = disabled;
    }
  }

  function updateRecalculationAvailability(mutationDisabled = false) {
    const configuration = state.configuration;
    const usable = Boolean(
      sessionId
      && configuration?.configuration?.configuration_id
      && configuration?.head_revision > 0
      && configuration?.catalog_state === "matching");
    const button = byId("cost-recalculate");
    button.disabled = mutationDisabled || !usable;
    button.title = usable
      ? "現在の immutable configuration で再計算します。"
      : "Session context と matching configuration が必要です。";
  }

  function selectCatalogEntry() {
    const selected = state.catalog?.entries?.find(
      item => item.entry_key === byId("cost-config-entry").value);
    byId("cost-config-provider").value = selected?.provider ?? "";
    byId("cost-config-mode").value = selected?.billing_mode ?? "";
    byId("cost-config-route").value = selected?.pricing_route ?? "";
  }

  byId("cost-filters").addEventListener("submit", event => {
    event.preventDefault();
    const range = currentRange();
    const from = new Date(`${range.from}T00:00:00Z`);
    const to = new Date(`${range.to}T00:00:00Z`);
    const days = (to - from) / 86_400_000;
    if (!Number.isFinite(days) || days <= 0 || days > 366) {
      announce("UTC range は nonempty かつ 366 日以内で指定してください。");
      return;
    }
    loadAll();
  });
  byId("cost-filter-reset").addEventListener("click", () => {
    byId("cost-filter-source").value = "";
    byId("cost-filter-model").value = "";
    byId("cost-filter-mode").value = "";
    byId("cost-filter-status").value = "";
    byId("cost-filter-registry").value = "";
    byId("cost-filter-from").value = "";
    byId("cost-filter-to").value = "";
    initializeDates();
    loadAll();
  });
  byId("cost-config-form").addEventListener("submit", previewConfiguration);
  byId("cost-config-entry").addEventListener("change", selectCatalogEntry);
  byId("cost-config-clear-sources").addEventListener("change", updateSourceEditorAvailability);
  byId("cost-commit").addEventListener("click", commitConfiguration);
  byId("cost-recalculate").addEventListener("click", startRecalculation);
  byId("cost-groups-next").addEventListener("click", loadNextGroups);
  byId("cost-catalog-next").addEventListener("click", loadNextCatalog);
  byId("cost-estimates-next").addEventListener("click", loadNextEstimates);
  byId("cost-attempts-next").addEventListener("click", loadNextAttempts);

  updateRecalculationAvailability();
  initializeDates();
  loadAll();
})();
