(() => {
  "use strict";

  const root = document.getElementById("local-monitor-repository-selection");
  const modal = document.getElementById("settings-modal");
  const navigationHost = document.querySelector("[data-settings-navigation-host]");
  const contentHost = document.querySelector("[data-settings-content-host]");
  const UUID_V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const REVISION = /^[0-9a-f]{64}$/;
  const CURSOR = /^[A-Za-z0-9_-]{135}$/;
  const TIMESTAMP = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})\.(\d{7})\+00:00$/;
  const ROOT_KEYS = [
    "schema_version", "workspace_revision", "repositories", "all_session_count",
    "unassigned_active_session_count", "archived_repository_count", "next_cursor",
  ];
  const REPOSITORY_KEYS = [
    "repository_id", "display_name", "archive_state", "archive_revision",
    "active_session_count", "last_observed_at", "assignment_conflict_count",
    "repository_revision",
  ];
  const MUTATION_REPOSITORY_KEYS = [
    "schema_version", "repository_id", "display_name", "revision", "created_at", "updated_at",
  ];
  const LOCATOR_ROOT_KEYS = ["schema_version", "repository_id", "repository_revision", "locators"];
  const ARCHIVE_ACTION_KEYS = ["schema_version", "action", "target_kind", "targets"];
  const ARCHIVE_TARGET_KEYS = ["target_id", "state", "revision", "archived_at", "updated_at"];
  const rootState = {
    requestGeneration: 0,
    controller: null,
    workspaceRevision: null,
    repositories: [],
    repositoryIds: new Set(),
    totals: null,
    nextCursor: null,
  };
  const settingsState = {
    requestGeneration: 0,
    controller: null,
    workspaceRevision: null,
    repositories: [],
    repositoryIds: new Set(),
    totals: null,
    nextCursor: null,
    section: null,
    selectedRepository: null,
    renameAuthorized: false,
    numericGeneration: 0,
    numericController: null,
    numericRevision: null,
    customInvoker: null,
    pendingFocus: null,
  };
  const mutationState = {
    generation: 0,
    controller: null,
    createSignature: null,
    createKey: null,
    renameSignature: null,
    renameKey: null,
    button: null,
  };
  let settingsDom = null;

  class ApiFailure extends Error {
    constructor(status, code) {
      super("repository operation failed");
      this.status = status;
      this.code = code;
    }
  }

  function hasExactKeys(value, expected) {
    if (!value || typeof value !== "object" || Array.isArray(value)) return false;
    const actual = Object.keys(value).sort();
    const wanted = [...expected].sort();
    return actual.length === wanted.length && actual.every((key, index) => key === wanted[index]);
  }

  function isCount(value) {
    return Number.isSafeInteger(value) && value >= 0;
  }

  function isPositiveRevision(value) {
    return Number.isSafeInteger(value) && value > 0;
  }

  function isDisplayName(value) {
    const length = typeof value === "string" ? Array.from(value).length : 0;
    return length >= 1 && length <= 200;
  }

  function isTimestamp(value) {
    if (value === null) return true;
    if (typeof value !== "string") return false;
    const match = TIMESTAMP.exec(value);
    if (!match) return false;
    const year = Number(match[1]);
    if (year < 1) return false;
    const milliseconds = Number(match[7].slice(0, 3));
    const instant = new Date(0);
    instant.setUTCFullYear(year, Number(match[2]) - 1, Number(match[3]));
    instant.setUTCHours(Number(match[4]), Number(match[5]), Number(match[6]), milliseconds);
    return Number.isFinite(instant.valueOf())
      && instant.getUTCFullYear() === year
      && instant.getUTCMonth() + 1 === Number(match[2])
      && instant.getUTCDate() === Number(match[3])
      && instant.getUTCHours() === Number(match[4])
      && instant.getUTCMinutes() === Number(match[5])
      && instant.getUTCSeconds() === Number(match[6]);
  }

  function validateRepository(value) {
    if (!hasExactKeys(value, REPOSITORY_KEYS)
        || !UUID_V7.test(value.repository_id)
        || !isDisplayName(value.display_name)
        || value.archive_state !== "active" && value.archive_state !== "archived"
        || !isCount(value.archive_revision)
        || !isCount(value.active_session_count)
        || !isTimestamp(value.last_observed_at)
        || !isCount(value.assignment_conflict_count)
        || typeof value.repository_revision !== "string"
        || !REVISION.test(value.repository_revision)) {
      throw new TypeError("invalid repository collection");
    }
    return Object.freeze({ ...value });
  }

  function validateCollection(value) {
    if (!hasExactKeys(value, ROOT_KEYS)
        || value.schema_version !== "local-monitor-repositories.response.v1"
        || typeof value.workspace_revision !== "string"
        || !REVISION.test(value.workspace_revision)
        || !Array.isArray(value.repositories)
        || value.repositories.length > 50
        || !isCount(value.all_session_count)
        || !isCount(value.unassigned_active_session_count)
        || !isCount(value.archived_repository_count)
        || value.next_cursor !== null
          && (typeof value.next_cursor !== "string" || !CURSOR.test(value.next_cursor))) {
      throw new TypeError("invalid repository collection");
    }
    const repositories = value.repositories.map(validateRepository);
    if (new Set(repositories.map(item => item.repository_id)).size !== repositories.length) {
      throw new TypeError("invalid repository collection");
    }
    return Object.freeze({
      workspaceRevision: value.workspace_revision,
      repositories: Object.freeze(repositories),
      allSessionCount: value.all_session_count,
      unassignedActiveSessionCount: value.unassigned_active_session_count,
      archivedRepositoryCount: value.archived_repository_count,
      nextCursor: value.next_cursor,
    });
  }

  function validateMutationRepository(value) {
    if (!hasExactKeys(value, MUTATION_REPOSITORY_KEYS)
        || value.schema_version !== "local-repository.v1"
        || !UUID_V7.test(value.repository_id)
        || !isDisplayName(value.display_name)
        || !isPositiveRevision(value.revision)
        || !isTimestamp(value.created_at)
        || !isTimestamp(value.updated_at)
        || value.created_at === null
        || value.updated_at === null) {
      throw new TypeError("invalid repository response");
    }
    return Object.freeze({ repositoryId: value.repository_id, revision: value.revision });
  }

  function validateNumericRevision(value, repositoryId) {
    if (!hasExactKeys(value, LOCATOR_ROOT_KEYS)
        || value.schema_version !== "local-repository-locators.v1"
        || value.repository_id !== repositoryId
        || !isPositiveRevision(value.repository_revision)
        || !Array.isArray(value.locators)
        || value.locators.length > 128) {
      throw new TypeError("invalid repository revision response");
    }
    return value.repository_revision;
  }

  function validateArchiveAction(value, action, repositoryId) {
    if (!hasExactKeys(value, ARCHIVE_ACTION_KEYS)
        || value.schema_version !== "local-archive-action.response.v1"
        || value.action !== action
        || value.target_kind !== "repository"
        || !Array.isArray(value.targets)
        || value.targets.length !== 1) {
      throw new TypeError("invalid archive response");
    }
    const target = value.targets[0];
    const expectedState = action === "archive" ? "archived" : "active";
    if (!hasExactKeys(target, ARCHIVE_TARGET_KEYS)
        || target.target_id !== repositoryId
        || target.state !== expectedState
        || !isCount(target.revision)
        || !isTimestamp(target.archived_at)
        || !isTimestamp(target.updated_at)
        || expectedState === "archived" && target.archived_at === null) {
      throw new TypeError("invalid archive response");
    }
  }

  async function fetchCollection(archiveScope, cursor, signal) {
    const after = cursor === null ? "" : `&after=${encodeURIComponent(cursor)}`;
    const response = await fetch(
      `/api/local-monitor/v1/repositories?archive_scope=${archiveScope}${after}&limit=50`,
      { cache: "no-store", credentials: "same-origin", signal });
    if (!response.ok) throw new ApiFailure(response.status, null);
    return validateCollection(await response.json());
  }

  async function errorCode(response) {
    try {
      const value = await response.json();
      return hasExactKeys(value, ["error"]) && typeof value.error === "string" ? value.error : null;
    } catch {
      return null;
    }
  }

  async function sendJson(path, method, body, operationKey, signal) {
    const headers = {
      "Content-Type": "application/json",
      "x-monitor-csrf": "local-monitor",
    };
    if (operationKey !== null) headers["Idempotency-Key"] = operationKey;
    const response = await fetch(path, {
      method,
      headers,
      body,
      cache: "no-store",
      credentials: "same-origin",
      signal,
    });
    if (!response.ok) throw new ApiFailure(response.status, await errorCode(response));
    return response.json();
  }

  function countText(value) {
    return `${value.toLocaleString("ja-JP")}件`;
  }

  function localTimestamp(value) {
    const parts = new Intl.DateTimeFormat("ja-JP", {
      year: "numeric", month: "numeric", day: "numeric",
      hour: "2-digit", minute: "2-digit", hour12: false,
    }).formatToParts(new Date(value));
    const part = type => parts.find(item => item.type === type)?.value ?? "";
    return `${part("year")}年${part("month")}月${part("day")}日 ${part("hour")}:${part("minute")}`;
  }

  function element(name, className, text) {
    const node = document.createElement(name);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function renderLastObserved(target, value) {
    target.replaceChildren();
    if (value === null) {
      window.LocalMonitorV1FactState.render(target, { state: "not_observed", recordedCount: null });
      return;
    }
    const time = element("time", null, localTimestamp(value));
    time.dateTime = value;
    target.append(time);
  }

  function renderRepository(item) {
    const card = element("article", "local-monitor-repository-card");
    card.dataset.repositoryCard = "";
    card.dataset.repositoryId = item.repository_id;
    const heading = element("h2", "local-monitor-repository-name", item.display_name);
    heading.dataset.repositoryName = "";
    heading.title = item.display_name;
    const count = element("p", "local-monitor-repository-count", countText(item.active_session_count));
    count.dataset.repositorySessionCount = "";
    count.setAttribute("aria-label", `アクティブなセッション ${countText(item.active_session_count)}`);
    const observed = element("div", "local-monitor-repository-last-observed");
    observed.dataset.repositoryLastObserved = "";
    const observedLabel = element("span", "local-monitor-repository-fact-label", "最終記録");
    const observedValue = element("div", "local-monitor-repository-fact-value");
    renderLastObserved(observedValue, item.last_observed_at);
    observed.append(observedLabel, observedValue);
    const actions = element("div", "local-monitor-repository-card-actions");
    const open = element("a", "local-monitor-repository-open", "セッションを開く");
    open.dataset.repositoryOpen = "";
    open.href = window.LocalMonitorV1Paths.repositorySessions(item.repository_id);
    const manage = element("button", "local-monitor-repository-manage", "管理");
    manage.type = "button";
    manage.dataset.repositoryManage = "";
    manage.addEventListener("click", () => openRepositoryManagement(item, manage));
    actions.append(open, manage);
    card.append(heading, count, observed);
    if (item.assignment_conflict_count > 0) {
      const conflict = element("a", "local-monitor-repository-conflict",
        `割り当ての確認が必要 ${countText(item.assignment_conflict_count)}`);
      conflict.dataset.repositoryConflictEntry = "";
      conflict.href = window.LocalMonitorV1Paths.unassignedSessions();
      card.append(conflict);
    }
    card.append(actions);
    return card;
  }

  function setRootStatus(text, retry) {
    const status = root?.querySelector("#repository-selection-status");
    if (!status) return;
    status.replaceChildren(document.createTextNode(text));
    if (retry) {
      const button = element("button", "local-monitor-repository-inline-action", "もう一度読み込む");
      button.type = "button";
      button.addEventListener("click", retry, { once: true });
      status.append(document.createTextNode(" "), button);
    }
  }

  function renderRoot() {
    if (!root || !rootState.totals) return;
    root.querySelector("[data-all-session-count]").textContent = countText(rootState.totals.allSessionCount);
    const unassigned = root.querySelector("#unassigned-sessions-entry");
    unassigned.hidden = rootState.totals.unassignedActiveSessionCount === 0;
    unassigned.querySelector("[data-unassigned-session-count]").textContent = countText(rootState.totals.unassignedActiveSessionCount);
    root.querySelector("[data-archived-repository-count]").textContent = countText(rootState.totals.archivedRepositoryCount);
    root.querySelector("#repository-grid").replaceChildren(...rootState.repositories.map(renderRepository));
    const loadMore = root.querySelector("#repository-load-more");
    loadMore.hidden = rootState.nextCursor === null;
    loadMore.disabled = false;
    setRootStatus(rootState.repositories.length === 0
      ? "登録されたアクティブなリポジトリはありません。"
      : `${countText(rootState.repositories.length)}のリポジトリを表示しています。`);
  }

  function sameTotals(state, page) {
    return state.totals !== null
      && state.totals.allSessionCount === page.allSessionCount
      && state.totals.unassignedActiveSessionCount === page.unassignedActiveSessionCount
      && state.totals.archivedRepositoryCount === page.archivedRepositoryCount;
  }

  async function refreshRoot() {
    if (!root) return true;
    rootState.controller?.abort();
    const controller = new AbortController();
    rootState.controller = controller;
    const generation = ++rootState.requestGeneration;
    setRootStatus("リポジトリを読み込んでいます。");
    try {
      const page = await fetchCollection("active_only", null, controller.signal);
      if (generation !== rootState.requestGeneration) return false;
      if (page.repositories.some(item => item.archive_state !== "active")) throw new TypeError("invalid active page");
      rootState.workspaceRevision = page.workspaceRevision;
      rootState.repositories = [...page.repositories];
      rootState.repositoryIds = new Set(page.repositories.map(item => item.repository_id));
      rootState.totals = page;
      rootState.nextCursor = page.nextCursor;
      renderRoot();
      return true;
    } catch {
      if (controller.signal.aborted || generation !== rootState.requestGeneration) return false;
      setRootStatus("リポジトリを読み込めませんでした。", refreshRoot);
      return false;
    }
  }

  async function loadMoreRoot() {
    if (!root || rootState.nextCursor === null) return;
    rootState.controller?.abort();
    const controller = new AbortController();
    rootState.controller = controller;
    const generation = ++rootState.requestGeneration;
    const cursor = rootState.nextCursor;
    const button = root.querySelector("#repository-load-more");
    button.disabled = true;
    setRootStatus("続きを読み込んでいます。");
    try {
      const page = await fetchCollection("active_only", cursor, controller.signal);
      if (generation !== rootState.requestGeneration) return;
      if (page.workspaceRevision !== rootState.workspaceRevision || !sameTotals(rootState, page)
          || page.repositories.some(item => item.archive_state !== "active" || rootState.repositoryIds.has(item.repository_id))) {
        throw new TypeError("incoherent repository pages");
      }
      for (const item of page.repositories) rootState.repositoryIds.add(item.repository_id);
      rootState.repositories.push(...page.repositories);
      rootState.nextCursor = page.nextCursor;
      renderRoot();
    } catch {
      if (controller.signal.aborted || generation !== rootState.requestGeneration) return;
      button.disabled = false;
      setRootStatus("続きを読み込めませんでした。", loadMoreRoot);
    }
  }

  function labeledInput(labelText, id, type, maximumLength) {
    const label = element("label", "local-monitor-repository-settings-field");
    const text = element("span", null, labelText);
    const input = document.createElement("input");
    input.id = id;
    input.type = type;
    input.maxLength = maximumLength;
    input.autocomplete = "off";
    label.append(text, input);
    return { label, input };
  }

  function buildSettings() {
    if (!modal || !navigationHost || !contentHost || settingsDom) return;
    const repositoriesNav = element("button", "local-monitor-repository-settings-nav", "リポジトリ");
    repositoriesNav.type = "button";
    repositoriesNav.dataset.repositorySettingsNavigation = "repositories";
    repositoriesNav.dataset.settingsNavigation = "repositories";
    const archiveNav = element("button", "local-monitor-repository-settings-nav", "アーカイブ");
    archiveNav.type = "button";
    archiveNav.dataset.repositorySettingsNavigation = "archive";
    archiveNav.dataset.settingsNavigation = "archive";
    navigationHost.append(repositoriesNav, archiveNav);
    const repositoriesSection = element("section", "local-monitor-repository-settings-section");
    repositoriesSection.dataset.repositorySettingsSection = "repositories";
    repositoriesSection.dataset.settingsSection = "repositories";
    repositoriesSection.hidden = true;
    const repositoriesHeading = element("h3", null, "リポジトリ");
    repositoriesHeading.tabIndex = -1;
    const createForm = element("form", "local-monitor-repository-create-form");
    createForm.id = "repository-create-form";
    const createHeading = element("h4", null, "リポジトリを追加");
    const createDisplay = labeledInput("表示名", "repository-create-display-name", "text", 200);
    const createLocator = labeledInput("GitHub locator（任意）", "repository-create-github-locator", "text", 2048);
    const createSubmit = element("button", null, "追加");
    createSubmit.type = "submit";
    createForm.append(createHeading, createDisplay.label, createLocator.label, createSubmit);
    const repositoriesStatus = element("p", "local-monitor-repository-settings-status", "リポジトリを読み込んでいます。");
    repositoriesStatus.setAttribute("aria-live", "polite");
    const unassignedEntry = element("a", "local-monitor-repository-inline-action");
    unassignedEntry.href = window.LocalMonitorV1Paths.unassignedSessions();
    const repositoriesList = element("div", "local-monitor-repository-settings-list");
    const repositoriesLoadMore = element("button", null, "さらに読み込む");
    repositoriesLoadMore.type = "button";
    repositoriesLoadMore.hidden = true;
    const manager = element("section", "local-monitor-repository-manager");
    manager.hidden = true;
    const managerHeading = element("h4", null, "リポジトリを管理");
    managerHeading.dataset.repositoryManagerName = "";
    const renameStatus = element("p", "local-monitor-repository-settings-status");
    renameStatus.setAttribute("aria-live", "polite");
    const renameForm = element("form", "local-monitor-repository-rename-form");
    renameForm.id = "repository-rename-form";
    const renameDisplay = labeledInput("表示名", "repository-rename-display-name", "text", 200);
    const renameSubmit = element("button", null, "表示名を変更");
    renameSubmit.type = "submit";
    renameSubmit.dataset.repositoryRename = "";
    renameSubmit.disabled = true;
    renameForm.append(renameDisplay.label, renameSubmit);
    const archiveNote = element("p", "local-monitor-repository-archive-note",
      "アーカイブは元に戻せる管理情報です。セッションのアーカイブ状態や割り当ては変更しません。");
    archiveNote.id = "repository-archive-confirmation-description";
    const archiveConfirmation = document.createElement("label");
    archiveConfirmation.className = "local-monitor-repository-archive-confirmation";
    const archiveConfirm = document.createElement("input");
    archiveConfirm.type = "checkbox";
    archiveConfirm.id = "repository-archive-confirmation";
    archiveConfirmation.append(archiveConfirm, document.createTextNode(" 上記の内容を確認しました"));
    const archiveSubmit = element("button", null, "アーカイブ");
    archiveSubmit.type = "button";
    archiveSubmit.dataset.repositoryArchive = "";
    archiveSubmit.setAttribute("aria-describedby", archiveNote.id);
    archiveSubmit.disabled = true;
    manager.append(managerHeading, renameStatus, renameForm, archiveNote, archiveConfirmation, archiveSubmit);
    repositoriesSection.append(repositoriesHeading, createForm, repositoriesStatus, unassignedEntry,
      repositoriesList, repositoriesLoadMore, manager);
    const archiveSection = element("section", "local-monitor-repository-settings-section");
    archiveSection.dataset.repositorySettingsSection = "archive";
    archiveSection.dataset.settingsSection = "archive";
    archiveSection.hidden = true;
    const archiveHeading = element("h3", null, "アーカイブ済みリポジトリ");
    archiveHeading.tabIndex = -1;
    const archiveStatus = element("p", "local-monitor-repository-settings-status", "リポジトリを読み込んでいます。");
    archiveStatus.setAttribute("aria-live", "polite");
    const archiveList = element("div", "local-monitor-repository-settings-list");
    const archiveLoadMore = element("button", null, "さらに読み込む");
    archiveLoadMore.type = "button";
    archiveLoadMore.hidden = true;
    archiveSection.append(archiveHeading, archiveStatus, archiveList, archiveLoadMore);
    const result = element("div", "local-monitor-repository-management-result");
    result.id = "repository-management-result";
    result.tabIndex = -1;
    result.hidden = true;
    result.setAttribute("role", "status");
    result.setAttribute("aria-live", "polite");
    contentHost.append(repositoriesSection, archiveSection, result);
    settingsDom = {
      repositoriesNav, archiveNav, repositoriesSection, repositoriesHeading,
      createForm, createDisplay: createDisplay.input, createLocator: createLocator.input,
      createSubmit, repositoriesStatus, unassignedEntry, repositoriesList, repositoriesLoadMore,
      manager, managerHeading, renameStatus, renameForm, renameDisplay: renameDisplay.input,
      renameSubmit, archiveConfirm, archiveSubmit, archiveSection, archiveHeading, archiveStatus,
      archiveList, archiveLoadMore, result,
    };
    repositoriesNav.addEventListener("click", () => openSettingsSection("repositories", repositoriesNav, "create"));
    archiveNav.addEventListener("click", () => openSettingsSection("archive", archiveNav, "archive"));
    createForm.addEventListener("submit", submitCreate);
    renameForm.addEventListener("submit", submitRename);
    archiveConfirm.addEventListener("change", () => {
      archiveSubmit.disabled = !archiveConfirm.checked;
    });
    archiveSubmit.addEventListener("click", submitArchive);
    repositoriesLoadMore.addEventListener("click", loadMoreSettings);
    archiveLoadMore.addEventListener("click", loadMoreSettings);
  }

  function setSettingsStatus(target, text, retry) {
    target.replaceChildren(document.createTextNode(text));
    if (retry) {
      const button = element("button", "local-monitor-repository-inline-action", "もう一度読み込む");
      button.type = "button";
      button.addEventListener("click", retry, { once: true });
      target.append(document.createTextNode(" "), button);
    }
  }

  function hideResult() {
    if (!settingsDom) return;
    settingsDom.result.hidden = true;
    settingsDom.result.replaceChildren();
  }

  function showResult(text, isError = false) {
    if (!settingsDom) return;
    settingsDom.result.classList.toggle("is-error", isError);
    settingsDom.result.replaceChildren(document.createTextNode(text));
    settingsDom.result.hidden = false;
    settingsDom.result.focus({ preventScroll: true });
  }

  function renderSettingsVisibility(section) {
    if (!settingsDom) return;
    settingsDom.repositoriesSection.hidden = section !== "repositories";
    settingsDom.archiveSection.hidden = section !== "archive";
    settingsDom.repositoriesNav.setAttribute("aria-current", section === "repositories" ? "page" : "false");
    settingsDom.archiveNav.setAttribute("aria-current", section === "archive" ? "page" : "false");
  }

  function renderRepositorySettingsItem(item) {
    const row = element("div", "local-monitor-repository-settings-item");
    const name = element("span", "local-monitor-repository-settings-name", item.display_name);
    name.title = item.display_name;
    const manage = element("button", null, "管理");
    manage.type = "button";
    manage.dataset.repositorySettingsManage = "";
    manage.addEventListener("click", () => openRepositoryManagement(item, manage));
    row.append(name, manage);
    return row;
  }

  function renderArchivedSettingsItem(item) {
    const row = element("div", "local-monitor-repository-settings-item");
    const name = element("span", "local-monitor-repository-settings-name", item.display_name);
    name.title = item.display_name;
    const restore = element("button", null, "復元");
    restore.type = "button";
    restore.dataset.repositoryRestore = "";
    restore.addEventListener("click", () => submitRestore(item, restore));
    row.append(name, restore);
    return row;
  }

  function renderManager() {
    if (!settingsDom) return;
    const selected = settingsState.selectedRepository;
    if (!selected || selected.archive_state !== "active" || !settingsState.renameAuthorized) {
      settingsDom.manager.hidden = true;
      return;
    }
    settingsDom.manager.hidden = false;
    settingsDom.managerHeading.textContent = selected.display_name;
    if (document.activeElement !== settingsDom.renameDisplay) settingsDom.renameDisplay.value = selected.display_name;
    settingsDom.renameSubmit.disabled = settingsState.numericRevision === null;
    setSettingsStatus(settingsDom.renameStatus, settingsState.numericRevision === null
      ? "変更に必要な情報を確認しています。" : "表示名を変更できます。");
  }

  function renderSettings() {
    if (!settingsDom || !settingsState.totals) return;
    const active = settingsState.repositories.filter(item => item.archive_state === "active");
    const archived = settingsState.repositories.filter(item => item.archive_state === "archived");
    settingsDom.repositoriesList.replaceChildren(...active.map(renderRepositorySettingsItem));
    settingsDom.archiveList.replaceChildren(...archived.map(renderArchivedSettingsItem));
    setSettingsStatus(settingsDom.repositoriesStatus,
      active.length === 0 ? "登録されたアクティブなリポジトリはありません。" : `${countText(active.length)}を表示しています。`);
    settingsDom.unassignedEntry.textContent = `リポジトリ未設定のセッション ${countText(settingsState.totals.unassignedActiveSessionCount)}`;
    setSettingsStatus(settingsDom.archiveStatus,
      archived.length === 0 ? "アーカイブ済みリポジトリはありません。" : `${countText(archived.length)}を表示しています。`);
    settingsDom.repositoriesLoadMore.hidden = settingsState.nextCursor === null;
    settingsDom.archiveLoadMore.hidden = settingsState.nextCursor === null;
    settingsDom.repositoriesLoadMore.disabled = false;
    settingsDom.archiveLoadMore.disabled = false;
    if (settingsState.selectedRepository) {
      const refreshed = settingsState.repositories.find(item => item.repository_id === settingsState.selectedRepository.repository_id);
      if (refreshed) settingsState.selectedRepository = refreshed;
    }
    renderManager();
  }

  async function refreshSettings(section = settingsState.section) {
    if (!settingsDom || section !== "repositories" && section !== "archive") return false;
    settingsState.controller?.abort();
    const controller = new AbortController();
    settingsState.controller = controller;
    const generation = ++settingsState.requestGeneration;
    const status = section === "repositories" ? settingsDom.repositoriesStatus : settingsDom.archiveStatus;
    setSettingsStatus(status, "リポジトリを読み込んでいます。");
    try {
      const page = await fetchCollection("include_archived", null, controller.signal);
      if (generation !== settingsState.requestGeneration || section !== settingsState.section) return false;
      settingsState.workspaceRevision = page.workspaceRevision;
      settingsState.repositories = [...page.repositories];
      settingsState.repositoryIds = new Set(page.repositories.map(item => item.repository_id));
      settingsState.totals = page;
      settingsState.nextCursor = page.nextCursor;
      renderSettings();
      return true;
    } catch {
      if (controller.signal.aborted || generation !== settingsState.requestGeneration) return false;
      setSettingsStatus(status, "リポジトリを読み込めませんでした。", () => refreshSettings(section));
      return false;
    }
  }

  async function publishDiagnosticsSummary() {
    settingsState.controller?.abort();
    const controller = new AbortController();
    settingsState.controller = controller;
    const generation = ++settingsState.requestGeneration;
    try {
      const page = await fetchCollection("include_archived", null, controller.signal);
      if (controller.signal.aborted || generation !== settingsState.requestGeneration) return;
      document.dispatchEvent(new CustomEvent("cao-repository-settings-summary", { detail: {
        repositoryCount: page.repositories.length,
        archivedRepositoryCount: page.archivedRepositoryCount,
        unassignedActiveSessionCount: page.unassignedActiveSessionCount,
      } }));
    } catch {
      if (!controller.signal.aborted && generation === settingsState.requestGeneration) {
        document.dispatchEvent(new CustomEvent("cao-repository-settings-summary", { detail: null }));
      }
    }
  }

  async function loadMoreSettings() {
    if (!settingsDom || settingsState.nextCursor === null || !settingsState.section) return;
    settingsState.controller?.abort();
    const controller = new AbortController();
    settingsState.controller = controller;
    const generation = ++settingsState.requestGeneration;
    const section = settingsState.section;
    const cursor = settingsState.nextCursor;
    const button = section === "repositories" ? settingsDom.repositoriesLoadMore : settingsDom.archiveLoadMore;
    const status = section === "repositories" ? settingsDom.repositoriesStatus : settingsDom.archiveStatus;
    button.disabled = true;
    setSettingsStatus(status, "続きを読み込んでいます。");
    try {
      const page = await fetchCollection("include_archived", cursor, controller.signal);
      if (generation !== settingsState.requestGeneration || section !== settingsState.section) return;
      if (page.workspaceRevision !== settingsState.workspaceRevision || !sameTotals(settingsState, page)
          || page.repositories.some(item => settingsState.repositoryIds.has(item.repository_id))) {
        throw new TypeError("incoherent repository pages");
      }
      for (const item of page.repositories) settingsState.repositoryIds.add(item.repository_id);
      settingsState.repositories.push(...page.repositories);
      settingsState.nextCursor = page.nextCursor;
      renderSettings();
    } catch {
      if (controller.signal.aborted || generation !== settingsState.requestGeneration) return;
      button.disabled = false;
      setSettingsStatus(status, "続きを読み込めませんでした。", loadMoreSettings);
    }
  }

  async function loadNumericRevision(repository) {
    if (!settingsDom || !settingsState.renameAuthorized) return false;
    settingsState.numericController?.abort();
    const controller = new AbortController();
    settingsState.numericController = controller;
    const generation = ++settingsState.numericGeneration;
    settingsState.numericRevision = null;
    renderManager();
    try {
      const response = await fetch(`/api/local-monitor/v1/repositories/${repository.repository_id}/locators`,
        { cache: "no-store", credentials: "same-origin", signal: controller.signal });
      if (!response.ok) throw new ApiFailure(response.status, null);
      const revision = validateNumericRevision(await response.json(), repository.repository_id);
      if (controller.signal.aborted || generation !== settingsState.numericGeneration
          || settingsState.selectedRepository?.repository_id !== repository.repository_id
          || !settingsState.renameAuthorized) return false;
      settingsState.numericRevision = revision;
      renderManager();
      return true;
    } catch {
      if (controller.signal.aborted || generation !== settingsState.numericGeneration) return false;
      settingsState.numericRevision = null;
      settingsDom.renameSubmit.disabled = true;
      setSettingsStatus(settingsDom.renameStatus, "変更に必要な情報を読み込めませんでした。",
        () => loadNumericRevision(repository));
      return false;
    }
  }

  function randomOperationKey() {
    const bytes = crypto.getRandomValues(new Uint8Array(32));
    let binary = "";
    for (const value of bytes) binary += String.fromCharCode(value);
    return `lrc1_${btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "")}`;
  }

  async function submissionDigest(body) {
    const bytes = new TextEncoder().encode(body);
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
    let binary = "";
    for (const value of digest) binary += String.fromCharCode(value);
    return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
  }

  function operationKey(kind, signature) {
    const signatureName = `${kind}Signature`;
    const keyName = `${kind}Key`;
    if (mutationState[signatureName] !== signature || mutationState[keyName] === null) {
      mutationState[signatureName] = signature;
      mutationState[keyName] = randomOperationKey();
    }
    return mutationState[keyName];
  }

  function clearOperationKey(kind) {
    mutationState[`${kind}Signature`] = null;
    mutationState[`${kind}Key`] = null;
  }

  function supersedeMutation() {
    mutationState.controller?.abort();
    if (mutationState.button?.isConnected) mutationState.button.disabled = false;
    mutationState.button = null;
    mutationState.generation++;
  }

  async function runMutation(button, action) {
    const previousButton = mutationState.button;
    mutationState.controller?.abort();
    if (previousButton?.isConnected) previousButton.disabled = false;
    const controller = new AbortController();
    mutationState.controller = controller;
    mutationState.button = button;
    const generation = ++mutationState.generation;
    const isCurrent = () => generation === mutationState.generation
      && mutationState.controller === controller
      && !controller.signal.aborted;
    button.disabled = true;
    try {
      await action(controller.signal, isCurrent);
    } catch (error) {
      if (!isCurrent()) return;
      throw error;
    }
    finally {
      if (generation === mutationState.generation) {
        mutationState.button = null;
        if (button.isConnected) button.disabled = false;
      } else if (button !== mutationState.button && button.isConnected) {
        button.disabled = false;
      }
    }
  }

  async function submitCreate(event) {
    event.preventDefault();
    if (!settingsDom) return;
    hideResult();
    const body = JSON.stringify({
      schema_version: "local-repository-create.v1",
      display_name: settingsDom.createDisplay.value.normalize("NFC"),
      github_locator: settingsDom.createLocator.value === "" ? null : settingsDom.createLocator.value,
    });
    let isCurrent = () => false;
    try {
      await runMutation(settingsDom.createSubmit, async (signal, current) => {
        isCurrent = current;
        const signature = await submissionDigest(body);
        if (!isCurrent()) return;
        const key = operationKey("create", signature);
        validateMutationRepository(await sendJson("/api/local-monitor/v1/repositories", "POST", body, key, signal));
        if (!isCurrent()) return;
        settingsDom.createDisplay.value = "";
        settingsDom.createLocator.value = "";
        clearOperationKey("create");
        const [rootCurrent, settingsCurrent] = await Promise.all([refreshRoot(), refreshSettings("repositories")]);
        if (!isCurrent()) return;
        showResult(rootCurrent && settingsCurrent
          ? "リポジトリを追加しました。" : "リポジトリを追加しました。最新の一覧は再読み込みしてください。");
      });
    } catch (error) {
      if (!isCurrent() || error instanceof DOMException && error.name === "AbortError") return;
      showResult("リポジトリを追加できませんでした。入力内容を確認して、もう一度お試しください。", true);
    }
  }

  async function submitRename(event) {
    event.preventDefault();
    if (!settingsDom || !settingsState.selectedRepository || settingsState.numericRevision === null) return;
    hideResult();
    const selectedId = settingsState.selectedRepository.repository_id;
    const draft = settingsDom.renameDisplay.value;
    const body = JSON.stringify({
      schema_version: "local-repository-update.v1",
      expected_revision: settingsState.numericRevision,
      operation: "rename",
      display_name: draft.normalize("NFC"),
      github_locator: null,
    });
    let isCurrent = () => false;
    try {
      await runMutation(settingsDom.renameSubmit, async (signal, current) => {
        isCurrent = current;
        const signature = await submissionDigest(body);
        if (!isCurrent()) return;
        const key = operationKey("rename", signature);
        const updated = validateMutationRepository(await sendJson(
          `/api/local-monitor/v1/repositories/${selectedId}`, "PATCH", body, key, signal));
        if (!isCurrent()) return;
        if (updated.repositoryId !== selectedId) throw new TypeError("invalid repository response");
        clearOperationKey("rename");
        const [rootCurrent, settingsCurrent] = await Promise.all([refreshRoot(), refreshSettings("repositories")]);
        if (!isCurrent()) return;
        if (settingsState.selectedRepository?.repository_id === selectedId) {
          await loadNumericRevision(settingsState.selectedRepository);
        }
        if (!isCurrent()) return;
        showResult(rootCurrent && settingsCurrent
          ? "表示名を変更しました。" : "表示名を変更しました。最新の一覧は再読み込みしてください。");
      });
    } catch (error) {
      if (!isCurrent() || error instanceof DOMException && error.name === "AbortError") return;
      if (error instanceof ApiFailure && error.code === "revision_conflict") {
        await refreshSettings("repositories");
        if (!isCurrent()) return;
        if (settingsState.selectedRepository?.repository_id === selectedId) {
          await loadNumericRevision(settingsState.selectedRepository);
          if (!isCurrent()) return;
          settingsDom.renameDisplay.value = draft;
        }
        showResult("情報が更新されています。最新の状態を確認して、もう一度実行してください。", true);
        return;
      }
      showResult("表示名を変更できませんでした。入力内容を確認して、もう一度お試しください。", true);
    }
  }

  async function submitArchive() {
    if (!settingsDom || !settingsState.selectedRepository || !settingsDom.archiveConfirm.checked) return;
    hideResult();
    const selected = settingsState.selectedRepository;
    const body = JSON.stringify({
      schema_version: "local-archive-action.v1",
      action: "archive",
      target_kind: "repository",
      targets: [{ target_id: selected.repository_id, expected_revision: selected.archive_revision }],
    });
    let isCurrent = () => false;
    try {
      await runMutation(settingsDom.archiveSubmit, async (signal, current) => {
        isCurrent = current;
        validateArchiveAction(await sendJson("/api/local-monitor/v1/archive-actions", "POST", body, null, signal),
          "archive", selected.repository_id);
        if (!isCurrent()) return;
        settingsState.renameAuthorized = false;
        settingsState.numericRevision = null;
        const [rootCurrent, settingsCurrent] = await Promise.all([refreshRoot(), refreshSettings("repositories")]);
        if (!isCurrent()) return;
        showResult(rootCurrent && settingsCurrent
          ? "リポジトリをアーカイブしました。" : "リポジトリをアーカイブしました。最新の一覧は再読み込みしてください。");
      });
    } catch (error) {
      if (!isCurrent() || error instanceof DOMException && error.name === "AbortError") return;
      if (error instanceof ApiFailure && error.code === "revision_conflict") {
        await refreshSettings("repositories");
        if (!isCurrent()) return;
        showResult("情報が更新されています。最新の状態を確認して、もう一度実行してください。", true);
        return;
      }
      showResult("リポジトリをアーカイブできませんでした。もう一度お試しください。", true);
    }
  }

  async function submitRestore(item, button) {
    hideResult();
    const body = JSON.stringify({
      schema_version: "local-archive-action.v1",
      action: "restore",
      target_kind: "repository",
      targets: [{ target_id: item.repository_id, expected_revision: item.archive_revision }],
    });
    let isCurrent = () => false;
    try {
      await runMutation(button, async (signal, current) => {
        isCurrent = current;
        validateArchiveAction(await sendJson("/api/local-monitor/v1/archive-actions", "POST", body, null, signal),
          "restore", item.repository_id);
        if (!isCurrent()) return;
        const [rootCurrent, settingsCurrent] = await Promise.all([refreshRoot(), refreshSettings("archive")]);
        if (!isCurrent()) return;
        showResult(rootCurrent && settingsCurrent
          ? "リポジトリを復元しました。" : "リポジトリを復元しました。最新の一覧は再読み込みしてください。");
      });
    } catch (error) {
      if (!isCurrent() || error instanceof DOMException && error.name === "AbortError") return;
      if (error instanceof ApiFailure && error.code === "revision_conflict") {
        await refreshSettings("archive");
        if (!isCurrent()) return;
        showResult("情報が更新されています。最新の状態を確認して、もう一度実行してください。", true);
        return;
      }
      showResult("リポジトリを復元できませんでした。もう一度お試しください。", true);
    }
  }

  function schedulePendingFocus() {
    const pending = settingsState.pendingFocus;
    settingsState.pendingFocus = null;
    queueMicrotask(() => {
      if (!settingsDom || !modal?.open) return;
      const target = pending === "rename" ? settingsDom.renameDisplay
        : pending === "archive" ? settingsDom.archiveHeading : settingsDom.createDisplay;
      if (target && !target.closest("[hidden]")) target.focus({ preventScroll: true });
    });
  }

  function openSettingsSection(section, invoker, focus) {
    if (!window.LocalMonitorV1History) return;
    supersedeMutation();
    settingsState.customInvoker = invoker;
    settingsState.pendingFocus = focus;
    window.LocalMonitorV1History.setSettings(section);
  }

  function openRepositoryManagement(item, invoker) {
    settingsState.numericController?.abort();
    settingsState.numericRevision = null;
    settingsState.selectedRepository = item;
    settingsState.renameAuthorized = true;
    if (settingsDom) {
      settingsDom.archiveConfirm.checked = false;
      settingsDom.archiveSubmit.disabled = true;
    }
    openSettingsSection("repositories", invoker, "rename");
    loadNumericRevision(item);
  }

  function discardRepositoryForms() {
    settingsState.numericController?.abort();
    supersedeMutation();
    settingsState.numericGeneration++;
    settingsState.numericRevision = null;
    settingsState.selectedRepository = null;
    settingsState.renameAuthorized = false;
    if (!settingsDom) return;
    settingsDom.createDisplay.value = "";
    settingsDom.createLocator.value = "";
    settingsDom.renameDisplay.value = "";
    settingsDom.archiveConfirm.checked = false;
    settingsDom.archiveSubmit.disabled = true;
    settingsDom.manager.hidden = true;
    clearOperationKey("create");
    clearOperationKey("rename");
  }

  function handleRouteState(state) {
    buildSettings();
    if (!settingsDom) return;
    const section = state?.settings ?? null;
    if (section === "diagnostics") {
      discardRepositoryForms();
      settingsState.section = null;
      renderSettingsVisibility(null);
      publishDiagnosticsSummary();
      return;
    }
    if (section !== "repositories" && section !== "archive") {
      settingsState.controller?.abort();
      settingsState.requestGeneration++;
      discardRepositoryForms();
      settingsState.section = null;
      renderSettingsVisibility(null);
      return;
    }
    if (settingsState.section === "repositories" && section !== "repositories") {
      discardRepositoryForms();
    }
    if (settingsState.section !== section) hideResult();
    settingsState.section = section;
    renderSettingsVisibility(section);
    if (section === "repositories") renderManager();
    refreshSettings(section);
    schedulePendingFocus();
  }

  function resetClosedSettings() {
    settingsState.controller?.abort();
    settingsState.numericController?.abort();
    settingsState.requestGeneration++;
    settingsState.numericGeneration++;
    discardRepositoryForms();
    settingsState.section = null;
    if (settingsDom) {
      hideResult();
    }
    const invoker = settingsState.customInvoker;
    settingsState.customInvoker = null;
    queueMicrotask(() => {
      if (invoker?.isConnected) invoker.focus({ preventScroll: true });
    });
  }

  buildSettings();
  document.addEventListener("cao-route-state", event => handleRouteState(event.detail));
  modal?.addEventListener("close", resetClosedSettings);
  if (root) {
    root.querySelector("#repository-load-more").addEventListener("click", loadMoreRoot);
    root.querySelector("#add-repository-action").addEventListener(
      "click", event => openSettingsSection("repositories", event.currentTarget, "create"));
    root.querySelector("#archived-repositories-action").addEventListener(
      "click", event => openSettingsSection("archive", event.currentTarget, "archive"));
    refreshRoot();
  }
  if (window.LocalMonitorV1History) {
    const initial = window.LocalMonitorV1History.current();
    if (["repositories", "archive", "diagnostics"].includes(initial.settings)) handleRouteState(initial);
  }
})();
