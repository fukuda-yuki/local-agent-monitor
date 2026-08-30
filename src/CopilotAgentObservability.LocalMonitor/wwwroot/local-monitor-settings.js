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
  const UUID_V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const DIGEST = /^[0-9a-f]{64}$/;
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
      const source = card("記録範囲", "取得元の状態を確認しています。");
      source.dataset.settingsReceiverSource = "";
      section.append(health, projection, source,
        element("p", "local-monitor-settings-note", "開始時刻・受信先・直近の受信件数: 現在の情報では確認できません。"),
        element("p", "local-monitor-settings-note", "記録内容の設定変更・再起動要否: この画面では対応していません。"),
        link("/diagnostics", "詳しい受信診断を開く"));
    } else if (token === "ai") {
      const ai = card("GitHub Copilot", "AI設定を確認しています。");
      ai.dataset.settingsAi = "";
      section.append(ai,
        element("p", "local-monitor-settings-note", "利用可否・認証・接続状態: 現在の情報では確認できません。"),
        element("p", "local-monitor-settings-note", "選択した内容は、明示的にAI操作を開始した場合に限りGitHub Copilotへ送信されます。資格情報は表示されません。"),
        element("p", "local-monitor-settings-note", "テンプレート情報: 現在の情報では確認できません。"));
    } else if (token === "storage") {
      section.append(card("保存状態", "保存状態を確認しています。"));
      const actions = element("div", "local-monitor-settings-actions");
      const backupNow = element("button", null, "今すぐバックアップ");
      backupNow.type = "button";
      backupNow.dataset.settingsBackupNow = "";
      actions.append(backupNow, link("/backup-restore", "復元の確認"), link("/diagnostics#retention-diagnostics", "保持と削除"),
        link("/historical-import", "履歴の取り込み"));
      const backupResult = element("p", "local-monitor-settings-note");
      backupResult.dataset.settingsBackupResult = "";
      backupResult.setAttribute("aria-live", "polite");
      const importResult = element("p", "local-monitor-settings-note", "履歴取り込み状態を確認しています。");
      importResult.dataset.settingsImportResult = "";
      importResult.setAttribute("aria-live", "polite");
      section.append(actions, backupResult, importResult, element("p", "local-monitor-settings-note",
        "保存場所・データサイズ・直近のバックアップ: 現在の情報では確認できません。"),
        element("p", "local-monitor-settings-note", "自動バックアップ: 対応していません。"),
        element("p", "local-monitor-settings-note",
          "アーカイブは元に戻せる管理情報です。削除・保持・固定とは異なります。復元や削除など影響のある操作は、移動先で確認してから実行します。"));
    } else if (token === "diagnostics") {
      const health = card("受信", "受信状態を確認しています。");
      health.dataset.settingsDiagnosticsHealth = "";
      const projection = card("投影", "投影状態を確認しています。");
      projection.dataset.settingsDiagnosticsProjection = "";
      const source = card("取得元", "取得元の状態を確認しています。");
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
  sessionSearch.placeholder = "セッションIDで絞り込み";
  const sessionList = element("div", "local-monitor-settings-list");
  const sessionMore = element("button", null, "さらに読み込む");
  sessionMore.type = "button";
  sessionMore.hidden = true;
  sessionArchive.append(sessionSearch, sessionList, sessionMore);
  archiveSection?.append(sessionArchive);
  let archivedSessions = [];
  let archivedCursor = null;
  let archiveController = null;
  let archiveGeneration = 0;
  let archivePending = false;

  function invalidateArchiveLoad() {
    archiveController?.abort();
    archiveController = null;
    archiveGeneration++;
    archivePending = false;
    sessionMore.disabled = false;
  }

  function renderArchivedSessions() {
    const query = sessionSearch.value;
    const visible = query === "" ? archivedSessions : UUID_V7.test(query)
      ? archivedSessions.filter(item => item.target_id === query) : [];
    sessionList.replaceChildren(...visible.map(item => {
      const row = element("div", "local-monitor-settings-row");
      const direct = link(window.LocalMonitorV1Paths.session(item.target_id), item.target_id);
      direct.dataset.archivedSessionId = "";
      const restore = element("button", null, "復元");
      restore.type = "button";
      restore.addEventListener("click", async () => {
        restore.disabled = true;
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
              || !count(value.targets[0].revision) || value.targets[0].revision <= 0 || value.targets[0].revision % 2 !== 0
              || value.targets[0].archived_at !== null
              || !timestamp(value.targets[0].updated_at)) throw new Error();
          archivedSessions = archivedSessions.filter(candidate => candidate.target_id !== item.target_id);
          renderArchivedSessions();
        } catch { restore.textContent = "復元できませんでした"; }
        finally { if (restore.isConnected) restore.disabled = false; }
      });
      row.append(direct, restore);
      return row;
    }));
    if (visible.length === 0) sessionList.textContent = query && !UUID_V7.test(query)
      ? "正しいセッションIDを入力してください。" : "アーカイブ済みセッションはありません。";
    sessionMore.hidden = archivedCursor === null;
    sessionArchive.querySelector("p").textContent = `${archivedSessions.length}件を表示しています。`;
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
  sessionSearch.addEventListener("input", renderArchivedSessions);
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
    if (!owned.get("state").hidden) {
      owned.get("state").querySelector("[data-settings-state-ai] p").textContent =
        "利用可否・認証・接続状態は現在の情報では確認できません。";
      return;
    }
    const target = owned.get("ai").querySelector("[data-settings-ai] p");
    try {
      const response = await fetch("/api/analysis/options", { cache: "no-store", credentials: "same-origin" });
      if (!response.ok) throw new Error();
      const value = await response.json();
      if (!exact(value, ["default_profile", "default_model", "reasoning_efforts", "profiles", "models"])
          || typeof value.default_profile !== "string" || typeof value.default_model !== "string"
          || !Array.isArray(value.profiles) || !Array.isArray(value.models) || value.models.length > 100) throw new Error();
      const selected = value.models.find(model => exact(model, ["id", "display_name", "provider", "supports_reasoning_effort", "is_default"])
        && model.id === value.default_model && typeof model.display_name === "string" && model.is_default === true);
      if (!selected) throw new Error();
      if (generation === requestGeneration) target.textContent = `既定: ${selected.display_name} · プロファイル ${value.default_profile}。認証と接続はAI実行時に確認します。`;
    } catch { if (generation === requestGeneration) target.textContent = "AI設定を読み込めませんでした。認証と接続はAI実行時に確認します。"; }
  }

  async function loadSourceSummary(generation) {
    const target = !owned.get("receiver")?.hidden
      ? owned.get("receiver").querySelector("[data-settings-receiver-source] p")
      : owned.get("diagnostics")?.querySelector("[data-settings-diagnostics-source] p");
    try {
      const response = await fetch("/api/monitor/source-diagnostics?limit=1", { cache: "no-store", credentials: "same-origin" });
      if (!response.ok) throw new Error();
      const value = await response.json();
      if (!exact(value, ["items", "next_cursor"]) || !Array.isArray(value.items) || value.items.length > 1
          || value.next_cursor !== null && !count(value.next_cursor)) throw new Error();
      let text = "取得元の観測はまだありません。";
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
      if (generation === requestGeneration && target) target.textContent = text;
    } catch { if (generation === requestGeneration && target) target.textContent = "取得元の状態を読み込めませんでした。"; }
  }

  async function loadImportSummary(generation) {
    const result = owned.get("storage").querySelector("[data-settings-import-result]");
    try {
      const response = await fetch("/api/historical-import/v1/history?limit=1", { cache: "no-store", credentials: "same-origin" });
      if (!response.ok) throw new Error();
      const value = await response.json();
      if (!exact(value, ["contract_version", "schema_version", "items"])
          || value.contract_version !== "historical-import-workflow/v1"
          || value.schema_version !== "historical-import-workflow-import-history/v1"
          || !Array.isArray(value.items) || value.items.length > 1) throw new Error();
      let text = "履歴の取り込みはまだありません。";
      if (value.items.length === 1) {
        const item = value.items[0];
        const keys = ["operation_id", "state", "outcome", "source_kind", "source_surface", "source_badge", "source_tier", "profile_id",
          "adapter_id", "new_observation_count", "duplicate_count", "conflict_count", "completeness", "completeness_reasons", "content_state", "retention_disposition"];
        if (!exact(item, keys) || !["succeeded", "failed", "rejected"].includes(item.state)
            || !["committed", "rolled_back", "not_started"].includes(item.outcome)) throw new Error();
        text = item.state === "succeeded" ? "直近の履歴取り込みは完了しています。" : "直近の履歴取り込みは完了していません。";
      }
      if (generation === requestGeneration) result.textContent = text;
    } catch { if (generation === requestGeneration) result.textContent = "履歴取り込み状態を読み込めませんでした。"; }
  }

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

  async function loadSafeState(section) {
    const generation = ++requestGeneration;
    if (section === "state" || section === "receiver" || section === "diagnostics") {
      loadWorkspace(section, generation);
      if (section === "receiver" || section === "diagnostics") loadSourceSummary(generation);
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
    if (section === "storage" || section === "state") {
      if (section === "storage") loadImportSummary(generation);
      try {
        const response = await fetch("/api/retention/v1/status", { cache: "no-store", credentials: "same-origin" });
        if (!response.ok) throw new Error();
        const value = await response.json();
        const keys = ["schema_version", "pending_count", "queued_count", "deleting_count", "failed_count", "retry_exhausted_count",
          "orphan_or_unexpected_missing_count", "expired_but_readable_violation_count", "oldest_pending_age_seconds", "worker_state",
          "last_successful_run_at", "inventory_version", "adapter_coverage_version", "items"];
        const aggregates = [value.pending_count, value.queued_count, value.deleting_count, value.failed_count, value.retry_exhausted_count,
          value.orphan_or_unexpected_missing_count, value.expired_but_readable_violation_count, value.oldest_pending_age_seconds];
        const allNull = aggregates.every(item => item === null);
        const allCounts = aggregates.every(count);
        if (!exact(value, keys) || value.schema_version !== 1 || !allNull && !allCounts
          || typeof value.worker_state !== "string" || !(value.worker_state in RETENTION_WORKER_STATES)
          || value.last_successful_run_at !== null && !timestamp(value.last_successful_run_at)
          || !Array.isArray(value.items) || value.items.length > 100) throw new Error();
        const retentionTarget = section === "state" ? owned.get("state").querySelector("[data-settings-state-data] p")
          : owned.get("storage").querySelector(".local-monitor-settings-card p");
        if (generation === requestGeneration) retentionTarget.textContent = value.pending_count === null
          ? "保持状態は利用できません。"
          : `保留 ${value.pending_count}件 · 待機 ${value.queued_count}件 · 削除中 ${value.deleting_count}件 · 失敗 ${value.failed_count}件 · 再試行終了 ${value.retry_exhausted_count}件 · 所在不明 ${value.orphan_or_unexpected_missing_count}件 · 期限切れ閲覧可能 ${value.expired_but_readable_violation_count}件 · 最古の保留 ${value.oldest_pending_age_seconds}秒 · cleanup ${RETENTION_WORKER_STATES[value.worker_state]} · 最終成功 ${value.last_successful_run_at ?? "記録なし"}`;
      } catch {
        if (generation === requestGeneration) {
          const target = section === "state" ? owned.get("state").querySelector("[data-settings-state-data] p") : null;
          if (target) target.textContent = "保存状態を読み込めませんでした。";
          else fixedFailure(owned.get("storage").querySelector(".local-monitor-settings-card"));
        }
      }
    }
  }

  function select(section) {
    const selected = sections.some(([token]) => token === section) ? section : "state";
    if (selectedSettings === "archive" && selected !== "archive") invalidateArchiveLoad();
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
      invalidateArchiveLoad();
    }
  });
  document.addEventListener("cao-repository-settings-summary", event => {
    const target = owned.get("diagnostics")?.querySelector("[data-settings-diagnostics-repositories] p");
    const value = event.detail;
    if (!target || owned.get("diagnostics").hidden) return;
    target.textContent = value && count(value.repositoryCount) && count(value.archivedRepositoryCount)
      && count(value.unassignedActiveSessionCount)
      ? `先頭ページ ${value.repositoryCount}件 · アーカイブ ${value.archivedRepositoryCount}件 · 未設定セッション ${value.unassignedActiveSessionCount}件`
      : "リポジトリ状態を読み込めませんでした。";
  });
  modal.addEventListener("close", () => {
    requestGeneration++;
    selectedSettings = null;
    invalidateArchiveLoad();
  });
  const initial = window.LocalMonitorV1History.current();
  if (initial.settings) select(initial.settings);
})();
