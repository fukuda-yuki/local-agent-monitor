(() => {
  "use strict";

  const host = document.querySelector("[data-local-monitor-v1-host]");
  if (!host) return;

  const SETTINGS = new Set(["state", "receiver", "ai", "repositories", "archive", "storage", "diagnostics"]);
  const EXPLORER_KEYS = new Set(["from", "to", "source", "status", "has_skill", "has_subagent", "has_error", "has_retry", "archive_scope", "cursor", "mode", "settings"]);
  const SESSION_KEYS = new Set(["execution", "node", "analysis", "settings"]);
  const SELECTION_KEYS = new Set(["settings"]);
  const FORBIDDEN = new Set(["q", "model", "limit", "draft", "raw", "repository_url", "repository_path", "search"]);
  const UUID_V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const NODE = /^node-[0-9a-f]{32}$/;
  const CURSOR = /^[A-Za-z0-9_-]{147}$/;
  const TIMESTAMP = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})\.(\d{7})\+00:00$/;
  const SOURCES = new Set(["copilot-sdk", "copilot-cli", "vscode", "hook-unknown", "claude-code"]);
  const STATUSES = new Set(["active", "completed", "failed", "unknown"]);
  const WHOLE_FIELD_TOKENS = [
    "observedpositive", "observedzero", "notobserved", "unsupported", "capturegap",
    "certificationpending", "rawnotcaptured", "notcaptured", "rawexpired", "rawdeleted",
    "rawreaddenied", "inconsistent", "projectioninvalid", "expiredpendingdeletion",
    "malformed", "oversized", "redacted"
  ];
  const IDENTIFIER_TOKENS = [
    "observed_positive", "observed-positive", "observed_zero", "observed-zero",
    "not_observed", "not-observed", "capture_gap", "capture-gap",
    "certification_pending", "certification-pending", "raw_not_captured", "raw-not-captured",
    "not_captured", "not-captured", "raw_expired", "raw-expired", "raw_deleted",
    "raw-deleted", "raw_read_denied", "raw-read-denied", "projection_invalid",
    "projection-invalid", "expired_pending_deletion", "expired-pending-deletion"
  ];
  const routeKind = host.dataset.routeKind;

  function requirePathArity(actual, expected) {
    if (actual !== expected) throw new TypeError("invalid primary path");
  }

  function requireUuidV7(value) {
    if (typeof value !== "string" || !UUID_V7.test(value)) throw new TypeError("invalid primary path");
    return value;
  }

  const pathApi = Object.freeze({
    repositorySelection() {
      requirePathArity(arguments.length, 0);
      return "/";
    },
    repositorySessions(repositoryId) {
      requirePathArity(arguments.length, 1);
      return `/repositories/${requireUuidV7(repositoryId)}/sessions`;
    },
    allSessions() {
      requirePathArity(arguments.length, 0);
      return "/sessions";
    },
    unassignedSessions() {
      requirePathArity(arguments.length, 0);
      return "/sessions/unassigned";
    },
    session(sessionId) {
      requirePathArity(arguments.length, 1);
      return `/sessions/${requireUuidV7(sessionId)}`;
    },
    comparison(repositoryId, comparisonId) {
      requirePathArity(arguments.length, 2);
      return `/repositories/${requireUuidV7(repositoryId)}/comparisons/${requireUuidV7(comparisonId)}`;
    },
  });

  function allowedKeys() {
    if (["RepositorySessions", "AllSessions", "UnassignedSessions"].includes(routeKind)) return EXPLORER_KEYS;
    if (routeKind === "SessionDetail") return SESSION_KEYS;
    return SELECTION_KEYS;
  }

  function values(name) {
    return new URL(window.location.href).searchParams.getAll(name);
  }

  function initialState() {
    const state = {};
    for (const key of allowedKeys()) {
      const found = values(key);
      if ((key === "source" || key === "status") && found.length > 0) state[key] = found.sort();
      else if (found.length === 1) state[key] = found[0];
    }
    return state;
  }

  function validTimestamp(value) {
    const match = TIMESTAMP.exec(value);
    if (!match) return false;
    const year = Number(match[1]);
    const month = Number(match[2]);
    const day = Number(match[3]);
    const hour = Number(match[4]);
    const minute = Number(match[5]);
    const second = Number(match[6]);
    if (year < 1 || month < 1 || month > 12 || hour > 23 || minute > 59 || second > 59) return false;
    const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
    const days = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    return day >= 1 && day <= days[month - 1];
  }

  function canonicalBase64Url(bytes) {
    let binary = "";
    for (const value of bytes) binary += String.fromCharCode(value);
    return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
  }

  function structurallyCanonicalCursor(value) {
    if (!CURSOR.test(value)) return false;
    let bytes;
    try {
      const padding = "=".repeat((4 - value.length % 4) % 4);
      const binary = atob(value.replaceAll("-", "+").replaceAll("_", "/") + padding);
      bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
    } catch { return false; }
    if (bytes.length !== 110 || canonicalBase64Url(bytes) !== value
        || bytes[0] !== 1 || bytes[33] !== 0 && bytes[33] !== 1) return false;
    if (bytes[33] === 1 && bytes.slice(34, 42).some(value => value !== 0)) return false;
    const sessionId = String.fromCharCode(...bytes.slice(42, 78));
    return UUID_V7.test(sessionId);
  }

  function validateScalar(key, value) {
    if (value === null || value === undefined) return;
    if (typeof value !== "string") throw new TypeError("invalid route state");
    const valid = key === "settings" ? SETTINGS.has(value)
      : key === "execution" || key === "analysis" ? UUID_V7.test(value)
      : key === "node" ? NODE.test(value)
      : key === "from" || key === "to" ? validTimestamp(value)
      : key === "has_skill" || key === "has_subagent" || key === "has_error" || key === "has_retry" ? value === "true" || value === "false"
      : key === "archive_scope" ? value === "active_only" || value === "include_archived"
      : key === "cursor" ? structurallyCanonicalCursor(value)
      : key === "mode" ? value === "compare"
      : false;
    if (!valid) throw new TypeError("invalid route state");
  }

  function validatePatch(patch) {
    if (!patch || typeof patch !== "object" || Array.isArray(patch)) throw new TypeError("invalid route state");
    const allowed = allowedKeys();
    for (const [key, value] of Object.entries(patch)) {
      if (FORBIDDEN.has(key) || !allowed.has(key)) throw new TypeError("unsupported route state");
      if (key === "source" || key === "status") {
        if (value === null) continue;
        if (!Array.isArray(value) || value.length > 16 || new Set(value).size !== value.length) throw new TypeError("invalid route state");
        const vocabulary = key === "source" ? SOURCES : STATUSES;
        if (value.some(item => typeof item !== "string" || !vocabulary.has(item))) throw new TypeError("invalid route state");
      } else {
        validateScalar(key, value);
      }
    }
  }

  function validateCursorEligibility(eligibility) {
    if (!eligibility || typeof eligibility !== "object" || Array.isArray(eligibility)) return false;
    const keys = Object.keys(eligibility).sort();
    return keys.length === 3
      && keys[0] === "limit" && keys[1] === "model" && keys[2] === "q"
      && eligibility.q === null
      && Array.isArray(eligibility.model) && eligibility.model.length === 0
      && eligibility.limit === null;
  }

  function validateCombinedState(state, patch, cursorEligibility) {
    validatePatch(state);
    if (state.from && state.to && state.from >= state.to) throw new TypeError("invalid route state");
    if (patch.cursor !== null && patch.cursor !== undefined
        && !validateCursorEligibility(cursorEligibility)) throw new TypeError("cursor eligibility unproven");
    if (state.cursor && patch.cursor === undefined
        && Object.keys(patch).some(key => key !== "settings")) throw new TypeError("cursor eligibility unproven");
  }

  function append(parts, key, value) {
    if (value === null || value === undefined || value === "active_only") return;
    const list = Array.isArray(value) ? [...value].sort() : [value];
    for (const item of list) {
      const encoded = key === "from" || key === "to" ? item.replace("+", "%2B") : item;
      parts.push(`${key}=${encoded}`);
    }
  }

  function buildUrl(state) {
    const parts = [];
    const order = routeKind === "SessionDetail"
      ? ["execution", "node", "analysis", "settings"]
      : ["RepositorySessions", "AllSessions", "UnassignedSessions"].includes(routeKind)
        ? ["from", "to", "source", "status", "has_skill", "has_subagent", "has_error", "has_retry", "archive_scope", "cursor", "mode", "settings"]
        : ["settings"];
    for (const key of order) append(parts, key, state[key]);
    return window.location.pathname + (parts.length ? `?${parts.join("&")}` : "");
  }

  function current() {
    const saved = history.state?.localMonitorV1;
    return saved && typeof saved === "object" ? structuredClone(saved) : initialState();
  }

  function change(patch, replace = false, cursorEligibility) {
    validatePatch(patch);
    const next = current();
    for (const [key, value] of Object.entries(patch)) {
      if (value === null || value === undefined || Array.isArray(value) && value.length === 0) delete next[key];
      else next[key] = Array.isArray(value) ? [...value].sort() : value;
    }
    validateCombinedState(next, patch, cursorEligibility);
    const state = { localMonitorV1: next };
    history[replace ? "replaceState" : "pushState"](state, "", buildUrl(next));
    document.dispatchEvent(new CustomEvent("cao-route-state", { detail: structuredClone(next) }));
    return structuredClone(next);
  }

  const api = Object.freeze({
    current,
    push: (patch, cursorEligibility) => change(patch, false, cursorEligibility),
    replace: (patch, cursorEligibility) => change(patch, true, cursorEligibility),
    setSettings: (section, replace = false) => change({ settings: section }, replace),
    closeSettings: (replace = false) => change({ settings: null }, replace),
  });

  const factText = Object.freeze({
    observed_positive: value => [`${value}件を記録`, null, true],
    observed_zero: () => ["0件", null, true],
    not_observed: () => ["今回の記録にはありません", "この記録では呼び出しを確認できませんでした。実際に使われなかったとは断定できません。", false],
    unsupported: () => ["この取得元では記録できません", null, false],
    capture_gap: () => ["記録が一部欠けています", null, false],
    projection_invalid: () => ["記録が一部欠けています", null, false],
    certification_pending: value => [`${value}件を記録`, "安定して取得できるか未確認です。", true],
    raw_not_captured: () => ["内容は記録されていません", null, false],
    raw_expired: () => ["保存期間を過ぎたため表示できません", null, false],
    raw_deleted: () => ["保存期間を過ぎたため表示できません", null, false],
    raw_read_denied: () => ["保存期間を過ぎたため表示できません", null, false],
    inconsistent: () => ["内訳を表示できません", "記録された値に整合しない項目があります。", false],
  });

  function isAsciiIdentifierCharacter(value) {
    return value !== undefined && /[A-Za-z0-9_-]/.test(value);
  }

  function containsIdentifier(value, token) {
    const normalized = value.toLowerCase();
    let searchStart = 0;
    while (searchStart < normalized.length) {
      const index = normalized.indexOf(token, searchStart);
      if (index < 0) return false;
      const end = index + token.length;
      if (!isAsciiIdentifierCharacter(normalized[index - 1])
          && !isAsciiIdentifierCharacter(normalized[end])) return true;
      searchStart = index + 1;
    }
    return false;
  }

  function containsReservedFactToken(value, textKind) {
    const normalized = value.toLowerCase();
    if (textKind === "source") {
      return WHOLE_FIELD_TOKENS.includes(normalized)
        || IDENTIFIER_TOKENS.some(token => containsIdentifier(normalized, token));
    }
    return IDENTIFIER_TOKENS.some(token => containsIdentifier(normalized, token))
      || WHOLE_FIELD_TOKENS.some(token => containsIdentifier(normalized, token));
  }

  function safeFactText(value, maximum, textKind) {
    if (value === null || value === undefined) return null;
    if (typeof value !== "string") throw new TypeError("invalid fact state");
    const normalized = value.trim();
    if (normalized.length === 0) return null;
    if (normalized.length > maximum || /[\u0000-\u001f\u007f-\u009f]/u.test(normalized)) {
      throw new TypeError("invalid fact state");
    }
    if (containsReservedFactToken(normalized, textKind)) {
      throw new TypeError("invalid fact state");
    }
    return normalized;
  }

  function sentence(value) {
    return value.endsWith("。") ? value : `${value}。`;
  }

  function renderFactState(target, fact) {
    if (!(target instanceof Element) || !fact || !factText[fact.state]) throw new TypeError("invalid fact state");
    let count = fact.recordedCount;
    const source = safeFactText(fact.sourceText, 80, "source");
    const reason = safeFactText(fact.reasonText, 240, "reason");
    const proof = fact.hasCompleteCoverageProof;
    if (proof !== undefined && typeof proof !== "boolean") throw new TypeError("invalid fact state");
    if (fact.state !== "observed_zero" && proof === true) throw new TypeError("invalid fact state");
    if ((fact.state === "observed_positive" || fact.state === "certification_pending")
        && (!Number.isSafeInteger(count) || count <= 0)) throw new TypeError("invalid fact state");
    if (fact.state === "observed_zero" && (!Number.isSafeInteger(count) || count !== 0)) {
      throw new TypeError("invalid fact state");
    }
    if (fact.state === "observed_zero" && proof !== true) {
      fact = { ...fact, state: "not_observed" };
      count = null;
    }
    if (fact.state === "observed_zero" && (!source || !reason)) throw new TypeError("invalid fact state");
    if (fact.state === "unsupported" && (!source || !reason)) throw new TypeError("invalid fact state");
    if (["capture_gap", "projection_invalid", "raw_not_captured", "raw_expired", "raw_deleted", "raw_read_denied"].includes(fact.state)
        && !reason) throw new TypeError("invalid fact state");
    if (["not_observed", "unsupported", "capture_gap", "projection_invalid", "raw_not_captured", "raw_expired", "raw_deleted", "raw_read_denied", "inconsistent"].includes(fact.state)
        && count !== null && count !== undefined) throw new TypeError("invalid fact state");
    const [primary, fixedDetail, allowsDerivedVisualization] = factText[fact.state](count);
    const details = [];
    if (fixedDetail) details.push(sentence(fixedDetail));
    if (source) details.push(`取得元: ${sentence(source)}`);
    if (reason) details.push(sentence(reason));
    const detail = details.length ? details.join("") : null;
    target.replaceChildren();
    const primaryNode = document.createElement("span");
    primaryNode.className = "fact-state-primary";
    primaryNode.textContent = primary;
    target.append(primaryNode);
    if (detail) {
      const detailNode = document.createElement("p");
      detailNode.textContent = detail;
      target.append(detailNode);
    }
    return Object.freeze({ primaryText: primary, detailText: detail, allowsDerivedVisualization });
  }

  if (routeKind) {
    history.replaceState({ localMonitorV1: initialState() }, "", buildUrl(initialState()));
    window.addEventListener("popstate", () => {
      document.dispatchEvent(new CustomEvent("cao-route-state", { detail: current() }));
    });
    window.LocalMonitorV1History = api;
    window.LocalMonitorV1Paths = pathApi;
    document.dispatchEvent(new CustomEvent("cao-route-state", { detail: current() }));
  }
  window.LocalMonitorV1FactState = Object.freeze({ render: renderFactState });
})();
