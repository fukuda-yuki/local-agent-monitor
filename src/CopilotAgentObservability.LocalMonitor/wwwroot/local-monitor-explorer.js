(() => {
  "use strict";

  const root = document.getElementById("local-monitor-session-explorer");
  if (!root || !window.LocalMonitorV1History || !window.LocalMonitorV1Paths) return;

  const UUID_V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const REVISION = /^[0-9a-f]{64}$/;
  const NODE_ID = /^node-[0-9a-f]{32}$/;
  const AI_STATES = new Set(["queued", "running", "succeeded", "zero_findings", "provider_failed", "provider_partial", "invalid_result", "invalid_evidence", "stale_snapshot", "scope_too_large", "timed_out", "canceled"]);
  const SESSION_CURSOR = /^[A-Za-z0-9_-]{147}$/;
  const REPOSITORY_CURSOR = /^[A-Za-z0-9_-]{135}$/;
  const TIMESTAMP = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})\.(\d{7})\+00:00$/;
  const FACT_STATES = new Set([
    "recorded", "not_observed", "source_unsupported", "capture_gap",
    "certification_pending", "not_captured", "expired", "redacted",
    "malformed", "oversized", "inconsistent", "projection_invalid",
  ]);
  const SOURCES = new Set(["copilot-sdk", "copilot-cli", "vscode", "hook-unknown", "claude-code"]);
  const STATUSES = new Set(["active", "completed", "failed", "unknown"]);
  const CAPTURE_NOTES = new Set([
    "raw_content_not_captured", "raw_content_expired", "source_unsupported",
    "capture_gap", "certification_pending", "projection_invalid",
    "token_inconsistent", "cache_inconsistent",
  ]);
  const ROOT_KEYS = ["schema_version", "workspace_revision", "items", "next_cursor"];
  const ITEM_KEYS = [
    "session_id", "assignment", "archive", "label", "status", "completeness",
    "source", "model", "summary", "tokens", "timing", "capture_notes", "workspace_revision",
  ];
  const EXPLORER_ROUTE_KEYS = [
    "from", "to", "source", "status", "has_skill", "has_subagent", "has_error",
    "has_retry", "archive_scope", "cursor", "mode",
  ];
  const ASSIGNMENT_KEYS = ["state", "authority", "revision", "repository_id", "candidate_repository_ids"];
  const ARCHIVE_KEYS = ["state", "revision", "effectively_eligible", "exclusion_reason"];
  const SUMMARY_KEYS = ["skill", "tool", "subagent", "error", "retry"];
  const TOKEN_KEYS = [
    "authority", "state", "available_execution_count", "total_execution_count", "input", "output",
    "total", "reasoning", "cache_read", "cache_creation", "new_input", "cache_read_ratio_basis_points",
  ];
  const REPOSITORY_ROOT_KEYS = [
    "schema_version", "workspace_revision", "repositories", "all_session_count",
    "unassigned_active_session_count", "archived_repository_count", "next_cursor",
  ];
  const REPOSITORY_KEYS = [
    "repository_id", "display_name", "archive_state", "archive_revision",
    "active_session_count", "last_observed_at", "assignment_conflict_count", "repository_revision",
  ];
  const COMPARISON_EXCLUSIONS = new Map([
    ["session_not_found", "セッションが見つかりません"],
    ["repository_mismatch", "対象リポジトリのセッションではありません"],
    ["duplicate", "同じ対象内で重複しています"],
    ["cohort_overlap", "基準と比較対象の両方に含まれています"],
    ["session_archived", "セッションがアーカイブ済みです"],
    ["repository_archived", "リポジトリがアーカイブ済みです"],
    ["projection_unavailable", "比較用データを利用できません"],
    ["unsupported_selection", "このセッションは比較できません"],
    ["workspace_too_large", "比較対象が大きすぎます"],
  ]);
  const state = {
    generation: 0,
    controller: null,
    route: null,
    dynamic: { q: null, model: [], limit: null },
    pendingDynamic: null,
    nextCursor: null,
    memoryCursor: null,
    workspaceRevision: null,
    items: [],
    compareMode: false,
    cohorts: { a: new Set(), b: new Set() },
    excludedSelections: new Set(),
    exclusionReasons: new Map(),
    selectionNotice: null,
    preserveCohortsOnNextRoute: false,
    browserTraversal: false,
    initiatingControl: null,
    focusAfterRender: null,
    comparison: { generation: 0, controller: null, preview: null, selection: null, invoker: null },
  };
  const assignmentPicker = {
    generation: 0,
    controller: null,
    item: null,
    invoker: null,
    repositories: [],
    repositoryIds: new Set(),
    workspaceRevision: null,
    totals: null,
    nextCursor: null,
    selectedRepositoryId: null,
  };
  const mutation = {
    generation: 0,
    controller: null,
    active: false,
  };
  const rows = root.querySelector("#session-explorer-rows");
  const status = root.querySelector("#session-explorer-status");
  const loadMore = root.querySelector("#session-load-more");
  const filters = root.querySelector("#session-explorer-filters");
  const search = root.querySelector("#session-search");
  const model = root.querySelector("#session-model");
  const from = root.querySelector("#session-from");
  const to = root.querySelector("#session-to");
  const sourceFilter = root.querySelector("#session-source");
  const statusFilter = root.querySelector("#session-status");
  const hasSkill = root.querySelector("#session-has-skill");
  const hasSubagent = root.querySelector("#session-has-subagent");
  const hasError = root.querySelector("#session-has-error");
  const hasRetry = root.querySelector("#session-has-retry");
  const limit = root.querySelector("#session-limit");
  const includeArchived = root.querySelector("#session-include-archived");
  const compareButton = root.querySelector("#session-compare-mode");
  const compareBar = root.querySelector("#session-compare-bar");
  const compareValidation = root.querySelector("#session-compare-validation");
  const compareValidationPrimary = root.querySelector("[data-compare-validation-primary]");
  const compareValidationDetails = root.querySelector("[data-compare-validation-details]");
  const compareValidationList = root.querySelector("[data-compare-validation-list]");
  const comparePreview = root.querySelector("#session-compare-preview");
  const comparisonDialog = root.querySelector("#session-comparison-preview-dialog");
  const comparisonStatus = root.querySelector("#session-comparison-preview-status");
  const comparisonCreate = root.querySelector("#session-comparison-create");
  const comparisonCancel = root.querySelector("#session-comparison-cancel");
  const comparisonExcluded = root.querySelector("[data-comparison-preview-excluded]");
  const comparisonExcludedList = root.querySelector("[data-comparison-preview-excluded-list]");
  const assignmentDialog = root.querySelector("#session-assignment-dialog");
  const assignmentForm = root.querySelector("#session-assignment-form");
  const assignmentStatus = root.querySelector("#session-assignment-status");
  const assignmentChoices = root.querySelector("#session-assignment-choices");
  const assignmentLoadMore = root.querySelector("#session-assignment-load-more");
  const assignmentCancel = root.querySelector("#session-assignment-cancel");
  const assignmentSubmit = root.querySelector("#session-assignment-submit");
  const aiOpen = root.querySelector("#session-ai-open");
  const aiDialog = root.querySelector("#session-ai-dialog");
  const aiStatus = root.querySelector("#session-ai-status");
  const aiPreviewContent = root.querySelector("#session-ai-preview-content");
  const aiResult = root.querySelector("#session-ai-result");
  const aiExplicitLabel = root.querySelector("#session-ai-explicit-label");
  const aiPreviewButton = root.querySelector("#session-ai-preview");
  const aiStart = root.querySelector("#session-ai-start");
  const aiCancel = root.querySelector("#session-ai-cancel");
  const aiState = { preview: null, runId: null, frozenIds: [] };

  class ApiFailure extends Error {
    constructor(statusCode, code = null) {
      super("session collection unavailable");
      this.statusCode = statusCode;
      this.code = code;
    }
  }

  function exactKeys(value, keys) {
    if (!value || typeof value !== "object" || Array.isArray(value)) return false;
    const actual = Object.keys(value);
    return actual.length === keys.length && actual.every((key, index) => key === keys[index]);
  }

  function exactKeySet(value, keys) {
    return value && typeof value === "object" && !Array.isArray(value)
      && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
  }

  function boundedText(value, maximum = 16_384) {
    return typeof value === "string" && value.length > 0 && value.length <= maximum && !hasUnpairedSurrogate(value);
  }

  function nonnegativeInteger(value) {
    return typeof value === "bigint" ? value >= 0n : Number.isSafeInteger(value) && value >= 0;
  }

  function validEvidenceLocation(value) {
    if (typeof value !== "string" || value.includes("#") || value.includes("%")) return false;
    const match = /^\/sessions\/([0-9a-f-]{36})(?:\?execution=([0-9a-f-]{36})(?:&node=(node-[0-9a-f]{32}))?|\?node=(node-[0-9a-f]{32}))?$/.exec(value);
    return !!match && UUID_V7.test(match[1]) && (!match[2] || UUID_V7.test(match[2])) && (!match[3] || NODE_ID.test(match[3])) && (!match[4] || NODE_ID.test(match[4]));
  }

  function validateAiResult(result) {
    const rootKeys = ["scope", "snapshot", "summary", "findings", "improvement_suggestions", "limitations", "provenance"];
    if (!exactKeySet(result, rootKeys) || !exactKeySet(result.scope, ["kind", "repository_id", "anchor_id"])
        || result.scope.kind !== "repository_selection" || result.scope.repository_id !== root.dataset.repositoryId
        || !boundedText(result.scope.anchor_id) || !exactKeySet(result.snapshot, ["snapshot_id", "payload_sha256"])
        || !UUID_V7.test(result.snapshot.snapshot_id) || !REVISION.test(result.snapshot.payload_sha256)
        || !boundedText(result.summary, 65_536) || !Array.isArray(result.findings) || result.findings.length > 200
        || !Array.isArray(result.improvement_suggestions) || result.improvement_suggestions.length > 200
        || !Array.isArray(result.limitations) || result.limitations.length > 200) return false;
    const refs = value => Array.isArray(value) && value.length >= 1 && value.length <= 16
      && new Set(value).size === value.length && value.every(validEvidenceLocation);
    for (const finding of result.findings) {
      if (!exactKeySet(finding, ["finding_id", "title", "explanation", "evidence_state", "evidence_refs", "limitation"])
          || !["finding_id", "title", "explanation", "limitation"].every(key => boundedText(finding[key]))
          || !["supported", "limited"].includes(finding.evidence_state) || !refs(finding.evidence_refs)) return false;
    }
    const targets = new Set(["instructions", "skill", "agent", "subagent_input", "tool_configuration"]);
    for (const suggestion of result.improvement_suggestions) {
      if (!exactKeySet(suggestion, ["suggestion_id", "target_kind", "target_label", "concrete_change", "rationale", "expected_effect", "risks_or_limitations", "evidence_refs"])
          || !targets.has(suggestion.target_kind) || !["suggestion_id", "target_label", "concrete_change", "rationale", "expected_effect", "risks_or_limitations"].every(key => boundedText(suggestion[key]))
          || !refs(suggestion.evidence_refs)) return false;
    }
    if (!result.limitations.every(value => boundedText(value))) return false;
    const provenanceKeys = ["provider", "model", "configuration_sha256", "prompt_template_version", "requested_at", "started_at", "completed_at", "snapshot_id", "snapshot_sha256", "coverage"];
    return exactKeySet(result.provenance, provenanceKeys) && ["provider", "model", "prompt_template_version", "requested_at", "started_at", "completed_at"].every(key => boundedText(result.provenance[key]))
      && REVISION.test(result.provenance.configuration_sha256) && UUID_V7.test(result.provenance.snapshot_id) && REVISION.test(result.provenance.snapshot_sha256)
      && result.provenance.snapshot_id === result.snapshot.snapshot_id && result.provenance.snapshot_sha256 === result.snapshot.payload_sha256
      && exactKeySet(result.provenance.coverage, ["included", "excluded", "content_available"])
      && nonnegativeInteger(result.provenance.coverage.included)
      && nonnegativeInteger(result.provenance.coverage.excluded)
      && typeof result.provenance.coverage.content_available === "boolean";
  }

  function validateRepositoryRun(value, expectedRunId) {
    if (!exactKeySet(value, ["run_id", "state", "scope_kind", "session_id", "node_id", "repository_id", "error", "result"])
        || !UUID_V7.test(value.run_id) || value.run_id !== expectedRunId || !AI_STATES.has(value.state)
        || value.scope_kind !== "repository_selection" || value.session_id !== null || value.node_id !== null
        || value.repository_id !== root.dataset.repositoryId || !(value.error === null || boundedText(value.error))) return false;
    return value.result === null || value.state === "succeeded" && validateAiResult(value.result);
  }

  function count(value) {
    return typeof value === "bigint" && value >= 0n;
  }

  function parseClosedJson(text) {
    let position = 0;
    let depth = 0;
    const fail = () => { throw new TypeError("invalid JSON response"); };
    const whitespace = () => {
      if (position < text.length && /[\u0009\u000a\u000d\u0020]/.test(text[position])) fail();
    };
    const stringValue = () => {
      const start = position++;
      let escaped = false;
      while (position < text.length) {
        const code = text.charCodeAt(position);
        const character = text[position++];
        if (!escaped && character === '"') {
          let value;
          try { value = JSON.parse(text.slice(start, position)); } catch { fail(); }
          for (let index = 0; index < value.length; index++) {
            const unit = value.charCodeAt(index);
            if (unit >= 0xd800 && unit <= 0xdbff) {
              const next = value.charCodeAt(++index);
              if (next < 0xdc00 || next > 0xdfff) fail();
            } else if (unit >= 0xdc00 && unit <= 0xdfff) fail();
          }
          return value;
        }
        if (!escaped && code < 0x20) fail();
        if (!escaped && character === "\\") escaped = true;
        else escaped = false;
      }
      return fail();
    };
    const numberValue = () => {
      const match = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(text.slice(position));
      if (!match) return fail();
      position += match[0].length;
      if (!/[.eE]/.test(match[0])) return BigInt(match[0]);
      const value = Number(match[0]);
      if (!Number.isFinite(value)) fail();
      return value;
    };
    const value = () => {
      whitespace();
      if (++depth > 32 || position >= text.length) fail();
      let result;
      const first = text[position];
      if (first === '"') result = stringValue();
      else if (first === "{") {
        position++;
        whitespace();
        result = Object.create(null);
        const keys = new Set();
        if (text[position] === "}") position++;
        else {
          while (true) {
            whitespace();
            if (text[position] !== '"') fail();
            const key = stringValue();
            if (keys.has(key)) fail();
            keys.add(key);
            whitespace();
            if (text[position++] !== ":") fail();
            result[key] = value();
            whitespace();
            const separator = text[position++];
            if (separator === "}") break;
            if (separator !== ",") fail();
          }
        }
      } else if (first === "[") {
        position++;
        whitespace();
        result = [];
        if (text[position] === "]") position++;
        else {
          while (true) {
            result.push(value());
            whitespace();
            const separator = text[position++];
            if (separator === "]") break;
            if (separator !== ",") fail();
          }
        }
      } else if (text.startsWith("true", position)) { position += 4; result = true; }
      else if (text.startsWith("false", position)) { position += 5; result = false; }
      else if (text.startsWith("null", position)) { position += 4; result = null; }
      else result = numberValue();
      depth--;
      return result;
    };
    const result = value();
    whitespace();
    if (position !== text.length) fail();
    return result;
  }

  function stringifyExactJson(value) {
    if (value === null || typeof value === "boolean" || typeof value === "string") return JSON.stringify(value);
    if (typeof value === "bigint") return value.toString(10);
    if (typeof value === "number" && Number.isSafeInteger(value)) return String(value);
    if (Array.isArray(value)) return `[${value.map(stringifyExactJson).join(",")}]`;
    if (value && typeof value === "object") {
      return `{${Object.entries(value).map(([key, member]) => `${JSON.stringify(key)}:${stringifyExactJson(member)}`).join(",")}}`;
    }
    throw new TypeError("invalid JSON request");
  }

  async function readJsonResponse(response, maximumBytes, requireUnsafeRelaxedBytes = true) {
    const bytes = await response.arrayBuffer();
    if (bytes.byteLength > maximumBytes) throw new TypeError("invalid JSON response");
    const text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
    const value = parseClosedJson(text);
    if (requireUnsafeRelaxedBytes && stringifyExactJson(value) !== text) throw new TypeError("invalid JSON response");
    return value;
  }

  async function apiFailure(response) {
    const value = await readJsonResponse(response, 512);
    if (!exactKeys(value, ["error"]) || typeof value.error !== "string") {
      throw new TypeError("invalid error response");
    }
    return new ApiFailure(response.status, value.error);
  }

  function greaterThan(value, maximum) {
    return value > BigInt(maximum);
  }

  function increment(value) {
    return value + 1n;
  }

  function formatInteger(value) {
    return value.toLocaleString("ja-JP");
  }

  function formatDurationMilliseconds(value) {
    const whole = value / 1000n;
    const remainder = (value % 1000n).toString(10).padStart(3, "0").replace(/0+$/, "");
    return remainder.length === 0 ? formatInteger(whole) : `${formatInteger(whole)}.${remainder}`;
  }

  function timestamp(value) {
    if (value === null) return true;
    const match = typeof value === "string" ? TIMESTAMP.exec(value) : null;
    if (!match || Number(match[1]) < 1) return false;
    const instant = new Date(0);
    instant.setUTCFullYear(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
    instant.setUTCHours(Number(match[4]), Number(match[5]), Number(match[6]), Number(match[7].slice(0, 3)));
    return Number.isFinite(instant.valueOf())
      && instant.getUTCFullYear() === Number(match[1])
      && instant.getUTCMonth() + 1 === Number(match[2])
      && instant.getUTCDate() === Number(match[3])
      && instant.getUTCHours() === Number(match[4])
      && instant.getUTCMinutes() === Number(match[5])
      && instant.getUTCSeconds() === Number(match[6]);
  }

  function countFact(value, member, maximum) {
    if (!exactKeys(value, ["state", member]) || !FACT_STATES.has(value.state)) throw new TypeError("invalid session collection");
    const recorded = value.state === "recorded";
    const supplied = value[member];
    if (recorded !== (count(supplied) && (maximum === undefined || !greaterThan(supplied, maximum)))
        || !recorded && supplied !== null) throw new TypeError("invalid session collection");
    return value;
  }

  function tokenComponent(value, maximum) {
    if (!exactKeys(value, ["state", "value"]) || !FACT_STATES.has(value.state)
        || value.value !== null && (!count(value.value) || maximum !== undefined && greaterThan(value.value, maximum))) {
      throw new TypeError("invalid session collection");
    }
    return value;
  }

  function setFact(value, maximum) {
    if (!exactKeys(value, ["state", "values"]) || !FACT_STATES.has(value.state)
        || !Array.isArray(value.values) || value.values.length > maximum
        || value.values.some(item => typeof item !== "string")
        || value.values.some(item => item.length === 0)
        || new Set(value.values).size !== value.values.length
        || value.values.some((item, index) => index > 0 && value.values[index - 1] >= item)
        || value.state === "recorded" && value.values.length === 0) {
      throw new TypeError("invalid session collection");
    }
    return value;
  }

  function validateItem(value) {
    if (!exactKeys(value, ITEM_KEYS) || !UUID_V7.test(value.session_id)
        || !REVISION.test(value.workspace_revision)
        || !STATUSES.has(value.status)
        || !new Set(["unbound", "partial", "rich", "full"]).has(value.completeness)) {
      throw new TypeError("invalid session collection");
    }
    const assignment = value.assignment;
    if (!exactKeys(assignment, ASSIGNMENT_KEYS)
        || !new Set(["assigned", "unassigned", "explicitly_unassigned", "conflict"]).has(assignment.state)
        || !new Set(["automatic", "manual", "none"]).has(assignment.authority)
        || !count(assignment.revision)
        || assignment.repository_id !== null && !UUID_V7.test(assignment.repository_id)
        || !Array.isArray(assignment.candidate_repository_ids)
        || assignment.candidate_repository_ids.length > 128
        || assignment.candidate_repository_ids.some(item => !UUID_V7.test(item))
        || new Set(assignment.candidate_repository_ids).size !== assignment.candidate_repository_ids.length
        || assignment.candidate_repository_ids.some((item, index) =>
          index > 0 && assignment.candidate_repository_ids[index - 1] >= item)) {
      throw new TypeError("invalid session collection");
    }
    const validAssignment = assignment.state === "assigned"
        && (assignment.authority === "automatic" || assignment.authority === "manual")
        && assignment.repository_id !== null && assignment.candidate_repository_ids.length === 0
      || assignment.state === "unassigned" && assignment.authority === "none"
        && assignment.repository_id === null && assignment.candidate_repository_ids.length === 0
      || assignment.state === "explicitly_unassigned" && assignment.authority === "manual"
        && assignment.repository_id === null && assignment.candidate_repository_ids.length === 0
      || assignment.state === "conflict" && assignment.authority === "automatic"
        && assignment.repository_id === null && assignment.candidate_repository_ids.length >= 2;
    if (!validAssignment) throw new TypeError("invalid session collection");
    const archive = value.archive;
    if (!exactKeys(archive, ARCHIVE_KEYS)
        || !new Set(["active", "archived"]).has(archive.state)
        || !count(archive.revision)
        || typeof archive.effectively_eligible !== "boolean"
        || archive.exclusion_reason !== null
          && !new Set(["session_archived", "repository_archived"]).has(archive.exclusion_reason)
        || archive.effectively_eligible !== (archive.exclusion_reason === null)
        || (archive.state === "archived") !== (archive.exclusion_reason === "session_archived")
        || archive.exclusion_reason === "repository_archived"
          && (assignment.state !== "assigned" || assignment.repository_id === null)) {
      throw new TypeError("invalid session collection");
    }
    const label = value.label;
    if (!exactKeys(label, ["state", "text"]) || !FACT_STATES.has(label.state)
        || (label.state === "recorded"
          ? typeof label.text !== "string" || Array.from(label.text).length > 160 || label.text.length === 0
          : label.text !== null)) {
      throw new TypeError("invalid session collection");
    }
    setFact(value.source, 5);
    setFact(value.model, 16);
    if (value.source.state === "recorded" && value.source.values.some(item => !SOURCES.has(item)))
      throw new TypeError("invalid session collection");
    if (!exactKeys(value.summary, SUMMARY_KEYS)) throw new TypeError("invalid session collection");
    for (const name of SUMMARY_KEYS) countFact(value.summary[name], "count");
    const tokens = value.tokens;
    if (!exactKeys(tokens, TOKEN_KEYS)
        || !new Set(["session_run", "llm_span", "mixed", "none"]).has(tokens.authority)
        || !FACT_STATES.has(tokens.state)
        || !count(tokens.available_execution_count)
        || !count(tokens.total_execution_count)
        || tokens.available_execution_count > tokens.total_execution_count) throw new TypeError("invalid session collection");
    for (const name of ["input", "output", "total", "reasoning", "cache_read", "cache_creation", "new_input"])
      tokenComponent(tokens[name]);
    tokenComponent(tokens.cache_read_ratio_basis_points, 10000);
    const input = tokens.input;
    const cacheRead = tokens.cache_read;
    const derivedInputsValid = input.state === "recorded" && count(input.value)
      && cacheRead.state === "recorded" && count(cacheRead.value) && cacheRead.value <= input.value;
    const contradictoryInputs = input.state === "recorded" && count(input.value)
      && cacheRead.state === "recorded" && count(cacheRead.value) && cacheRead.value > input.value;
    if ((tokens.new_input.state === "recorded"
          && (!derivedInputsValid || tokens.new_input.value !== input.value - cacheRead.value))
        || (tokens.cache_read_ratio_basis_points.state === "recorded"
          && (!derivedInputsValid || input.value === 0n || tokens.cache_read_ratio_basis_points.value === null))
        || contradictoryInputs
          && (tokens.new_input.state !== "inconsistent" || tokens.new_input.value !== null
            || tokens.cache_read_ratio_basis_points.state !== "inconsistent"
            || tokens.cache_read_ratio_basis_points.value !== null)) {
      throw new TypeError("invalid session collection");
    }
    const timing = value.timing;
    if (!exactKeys(timing, ["state", "started_at", "ended_at", "duration_ms"])
        || !FACT_STATES.has(timing.state)
        || !timestamp(timing.started_at) || !timestamp(timing.ended_at)
        || timing.duration_ms !== null && !count(timing.duration_ms)
        || timing.state === "recorded" && (timing.started_at === null || timing.ended_at === null || timing.duration_ms === null)
        || timing.ended_at !== null && timing.started_at === null
        || timing.started_at !== null && timing.ended_at !== null
          && timing.ended_at < timing.started_at
        || timing.duration_ms !== null && (timing.started_at === null || timing.ended_at === null)) {
      throw new TypeError("invalid session collection");
    }
    if (!Array.isArray(value.capture_notes) || value.capture_notes.length > 16
        || value.capture_notes.some(item => !CAPTURE_NOTES.has(item))
        || new Set(value.capture_notes).size !== value.capture_notes.length
        || value.capture_notes.some((item, index) => index > 0 && value.capture_notes[index - 1] >= item)) {
      throw new TypeError("invalid session collection");
    }
    return Object.freeze(value);
  }

  function validateCollection(value, effectiveLimit) {
    if (!exactKeys(value, ROOT_KEYS)
        || value.schema_version !== "local-monitor-sessions.response.v1"
        || typeof value.workspace_revision !== "string" || !REVISION.test(value.workspace_revision)
        || !Array.isArray(value.items) || value.items.length > effectiveLimit
        || value.next_cursor !== null && value.items.length !== effectiveLimit
        || value.next_cursor !== null
          && (typeof value.next_cursor !== "string" || !SESSION_CURSOR.test(value.next_cursor))) {
      throw new TypeError("invalid session collection");
    }
    const items = value.items.map(validateItem);
    if (new Set(items.map(item => item.session_id)).size !== items.length) throw new TypeError("invalid session collection");
    return Object.freeze({ workspaceRevision: value.workspace_revision, items: Object.freeze(items), nextCursor: value.next_cursor });
  }

  function validDisplayName(value) {
    return typeof value === "string" && !hasUnpairedSurrogate(value)
      && Array.from(value).length >= 1 && Array.from(value).length <= 200;
  }

  function validateRepository(value) {
    if (!exactKeys(value, REPOSITORY_KEYS)
        || !UUID_V7.test(value.repository_id)
        || !validDisplayName(value.display_name)
        || !new Set(["active", "archived"]).has(value.archive_state)
        || !count(value.archive_revision)
        || !count(value.active_session_count)
        || !timestamp(value.last_observed_at)
        || !count(value.assignment_conflict_count)
        || typeof value.repository_revision !== "string" || !REVISION.test(value.repository_revision)) {
      throw new TypeError("invalid repository collection");
    }
    return Object.freeze(value);
  }

  function validateRepositoryCollection(value) {
    if (!exactKeys(value, REPOSITORY_ROOT_KEYS)
        || value.schema_version !== "local-monitor-repositories.response.v1"
        || typeof value.workspace_revision !== "string" || !REVISION.test(value.workspace_revision)
        || !Array.isArray(value.repositories) || value.repositories.length > 200
        || value.next_cursor !== null && value.repositories.length !== 200
        || value.next_cursor !== null
          && (typeof value.next_cursor !== "string" || !REPOSITORY_CURSOR.test(value.next_cursor))
        || !count(value.all_session_count)
        || !count(value.unassigned_active_session_count)
        || !count(value.archived_repository_count)) {
      throw new TypeError("invalid repository collection");
    }
    const repositories = value.repositories.map(validateRepository);
    if (new Set(repositories.map(item => item.repository_id)).size !== repositories.length
        || repositories.some((item, index) => index > 0
          && repositories[index - 1].repository_id >= item.repository_id)) {
      throw new TypeError("invalid repository collection");
    }
    return Object.freeze({
      workspaceRevision: value.workspace_revision,
      repositories: Object.freeze(repositories),
      totals: Object.freeze([
        value.all_session_count, value.unassigned_active_session_count, value.archived_repository_count,
      ]),
      nextCursor: value.next_cursor,
    });
  }

  function bool(value) {
    return value === "true" ? true : value === "false" ? false : null;
  }

  function selected(select) {
    return [...select.selectedOptions].map(option => option.value);
  }

  function hasUnpairedSurrogate(value) {
    for (let index = 0; index < value.length; index++) {
      const unit = value.charCodeAt(index);
      if (unit >= 0xd800 && unit <= 0xdbff) {
        const next = value.charCodeAt(++index);
        if (next < 0xdc00 || next > 0xdfff) return true;
      } else if (unit >= 0xdc00 && unit <= 0xdfff) return true;
    }
    return false;
  }

  function utf8Length(value) {
    return new TextEncoder().encode(value).byteLength;
  }

  function validQueryText(value) {
    if (hasUnpairedSurrogate(value) || Array.from(value).length < 1 || Array.from(value).length > 200
        || utf8Length(value) > 800) return false;
    const normalized = value.normalize("NFKC").toLowerCase();
    return normalized.length > 0 && utf8Length(normalized) <= 800;
  }

  function validModelText(value) {
    const scalars = hasUnpairedSurrogate(value) ? 0 : Array.from(value).length;
    return scalars >= 1 && scalars <= 128 && utf8Length(value) <= 256
      && !/[\u0000-\u001f\u007f-\u009f\u2028\u2029]/u.test(value);
  }

  function sameValues(left, right) {
    const a = Array.isArray(left) ? left : [];
    return a.length === right.length && a.every((value, index) => value === right[index]);
  }

  function requestBody(cursor) {
    const route = state.route ?? {};
    return {
      schema_version: "local-monitor-session-search.request.v1",
      scope: root.dataset.explorerScope,
      repository_id: root.dataset.explorerScope === "repository" ? root.dataset.repositoryId : null,
      archive_scope: route.archive_scope ?? "active_only",
      from: route.from ?? null,
      to: route.to ?? null,
      source: Array.isArray(route.source) ? [...route.source] : [],
      model: [...state.dynamic.model],
      status: Array.isArray(route.status) ? [...route.status] : [],
      has_skill: bool(route.has_skill),
      has_subagent: bool(route.has_subagent),
      has_error: bool(route.has_error),
      has_retry: bool(route.has_retry),
      q: state.dynamic.q,
      cursor,
      limit: state.dynamic.limit,
    };
  }

  async function aiJson(path, body) {
    const response = await fetch(path, { method: "POST", headers: { "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" }, body: stringifyExactJson(body), cache: "no-store", credentials: "same-origin" });
    if (!response.ok) throw await apiFailure(response);
    return readJsonResponse(response, 8_388_608);
  }

  function renderAiMetadata(preview) {
    aiPreviewContent.replaceChildren();
    for (const [heading, values] of [["対象", preview.included], ["除外", preview.excluded]]) {
      const section = element("section");
      section.append(element("h3", null, `${heading} ${values.length}件`));
      const list = element("ul");
      for (const value of values) {
        const item = element("li");
        item.textContent = [value.session_archive_state, value.session_archive_revision, value.archive_exclusion_reason ?? value.reason, value.source, value.model, value.completeness, value.content_state, value.workspace_revision, value.truncated].filter(x => x !== null && x !== undefined).flat().join(" / ");
        list.append(item);
      }
      section.append(list); aiPreviewContent.append(section);
    }
  }

  async function previewAiSelection() {
    aiStart.disabled = true; aiStatus.textContent = "分析対象を確認しています。";
    const mode = root.querySelector("input[name='session-ai-mode']:checked")?.value ?? "filter";
    const selection = mode === "explicit"
      ? { kind: "explicit", archive_scope: state.route?.archive_scope ?? "active_only", session_ids: [...aiState.frozenIds] }
      : { kind: "filter", request: { ...requestBody(null), cursor: null, limit: null } };
    try {
      const value = await aiJson("/api/local-monitor/v1/ai/repository-preview", { schema_version: "local-ai-repository-preview.request.v1", repository_id: root.dataset.repositoryId, selection });
      if (!exactKeys(value, ["schema_version", "snapshot_id", "payload_sha256", "expires_at", "included", "excluded", "truncated"])
          || value.schema_version !== "local-ai-repository-preview.response.v1" || !UUID_V7.test(value.snapshot_id)
          || !REVISION.test(value.payload_sha256) || typeof value.expires_at !== "string"
          || !Array.isArray(value.included) || !Array.isArray(value.excluded) || value.truncated !== false) throw new TypeError("invalid preview");
      aiState.preview = value; renderAiMetadata(value);
      aiStatus.textContent = value.included.length === 0 ? "分析できる対象がありません。" : "この対象で分析を開始できます。";
      aiStart.disabled = value.included.length === 0;
    } catch (error) { aiStatus.textContent = error instanceof ApiFailure && error.code === "scope_too_large" ? "対象が200件を超えています。" : "分析対象を確認できませんでした。"; }
  }

  async function pollAiRun() {
    try {
      const response = await fetch(`/api/local-monitor/v1/ai/runs/${aiState.runId}`, { cache: "no-store", credentials: "same-origin" });
      if (!response.ok) throw await apiFailure(response); const value = await response.json();
      if (!validateRepositoryRun(value, aiState.runId)) throw new TypeError("invalid repository run");
      if (["queued", "running"].includes(value.state)) { setTimeout(pollAiRun, 250); return; }
      aiCancel.hidden = true; aiStatus.textContent = value.state === "succeeded" ? "分析が完了しました。" : `分析は ${value.state} で終了しました。`;
      aiResult.replaceChildren(); if (value.result) renderRepositoryAiResult(value.result);
    } catch { aiCancel.hidden = true; aiStatus.textContent = "分析結果を取得できませんでした。一覧は引き続き利用できます。"; }
  }

  function appendAiValue(target, label, value) {
    const row = element("p"); row.append(element("strong", null, `${label}: `), document.createTextNode(String(value))); target.append(row);
  }

  function appendEvidence(target, references) {
    const list = element("ul");
    for (const reference of references) { const item = element("li");
      if (validEvidenceLocation(reference)) { const link = element("a", null, "正確な証拠を開く"); link.href = reference; item.append(link); }
      else item.textContent = "この証拠は利用できません"; list.append(item);
    }
    target.append(list);
  }

  function renderRepositoryAiResult(result) {
    aiResult.append(element("h3", null, "AI による解釈（Explorer の事実ではありません）"));
    const scope = element("section"); scope.append(element("h4", null, "対象範囲とスナップショット"));
    appendAiValue(scope, "scope", result.scope.kind); appendAiValue(scope, "repository", result.scope.repository_id);
    appendAiValue(scope, "snapshot", result.snapshot.snapshot_id); appendAiValue(scope, "payload SHA-256", result.snapshot.payload_sha256); aiResult.append(scope);
    aiResult.append(element("h4", null, "要約"), element("p", null, result.summary));
    const findings = element("section"); findings.append(element("h4", null, "指摘"));
    for (const finding of result.findings) { const article = element("article"); article.append(element("h5", null, finding.title), element("p", null, finding.explanation)); appendAiValue(article, "evidence state", finding.evidence_state); appendAiValue(article, "limitation", finding.limitation); appendEvidence(article, finding.evidence_refs); findings.append(article); } aiResult.append(findings);
    const suggestions = element("section"); suggestions.append(element("h4", null, "改善案"));
    for (const suggestion of result.improvement_suggestions) { const article = element("article"); appendAiValue(article, "target", suggestion.target_label); appendAiValue(article, "change", suggestion.concrete_change); appendAiValue(article, "rationale", suggestion.rationale); appendAiValue(article, "expected effect", suggestion.expected_effect); appendAiValue(article, "risks", suggestion.risks_or_limitations); appendEvidence(article, suggestion.evidence_refs); suggestions.append(article); } aiResult.append(suggestions);
    const limitations = element("section"); limitations.append(element("h4", null, "制約")); for (const value of result.limitations) limitations.append(element("p", null, value)); aiResult.append(limitations);
    const provenance = element("section"); provenance.append(element("h4", null, "来歴")); for (const key of ["provider", "model", "prompt_template_version", "requested_at", "started_at", "completed_at"]) appendAiValue(provenance, key, result.provenance[key]); aiResult.append(provenance);
  }

  async function startAiRun() {
    if (!aiState.preview) return; aiStart.disabled = true;
    try { const value = await aiJson("/api/local-monitor/v1/ai/repository-runs", { schema_version: "local-ai-repository-run.request.v1", snapshot_id: aiState.preview.snapshot_id, payload_sha256: aiState.preview.payload_sha256, timeout_seconds: 120 });
      if (!UUID_V7.test(value.run_id)) throw new TypeError("invalid run"); aiState.runId = value.run_id; aiCancel.hidden = false; aiStatus.textContent = "分析しています。";
      try { window.LocalMonitorV1History.push({ analysis: value.run_id }); } catch { }
      pollAiRun();
    } catch { aiStatus.textContent = "分析を開始できませんでした。一覧は引き続き利用できます。"; }
  }

  async function enableRepositoryAi() {
    if (root.dataset.explorerScope !== "repository" || !UUID_V7.test(root.dataset.repositoryId ?? "")) return;
    try { const response = await fetch("/api/local-monitor/v1/settings/ai-readiness", { cache: "no-store", credentials: "same-origin" }); const value = response.ok ? await response.json() : null; if (value?.readiness_state === "ready") aiOpen.hidden = false; } catch { }
  }

  async function restoreRepositoryAnalysis(route) {
    const runId = route?.analysis;
    if (!UUID_V7.test(runId ?? "") || runId === aiState.runId) return;
    aiState.runId = runId;
    try {
      const response = await fetch(`/api/local-monitor/v1/ai/runs/${runId}`, { cache: "no-store", credentials: "same-origin" });
      if (!response.ok) throw await apiFailure(response); const value = await response.json();
      if (!validateRepositoryRun(value, runId)) throw new TypeError("invalid repository run");
      aiDialog.showModal(); aiStatus.textContent = value.state === "succeeded" ? "保存された分析を表示しています。" : `分析は ${value.state} です。`;
      aiResult.replaceChildren(); if (value.result) renderRepositoryAiResult(value.result); else if (["queued", "running"].includes(value.state)) { aiCancel.hidden = false; pollAiRun(); }
    } catch { aiState.runId = null; aiStatus.textContent = "保存された分析を復元できませんでした。"; }
  }

  async function readCollection(cursor, signal) {
    const body = requestBody(cursor);
    const response = await fetch("/api/local-monitor/v1/sessions", {
      method: "POST",
      headers: { "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" },
      body: stringifyExactJson(body),
      cache: "no-store",
      credentials: "same-origin",
      signal,
    });
    if (!response.ok) throw await apiFailure(response);
    return validateCollection(await readJsonResponse(response, 8_388_608), body.limit ?? 50);
  }

  async function readRepositoryPage(cursor, signal) {
    const after = cursor === null ? "" : `&after=${encodeURIComponent(cursor)}`;
    const response = await fetch(
      `/api/local-monitor/v1/repositories?archive_scope=include_archived${after}&limit=200`,
      { cache: "no-store", credentials: "same-origin", signal });
    if (!response.ok) throw await apiFailure(response);
    return validateRepositoryCollection(await readJsonResponse(response, 8_388_608));
  }

  function element(name, className, text) {
    const node = document.createElement(name);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function sameCounts(left, right) {
    return left !== null && left.length === right.length
      && left.every((value, index) => value === right[index]);
  }

  function resetAssignmentPicker(item, invoker) {
    assignmentPicker.controller?.abort();
    assignmentPicker.item = item;
    assignmentPicker.invoker = invoker;
    assignmentPicker.repositories = [];
    assignmentPicker.repositoryIds.clear();
    assignmentPicker.workspaceRevision = null;
    assignmentPicker.totals = null;
    assignmentPicker.nextCursor = null;
    assignmentPicker.selectedRepositoryId = item.assignment.repository_id;
    assignmentChoices.replaceChildren();
    assignmentLoadMore.hidden = true;
    assignmentSubmit.disabled = true;
  }

  function repositoryChoiceLabel(repository, item) {
    const parts = [repository.display_name, `ローカルID ${repository.repository_id}`];
    if (item.assignment.repository_id === repository.repository_id) parts.push("現在の割り当て先");
    if (item.assignment.candidate_repository_ids.includes(repository.repository_id)) parts.push("記録された候補");
    if (repository.archive_state === "archived") parts.push("アーカイブ済み");
    return parts.join("、");
  }

  function renderAssignmentChoices(focusLoadMore = false) {
    const item = assignmentPicker.item;
    if (item === null) return;
    const controls = assignmentPicker.repositories.map(repository => {
      const label = element("label", "local-monitor-session-assignment-choice");
      const radio = element("input");
      radio.type = "radio";
      radio.name = "repository_id";
      radio.value = repository.repository_id;
      radio.disabled = repository.archive_state === "archived";
      radio.checked = assignmentPicker.selectedRepositoryId === repository.repository_id;
      radio.setAttribute("aria-label", repositoryChoiceLabel(repository, item));
      radio.addEventListener("change", () => {
        assignmentPicker.selectedRepositoryId = radio.value;
        assignmentSubmit.disabled = radio.value === item.assignment.repository_id;
      });
      const name = element("span", "local-monitor-session-assignment-name", repository.display_name);
      const evidence = element("small", null, `ローカルID ${repository.repository_id}`);
      if (item.assignment.repository_id === repository.repository_id) {
        evidence.append(document.createTextNode(" · 現在の割り当て先"));
      }
      if (item.assignment.candidate_repository_ids.includes(repository.repository_id)) {
        evidence.append(document.createTextNode(" · 記録された候補"));
      }
      if (repository.archive_state === "archived") evidence.append(document.createTextNode(" · アーカイブ済み"));
      label.append(radio, name, evidence);
      return label;
    });
    assignmentChoices.replaceChildren(...controls);
    assignmentSubmit.disabled = assignmentPicker.selectedRepositoryId === null
      || assignmentPicker.selectedRepositoryId === item.assignment.repository_id;
    assignmentLoadMore.hidden = assignmentPicker.nextCursor === null;
    assignmentStatus.textContent = controls.length === 0
      ? "割り当て可能なリポジトリはありません。"
      : `${controls.length.toLocaleString("ja-JP")}件のリポジトリを表示しています。`;
    if (focusLoadMore) {
      const target = assignmentLoadMore.hidden ? assignmentStatus : assignmentLoadMore;
      target.tabIndex = target === assignmentStatus ? -1 : target.tabIndex;
      target.focus({ preventScroll: true });
    } else {
      assignmentChoices.querySelector("input:not(:disabled)")?.focus({ preventScroll: true });
    }
  }

  async function loadAssignmentChoices(cursor, focusLoadMore = false) {
    assignmentPicker.controller?.abort();
    const controller = new AbortController();
    assignmentPicker.controller = controller;
    const generation = ++assignmentPicker.generation;
    assignmentStatus.textContent = cursor === null
      ? "リポジトリを読み込んでいます。"
      : "候補をさらに読み込んでいます。";
    try {
      const page = await readRepositoryPage(cursor, controller.signal);
      if (controller.signal.aborted || generation !== assignmentPicker.generation) return;
      if (assignmentPicker.workspaceRevision !== null
          && (assignmentPicker.workspaceRevision !== page.workspaceRevision
            || !sameCounts(assignmentPicker.totals, page.totals))) {
        throw new TypeError("incoherent repository collection");
      }
      const previous = assignmentPicker.repositories.at(-1);
      if (previous && page.repositories.length > 0
          && previous.repository_id >= page.repositories[0].repository_id) {
        throw new TypeError("incoherent repository collection");
      }
      if (page.repositories.some(repository => assignmentPicker.repositoryIds.has(repository.repository_id))) {
        throw new TypeError("incoherent repository collection");
      }
      assignmentPicker.workspaceRevision ??= page.workspaceRevision;
      assignmentPicker.totals ??= page.totals;
      for (const repository of page.repositories) assignmentPicker.repositoryIds.add(repository.repository_id);
      assignmentPicker.repositories.push(...page.repositories);
      assignmentPicker.nextCursor = page.nextCursor;
      renderAssignmentChoices(focusLoadMore);
    } catch (error) {
      if (controller.signal.aborted || generation !== assignmentPicker.generation) return;
      assignmentChoices.replaceChildren();
      assignmentLoadMore.hidden = true;
      assignmentSubmit.disabled = true;
      assignmentStatus.replaceChildren(document.createTextNode("リポジトリを読み込めませんでした。 "));
      const retry = element("button", null, "もう一度読み込む");
      retry.type = "button";
      retry.addEventListener("click", () => loadAssignmentChoices(cursor), { once: true });
      assignmentStatus.append(retry);
      retry.focus({ preventScroll: true });
    }
  }

  function openAssignmentPicker(item, invoker) {
    resetAssignmentPicker(item, invoker);
    const sessionLabel = item.label.state === "recorded" ? item.label.text : fallbackLabel(item);
    root.querySelector("#session-assignment-title").textContent = `${sessionLabel} の割り当て先`;
    assignmentDialog.showModal();
    loadAssignmentChoices(null);
  }

  function closeAssignmentPicker(returnFocus = true) {
    assignmentPicker.controller?.abort();
    const invoker = assignmentPicker.invoker;
    assignmentDialog.close();
    if (returnFocus && invoker?.isConnected) invoker.focus({ preventScroll: true });
  }

  function localTime(value) {
    return new Intl.DateTimeFormat("ja-JP", {
      year: "numeric", month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit", hour12: false,
    }).format(new Date(value));
  }

  function fallbackLabel(item) {
    return item.timing.started_at === null ? "日時不明のセッション" : `${localTime(item.timing.started_at)} のセッション`;
  }

  function statusText(value) {
    return { active: "実行中", completed: "完了", failed: "失敗", unknown: "不明" }[value];
  }

  function assignmentText(value) {
    return {
      assigned: "リポジトリ割り当て済み",
      unassigned: "リポジトリ未設定",
      explicitly_unassigned: "手動で未設定",
      conflict: "割り当ての確認が必要",
    }[value];
  }

  function renderCollectionFact(target, value) {
    window.LocalMonitorV1FactState.renderSessionCollection(target, value);
  }

  function renderFactDisclosure(target, item, label, renderFact) {
    const disclosure = element("details", "local-monitor-session-fact-disclosure");
    const disclosureSummary = element("summary", null, "記録状態を確認");
    const sessionLabel = item.label.state === "recorded" ? item.label.text : fallbackLabel(item);
    disclosureSummary.setAttribute("aria-label", `${sessionLabel}: ${label}の記録状態を確認`);
    const panel = element("div", "local-monitor-session-fact-panel");
    const wrapper = element("div", "local-monitor-session-named-fact");
    wrapper.append(element("span", "local-monitor-session-fact-name", `${label}: `));
    const factTarget = element("span", "local-monitor-session-fact");
    renderFact(factTarget);
    wrapper.append(factTarget);
    panel.append(wrapper);
    disclosure.append(disclosureSummary, panel);
    target.append(disclosure);
  }

  function renderSummary(target, item) {
    const names = { skill: "スキル", tool: "ツール", subagent: "サブエージェント", error: "エラー", retry: "再試行" };
    const positive = SUMMARY_KEYS
      .filter(name => item.summary[name].state === "recorded" && item.summary[name].count > 0n)
      .map(name => `${names[name]} ${formatInteger(item.summary[name].count)}件`);
    const unresolved = SUMMARY_KEYS.filter(name => item.summary[name].state !== "recorded");
    const needsDisclosure = unresolved.length > 0 || positive.length > 1;
    if (positive.length) {
      target.append(element("span", null,
        positive.length > 1 ? `記録あり ${positive.length}項目` : positive[0]));
    }
    if (needsDisclosure) {
      const disclosure = element("details", "local-monitor-session-fact-disclosure");
      const disclosureSummary = element("summary", null,
        unresolved.length > 0 ? "記録状態を確認" : "要約を確認");
      const sessionLabel = item.label.state === "recorded" ? item.label.text : fallbackLabel(item);
      disclosureSummary.setAttribute("aria-label", `${sessionLabel}: 要約を確認`);
      const panel = element("div", "local-monitor-session-fact-panel");
      if (positive.length) {
        for (const name of SUMMARY_KEYS.filter(name =>
          item.summary[name].state === "recorded" && item.summary[name].count > 0n)) {
          const wrapper = element("div", "local-monitor-session-named-fact");
          wrapper.dataset.summaryRecorded = name;
          wrapper.append(
            element("span", "local-monitor-session-fact-name", `${names[name]}: `),
            document.createTextNode(`${formatInteger(item.summary[name].count)}件`));
          panel.append(wrapper);
        }
      }
      for (const name of unresolved) {
        const wrapper = element("div", "local-monitor-session-named-fact");
        wrapper.dataset.summaryFamily = name;
        wrapper.append(element("span", "local-monitor-session-fact-name", `${names[name]}: `));
        const factTarget = element("span", "local-monitor-session-fact");
        renderCollectionFact(factTarget, item.summary[name]);
        wrapper.append(factTarget);
        panel.append(wrapper);
      }
      disclosure.append(disclosureSummary, panel);
      target.append(disclosure);
    } else if (!positive.length) {
      const zero = element("div", "local-monitor-session-fact");
      renderCollectionFact(zero, item.summary.skill);
      target.append(zero);
    }
  }

  function renderTokens(target, item) {
    const tokens = item.tokens;
    if (tokens.total.state === "recorded" && count(tokens.total.value)) {
      target.append(element("span", null, formatInteger(tokens.total.value)));
      if (tokens.state === "recorded" && tokens.cache_read_ratio_basis_points.state === "recorded") {
        const ratio = Number(tokens.cache_read_ratio_basis_points.value) / 100;
        target.append(element("small", null, `キャッシュから読み込み ${ratio.toLocaleString("ja-JP")}%`));
      } else {
        const disclosure = element("details", "local-monitor-session-fact-disclosure");
        const disclosureSummary = element("summary", null, "記録状態を確認");
        const sessionLabel = item.label.state === "recorded" ? item.label.text : fallbackLabel(item);
        disclosureSummary.setAttribute("aria-label", `${sessionLabel}: トークンの記録状態を確認`);
        disclosure.append(disclosureSummary);
        const panel = element("div", "local-monitor-session-fact-panel");
        const ratioState = element("div", "local-monitor-session-named-fact");
        ratioState.append(element("span", "local-monitor-session-fact-name", "キャッシュから読み込み: "));
        if (tokens.cache_read_ratio_basis_points.state === "recorded") {
          const ratio = Number(tokens.cache_read_ratio_basis_points.value) / 100;
          ratioState.append(document.createTextNode(`${ratio.toLocaleString("ja-JP")}%`));
        } else {
          const factTarget = element("span", "local-monitor-session-fact");
          renderCollectionFact(factTarget, { state: tokens.cache_read_ratio_basis_points.state, count: null });
          ratioState.append(factTarget);
        }
        panel.append(ratioState);
        if (tokens.state !== "recorded") {
          const stateTarget = element("div", "local-monitor-session-named-fact");
          stateTarget.append(element("span", "local-monitor-session-fact-name", "トークン全体: "));
          const factTarget = element("span", "local-monitor-session-fact");
          renderCollectionFact(factTarget, { state: tokens.state, count: null });
          stateTarget.append(factTarget);
          panel.append(stateTarget);
        }
        disclosure.append(panel);
        target.append(disclosure);
      }
      return;
    }
    if (tokens.total.state === "recorded") {
      renderFactDisclosure(target, item, "トークン合計", factTarget => {
        window.LocalMonitorV1FactState.render(factTarget, {
          state: "inconsistent", recordedCount: null, reasonText: "トークン合計の値を確定できません",
        });
      });
    } else {
      const unavailableState = tokens.state === "recorded" ? tokens.total.state : tokens.state;
      renderFactDisclosure(target, item, "トークン合計", factTarget => {
        renderCollectionFact(factTarget, { state: unavailableState, count: null });
      });
    }
  }

  function cohortControl(item, cohort, label) {
    const wrapper = element("label", "local-monitor-session-cohort-option");
    const input = element("input");
    input.type = "checkbox";
    input.dataset.cohort = cohort;
    input.dataset.sessionId = item.session_id;
    input.checked = state.cohorts[cohort].has(item.session_id);
    input.setAttribute("aria-label", `${item.label.state === "recorded" ? item.label.text : fallbackLabel(item)}: ${label}`);
    input.addEventListener("change", () => {
      if (comparisonDialog.open) clearComparisonDialog(false);
      state.selectionNotice = null;
      if (input.checked) {
        state.cohorts[cohort].add(item.session_id);
        if (!item.archive.effectively_eligible) {
          state.excludedSelections.add(item.session_id);
          state.exclusionReasons.set(item.session_id, item.archive.exclusion_reason);
        }
      } else {
        state.cohorts[cohort].delete(item.session_id);
        if (!state.cohorts.a.has(item.session_id) && !state.cohorts.b.has(item.session_id)) {
          state.excludedSelections.delete(item.session_id);
          state.exclusionReasons.delete(item.session_id);
        }
      }
      updateCompareBar();
    });
    wrapper.append(input, document.createTextNode(label));
    return wrapper;
  }

  function randomOperationKey() {
    const bytes = crypto.getRandomValues(new Uint8Array(32));
    let binary = "";
    for (const value of bytes) binary += String.fromCharCode(value);
    return `lrc1_${btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "")}`;
  }

  async function sendOwnerAction(path, body, operationKey, signal, requireUnsafeRelaxedBytes = true) {
    const headers = { "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" };
    if (operationKey) headers["Idempotency-Key"] = operationKey;
    const response = await fetch(path, {
      method: "POST", headers, body: stringifyExactJson(body), cache: "no-store", credentials: "same-origin", signal,
    });
    if (!response.ok) throw await apiFailure(response);
    return readJsonResponse(response, 8_388_608, requireUnsafeRelaxedBytes);
  }

  function validateArchiveResponse(value, item, action) {
    if (!exactKeys(value, ["schema_version", "action", "target_kind", "targets"])
        || value.schema_version !== "local-archive-action.response.v1"
        || value.action !== action || value.target_kind !== "session"
        || !Array.isArray(value.targets) || value.targets.length !== 1) {
      throw new TypeError("invalid archive response");
    }
    const target = value.targets[0];
    const expectedState = action === "archive" ? "archived" : "active";
    if (!exactKeys(target, ["target_id", "state", "revision", "archived_at", "updated_at"])
        || target.target_id !== item.session_id || target.state !== expectedState
        || target.revision !== increment(item.archive.revision)
        || !timestamp(target.archived_at) || !timestamp(target.updated_at) || target.updated_at === null
        || (expectedState === "archived") !== (target.archived_at !== null)) {
      throw new TypeError("invalid archive response");
    }
  }

  function validateAssignmentResponse(value, item, action, repositoryId = null) {
    if (!exactKeys(value, [
      "schema_version", "session_id", "assignment_revision", "state", "authority",
      "repository_id", "conflicting_repository_ids", "observed_label_candidates", "updated_at",
    ])
        || value.schema_version !== "local-session-repository-assignment.v1"
        || value.session_id !== item.session_id
        || value.assignment_revision !== increment(item.assignment.revision)
        || !timestamp(value.updated_at) || value.updated_at === null
        || !Array.isArray(value.conflicting_repository_ids)
        || value.conflicting_repository_ids.length > 128
        || value.conflicting_repository_ids.some(id => !UUID_V7.test(id))
        || new Set(value.conflicting_repository_ids).size !== value.conflicting_repository_ids.length
        || value.conflicting_repository_ids.some((id, index) =>
          index > 0 && value.conflicting_repository_ids[index - 1] >= id)
        || !Array.isArray(value.observed_label_candidates) || value.observed_label_candidates.length !== 0) {
      throw new TypeError("invalid assignment response");
    }
    const valid = value.state === "assigned"
        && (value.authority === "automatic" || value.authority === "manual")
        && UUID_V7.test(value.repository_id) && value.conflicting_repository_ids.length === 0
      || value.state === "unassigned" && value.authority === "none"
        && value.repository_id === null && value.conflicting_repository_ids.length === 0
      || value.state === "explicitly_unassigned" && value.authority === "manual"
        && value.repository_id === null && value.conflicting_repository_ids.length === 0
      || value.state === "conflict" && value.authority === "automatic"
        && value.repository_id === null && value.conflicting_repository_ids.length >= 2;
    if (!valid || action === "assign"
          && (value.state !== "assigned" || value.authority !== "manual" || value.repository_id !== repositoryId)
        || action === "explicitly_unassign" && value.state !== "explicitly_unassigned"
        || action === "resume_automatic"
          && (value.authority === "manual" || value.state === "explicitly_unassigned")) {
      throw new TypeError("invalid assignment response");
    }
  }

  async function archiveSession(item, button) {
    const action = item.archive.state === "active" ? "archive" : "restore";
    if (!confirm(action === "archive" ? "このセッションをアーカイブしますか？" : "このセッションを復元しますか？")) return;
    await runOwnerAction(button, async signal => {
      const response = await sendOwnerAction("/api/local-monitor/v1/archive-actions", {
        schema_version: "local-archive-action.v1",
        action,
        target_kind: "session",
        targets: [{ target_id: item.session_id, expected_revision: item.archive.revision }],
      }, null, signal);
      validateArchiveResponse(response, item, action);
      if (action === "archive"
          && (state.cohorts.a.has(item.session_id) || state.cohorts.b.has(item.session_id))) {
        state.excludedSelections.add(item.session_id);
        state.exclusionReasons.set(item.session_id, "session_archived");
      }
    });
  }

  async function correctAssignment(item, button) {
    const action = item.assignment.state === "explicitly_unassigned" ? "resume_automatic" : "explicitly_unassign";
    if (!confirm(action === "resume_automatic" ? "自動割り当てを再開しますか？" : "割り当てを解除しますか？")) return;
    await runOwnerAction(button, async signal => {
      const response = await sendOwnerAction("/api/local-monitor/v1/session-repository-actions", {
        schema_version: "local-session-repository-action.v1",
        session_id: item.session_id,
        expected_revision: item.assignment.revision,
        action,
        repository_id: null,
      }, randomOperationKey(), signal, false);
      validateAssignmentResponse(response, item, action);
    });
  }

  async function runOwnerAction(button, action, actionKindOverride = null) {
    if (mutation.active) return;
    const controller = new AbortController();
    mutation.controller = controller;
    const generation = ++mutation.generation;
    mutation.active = true;
    setOwnerActionsDisabled(true);
    state.initiatingControl = button;
    const row = button.closest("[data-session-row]");
    const actionKind = actionKindOverride
      ?? (button.hasAttribute("data-session-archive") ? "archive" : "assignment");
    const successFocus = row?.dataset.sessionId
      ? { kind: "row-action", sessionId: row.dataset.sessionId, action: actionKind }
      : null;
    try {
      await action(controller.signal);
      if (controller.signal.aborted || generation !== mutation.generation) return;
      mutation.active = false;
      mutation.controller = null;
      setOwnerActionsDisabled(false);
      state.focusAfterRender = successFocus;
      await loadPage(state.memoryCursor ?? state.route?.cursor ?? null, true, { mutationConfirmed: true });
    } catch (error) {
      if (controller.signal.aborted || generation !== mutation.generation) return;
      showError("操作を完了できませんでした。最新の状態を確認して、もう一度お試しください。", button);
    } finally {
      if (generation === mutation.generation) {
        mutation.active = false;
        mutation.controller = null;
        setOwnerActionsDisabled(false);
      }
    }
  }

  function setOwnerActionsDisabled(disabled) {
    for (const control of root.querySelectorAll(
      "[data-session-archive],[data-session-assignment],[data-session-assignment-picker]")) {
      control.disabled = disabled;
    }
  }

  function rowActions(item) {
    const details = element("details", "local-monitor-session-row-actions");
    const summary = element("summary", null, "操作");
    const sessionLabel = item.label.state === "recorded" ? item.label.text : fallbackLabel(item);
    summary.setAttribute("aria-label", `${sessionLabel}: 操作`);
    const archive = element("button", null, item.archive.state === "active" ? "アーカイブ" : "復元");
    archive.type = "button";
    archive.dataset.sessionArchive = "";
    archive.setAttribute("aria-label", `${sessionLabel}: ${archive.textContent}`);
    archive.addEventListener("click", () => archiveSession(item, archive));
    details.append(summary, archive);
    const picker = element("button", null,
      item.assignment.state === "assigned" ? "割り当て先を変更" : "リポジトリを割り当てる");
    picker.type = "button";
    picker.dataset.sessionAssignmentPicker = "";
    picker.setAttribute("aria-label", `${sessionLabel}: ${picker.textContent}`);
    picker.addEventListener("click", () => openAssignmentPicker(item, picker));
    details.append(picker);
    if (["assigned", "conflict", "explicitly_unassigned"].includes(item.assignment.state)) {
      const assignment = element("button", null,
        item.assignment.state === "explicitly_unassigned" ? "自動割り当てを再開" : "割り当てを解除");
      assignment.type = "button";
      assignment.dataset.sessionAssignment = "";
      assignment.setAttribute("aria-label", `${sessionLabel}: ${assignment.textContent}`);
      assignment.addEventListener("click", () => correctAssignment(item, assignment));
      details.append(assignment);
    }
    return details;
  }

  function renderRow(item) {
    const row = element("tr");
    row.dataset.sessionRow = "";
    row.dataset.sessionId = item.session_id;
    const compare = element("td", "local-monitor-session-cohorts");
    compare.dataset.compareColumn = "";
    compare.hidden = !state.compareMode;
    if (state.compareMode) compare.append(cohortControl(item, "a", "基準"), cohortControl(item, "b", "比較対象"));
    const identity = element("td", "local-monitor-session-identity");
    const link = element("a", "local-monitor-session-open", item.label.state === "recorded" ? item.label.text : fallbackLabel(item));
    link.dataset.sessionOpen = "";
    link.dataset.sessionLabel = "";
    link.href = window.LocalMonitorV1Paths.session(item.session_id);
    const secondary = element("small");
    const disclosure = element("details", "local-monitor-session-fact-disclosure");
    const disclosureSummary = element("summary", null, "取得情報を確認");
    disclosureSummary.setAttribute("aria-label", `${link.textContent}: 取得情報を確認`);
    const panel = element("div", "local-monitor-session-fact-panel");
    for (const [name, fact, knownText] of [
      ["取得元", item.source, item.source.values.length > 0
        ? item.source.values.map(window.LocalMonitorV1FactState.sessionSourceLabel).join(" / ")
        : null],
      ["モデル", item.model, item.model.values.length > 0 ? item.model.values.join(" / ") : null],
    ]) {
      const wrapper = element("span", "local-monitor-session-named-fact");
      wrapper.append(element("span", "local-monitor-session-fact-name", `${name}: `));
      if (knownText !== null) wrapper.append(document.createTextNode(knownText));
      if (fact.state !== "recorded") {
        if (knownText !== null) wrapper.append(document.createTextNode(" · "));
        const factTarget = element("span", "local-monitor-session-fact");
        renderCollectionFact(factTarget, { state: fact.state, count: null });
        wrapper.append(factTarget);
      }
      panel.append(wrapper);
    }
    const assignmentFact = element("span", "local-monitor-session-named-fact");
    assignmentFact.append(
      element("span", "local-monitor-session-fact-name", "割り当て: "),
      document.createTextNode(assignmentText(item.assignment.state)));
    panel.append(assignmentFact);
    for (const note of item.capture_notes) {
      const wrapper = element("span", "local-monitor-session-named-fact");
      wrapper.dataset.captureNote = note;
      wrapper.append(element("span", "local-monitor-session-fact-name", "記録状態: "));
      const factTarget = element("span", "local-monitor-session-fact");
      window.LocalMonitorV1FactState.renderSessionCaptureNote(factTarget, note);
      wrapper.append(factTarget);
      panel.append(wrapper);
    }
    disclosure.append(disclosureSummary, panel);
    secondary.append(disclosure);
    identity.append(link, secondary);
    const sessionStatus = element("td", "local-monitor-session-status", statusText(item.status));
    sessionStatus.dataset.sessionStatus = "";
    if (item.archive.exclusion_reason === "session_archived") {
      sessionStatus.append(element("small", null, "セッションをアーカイブ済み"));
    } else if (item.archive.exclusion_reason === "repository_archived") {
      sessionStatus.append(element("small", null, "リポジトリをアーカイブ済み"));
    }
    const summaryCell = element("td", "local-monitor-session-summary");
    summaryCell.dataset.sessionSummary = "";
    renderSummary(summaryCell, item);
    const tokens = element("td", "local-monitor-session-tokens");
    tokens.dataset.sessionTokens = "";
    renderTokens(tokens, item);
    const started = element("td", "local-monitor-session-started");
    started.dataset.sessionStarted = "";
    if (item.timing.started_at !== null) {
      const time = element("time", null, localTime(item.timing.started_at));
      time.dateTime = item.timing.started_at;
      started.append(time);
      if (item.timing.duration_ms !== null) started.append(element("small", null, `${formatDurationMilliseconds(item.timing.duration_ms)}秒`));
      if (item.timing.state !== "recorded") {
        renderFactDisclosure(started, item, "時間", factTarget => {
          renderCollectionFact(factTarget, { state: item.timing.state, count: null });
        });
      }
    } else {
      renderFactDisclosure(started, item, "時間", factTarget => {
        renderCollectionFact(factTarget, { state: item.timing.state, count: null });
      });
    }
    const actions = element("td", "local-monitor-session-actions");
    actions.append(rowActions(item));
    row.append(compare, identity, sessionStatus, summaryCell, tokens, started, actions);
    row.addEventListener("click", event => {
      if (event.target instanceof Element
          && event.target.closest("a,button,input,select,textarea,summary,details,label")) return;
      window.location.assign(link.href);
    });
    return row;
  }

  function render() {
    const selectedIds = new Set([...state.cohorts.a, ...state.cohorts.b]);
    for (const item of state.items) {
      if (!selectedIds.has(item.session_id)) continue;
      if (item.archive.effectively_eligible) {
        state.excludedSelections.delete(item.session_id);
        state.exclusionReasons.delete(item.session_id);
      } else {
        state.excludedSelections.add(item.session_id);
        state.exclusionReasons.set(item.session_id, item.archive.exclusion_reason);
      }
    }
    rows.replaceChildren(...state.items.map(renderRow));
    root.querySelectorAll("[data-compare-column]").forEach(node => { node.hidden = !state.compareMode; });
    root.querySelector("#session-result-count").textContent = `${state.items.length.toLocaleString("ja-JP")}件`;
    loadMore.hidden = state.nextCursor === null;
    loadMore.disabled = false;
    compareBar.hidden = !state.compareMode;
    compareButton.hidden = state.compareMode;
    if (state.items.length === 0) {
      const route = state.route ?? {};
      const filtered = state.dynamic.q !== null || state.dynamic.model.length > 0
        || route.from !== undefined || route.to !== undefined
        || (route.source?.length ?? 0) > 0 || (route.status?.length ?? 0) > 0
        || [route.has_skill, route.has_subagent, route.has_error, route.has_retry]
          .some(value => value !== undefined);
      status.textContent = filtered
        ? "条件に一致するセッションはありません。"
        : "この範囲にはセッションがありません。";
    }
    else status.textContent = `${state.items.length.toLocaleString("ja-JP")}件を表示しています。`;
    updateCompareBar();
    setOwnerActionsDisabled(mutation.active);
    restoreFocusAfterRender();
  }

  function restoreFocusAfterRender() {
    const request = state.focusAfterRender;
    state.focusAfterRender = null;
    if (request === null) return;
    let target = null;
    if (request.kind === "compare-first") {
      target = root.querySelector("[data-cohort='a']") ?? root.querySelector("#session-compare-cancel");
    } else if (request.kind === "compare-trigger") {
      target = compareButton;
    } else if (request.kind === "row-action") {
      const row = root.querySelector(`[data-session-id='${request.sessionId}']`);
      const selector = request.action === "assignmentPicker"
        ? "[data-session-assignment-picker]"
        : `[data-session-${request.action}]`;
      target = row?.querySelector(selector) ?? row?.querySelector("[data-session-open]");
      if (target?.closest("details") instanceof HTMLDetailsElement) target.closest("details").open = true;
      if (target === null || target === undefined) {
        status.tabIndex = -1;
        target = status;
      }
    } else if (request.kind === "pagination") {
      if (loadMore.hidden) {
        status.tabIndex = -1;
        target = status;
      } else target = loadMore;
    }
    if (target instanceof HTMLElement && !target.hidden) target.focus({ preventScroll: true });
  }

  function showError(message, returnControl, recovery) {
    status.replaceChildren(document.createTextNode(message));
    status.tabIndex = -1;
    status.focus({ preventScroll: true });
    if (returnControl?.isConnected) {
      const retry = element("button", null, "もう一度読み込む");
      retry.type = "button";
      if (recovery?.label) retry.textContent = recovery.label;
      if (recovery?.attribute) retry.dataset[recovery.attribute] = "";
      retry.addEventListener("click", () => {
        retry.remove();
        returnControl.focus({ preventScroll: true });
        if (recovery?.run) recovery.run();
        else loadPage(state.memoryCursor ?? state.route?.cursor ?? null, true);
      }, { once: true });
      status.append(document.createTextNode(" "), retry);
    }
  }

  async function loadPage(cursor, refresh = false, recovery = null) {
    state.controller?.abort();
    const controller = new AbortController();
    state.controller = controller;
    const generation = ++state.generation;
    state.memoryCursor = cursor;
    state.items = [];
    state.nextCursor = null;
    rows.replaceChildren();
    root.querySelector("#session-result-count").textContent = "読み込み中";
    loadMore.hidden = true;
    loadMore.disabled = true;
    updateCompareBar();
    status.textContent = refresh ? "セッションを更新しています。" : "セッションを読み込んでいます。";
    try {
      const page = await readCollection(cursor, controller.signal);
      if (controller.signal.aborted || generation !== state.generation) return;
      state.workspaceRevision = page.workspaceRevision;
      state.items = [...page.items];
      state.nextCursor = page.nextCursor;
      render();
      return true;
    } catch (error) {
      if (controller.signal.aborted || generation !== state.generation) return;
      state.items = [];
      state.nextCursor = null;
      rows.replaceChildren();
      loadMore.hidden = true;
      root.querySelector("#session-result-count").textContent = "取得できません";
      updateCompareBar();
      const returnControl = state.initiatingControl?.isConnected
        ? state.initiatingControl
        : filters.querySelector("button[type='submit']");
      if (error instanceof ApiFailure && error.statusCode === 400
          && error.code === "invalid_cursor" && cursor !== null) {
        const message = recovery?.mutationConfirmed
          ? "操作は完了しましたが、ページ情報を使用できません。最初のページから更新してください。"
          : "ページ情報を使用できません。最初のページから更新してください。";
        showError(message, returnControl, {
          label: "最初のページから更新",
          attribute: "clearStaleCursor",
          run: () => {
            state.memoryCursor = null;
            if (state.route?.cursor !== undefined) {
              state.pendingDynamic = state.dynamic;
              state.preserveCohortsOnNextRoute = state.compareMode;
              window.LocalMonitorV1History.push({ cursor: null });
            } else {
              loadPage(null, true);
            }
          },
        });
      } else if (recovery?.mutationConfirmed) {
        showError("操作は完了しましたが、一覧を更新できませんでした。", filters.querySelector("button[type='submit']"), {
          label: "一覧を更新",
          attribute: "refreshAfterOwnerAction",
          run: () => loadPage(state.memoryCursor ?? state.route?.cursor ?? null, true, recovery),
        });
      } else {
        showError("セッションを読み込めませんでした。", returnControl, {
          run: () => loadPage(cursor, true),
        });
      }
      return false;
    } finally {
      state.initiatingControl = null;
    }
  }

  function applyControls(route) {
    const routeSources = new Set(Array.isArray(route.source) ? route.source : []);
    const routeStatuses = new Set(Array.isArray(route.status) ? route.status : []);
    for (const option of sourceFilter.options) option.selected = routeSources.has(option.value);
    for (const option of statusFilter.options) option.selected = routeStatuses.has(option.value);
    from.value = route.from ?? "";
    to.value = route.to ?? "";
    hasSkill.value = route.has_skill ?? "";
    hasSubagent.value = route.has_subagent ?? "";
    hasError.value = route.has_error ?? "";
    hasRetry.value = route.has_retry ?? "";
    includeArchived.checked = route.archive_scope === "include_archived";
    search.value = state.dynamic.q ?? "";
    model.value = state.dynamic.model.join("\n");
    limit.value = state.dynamic.limit === null ? "" : String(state.dynamic.limit);
  }

  function onlySettingsMayDiffer(route) {
    if (state.route === null) return false;
    return EXPLORER_ROUTE_KEYS.every(key => {
      const left = state.route[key];
      const right = route[key];
      return Array.isArray(left) || Array.isArray(right)
        ? sameValues(left, Array.isArray(right) ? right : [])
        : (left ?? null) === (right ?? null);
    });
  }

  function applyRoute(route) {
    const browserTraversal = state.browserTraversal;
    state.browserTraversal = false;
    if (!browserTraversal && onlySettingsMayDiffer(route)) {
      state.route = route;
      return;
    }
    const preserveCohorts = !browserTraversal && state.preserveCohortsOnNextRoute
      && state.compareMode && route.mode === "compare";
    state.preserveCohortsOnNextRoute = false;
    state.route = route;
    state.dynamic = browserTraversal
      ? { q: null, model: [], limit: null }
      : state.pendingDynamic ?? { q: null, model: [], limit: null };
    state.pendingDynamic = null;
    state.memoryCursor = route.cursor ?? null;
    state.compareMode = route.mode === "compare";
    if (!preserveCohorts) {
      state.cohorts.a.clear();
      state.cohorts.b.clear();
      state.excludedSelections.clear();
      state.exclusionReasons.clear();
      state.selectionNotice = null;
    }
    applyControls(route);
    loadPage(route.cursor ?? null);
  }

  function updateCompareBar() {
    if (!state.compareMode) return;
    const a = state.cohorts.a;
    const b = state.cohorts.b;
    root.querySelector("[data-cohort-count='a']").textContent = `基準 ${a.size}件`;
    root.querySelector("[data-cohort-count='b']").textContent = `比較対象 ${b.size}件`;
    const overlap = [...a].filter(id => b.has(id));
    const selected = new Set([...a, ...b]);
    const locallyExcluded = id => state.excludedSelections.has(id)
      && !(includeArchived.checked && state.exclusionReasons.get(id) === "session_archived");
    const excluded = [...selected].filter(locallyExcluded);
    const availableA = [...a].filter(id => !locallyExcluded(id));
    const availableB = [...b].filter(id => !locallyExcluded(id));
    const total = a.size + b.size;
    const messages = [];
    if (state.selectionNotice !== null) messages.push(state.selectionNotice);
    if (a.size === 0 || b.size === 0) messages.push("基準と比較対象を1件以上選択してください。");
    if (overlap.length) messages.push("同じセッションを両方の対象には選択できません。");
    if (total > 200) messages.push("選択できるセッションは合計200件までです。");
    const sessionArchived = excluded.filter(id => state.exclusionReasons.get(id) === "session_archived").length;
    const repositoryArchived = excluded.filter(id => state.exclusionReasons.get(id) === "repository_archived").length;
    if (sessionArchived) messages.push(`セッションのアーカイブ除外 ${sessionArchived}件を選択から外してください。`);
    if (repositoryArchived) messages.push(`リポジトリのアーカイブ除外 ${repositoryArchived}件を選択から外してください。`);
    if (a.size > 0 && availableA.length === 0) messages.push("アーカイブ除外後に基準が空になります。");
    if (b.size > 0 && availableB.length === 0) messages.push("アーカイブ除外後に比較対象が空になります。");
    const valid = messages.length === 0 && UUID_V7.test(root.dataset.repositoryId ?? "");
    if (messages.length === 0 && !valid) messages.push("リポジトリ別の一覧から比較を作成してください。");
    const validMessage = "選択は有効です。比較内容を確認できます。";
    compareValidationPrimary.textContent = messages.length === 0
      ? validMessage
      : messages.length === 1 ? messages[0] : `${messages[0]}（ほか${messages.length - 1}件）`;
    compareValidationList.replaceChildren(...messages.map(message => element("li", null, message)));
    compareValidationDetails.hidden = messages.length <= 1;
    if (compareValidationDetails.hidden) compareValidationDetails.open = false;
    comparePreview.disabled = false;
    comparePreview.setAttribute("aria-disabled", valid ? "false" : "true");
    comparePreview.setAttribute("aria-describedby", "session-compare-validation");
    comparePreview.removeAttribute("data-owner-boundary");
  }

  function comparisonSelection() {
    return {
      cohorts: { a: [...state.cohorts.a], b: [...state.cohorts.b] },
      include_archived: includeArchived.checked,
    };
  }

  function clearComparisonDialog(restoreFocus = true) {
    state.comparison.controller?.abort();
    state.comparison.controller = null;
    state.comparison.preview = null;
    state.comparison.selection = null;
    comparisonStatus.textContent = "";
    root.querySelectorAll("[data-comparison-preview-included]").forEach(node => node.replaceChildren());
    comparisonExcludedList.replaceChildren();
    comparisonExcluded.hidden = true;
    comparisonCreate.disabled = true;
    if (comparisonDialog.open) comparisonDialog.close();
    if (restoreFocus && state.comparison.invoker?.isConnected) state.comparison.invoker.focus();
    state.comparison.invoker = null;
  }

  function comparisonLabel(sessionId) {
    const item = state.items.find(candidate => candidate.session_id === sessionId);
    return item?.label.state === "recorded" ? item.label.text : "選択したセッション";
  }

  function validateComparisonPreview(value, selection) {
    if (!exactKeys(value, ["schema_version", "valid", "selection_sha256", "preview_revision", "cohorts", "requested", "included", "excluded"])
        || value.schema_version !== "local-monitor-comparison-preview.response.v1"
        || typeof value.valid !== "boolean"
        || !(value.selection_sha256 === null || REVISION.test(value.selection_sha256))
        || (value.valid && value.selection_sha256 === null)
        || !REVISION.test(value.preview_revision) || !Array.isArray(value.requested)
        || !Array.isArray(value.included) || !Array.isArray(value.excluded)) throw new TypeError("invalid comparison preview");
    const summary = (candidate, label, requested) => exactKeys(candidate, ["label", "requested_count", "included_count", "excluded_count"])
      && candidate.label === label && candidate.requested_count === BigInt(requested)
      && typeof candidate.included_count === "bigint" && candidate.included_count >= 0n
      && typeof candidate.excluded_count === "bigint" && candidate.excluded_count >= 0n
      && candidate.included_count + candidate.excluded_count === candidate.requested_count;
    if (!exactKeys(value.cohorts, ["a", "b"])
        || !summary(value.cohorts.a, "基準", selection.cohorts.a.length)
        || !summary(value.cohorts.b, "比較対象", selection.cohorts.b.length)) throw new TypeError("invalid comparison preview");
    const expected = [...selection.cohorts.a.map((session_id, index) => ({ cohort: "a", request_ordinal: BigInt(index + 1), session_id })),
      ...selection.cohorts.b.map((session_id, index) => ({ cohort: "b", request_ordinal: BigInt(index + 1), session_id }))];
    if (value.requested.length !== expected.length || value.requested.some((item, index) =>
      !exactKeys(item, ["cohort", "request_ordinal", "session_id"])
      || item.cohort !== expected[index].cohort || item.request_ordinal !== expected[index].request_ordinal
      || item.session_id !== expected[index].session_id)) throw new TypeError("invalid comparison preview");
    const metadata = candidate => exactKeys(candidate, ["archive_state", "source", "model", "projection_version", "completeness", "metric_coverage", "session_revision", "projection_revision"])
      && ["active", "archived"].includes(candidate.archive_state)
      && (candidate.source === null || typeof candidate.source === "string")
      && (candidate.model === null || typeof candidate.model === "string")
      && (candidate.projection_version === null || typeof candidate.projection_version === "bigint")
      && ["unbound", "partial", "rich", "full", null].includes(candidate.completeness)
      && Array.isArray(candidate.metric_coverage)
      && (candidate.session_revision === null || typeof candidate.session_revision === "bigint")
      && (candidate.projection_revision === null || REVISION.test(candidate.projection_revision));
    if (value.included.some(item => !exactKeys(item, ["cohort", "session_id", "metadata"])
        || !["a", "b"].includes(item.cohort) || !UUID_V7.test(item.session_id) || !metadata(item.metadata))
        || value.excluded.some(item => !exactKeys(item, ["cohort", "request_ordinal", "session_id", "reason", "metadata"])
          || !["a", "b"].includes(item.cohort) || typeof item.request_ordinal !== "bigint"
          || !UUID_V7.test(item.session_id) || !COMPARISON_EXCLUSIONS.has(item.reason)
          || !(item.metadata === null || metadata(item.metadata)))) throw new TypeError("invalid comparison preview");
    return value;
  }

  async function comparisonPost(path, body, signal) {
    const response = await fetch(path, {
      method: "POST", credentials: "same-origin", cache: "no-store", redirect: "manual", signal,
      headers: { "Content-Type": "application/json; charset=utf-8", "x-monitor-csrf": "local-monitor" },
      body: JSON.stringify(body),
    });
    const text = await response.text();
    if (!response.ok) {
      let code = null;
      try { code = parseClosedJson(text).error; } catch { /* fail closed below */ }
      throw new ApiFailure(response.status, code);
    }
    return { response, value: parseClosedJson(text) };
  }

  function renderComparisonPreview(preview) {
    for (const cohort of ["a", "b"]) {
      const list = root.querySelector(`[data-comparison-preview-included='${cohort}']`);
      list.replaceChildren(...preview.included.filter(item => item.cohort === cohort)
        .map(item => element("li", null, comparisonLabel(item.session_id))));
    }
    comparisonExcludedList.replaceChildren(...preview.excluded.map(item =>
      element("li", null, `${item.cohort === "a" ? "基準" : "比較対象"}: ${comparisonLabel(item.session_id)} — ${COMPARISON_EXCLUSIONS.get(item.reason)}`)));
    comparisonExcluded.hidden = preview.excluded.length === 0;
    comparisonStatus.textContent = preview.valid ? "比較内容を確認してください。" : "この選択では比較を作成できません。";
    comparisonCreate.disabled = !preview.valid;
  }

  async function requestComparisonPreview(invoker = comparePreview) {
    const selection = comparisonSelection();
    const generation = ++state.comparison.generation;
    state.comparison.controller?.abort();
    const controller = new AbortController();
    state.comparison.controller = controller;
    state.comparison.invoker = invoker;
    state.comparison.preview = null;
    state.comparison.selection = selection;
    comparisonCreate.disabled = true;
    comparisonStatus.textContent = "比較内容を読み込んでいます。";
    if (!comparisonDialog.open) comparisonDialog.showModal();
    comparisonCancel.focus();
    try {
      const body = { schema_version: "local-monitor-comparison-preview.request.v1", cohorts: selection.cohorts, include_archived: selection.include_archived };
      const result = await comparisonPost(`/api/local-monitor/v1/repositories/${root.dataset.repositoryId}/comparisons/preview`, body, controller.signal);
      if (generation !== state.comparison.generation) return;
      state.comparison.preview = validateComparisonPreview(result.value, selection);
      renderComparisonPreview(state.comparison.preview);
    } catch (error) {
      if (controller.signal.aborted || generation !== state.comparison.generation) return;
      comparisonStatus.textContent = error instanceof ApiFailure && error.code === "workspace_too_large"
        ? "比較対象が大きすぎます。" : "比較内容を読み込めませんでした。";
    }
  }

  async function createComparison() {
    const preview = state.comparison.preview;
    const selection = state.comparison.selection;
    if (!preview?.valid || selection === null) return;
    const generation = ++state.comparison.generation;
    state.comparison.controller?.abort();
    const controller = new AbortController();
    state.comparison.controller = controller;
    comparisonCreate.disabled = true;
    comparisonStatus.textContent = "比較を作成しています。";
    try {
      const body = { schema_version: "local-monitor-comparison-create.request.v1", cohorts: selection.cohorts,
        include_archived: selection.include_archived, selection_sha256: preview.selection_sha256, preview_revision: preview.preview_revision };
      const result = await comparisonPost(`/api/local-monitor/v1/repositories/${root.dataset.repositoryId}/comparisons`, body, controller.signal);
      if (generation !== state.comparison.generation
          || !exactKeys(result.value, ["schema_version", "comparison_id", "location", "receipt_sha256", "created_at", "expires_at"])
          || result.value.schema_version !== "local-monitor-comparison-create.response.v1"
          || !UUID_V7.test(result.value.comparison_id)
          || !REVISION.test(result.value.receipt_sha256)
          || !timestamp(result.value.created_at) || !timestamp(result.value.expires_at)
          || Date.parse(result.value.expires_at) - Date.parse(result.value.created_at) !== 86_400_000
          || result.value.location !== `/repositories/${root.dataset.repositoryId}/comparisons/${result.value.comparison_id}`
          || result.response.headers.get("Location") !== result.value.location) throw new TypeError("invalid comparison create");
      const location = result.value.location;
      clearComparisonDialog(false);
      window.location.assign(location);
    } catch (error) {
      if (controller.signal.aborted || generation !== state.comparison.generation) return;
      if (error instanceof ApiFailure && error.code === "comparison_preview_stale") {
        comparisonStatus.textContent = "比較内容が更新されました。もう一度確認してください。";
        await requestComparisonPreview(state.comparison.invoker ?? comparePreview);
        return;
      }
      comparisonStatus.textContent = "比較を作成できませんでした。";
      comparisonCreate.disabled = false;
    }
  }

  function resetSelectionForFilterChange() {
    if (state.cohorts.a.size === 0 && state.cohorts.b.size === 0) return;
    state.cohorts.a.clear();
    state.cohorts.b.clear();
    state.excludedSelections.clear();
    state.exclusionReasons.clear();
    state.selectionNotice = "条件変更のため比較対象の選択をクリアしました。";
    updateCompareBar();
  }

  function applyFilters(event) {
    event.preventDefault();
    const q = search.value;
    const models = model.value.split("\n").filter(value => value !== "");
    const fromValue = from.value;
    const toValue = to.value;
    if (!timestamp(fromValue === "" ? null : fromValue)
        || !timestamp(toValue === "" ? null : toValue)
        || fromValue !== "" && toValue !== "" && fromValue >= toValue) {
      showError("期間は正しいUTC日時で、開始を終了より前にしてください。", from);
      return;
    }
    if (q !== "" && !validQueryText(q)) {
      showError("検索条件を使用できません。200文字以内で入力してください。", search);
      return;
    }
    if (new Set(models).size !== models.length || models.length > 16
        || models.some(value => !validModelText(value))) {
      showError("モデルは改行区切りで、各128文字・16件以内の有効な値を入力してください。", model);
      return;
    }
    const sources = selected(sourceFilter);
    const statuses = selected(statusFilter);
    const limitValue = limit.value === "" ? null : Number(limit.value);
    const current = window.LocalMonitorV1History.current();
    const patch = {
      from: fromValue === "" ? null : fromValue,
      to: toValue === "" ? null : toValue,
      source: sources,
      status: statuses,
      has_skill: hasSkill.value === "" ? null : hasSkill.value,
      has_subagent: hasSubagent.value === "" ? null : hasSubagent.value,
      has_error: hasError.value === "" ? null : hasError.value,
      has_retry: hasRetry.value === "" ? null : hasRetry.value,
      archive_scope: includeArchived.checked ? "include_archived" : null,
      cursor: null,
    };
    state.pendingDynamic = { q: q === "" ? null : q, model: models, limit: limitValue };
    state.initiatingControl = filters.querySelector("button[type='submit']");
    const safeChanged = (current.from ?? "") !== fromValue
      || (current.to ?? "") !== toValue
      || !sameValues(current.source, sources)
      || !sameValues(current.status, statuses)
      || (current.has_skill ?? "") !== hasSkill.value
      || (current.has_subagent ?? "") !== hasSubagent.value
      || (current.has_error ?? "") !== hasError.value
      || (current.has_retry ?? "") !== hasRetry.value
      || (current.archive_scope ?? "active_only") !== (includeArchived.checked ? "include_archived" : "active_only")
      || current.cursor !== undefined;
    const dynamicChanged = (state.dynamic.q ?? "") !== q
      || !sameValues(state.dynamic.model, models)
      || state.dynamic.limit !== limitValue;
    if (safeChanged || dynamicChanged) resetSelectionForFilterChange();
    if (safeChanged) {
      state.preserveCohortsOnNextRoute = state.compareMode;
      window.LocalMonitorV1History.push(patch);
    }
    else {
      state.dynamic = state.pendingDynamic;
      state.pendingDynamic = null;
      loadPage(null);
    }
  }

  filters.addEventListener("submit", applyFilters);
  includeArchived.addEventListener("change", () => {
    if (comparisonDialog.open) clearComparisonDialog(false);
    updateCompareBar();
  });

  loadMore.addEventListener("click", () => {
    if (state.nextCursor === null) return;
    state.initiatingControl = loadMore;
    state.focusAfterRender = { kind: "pagination" };
    const eligible = state.dynamic.q === null && state.dynamic.model.length === 0 && state.dynamic.limit === null;
    if (eligible) {
      state.pendingDynamic = state.dynamic;
      state.preserveCohortsOnNextRoute = state.compareMode;
      window.LocalMonitorV1History.push(
        { cursor: state.nextCursor },
        { q: null, model: [], limit: null });
    } else {
      loadPage(state.nextCursor);
    }
  });

  compareButton.addEventListener("click", () => {
    state.pendingDynamic = state.dynamic;
    state.focusAfterRender = { kind: "compare-first" };
    window.LocalMonitorV1History.push({ mode: "compare", cursor: null });
  });

  comparePreview.addEventListener("click", () => {
    if (comparePreview.getAttribute("aria-disabled") === "false") requestComparisonPreview(comparePreview);
  });
  comparisonCancel.addEventListener("click", () => clearComparisonDialog());
  comparisonCreate.addEventListener("click", createComparison);
  comparisonDialog.addEventListener("cancel", event => {
    event.preventDefault();
    clearComparisonDialog();
  });

  root.querySelector("#session-compare-cancel").addEventListener("click", () => {
    state.pendingDynamic = state.dynamic;
    state.focusAfterRender = { kind: "compare-trigger" };
    window.LocalMonitorV1History.push({ mode: null, cursor: null });
  });

  assignmentLoadMore.addEventListener("click", () => {
    if (assignmentPicker.nextCursor !== null) loadAssignmentChoices(assignmentPicker.nextCursor, true);
  });

  assignmentCancel.addEventListener("click", () => closeAssignmentPicker());
  assignmentDialog.addEventListener("cancel", event => {
    event.preventDefault();
    closeAssignmentPicker();
  });

  assignmentForm.addEventListener("submit", event => {
    event.preventDefault();
    const selectedRepository = assignmentChoices.querySelector("input[name='repository_id']:checked:not(:disabled)");
    if (!(selectedRepository instanceof HTMLInputElement)
        || !UUID_V7.test(selectedRepository.value)
        || !assignmentPicker.repositoryIds.has(selectedRepository.value)
        || selectedRepository.value === assignmentPicker.item?.assignment.repository_id
        || assignmentPicker.item === null || assignmentPicker.invoker === null) {
      assignmentStatus.textContent = "有効な割り当て先を選択してください。";
      return;
    }
    const item = assignmentPicker.item;
    const invoker = assignmentPicker.invoker;
    const repositoryId = selectedRepository.value;
    closeAssignmentPicker(false);
    runOwnerAction(invoker, async signal => {
      const response = await sendOwnerAction("/api/local-monitor/v1/session-repository-actions", {
        schema_version: "local-session-repository-action.v1",
        session_id: item.session_id,
        expected_revision: item.assignment.revision,
        action: "assign",
        repository_id: repositoryId,
      }, randomOperationKey(), signal, false);
      validateAssignmentResponse(response, item, "assign", repositoryId);
    }, "assignmentPicker");
  });

  aiOpen.addEventListener("click", () => {
    aiState.preview = null;
    aiState.frozenIds = [...new Set([...state.cohorts.a, ...state.cohorts.b])].slice(0, 200);
    aiExplicitLabel.hidden = aiState.frozenIds.length === 0;
    aiPreviewContent.replaceChildren(); aiResult.replaceChildren(); aiStart.disabled = true;
    aiStatus.textContent = "分析対象を選んで確認してください。"; aiDialog.showModal();
  });
  root.querySelector("#session-ai-close").addEventListener("click", () => aiDialog.close());
  aiPreviewButton.addEventListener("click", previewAiSelection);
  aiStart.addEventListener("click", startAiRun);
  aiCancel.addEventListener("click", async () => {
    if (!aiState.runId) return;
    try { await aiJson(`/api/local-monitor/v1/ai/runs/${aiState.runId}/cancel`, {}); aiStatus.textContent = "キャンセルしました。"; } catch { aiStatus.textContent = "キャンセルできませんでした。"; }
    aiCancel.hidden = true;
  });

  document.addEventListener("cao-route-popstate", () => {
    if (comparisonDialog.open) clearComparisonDialog(false);
    state.browserTraversal = true;
    if (assignmentDialog.open) closeAssignmentPicker(false);
  });
  document.addEventListener("cao-route-state", event => { applyRoute(event.detail); restoreRepositoryAnalysis(event.detail); });
  enableRepositoryAi();
  const initialRoute = window.LocalMonitorV1History.current();
  applyRoute(initialRoute); restoreRepositoryAnalysis(initialRoute);
})();
