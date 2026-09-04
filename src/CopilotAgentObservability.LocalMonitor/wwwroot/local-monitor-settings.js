(() => {
  "use strict";

  const modal = document.getElementById("settings-modal");
  const navigation = document.querySelector("[data-settings-navigation-host]");
  const content = document.querySelector("[data-settings-content-host]");
  if (!modal || !navigation || !content || !window.LocalMonitorV1History) return;

  const sections = [
    ["state", "状態"],
    ["receiver", "受信"],
    ["ai", "AI設定"],
    ["repositories", "リポジトリ"],
    ["archive", "アーカイブ"],
    ["storage", "保存・バックアップ"],
    ["diagnostics", "診断"],
  ];
  const owned = new Map();
  let requestGeneration = 0;
  let selectedSettings = null;
  let aiCheckController = null;
  let receiverRuntimeController = null;
  let storageController = null;
  const UUID_V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const DIGEST = /^[0-9a-f]{64}$/;
  const WRONG_ARCHIVE_TARGET = Symbol("wrong-archive-target");
  const READINESS_REASONS = Object.freeze({
    loopback_unbound: "ローカル受信の準備ができていません。",
    db_unavailable: "保存先を利用できません。",
    migration_failed: "保存先の更新に失敗しました。",
    fatal_error: "受信処理を継続できません。",
    ingestion_stalled: "受信処理が停止しています。",
    ingestion_backpressure: "受信処理が混み合っています。",
    writer_not_running: "記録処理が動作していません。",
    projection_worker_missing: "投影処理が動作していません。",
    projection_status_unknown: "投影状態を確認できません。",
    projection_lag_exceeded: "投影の遅れが許容範囲を超えています。",
    projection_lag: "投影に遅れがあります。",
    span_projection_backlog: "詳細投影に待ちがあります。",
  });
  const BLOCKING_READINESS_REASONS = new Set(["loopback_unbound", "db_unavailable", "migration_failed", "fatal_error",
    "ingestion_stalled", "writer_not_running", "projection_worker_missing", "projection_status_unknown", "projection_lag_exceeded"]);
  const DEGRADED_READINESS_REASONS = new Set(["ingestion_backpressure", "projection_lag", "span_projection_backlog"]);
  const BACKUP_WARNINGS = ["raw_content_included", "not_repository_safe", "retention_backup_not_purged"];
  const RETENTION_WORKER_STATES = Object.freeze({ idle: "待機中", running: "実行中", degraded: "注意", disabled: "停止中", unknown: "状態不明" });

  function exact(value, keys) {
    if (!value || typeof value !== "object" || Array.isArray(value)) return false;
    const actual = Object.keys(value).sort();
    const expected = [...keys].sort();
    return actual.length === expected.length && actual.every((key, index) => key === expected[index]);
  }

  function count(value) { return Number.isSafeInteger(value) && value >= 0; }
  function timestamp(value) {
    return typeof value === "string" && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00$/.test(value)
      && !Number.isNaN(Date.parse(value));
  }

  function element(name, className, text) {
    const node = document.createElement(name);
    if (className) node.className = className;
    if (text) node.textContent = text;
    return node;
  }

  function link(href, text) {
    const node = element("a", "local-monitor-settings-action", text);
    node.href = href;
    return node;
  }

  function card(title, text) {
    const node = element("section", "local-monitor-settings-card");
    node.append(element("h4", null, title), element("p", null, text));
    return node;
  }

  function factCard(title, text) {
    const node = element("section", "local-monitor-settings-card");
    const value = element("div", null, text);
    value.dataset.settingsSourceSummary = "";
    node.append(element("h4", null, title), value);
    return node;
  }

  function buildSection(token, title) {
    const section = element("section", "local-monitor-settings-section");
    section.dataset.settingsSection = token;
    section.hidden = true;
    const heading = element("h3", null, title);
    heading.tabIndex = -1;
    section.append(heading);

    if (token === "state") {
      const receiver = card("受信", "受信状態を確認しています。");
      receiver.dataset.settingsStateReceiver = "";
      const projection = card("投影", "投影状態を確認しています。");
      projection.dataset.settingsStateProjection = "";
      const ai = card("AI", "AI設定を確認しています。");
      ai.dataset.settingsStateAi = "";
      const data = card("データ", "保存状態を確認しています。");
      data.dataset.settingsStateData = "";
      section.append(receiver, projection, ai, data);
    } else if (token === "receiver") {
      const health = card("受信状態", "受信状態を確認しています。");
      health.dataset.settingsReceiverHealth = "";
      const projection = card("投影", "投影状態を確認しています。");
      projection.dataset.settingsReceiverProjection = "";
      const source = factCard("記録範囲", "取得元の状態を確認しています。");
      source.dataset.settingsReceiverSource = "";
      const runtime = card("稼働情報", "稼働情報を確認しています。");
      runtime.dataset.settingsReceiverRuntime = "";
      section.append(health, projection, source,
        runtime,
        link("/diagnostics", "詳しい受信診断を開く"));
    } else if (token === "ai") {
      const ai = card("GitHub Copilot", "AI設定を確認しています。");
      ai.dataset.settingsAi = "";
      const check = element("button", null, "接続を確認");
      check.type = "button";
      check.dataset.settingsAiCheck = "";
      section.append(ai,
        check,
        element("p", "local-monitor-settings-note", "選択した内容は、明示的にAI操作を開始した場合に限りGitHub Copilotへ送信されます。資格情報は表示されません。"));
    } else if (token === "storage") {
      const storage = card("保存状態", "保存状態を確認しています。");
      storage.dataset.settingsStorageSummary = "";
      section.append(storage);
      const actions = element("div", "local-monitor-settings-actions");
      const backupNow = element("button", null, "今すぐバックアップ");
      backupNow.type = "button";
      backupNow.dataset.settingsBackupNow = "";
      actions.append(backupNow, link("/backup-restore", "復元の確認"), link("/diagnostics#retention-diagnostics", "保持と削除"),
        link("/historical-import", "履歴の取り込み"));
      const backupResult = element("p", "local-monitor-settings-note");
      backupResult.dataset.settingsBackupResult = "";
      backupResult.setAttribute("aria-live", "polite");
      const operationResult = element("p", "local-monitor-settings-note", "操作状態を確認しています。");
      operationResult.dataset.settingsStorageOperations = "";
      operationResult.setAttribute("aria-live", "polite");
      section.append(actions, backupResult, operationResult,
        element("p", "local-monitor-settings-note", "自動バックアップ: この画面では確認できません。"),
        element("p", "local-monitor-settings-note",
          "アーカイブは元に戻せる管理情報です。削除・保持・固定とは異なります。復元や削除など影響のある操作は、移動先で確認してから実行します。"));
    } else if (token === "diagnostics") {
      const health = card("受信", "受信状態を確認しています。");
      health.dataset.settingsDiagnosticsHealth = "";
      const projection = card("投影", "投影状態を確認しています。");
      projection.dataset.settingsDiagnosticsProjection = "";
      const source = factCard("取得元", "取得元の状態を確認しています。");
      source.dataset.settingsDiagnosticsSource = "";
      const repositories = card("リポジトリ", "リポジトリ状態を確認しています。");
      repositories.dataset.settingsDiagnosticsRepositories = "";
      section.append(health, projection, source, repositories, link("/diagnostics", "診断を開く"));
    }
    return section;
  }

  const repositoryNav = navigation.querySelector("[data-settings-navigation='repositories']");
  const archiveNav = navigation.querySelector("[data-settings-navigation='archive']");
  for (const [token, title] of sections) {
    let nav = navigation.querySelector(`[data-settings-navigation='${token}']`);
    let section = content.querySelector(`[data-settings-section='${token}']`);
    if (!nav) {
      nav = element("button", "local-monitor-settings-nav", title);
      nav.type = "button";
      nav.dataset.settingsNavigation = token;
      if (["state", "receiver", "ai"].includes(token)) navigation.insertBefore(nav, repositoryNav);
      else if (token === "storage") navigation.insertBefore(nav, archiveNav?.nextSibling ?? null);
      else navigation.append(nav);
      nav.addEventListener("click", () => window.LocalMonitorV1History.setSettings(token));
    }
    if (!section) {
      section = buildSection(token, title);
      content.insertBefore(section, content.querySelector("#repository-management-result"));
      owned.set(token, section);
    }
  }

  const archiveSection = content.querySelector("[data-settings-section='archive']");
  const sessionArchive = card("アーカイブ済みセッション", "読み込んでいます。");
  sessionArchive.dataset.settingsArchivedSessions = "";
  const sessionSearch = document.createElement("input");
  sessionSearch.type = "search";
  sessionSearch.setAttribute("aria-label", "アーカイブ済みセッションID");
  sessionSearch.placeholder = "セッションIDで検索";
  const sessionSearchResult = element("div", "local-monitor-settings-list");
  sessionSearchResult.dataset.settingsArchivedSessionSearchResult = "";
  sessionSearchResult.setAttribute("aria-live", "polite");
  const sessionList = element("div", "local-monitor-settings-list");
  const sessionMore = element("button", null, "さらに読み込む");
  sessionMore.type = "button";
  sessionMore.hidden = true;
  sessionArchive.append(sessionSearch, sessionSearchResult, sessionList, sessionMore);
  archiveSection?.append(sessionArchive);
  let archivedSessions = [];
  let archivedCursor = null;
  let archiveController = null;
  let archiveGeneration = 0;
  let archivePending = false;
  let exactSearchController = null;
  let exactSearchGeneration = 0;
  let exactSearchPending = false;
  let exactSearchResult = null;

  function invalidateArchiveLoad() {
    archiveController?.abort();
    archiveController = null;
    archiveGeneration++;
    archivePending = false;
    sessionMore.disabled = false;
  }

  function invalidateExactSearch(clear = true) {
    exactSearchController?.abort();
    exactSearchController = null;
    exactSearchGeneration++;
    exactSearchPending = false;
    if (clear) {
      exactSearchResult = null;
      sessionSearchResult.replaceChildren();
    }
  }

  function validateArchiveTarget(value, expectedId) {
    if (!exact(value, ["schema_version", "target_kind", "target_id", "state", "revision", "archived_at", "updated_at"])
        || value.schema_version !== "local-archive.response.v1" || value.target_kind !== "session"
        || value.target_id !== expectedId || !["active", "archived"].includes(value.state)
        || !count(value.revision) || value.state === "archived" !== (value.revision > 0 && value.revision % 2 === 1)
        || value.state === "archived" && (!timestamp(value.archived_at) || !timestamp(value.updated_at))
        || value.state === "active" && (value.archived_at !== null || value.updated_at !== null && !timestamp(value.updated_at))) throw WRONG_ARCHIVE_TARGET;
    return value;
  }

  async function restoreArchivedSession(item, button, onRestored) {
    button.disabled = true;
    try {
      const response = await fetch("/api/local-monitor/v1/archive-actions", { method: "POST", cache: "no-store", credentials: "same-origin",
        headers: { "content-type": "application/json", "x-monitor-csrf": "local-monitor" },
        body: JSON.stringify({ schema_version: "local-archive-action.v1", action: "restore", target_kind: "session",
          targets: [{ target_id: item.target_id, expected_revision: item.revision }] }) });
      if (!response.ok) throw new Error();
      const value = await response.json();
      if (!exact(value, ["schema_version", "action", "target_kind", "targets"])
          || value.schema_version !== "local-archive-action.response.v1" || value.action !== "restore"
          || value.target_kind !== "session" || !Array.isArray(value.targets) || value.targets.length !== 1
          || !exact(value.targets[0], ["target_id", "state", "revision", "archived_at", "updated_at"])
          || value.targets[0].target_id !== item.target_id || value.targets[0].state !== "active"
          || value.targets[0].revision !== item.revision + 1 || value.targets[0].revision % 2 !== 0
          || value.targets[0].archived_at !== null || !timestamp(value.targets[0].updated_at)) throw new Error();
      onRestored();
    } catch { button.textContent = "復元できませんでした"; }
    finally { if (button.isConnected) button.disabled = false; }
  }

  function archivedSessionRow(item, onRestored) {
    const row = element("div", "local-monitor-settings-row");
    const direct = link(window.LocalMonitorV1Paths.session(item.target_id), item.target_id);
    direct.dataset.archivedSessionId = "";
    const restore = element("button", null, "復元");
    restore.type = "button";
    restore.addEventListener("click", () => restoreArchivedSession(item, restore, onRestored));
    row.append(direct, restore);
    return row;
  }

  function renderArchivedSessions() {
    sessionList.replaceChildren(...archivedSessions.map(item => archivedSessionRow(item, () => {
      archivedSessions = archivedSessions.filter(candidate => candidate.target_id !== item.target_id);
      renderArchivedSessions();
    })));
    if (archivedSessions.length === 0) sessionList.textContent = "アーカイブ済みセッションはありません。";
    sessionMore.hidden = archivedCursor === null;
    sessionArchive.querySelector("p").textContent = `${archivedSessions.length}件を表示しています。`;
  }

  function renderExactSearchResult() {
    sessionSearchResult.replaceChildren();
    if (exactSearchPending) {
      sessionSearchResult.textContent = "セッションを検索しています。";
      return;
    }
    if (!exactSearchResult) return;
    if (exactSearchResult.kind === "archived") {
      const summary = element("p", "local-monitor-settings-note", "アーカイブ済みです。");
      sessionSearchResult.append(summary, archivedSessionRow(exactSearchResult.item, () => {
        exactSearchResult = { kind: "active" };
        renderExactSearchResult();
      }));
      return;
    }
    const messages = {
      invalid: "正しいセッションIDを入力してください。",
      active: "このセッションはアクティブで、アーカイブされていません。",
      missing: "セッションが見つかりません。",
      wrong: "検索結果が指定したセッションと一致しません。",
      busy: "保存先が使用中です。しばらくしてからもう一度お試しください。",
      error: "セッションを読み込めませんでした。",
    };
    sessionSearchResult.textContent = messages[exactSearchResult.kind];
  }

  async function searchArchivedSession() {
    const targetId = sessionSearch.value;
    invalidateExactSearch(false);
    if (targetId === "") {
      exactSearchResult = null;
      renderExactSearchResult();
      return;
    }
    if (!UUID_V7.test(targetId)) {
      exactSearchResult = { kind: "invalid" };
      renderExactSearchResult();
      return;
    }
    const controller = new AbortController();
    exactSearchController = controller;
    const generation = ++exactSearchGeneration;
    exactSearchPending = true;
    exactSearchResult = null;
    renderExactSearchResult();
    try {
      const response = await fetch(`/api/local-monitor/v1/archive?target_kind=session&target_id=${encodeURIComponent(targetId)}`,
        { cache: "no-store", credentials: "same-origin", signal: controller.signal });
      let result;
      if (response.ok) {
        const value = validateArchiveTarget(await response.json(), targetId);
        result = value.state === "archived" ? { kind: "archived", item: value } : { kind: "active" };
      } else if (response.status === 404) {
        const value = await response.json();
        result = exact(value, ["error"]) && value.error === "target_not_found" ? { kind: "missing" } : { kind: "error" };
      } else if (response.status === 503) {
        const value = await response.json();
        result = exact(value, ["error"]) && value.error === "persistence_busy" ? { kind: "busy" } : { kind: "error" };
      } else result = { kind: "error" };
      if (controller.signal.aborted || generation !== exactSearchGeneration) return;
      exactSearchResult = result;
    } catch (error) {
      if (controller.signal.aborted || generation !== exactSearchGeneration) return;
      exactSearchResult = error === WRONG_ARCHIVE_TARGET ? { kind: "wrong" } : { kind: "error" };
    } finally {
      if (generation === exactSearchGeneration) {
        exactSearchController = null;
        exactSearchPending = false;
        renderExactSearchResult();
      }
    }
  }

  async function loadArchivedSessions(after = null) {
    if (archivePending) return;
    if (after === null) invalidateArchiveLoad();
    const controller = new AbortController();
    archiveController = controller;
    const generation = ++archiveGeneration;
    archivePending = true;
    sessionMore.disabled = true;
    try {
      const suffix = after === null ? "" : `&after=${encodeURIComponent(after)}`;
      const response = await fetch(`/api/local-monitor/v1/archived-items?target_kind=session&limit=50${suffix}`,
        { cache: "no-store", credentials: "same-origin", signal: controller.signal });
      if (!response.ok) throw new Error();
      const value = await response.json();
      if (!exact(value, ["schema_version", "target_kind", "items", "next_cursor"])
          || value.schema_version !== "local-archived-items.response.v1" || value.target_kind !== "session"
          || !Array.isArray(value.items) || value.items.length > 50
          || value.next_cursor !== null && (typeof value.next_cursor !== "string" || !/^[A-Za-z0-9_-]{136}$/.test(value.next_cursor))) throw new Error();
      const items = value.items.map(item => {
        if (!exact(item, ["target_id", "state", "revision", "archived_at", "updated_at"])
            || !UUID_V7.test(item.target_id) || item.state !== "archived" || !count(item.revision)
            || item.revision <= 0 || item.revision % 2 !== 1
            || !timestamp(item.archived_at) || !timestamp(item.updated_at)) throw new Error();
        return item;
      });
      const ids = new Set(after === null ? [] : archivedSessions.map(item => item.target_id));
      if (items.some(item => ids.has(item.target_id) || !ids.add(item.target_id))) throw new Error();
      if (controller.signal.aborted || generation !== archiveGeneration) return;
      archivedSessions = after === null ? items : [...archivedSessions, ...items];
      archivedCursor = value.next_cursor;
      renderArchivedSessions();
    } catch {
      if (controller.signal.aborted || generation !== archiveGeneration) return;
      archivedSessions = [];
      archivedCursor = null;
      renderArchivedSessions();
      sessionArchive.querySelector("p").textContent = "アーカイブ済みセッションを読み込めませんでした。";
    } finally {
      if (generation === archiveGeneration) {
        archiveController = null;
        archivePending = false;
        sessionMore.disabled = false;
      }
    }
  }
  sessionSearch.addEventListener("input", searchArchivedSession);
  sessionMore.addEventListener("click", () => archivedCursor && loadArchivedSessions(archivedCursor));

  function fixedFailure(target) {
    target.querySelector("p").textContent = "保存状態を読み込めませんでした。";
  }

  async function loadWorkspace(section, generation) {
    const target = section === "state"
      ? owned.get("state").querySelector("[data-settings-state-projection] p")
      : section === "receiver" ? owned.get("receiver")?.querySelector("[data-settings-receiver-projection] p")
        : owned.get("diagnostics")?.querySelector("[data-settings-diagnostics-projection] p");
    try {
      const response = await fetch("/api/session-workspace/status", { cache: "no-store", credentials: "same-origin" });
      if (!response.ok) throw new Error();
      const value = await response.json();
      if (!exact(value, ["schema_version", "normalizer_status", "unsupported_event_version_count", "projection_cursor", "projection_backlog"])
          || value.schema_version !== 1 || !["ready", "degraded"].includes(value.normalizer_status)
          || !count(value.unsupported_event_version_count) || !count(value.projection_backlog)
          || value.projection_cursor !== null && !Number.isSafeInteger(value.projection_cursor)) throw new Error();
      if (generation === requestGeneration && target) target.textContent =
        `${value.normalizer_status === "ready" ? "投影は正常です" : "投影に注意が必要です"} · 投影待ち ${value.projection_backlog}件`;
    } catch { if (generation === requestGeneration && target) target.textContent = "投影状態を読み込めませんでした。"; }
  }

  async function loadAi(generation) {
    const stateTarget = owned.get("state").querySelector("[data-settings-state-ai] p");
    const target = owned.get("ai").querySelector("[data-settings-ai] p");
    try {
      const response = await fetch("/api/local-monitor/v1/settings/ai-readiness", { cache: "no-store", credentials: "same-origin" });
      if (!response.ok) throw new Error();
      const value = await response.json();
      renderAi(value, generation, target, stateTarget);
      if (generation === requestGeneration && selectedSettings === "ai")
        owned.get("ai").querySelector("[data-settings-ai-check]").disabled = false;
    } catch { if (generation === requestGeneration) { target.textContent = "AI設定を読み込めませんでした。"; stateTarget.textContent = "AI設定を読み込めませんでした。"; } }
  }

  function renderAi(value, generation, target, stateTarget) {
    const states = Object.freeze({ unconfigured: "未設定", configured_not_checked: "未確認", ready: "接続できます",
      authentication_required: "認証が必要です", unavailable: "利用できません", check_failed: "接続確認に失敗しました" });
    if (!exact(value, ["provider", "selected_model", "selected_configuration", "readiness_state", "last_check_result", "provider_egress_notice"])
        || value.provider !== "github_copilot" || typeof value.selected_model !== "string" || typeof value.selected_configuration !== "string"
        || !Object.hasOwn(states, value.readiness_state)
        || !["not_checked", "unconfigured", "ready", "authentication_required", "unavailable", "check_failed"].includes(value.last_check_result)
        || value.provider_egress_notice !== "selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action") throw new Error();
    if (generation !== requestGeneration) return;
    const text = `${states[value.readiness_state]} · GitHub Copilot · モデル ${value.selected_model} · 設定 ${value.selected_configuration}`;
    target.textContent = text;
    stateTarget.textContent = states[value.readiness_state];
  }

  async function checkAi() {
    const button = owned.get("ai").querySelector("[data-settings-ai-check]");
    const target = owned.get("ai").querySelector("[data-settings-ai] p");
    const generation = requestGeneration;
    aiCheckController?.abort();
    const controller = aiCheckController = new AbortController();
    button.disabled = true;
    target.textContent = "接続を確認しています。";
    try {
      const response = await fetch("/api/local-monitor/v1/settings/ai-readiness", {
        method: "POST", cache: "no-store", credentials: "same-origin",
        headers: { "x-monitor-csrf": "local-monitor" },
        signal: controller.signal,
      });
      if (!response.ok) throw new Error();
      renderAi(await response.json(), generation, target, owned.get("state").querySelector("[data-settings-state-ai] p"));
    } catch {
      if (!controller.signal.aborted && generation === requestGeneration && selectedSettings === "ai")
        target.textContent = "接続確認に失敗しました。";
    } finally {
      if (generation === requestGeneration && selectedSettings === "ai") button.disabled = false;
      if (aiCheckController === controller) aiCheckController = null;
    }
  }

  async function loadSourceSummary(generation) {
    const target = !owned.get("receiver")?.hidden
      ? owned.get("receiver").querySelector("[data-settings-receiver-source] [data-settings-source-summary]")
      : owned.get("diagnostics")?.querySelector("[data-settings-diagnostics-source] [data-settings-source-summary]");
    try {
      const response = await fetch("/api/monitor/source-diagnostics?limit=1", { cache: "no-store", credentials: "same-origin" });
      if (!response.ok) throw new Error();
      const value = await response.json();
      if (!exact(value, ["items", "next_cursor"]) || !Array.isArray(value.items) || value.items.length > 1
          || value.next_cursor !== null && !count(value.next_cursor)) throw new Error();
      let text = null;
      if (value.items.length === 1) {
        const item = value.items[0];
        const keys = ["observation_id", "ingest_batch_id", "source_surface", "source_application_version", "source_adapter", "adapter_version",
          "schema_fingerprint", "inventory_hash", "compatibility_state", "reason_codes", "unknown_span_count", "unknown_event_count",
          "unknown_attribute_count", "observed_at", "next_action"];
        if (!exact(item, keys) || !["supported", "supported_with_unknown_fields", "unsupported"].includes(item.compatibility_state)
            || !Array.isArray(item.reason_codes) || !count(item.unknown_span_count) || !count(item.unknown_event_count)
            || !count(item.unknown_attribute_count)) throw new Error();
        text = item.compatibility_state === "supported" ? "取得元の互換性を確認済みです。" : "取得元の互換性に注意が必要です。";
      }
      if (generation === requestGeneration && target) {
        if (text === null) window.LocalMonitorV1FactState.render(target, { state: "not_observed" });
        else {
          delete target.dataset.factState;
          target.textContent = text;
        }
      }
    } catch {
      if (generation === requestGeneration && target) {
        delete target.dataset.factState;
        target.textContent = "取得元の状態を読み込めませんでした。";
      }
    }
  }

  async function loadStorageSummary(generation) {
    storageController?.abort();
    const controller = storageController = new AbortController();
    const summary = owned.get("storage").querySelector("[data-settings-storage-summary] p");
    const operations = owned.get("storage").querySelector("[data-settings-storage-operations]");
    try {
      const response = await fetch("/api/local-monitor/v1/settings/storage", {
        cache: "no-store", credentials: "same-origin", signal: controller.signal,
      });
      if (!response.ok) throw new Error();
      const value = await response.json();
      if (!exact(value, ["schema_version", "database_file_size_bytes", "retention", "backup", "historical_import", "restart_requirement"])
          || value.schema_version !== "settings-storage-summary.v1"
          || value.database_file_size_bytes !== null && !count(value.database_file_size_bytes)
          || !exact(value.retention, ["state"])
          || !["idle", "running", "degraded", "disabled", "unknown"].includes(value.retention.state)
          || !exact(value.backup, ["state", "last_successful_at", "validation_state"])
          || !["idle", "running", "succeeded", "failed"].includes(value.backup.state)
          || value.backup.last_successful_at !== null && !timestamp(value.backup.last_successful_at)
          || !["passed", "unknown"].includes(value.backup.validation_state)
          || !exact(value.historical_import, ["state"])
          || !["not_run", "queued", "running", "succeeded", "failed", "rejected", "unknown"].includes(value.historical_import.state)
          || !["not_required", "unknown"].includes(value.restart_requirement)) throw new Error();
      if (controller.signal.aborted || generation !== requestGeneration || selectedSettings !== "storage") return;
      const bytes = value.database_file_size_bytes === null ? "確認できません" : `${value.database_file_size_bytes}バイト`;
      const retention = value.retention.state === "unknown" ? "保持状態は確認できません" : `保持 ${RETENTION_WORKER_STATES[value.retention.state]}`;
      summary.textContent = `データベース ${bytes} · ${retention} · 再起動 ${value.restart_requirement === "not_required" ? "不要" : "確認できません"}`;
      const backup = value.backup.state === "idle" ? "バックアップ操作なし"
        : value.backup.state === "running" ? "バックアップ作成中"
          : value.backup.state === "succeeded" ? `バックアップ成功 · 検証済み · 最終成功 ${value.backup.last_successful_at}` : "バックアップ失敗";
      const importLabels = {
        not_run: "履歴取り込みはまだありません",
        queued: "直近の履歴取り込みは待機中です",
        running: "直近の履歴取り込みは実行中です",
        succeeded: "直近の履歴取り込みは完了しています",
        failed: "直近の履歴取り込みは失敗しました",
        rejected: "直近の履歴取り込みは受け付けられませんでした",
        unknown: "履歴取り込み状態は確認できません",
      };
      const imported = importLabels[value.historical_import.state];
      operations.textContent = `${backup} · ${imported}`;
    } catch {
      if (!controller.signal.aborted && generation === requestGeneration && selectedSettings === "storage") {
        summary.textContent = "保存状態を読み込めませんでした。";
        operations.textContent = "操作状態を読み込めませんでした。";
      }
    } finally { if (storageController === controller) storageController = null; }
  }

  function invalidateStorageLoad() { storageController?.abort(); storageController = null; }

  async function createBackup() {
    const section = owned.get("storage");
    const button = section.querySelector("[data-settings-backup-now]");
    const result = section.querySelector("[data-settings-backup-result]");
    button.disabled = true;
    result.textContent = "バックアップを作成しています。";
    try {
      const response = await fetch("/api/runtime-backup/v1/backups", { method: "POST", cache: "no-store", credentials: "same-origin",
        headers: { "content-type": "application/json", "x-monitor-csrf": "local-monitor" }, body: "{}" });
      if (response.status !== 201) throw new Error();
      const value = await response.json();
      if (!exact(value, ["backup_id", "error_code", "archive_sha256", "warnings", "download_path"])
          || value.error_code !== null || !Array.isArray(value.warnings)
          || value.warnings.length !== BACKUP_WARNINGS.length
          || value.warnings.some((warning, index) => warning !== BACKUP_WARNINGS[index])) throw new Error();
      const id = value.backup_id;
      const path = value.download_path;
      if (!DIGEST.test(id) || value.archive_sha256 !== id || path !== `/api/runtime-backup/v1/backups/${id}/archive`) throw new Error();
      const download = link(path, "作成したバックアップを保存");
      download.dataset.settingsBackupDownload = "";
      result.replaceChildren(document.createTextNode(
        "バックアップを作成しました。生の記録を含むため、リポジトリへ保存せず安全な場所で管理してください。保持処理では削除されません。 "), download);
    } catch { result.textContent = "バックアップを作成できませんでした。"; }
    finally { button.disabled = false; }
  }
  owned.get("storage").querySelector("[data-settings-backup-now]").addEventListener("click", createBackup);
  owned.get("ai").querySelector("[data-settings-ai-check]").addEventListener("click", checkAi);

  async function loadSafeState(section) {
    const generation = ++requestGeneration;
    if (section === "state" || section === "receiver" || section === "diagnostics") {
      loadWorkspace(section, generation);
      if (section === "receiver" || section === "diagnostics") loadSourceSummary(generation);
      if (section === "receiver") loadReceiverRuntime(generation);
      try {
        const response = await fetch("/health/ready", { cache: "no-store", credentials: "same-origin" });
        if (!response.ok && response.status !== 503) throw new Error();
        const body = await response.json();
        const checkKeys = ["loopback_bound", "db_open", "migration_complete", "writer_running", "projection_worker_running",
          "ingestion_accepting", "projection_lag_seconds", "projection_backlog", "span_projection_lag_seconds",
          "span_projection_backlog", "projection_failure_count"];
        if (!exact(body, ["status", "checks", "degraded_reasons"])
            || !["ready", "degraded", "not_ready"].includes(body.status)
            || !exact(body.checks, checkKeys) || !Array.isArray(body.degraded_reasons)
            || !checkKeys.slice(0, 6).every(key => typeof body.checks[key] === "boolean")
            || !checkKeys.slice(6).every(key => count(body.checks[key]))) throw new Error();
        const uniqueReasons = new Set(body.degraded_reasons);
        if (body.degraded_reasons.length > Object.keys(READINESS_REASONS).length
            || uniqueReasons.size !== body.degraded_reasons.length
            || body.degraded_reasons.some(reason => typeof reason !== "string" || !(reason in READINESS_REASONS))
            || body.status === "ready" && (response.status !== 200 || body.degraded_reasons.length !== 0)
            || body.status === "degraded" && (response.status !== 200 || body.degraded_reasons.length === 0
              || body.degraded_reasons.some(reason => !DEGRADED_READINESS_REASONS.has(reason)))
            || body.status === "not_ready" && (response.status !== 503
              || !body.degraded_reasons.some(reason => BLOCKING_READINESS_REASONS.has(reason)))) throw new Error();
        if (generation !== requestGeneration) return;
        const target = section === "state" ? owned.get("state").querySelector("[data-settings-state-receiver] p")
          : section === "receiver" ? owned.get("receiver")?.querySelector("[data-settings-receiver-health] p")
            : owned.get("diagnostics")?.querySelector("[data-settings-diagnostics-health] p");
        if (target) {
          const statusText = body.status === "ready" ? "正常に受信しています。"
            : body.status === "degraded" ? "受信状態に注意が必要です。" : "受信できません。";
          target.textContent = `${statusText}${body.degraded_reasons.map(reason => ` ${READINESS_REASONS[reason]}`).join("")}`;
        }
      } catch {
        const target = section === "state" ? owned.get("state").querySelector("[data-settings-state-receiver] p")
          : section === "receiver" ? owned.get("receiver")?.querySelector("[data-settings-receiver-health] p")
            : owned.get("diagnostics")?.querySelector("[data-settings-diagnostics-health] p");
        if (generation === requestGeneration && target) target.textContent = "受信状態を読み込めませんでした。";
      }
    }
    if (section === "ai" || section === "state") loadAi(generation);
    if (section === "archive") loadArchivedSessions();
    if (section === "storage") {
      loadStorageSummary(generation);
    } else if (section === "state") {
      try {
        const response = await fetch("/api/retention/v1/status", { cache: "no-store", credentials: "same-origin" });
        if (!response.ok) throw new Error();
        const value = await response.json();
        const keys = ["schema_version", "pending_count", "queued_count", "deleting_count", "failed_count", "retry_exhausted_count",
          "orphan_or_unexpected_missing_count", "expired_but_readable_violation_count", "oldest_pending_age_seconds", "worker_state",
          "last_successful_run_at", "inventory_version", "adapter_coverage_version", "items"];
        const aggregates = [value.pending_count, value.queued_count, value.deleting_count, value.failed_count, value.retry_exhausted_count,
          value.orphan_or_unexpected_missing_count, value.expired_but_readable_violation_count];
        const allNull = aggregates.every(item => item === null);
        const allCounts = aggregates.every(count);
        if (!exact(value, keys) || value.schema_version !== 1 || !allNull && !allCounts
          || value.oldest_pending_age_seconds !== null && (!allCounts || !count(value.oldest_pending_age_seconds))
          || typeof value.worker_state !== "string" || !(value.worker_state in RETENTION_WORKER_STATES)
          || value.last_successful_run_at !== null && !timestamp(value.last_successful_run_at)
          || !Array.isArray(value.items) || value.items.length > 100) throw new Error();
        const retentionTarget = section === "state" ? owned.get("state").querySelector("[data-settings-state-data] p")
          : owned.get("storage").querySelector(".local-monitor-settings-card p");
        const oldestPending = value.oldest_pending_age_seconds === null
          ? value.pending_count === 0 ? "対象なし" : "確認できません"
          : `${value.oldest_pending_age_seconds}秒`;
        if (generation === requestGeneration) retentionTarget.textContent = value.pending_count === null
          ? "保持状態は利用できません。"
          : `保留 ${value.pending_count}件 · 待機 ${value.queued_count}件 · 削除中 ${value.deleting_count}件 · 失敗 ${value.failed_count}件 · 再試行終了 ${value.retry_exhausted_count}件 · 所在不明 ${value.orphan_or_unexpected_missing_count}件 · 期限切れ閲覧可能 ${value.expired_but_readable_violation_count}件 · 最古の保留 ${oldestPending} · 保持処理 ${RETENTION_WORKER_STATES[value.worker_state]} · 最終成功 ${value.last_successful_run_at ?? "記録なし"}`;
      } catch {
        if (generation === requestGeneration) {
          const target = section === "state" ? owned.get("state").querySelector("[data-settings-state-data] p") : null;
          if (target) target.textContent = "保存状態を読み込めませんでした。";
          else fixedFailure(owned.get("storage").querySelector(".local-monitor-settings-card"));
        }
      }
    }
  }

  async function loadReceiverRuntime(generation) {
    const target = owned.get("receiver")?.querySelector("[data-settings-receiver-runtime] p");
    receiverRuntimeController?.abort();
    const controller = receiverRuntimeController = new AbortController();
    try {
      const response = await fetch("/api/local-monitor/v1/settings/runtime", {
        cache: "no-store", credentials: "same-origin", signal: controller.signal,
      });
      if (!response.ok) throw new Error();
      const value = await response.json();
      if (!exact(value, ["application_started_at", "receiver_readiness", "endpoint", "activity_state", "latest_received_at",
        "recent_received_count", "projection_backlog", "capture_reasons", "projection_reasons", "restart_requirement"])
        || !timestamp(value.application_started_at) || !["ready", "degraded", "not_ready"].includes(value.receiver_readiness)
        || !exact(value.endpoint, ["transport", "scope", "port"]) || value.endpoint.transport !== "http"
        || value.endpoint.scope !== "loopback" || !Number.isSafeInteger(value.endpoint.port) || value.endpoint.port < 1 || value.endpoint.port > 65535
        || !["available", "unavailable"].includes(value.activity_state)
        || value.latest_received_at !== null && !timestamp(value.latest_received_at)
        || value.recent_received_count !== null && !count(value.recent_received_count)
        || value.projection_backlog !== null && !count(value.projection_backlog)
        || !Array.isArray(value.capture_reasons) || !Array.isArray(value.projection_reasons)
        || [...value.capture_reasons, ...value.projection_reasons].some(reason => typeof reason !== "string" || !(reason in READINESS_REASONS))
        || value.restart_requirement !== "unavailable") throw new Error();
      if (generation !== requestGeneration || !target) return;
      const format = raw => raw.replace("T", " ").slice(0, 19) + " UTC";
      const activity = value.activity_state === "unavailable" ? "受信履歴は確認できません"
        : `直近5分 ${value.recent_received_count}件 · 最新受信 ${value.latest_received_at === null ? "記録なし" : format(value.latest_received_at)}`;
      target.textContent = `開始 ${format(value.application_started_at)} · 受信先 HTTP · ループバック · ポート ${value.endpoint.port} · ${activity} · 再起動要否は確認できません`;
    } catch {
      if (!controller.signal.aborted && generation === requestGeneration && target) target.textContent = "稼働情報を読み込めませんでした。";
    } finally {
      if (receiverRuntimeController === controller) receiverRuntimeController = null;
    }
  }

  function invalidateReceiverRuntimeLoad() {
    receiverRuntimeController?.abort();
    receiverRuntimeController = null;
  }

  function select(section) {
    const selected = sections.some(([token]) => token === section) ? section : "state";
    if (selectedSettings === "archive" && selected !== "archive") {
      invalidateArchiveLoad();
      invalidateExactSearch();
      sessionSearch.value = "";
    }
    if (selectedSettings === "ai" && selected !== "ai") { aiCheckController?.abort(); aiCheckController = null; }
    if (selectedSettings === "receiver") invalidateReceiverRuntimeLoad();
    if (selectedSettings === "storage" && selected !== "storage") invalidateStorageLoad();
    selectedSettings = selected;
    for (const [token] of sections) {
      const nav = navigation.querySelector(`[data-settings-navigation='${token}']`);
      const panel = content.querySelector(`[data-settings-section='${token}']`);
      nav?.setAttribute("aria-current", token === selected ? "page" : "false");
      if (panel) panel.hidden = token !== selected;
    }
    loadSafeState(selected);
  }

  document.addEventListener("cao-route-state", event => {
    if (event.detail?.settings) select(event.detail.settings);
    else {
      requestGeneration++;
      selectedSettings = null;
      aiCheckController?.abort();
      aiCheckController = null;
      invalidateReceiverRuntimeLoad();
      invalidateStorageLoad();
      invalidateArchiveLoad();
      invalidateExactSearch();
      sessionSearch.value = "";
    }
  });
  document.addEventListener("cao-repository-settings-summary", event => {
    const target = owned.get("diagnostics")?.querySelector("[data-settings-diagnostics-repositories] p");
    const value = event.detail;
    if (!target || owned.get("diagnostics").hidden) return;
    target.textContent = value && count(value.repositoryCount) && count(value.archivedRepositoryCount)
      && count(value.unassignedActiveSessionCount)
      ? `先頭ページ ${value.repositoryCount}件 · アーカイブ ${value.archivedRepositoryCount}件 · リポジトリ未設定のセッション ${value.unassignedActiveSessionCount}件`
      : "リポジトリ状態を読み込めませんでした。";
  });
  modal.addEventListener("close", () => {
    requestGeneration++;
    selectedSettings = null;
    aiCheckController?.abort();
    aiCheckController = null;
    invalidateReceiverRuntimeLoad();
    invalidateStorageLoad();
    invalidateArchiveLoad();
    invalidateExactSearch();
    sessionSearch.value = "";
  });
  const initial = window.LocalMonitorV1History.current();
  if (initial.settings) select(initial.settings);
})();
