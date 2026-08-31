(() => {
  "use strict";

  const root = document.querySelector("[data-session-workspace]");
  if (!root || !window.LocalMonitorV1History || !window.LocalMonitorV1Paths || !window.LocalMonitorV1FactState) return;

  const UUID_V7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
  const NODE = /^node-[0-9a-f]{32}$/;
  const REVISION = /^[0-9a-f]{64}$/;
  const FACT_STATES = new Set(["recorded", "not_observed", "source_unsupported", "capture_gap", "certification_pending", "not_captured", "expired", "redacted", "malformed", "oversized", "inconsistent", "projection_invalid"]);
  const COVERAGE_STATES = new Set(["recorded", "complete_zero", "not_observed", "source_unsupported", "capture_gap", "certification_pending", "inconsistent", "projection_invalid"]);
  const ROOT_KEYS = ["schema_version", "workspace_revision", "session", "executions", "technical_references"];
  const SESSION_KEYS = ["session_id", "status", "completeness", "assignment", "archive", "instruction", "source", "model", "version", "timing", "tokens", "activity", "capture"];
  const TOKEN_KEYS = ["authority", "state", "available_execution_count", "total_execution_count", "input", "output", "total", "reasoning", "cache_read", "cache_creation", "new_input", "cache_read_ratio_basis_points"];
  const ACTIVITY_KEYS = ["skill", "tool", "subagent", "error", "retry"];
  const SOURCES = new Set(["copilot-sdk", "copilot-cli", "vscode", "hook-unknown", "claude-code"]);
  const CURSOR = /^[A-Za-z0-9_-]{158}[AEIMQUYcgkosw048]$/;
  const KINDS = new Set(["execution", "agent", "skill", "tool", "subagent", "event", "error", "retry", "permission", "unknown_relation_group"]);
  const CONTENT_PARTS = ["instruction", "tool_input", "tool_result", "error_message", "subagent_input", "event_content"];
  const ITEM_KEYS = ["node_id", "execution_id", "parent_node_id", "relationship_authority", "kind", "name", "lifecycle", "status", "timing", "activity", "tokens", "child_count", "has_more_children", "collapsed_children", "content_parts", "source_references"];
  const state = { summary: null, revision: null, selectedNodeId: null, selectedExecutionId: null, executionState: new Map(), ignoreRouteEvent: false };
  const rawDialog = document.querySelector("[data-raw-content-dialog]");
  const inspector = root.querySelector("[data-session-overview]");
  const narrowInspector = matchMedia("(max-width: 1179px)");
  let inspectorReturnFocus = null;
  let rawDialogTrigger = null;
  let aiReady = false;
  let sessionAiInvoker = null;
  let sessionReports = [];
  let sessionReportCursor = null;
  let nodeTranscript = [];
  let nodeAiContext = null;
  let activeSessionRun = null;
  let sessionPollGeneration = 0;
  let nodePollGeneration = 0;
  let routeGeneration = 0;
  class RouteSuperseded extends Error {}
  window.LocalMonitorSessionWorkspace = state;

  const exact = (value, keys) => value && typeof value === "object" && !Array.isArray(value)
    && Object.keys(value).length === keys.length && Object.keys(value).every((key, index) => key === keys[index]);
  const nonnegative = value => Number.isSafeInteger(value) && value >= 0;
  const oneOf = (value, allowed) => allowed.includes(value);
  const distinct = values => new Set(values).size === values.length;
  const sorted = values => values.every((value, index) => index === 0 || values[index - 1] < value);
  const instant = value => typeof value === "string" && /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}\+00:00$/.test(value);
  const scalarFact = (value, key, maximum = null) => exact(value, ["state", key]) && FACT_STATES.has(value.state)
    && (value.state === "recorded" ? nonnegative(value[key]) && (maximum === null || value[key] <= maximum) : value[key] === null);
  const countFact = value => scalarFact(value, "count");

  function timelineItem(value, executionId) {
    return exact(value, ITEM_KEYS) && NODE.test(value.node_id) && value.execution_id === executionId
      && (value.parent_node_id === null || NODE.test(value.parent_node_id))
      && oneOf(value.relationship_authority, ["exact", "explicit", "unknown"])
      && KINDS.has(value.kind)
      && exact(value.name, ["state", "text"]) && oneOf(value.name.state, ["recorded", "not_observed", "invalid"])
      && (value.name.state === "recorded" ? typeof value.name.text === "string" && value.name.text.length > 0 : value.name.text === null)
      && oneOf(value.lifecycle, ["selected", "started", "completed", "failed", "deselected", "unknown"])
      && oneOf(value.status, ["active", "completed", "failed", "unknown"])
      && timing(value.timing, value.status, true) && activity(value.activity) && tokens(value.tokens)
      && nonnegative(value.child_count) && value.child_count <= 4096 && typeof value.has_more_children === "boolean"
      && exact(value.collapsed_children, ["state", "count"]) && oneOf(value.collapsed_children.state, ["complete", "partial", "unavailable"])
      && (value.collapsed_children.state === "unavailable" ? value.collapsed_children.count === null : nonnegative(value.collapsed_children.count) && value.collapsed_children.count <= 4096)
      && Array.isArray(value.content_parts) && value.content_parts.every((part, index) => CONTENT_PARTS.includes(part)
        && (index === 0 || CONTENT_PARTS.indexOf(value.content_parts[index - 1]) < CONTENT_PARTS.indexOf(part)))
      && referenceFact(value.source_references);
  }

  function validateTimeline(page, executionId, parentNodeId) {
    if (!exact(page, ["schema_version", "workspace_revision", "session_id", "execution_id", "parent_node_id", "items", "next_cursor"])
        || page.schema_version !== "local-monitor-session-timeline.response.v2" || page.workspace_revision !== state.revision
        || page.session_id !== root.dataset.sessionId || page.execution_id !== executionId || page.parent_node_id !== parentNodeId
        || !Array.isArray(page.items) || page.items.length > 200 || !page.items.every(item => timelineItem(item, executionId))
        || page.next_cursor !== null && !CURSOR.test(page.next_cursor)) throw new TypeError("invalid timeline");
    return page;
  }

  const valueFact = (value, key = "value") => exact(value, ["state", key]) && FACT_STATES.has(value.state)
    && (value.state === "recorded" ? value[key] !== null : value[key] === null);
  const typedValueFact = (value, predicate, key = "value") => valueFact(value, key) && (value.state !== "recorded" || predicate(value[key]));
  const availabilityFact = value => exact(value, ["state", "available"])
    && oneOf(value.state, ["available", "not_captured", "expired", "deleted", "read_denied", "oversized", "invalid", "source_unsupported", "not_observed"])
    && value.available === (value.state === "available");
  const contentFact = value => exact(value, ["state", "available"])
    && oneOf(value.state, ["available", "not_captured", "expired", "deleted", "read_denied", "oversized", "invalid"])
    && value.available === (value.state === "available");
  const lifecycleFacts = value => exact(value, ["selected", "started", "completed", "failed", "deselected"])
    && Object.values(value).every(item => exact(item, ["state"]) && FACT_STATES.has(item.state));
  const stateOnlyFact = value => exact(value, ["state"]) && FACT_STATES.has(value.state);
  const referenceFact = value => exact(value, ["state", "references"]) && FACT_STATES.has(value.state) && Array.isArray(value.references)
    && (value.state === "recorded" ? value.references.length >= 1 && value.references.length <= 16 : value.references.length === 0)
    && distinct(value.references.map(item => JSON.stringify(item)))
    && value.references.every(item => exact(item, ["source_kind", "source_identity", "trace_id", "span_id", "event_id"])
      && (item.source_kind === null || typeof item.source_kind === "string" && item.source_kind.length > 0)
      && ["source_identity", "trace_id", "span_id", "event_id"].every(key => item[key] === null || typeof item[key] === "string" && item[key].length > 0)
      && (item.trace_id === null || /^[0-9a-f]{32}$/.test(item.trace_id)) && (item.span_id === null || /^[0-9a-f]{16}$/.test(item.span_id))
      && ["source_identity", "trace_id", "span_id", "event_id"].some(key => item[key] !== null));
  const nodeSetFact = value => exact(value, ["state", "node_ids"]) && FACT_STATES.has(value.state) && Array.isArray(value.node_ids)
    && value.node_ids.length <= 200 && distinct(value.node_ids) && value.node_ids.every(id => NODE.test(id))
    && (value.state === "recorded" || value.node_ids.length === 0);

  function metadata(value, kind) {
    if (!value || value.kind !== kind) return false;
    if (["execution", "agent", "unknown_relation_group"].includes(kind)) return exact(value, ["kind"]);
    if (kind === "tool") return exact(value, ["kind", "caller", "lifecycle", "status", "exit", "mcp_server_identity", "mcp_server_name", "mcp_tool_name", "input", "result", "error", "retry", "recovery", "child_activity", "source_references"])
      && typedValueFact(value.caller, item => NODE.test(item), "node_id")
      && typedValueFact(value.lifecycle, item => oneOf(item, ["selected", "started", "completed", "failed", "deselected", "unknown"]))
      && typedValueFact(value.status, item => oneOf(item, ["active", "completed", "failed", "unknown"])) && stateOnlyFact(value.exit)
      && valueFact(value.mcp_server_identity) && (value.mcp_server_identity.value === null || REVISION.test(value.mcp_server_identity.value))
      && typedValueFact(value.mcp_server_name, item => typeof item === "string" && item.length > 0)
      && typedValueFact(value.mcp_tool_name, item => typeof item === "string" && item.length > 0)
      && contentFact(value.input) && contentFact(value.result) && contentFact(value.error)
      && nodeSetFact(value.retry) && nodeSetFact(value.recovery) && activity(value.child_activity) && referenceFact(value.source_references);
    if (kind === "skill") return exact(value, ["kind", "current_valid_state", "source", "trigger", "inventory_reference", "historical_snapshot_reference"])
      && oneOf(value.current_valid_state, ["current", "stale", "invalid", "certification_pending", "unavailable"])
      && typedValueFact(value.source, item => typeof item === "string" && item.length > 0)
      && typedValueFact(value.trigger, item => typeof item === "string" && item.length > 0)
      && typedValueFact(value.inventory_reference, item => typeof item === "string" && item.length > 0)
      && typedValueFact(value.historical_snapshot_reference, item => typeof item === "string" && item.length > 0);
    if (kind === "subagent") return exact(value, ["kind", "lifecycle", "input", "activity", "tokens", "children", "source_references"])
      && lifecycleFacts(value.lifecycle) && contentFact(value.input) && activity(value.activity) && tokens(value.tokens) && countFact(value.children) && referenceFact(value.source_references);
    if (kind === "error") return exact(value, ["kind", "error_code", "message", "status", "source_references"])
      && typedValueFact(value.error_code, item => typeof item === "string" && item.length > 0) && contentFact(value.message)
      && typedValueFact(value.status, item => oneOf(item, ["active", "completed", "failed", "unknown"])) && referenceFact(value.source_references);
    if (kind === "permission") return exact(value, ["kind", "decision", "wait", "source_references"])
      && typedValueFact(value.decision, item => oneOf(item, ["allowed", "denied", "asked", "unknown"])) && stateOnlyFact(value.wait) && referenceFact(value.source_references);
    if (kind === "event") return exact(value, ["kind", "event_name", "source_time", "content", "source_references"])
      && typedValueFact(value.event_name, item => typeof item === "string" && item.length > 0)
      && typedValueFact(value.source_time, instant) && contentFact(value.content) && referenceFact(value.source_references);
    if (kind === "retry") return exact(value, ["kind", "attempt", "target", "recovered", "source_references"])
      && typedValueFact(value.attempt, item => nonnegative(item)) && typedValueFact(value.target, item => NODE.test(item), "node_id")
      && typedValueFact(value.recovered, item => typeof item === "boolean") && referenceFact(value.source_references);
    return false;
  }

  function validateNode(detail, nodeId, expectedExecutionId) {
    if (!exact(detail, ["schema_version", "workspace_revision", "session_id", "execution", "node", "parent_path", "related", "content"])
        || detail.schema_version !== "local-monitor-session-node.response.v2" || detail.workspace_revision !== state.revision
        || detail.session_id !== root.dataset.sessionId
        || !exact(detail.execution, ["execution_id", "node_id", "latest", "source", "model", "lifecycle", "status", "timing", "tokens", "activity", "child_count"])
        || !UUID_V7.test(detail.execution.execution_id) || !NODE.test(detail.execution.node_id)
        || typeof detail.execution.latest !== "boolean"
        || detail.execution.source !== null && (typeof detail.execution.source !== "string" || detail.execution.source.length === 0)
        || detail.execution.model !== null && (typeof detail.execution.model !== "string" || detail.execution.model.length === 0)
        || !oneOf(detail.execution.lifecycle, ["selected", "started", "completed", "failed", "deselected", "unknown"])
        || !oneOf(detail.execution.status, ["active", "completed", "failed", "unknown"]) || !timing(detail.execution.timing, detail.execution.status, true)
        || !tokens(detail.execution.tokens) || !activity(detail.execution.activity) || !nonnegative(detail.execution.child_count) || detail.execution.child_count > 4096
        || !exact(detail.node, [...ITEM_KEYS, "technical_references", "metadata"])
        || !timelineItem(Object.fromEntries(ITEM_KEYS.map(key => [key, detail.node[key]])), detail.execution.execution_id)
        || !exact(detail.node.technical_references, ["source_kind", "source_identity", "trace_id", "span_id", "event_id"])
        || detail.node.technical_references.source_kind !== null && (typeof detail.node.technical_references.source_kind !== "string" || detail.node.technical_references.source_kind.length === 0)
        || ["source_identity", "trace_id", "span_id", "event_id"].some(key => detail.node.technical_references[key] !== null && (typeof detail.node.technical_references[key] !== "string" || detail.node.technical_references[key].length === 0))
        || detail.node.technical_references.trace_id !== null && !/^[0-9a-f]{32}$/.test(detail.node.technical_references.trace_id)
        || detail.node.technical_references.span_id !== null && !/^[0-9a-f]{16}$/.test(detail.node.technical_references.span_id)
        || !metadata(detail.node.metadata, detail.node.kind)
        || !Array.isArray(detail.parent_path) || detail.parent_path.length > 4096 || !detail.parent_path.every(item => timelineItem(item, detail.execution.execution_id))
        || new Set(detail.parent_path.map(item => item.node_id)).size !== detail.parent_path.length
        || detail.parent_path.some(item => item.node_id === nodeId)
        || detail.parent_path.length > 0 && (detail.parent_path[0].node_id !== detail.execution.node_id || detail.parent_path[0].kind !== "execution")
        || detail.parent_path.some((item, index) => index === 0 ? item.parent_node_id !== null : item.parent_node_id !== detail.parent_path[index - 1].node_id)
        || (detail.parent_path.length ? detail.node.parent_node_id !== detail.parent_path.at(-1).node_id : detail.node.parent_node_id !== null)
        || !exact(detail.related, ["retry", "recovery", "children"])
        || ![detail.related.retry, detail.related.recovery, detail.related.children].every(items => Array.isArray(items) && items.length <= 200
          && items.every(item => timelineItem(item, detail.execution.execution_id) && oneOf(item.relationship_authority, ["exact", "explicit"])))
        || !exact(detail.content, ["instruction", "tool_input", "tool_result", "error_message", "subagent_input", "event_content"])
        || !Object.values(detail.content).every(value => exact(value, ["state", "available"])
          && oneOf(value.state, ["available", "not_captured", "expired", "deleted", "read_denied", "oversized", "invalid"])
          && value.available === (value.state === "available"))) throw new TypeError("invalid node");
    if (detail.node.node_id !== nodeId || expectedExecutionId && detail.execution.execution_id !== expectedExecutionId) {
      const mismatch = new Error("Session detail unavailable"); mismatch.status = 404; throw mismatch;
    }
    return detail;
  }

  function tokens(value) {
    if (!(exact(value, TOKEN_KEYS) && oneOf(value.authority, ["session_run", "llm_span", "mixed", "none"])
      && FACT_STATES.has(value.state) && nonnegative(value.available_execution_count) && nonnegative(value.total_execution_count)
      && value.available_execution_count <= value.total_execution_count
      && ["input", "output", "total", "reasoning", "cache_read", "cache_creation", "new_input"].every(key => scalarFact(value[key], "value"))
      && scalarFact(value.cache_read_ratio_basis_points, "value", 10000))) return false;
    const derivedInputsValid = value.input.state === "recorded" && value.cache_read.state === "recorded"
      && value.cache_read.value <= value.input.value;
    const contradictoryInputs = value.input.state === "recorded" && value.cache_read.state === "recorded"
      && value.cache_read.value > value.input.value;
    if (contradictoryInputs && (value.new_input.state !== "inconsistent"
        || value.cache_read_ratio_basis_points.state !== "inconsistent")) return false;
    if (value.new_input.state === "recorded"
        && (!derivedInputsValid || value.new_input.value !== value.input.value - value.cache_read.value)) return false;
    if (value.cache_read_ratio_basis_points.state === "recorded"
        && (!derivedInputsValid || value.input.value === 0)) return false;
    return true;
  }

  const activity = value => exact(value, ACTIVITY_KEYS) && ACTIVITY_KEYS.every(key => countFact(value[key]));
  const valuesFact = (value, maximum = null) => exact(value, ["state", "values"]) && FACT_STATES.has(value.state)
    && Array.isArray(value.values) && (maximum === null || value.values.length <= maximum) && distinct(value.values)
    && sorted(value.values) && value.values.every(item => typeof item === "string" && item.length > 0)
    && (value.state !== "recorded" || value.values.length > 0);

  function timing(value, status, execution = false) {
    const keys = execution ? ["state", "started_at", "ended_at", "duration_ms"] : ["state", "started_at", "ended_at", "last_seen_at", "duration_ms"];
    if (!exact(value, keys)) return false;
    if (execution) {
      if (!oneOf(value.state, ["recorded", "missing", "invalid"])) return false;
      if (value.state !== "recorded") return value.started_at === null && value.ended_at === null && value.duration_ms === null;
      const open = value.ended_at === null && value.duration_ms === null;
      const closed = instant(value.ended_at) && nonnegative(value.duration_ms) && value.ended_at >= value.started_at;
      return instant(value.started_at) && (status === "active" ? open
        : oneOf(status, ["completed", "failed"]) ? closed : open || closed);
    }
    if (!FACT_STATES.has(value.state) || value.last_seen_at !== null && !instant(value.last_seen_at)) return false;
    if (value.state !== "recorded") return (value.started_at === null || instant(value.started_at))
      && (value.ended_at === null || instant(value.ended_at)) && (value.duration_ms === null || nonnegative(value.duration_ms));
    const open = value.ended_at === null && value.duration_ms === null;
    const closed = instant(value.ended_at) && nonnegative(value.duration_ms) && value.ended_at >= value.started_at;
    return instant(value.started_at) && instant(value.last_seen_at) && value.last_seen_at >= value.started_at
      && (status === "active" ? open : oneOf(status, ["completed", "failed"]) ? closed : open || closed);
  }

  function assignment(value) {
    if (!(exact(value, ["state", "authority", "revision", "repository_id", "candidate_repository_ids"])
      && oneOf(value.state, ["assigned", "unassigned", "explicitly_unassigned", "conflict"])
      && oneOf(value.authority, ["automatic", "manual", "none"]) && nonnegative(value.revision)
      && (value.repository_id === null || UUID_V7.test(value.repository_id)) && Array.isArray(value.candidate_repository_ids)
      && value.candidate_repository_ids.length <= 128 && distinct(value.candidate_repository_ids)
      && sorted(value.candidate_repository_ids) && value.candidate_repository_ids.every(id => UUID_V7.test(id)))) return false;
    return value.state === "assigned" && oneOf(value.authority, ["automatic", "manual"])
        && value.repository_id !== null && value.candidate_repository_ids.length === 0
      || value.state === "unassigned" && value.authority === "none" && value.repository_id === null && value.candidate_repository_ids.length === 0
      || value.state === "explicitly_unassigned" && value.authority === "manual" && value.repository_id === null && value.candidate_repository_ids.length === 0
      || value.state === "conflict" && value.authority === "automatic" && value.repository_id === null && value.candidate_repository_ids.length >= 2;
  }

  const archive = value => exact(value, ["state", "revision", "effectively_eligible", "exclusion_reason"])
    && oneOf(value.state, ["active", "archived"]) && nonnegative(value.revision) && typeof value.effectively_eligible === "boolean"
    && oneOf(value.exclusion_reason, [null, "session_archived", "repository_archived"])
    && value.effectively_eligible === (value.exclusion_reason === null)
    && (value.state === "archived") === (value.exclusion_reason === "session_archived");

  const instruction = value => exact(value, ["state", "label", "additional_count", "content_available"])
    && oneOf(value.state, ["recorded", "not_observed", "not_captured", "expired", "invalid"])
    && (value.additional_count === null || nonnegative(value.additional_count)) && typeof value.content_available === "boolean"
    && (value.state === "recorded" ? typeof value.label === "string" && Array.from(value.label).length >= 1 && Array.from(value.label).length <= 160
      : value.label === null && value.content_available === false);

  function capture(value) {
    const families = ["instruction", "source", "model", "version", "timing", "tokens", "cache", "skill", "tool", "subagent", "error", "retry"];
    const notes = ["raw_content_not_captured", "raw_content_expired", "source_unsupported", "capture_gap", "certification_pending", "projection_invalid", "token_inconsistent", "cache_inconsistent"];
    return exact(value, ["state", "notes", "coverage"]) && oneOf(value.state, ["complete", "partial", "not_observed", "invalid"])
      && Array.isArray(value.notes) && value.notes.length <= 16 && distinct(value.notes) && sorted(value.notes) && value.notes.every(note => notes.includes(note))
      && Array.isArray(value.coverage) && value.coverage.length === 12
      && value.coverage.every((item, index) => exact(item, ["signal_family", "state"])
        && item.signal_family === families[index] && COVERAGE_STATES.has(item.state));
  }

  function validate(summary) {
    if (!exact(summary, ROOT_KEYS) || summary.schema_version !== "local-monitor-session-summary.response.v2"
        || !REVISION.test(summary.workspace_revision) || !exact(summary.session, SESSION_KEYS)
        || summary.session.session_id !== root.dataset.sessionId || !UUID_V7.test(summary.session.session_id)
        || !oneOf(summary.session.status, ["active", "completed", "failed", "unknown"])
        || !oneOf(summary.session.completeness, ["unbound", "partial", "rich", "full"])
        || !assignment(summary.session.assignment) || !archive(summary.session.archive) || !instruction(summary.session.instruction)
        || !valuesFact(summary.session.source, 5) || !valuesFact(summary.session.model, 16) || !valuesFact(summary.session.version)
        || summary.session.source.values.some(value => !SOURCES.has(value))
        || summary.session.archive.exclusion_reason === "repository_archived"
          && (summary.session.assignment.state !== "assigned" || summary.session.assignment.repository_id === null)
        || !timing(summary.session.timing, summary.session.status) || !tokens(summary.session.tokens) || !activity(summary.session.activity)
        || !capture(summary.session.capture) || !Array.isArray(summary.executions) || summary.executions.length > 256
        || !exact(summary.technical_references, ["native_session_ids", "trace_ids"])
        || !Array.isArray(summary.technical_references.native_session_ids) || !Array.isArray(summary.technical_references.trace_ids)) throw new TypeError("invalid Session summary");
    if (summary.technical_references.native_session_ids.some(value => typeof value !== "string" || value.length === 0)
        || !distinct(summary.technical_references.native_session_ids) || !sorted(summary.technical_references.native_session_ids)
        || summary.technical_references.trace_ids.some(value => typeof value !== "string" || !/^[0-9a-f]{32}$/.test(value))
        || !distinct(summary.technical_references.trace_ids) || !sorted(summary.technical_references.trace_ids)) throw new TypeError("invalid Session summary");
    let latest = 0;
    for (const execution of summary.executions) {
      if (!exact(execution, ["execution_id", "node_id", "latest", "source", "model", "lifecycle", "status", "timing", "tokens", "activity", "child_count"])
          || !UUID_V7.test(execution.execution_id) || !NODE.test(execution.node_id) || typeof execution.latest !== "boolean"
          || execution.source !== null && (typeof execution.source !== "string" || execution.source.length === 0)
          || execution.model !== null && (typeof execution.model !== "string" || execution.model.length === 0)
          || !oneOf(execution.lifecycle, ["selected", "started", "completed", "failed", "deselected", "unknown"])
          || !oneOf(execution.status, ["active", "completed", "failed", "unknown"]) || !timing(execution.timing, execution.status, true)
          || !tokens(execution.tokens) || !activity(execution.activity) || !nonnegative(execution.child_count) || execution.child_count > 4096) throw new TypeError("invalid Session summary");
      if (execution.latest) latest++;
    }
    if (latest !== (summary.executions.length === 0 ? 0 : 1)) throw new TypeError("invalid Session summary");
    if (summary.executions.some((execution, index) => index > 0
        && compareExecutions(summary.executions[index - 1], execution) > 0)) throw new TypeError("invalid Session summary");
    return summary;
  }

  function compareExecutions(left, right) {
    const group = timing => timing.state === "recorded" ? 0 : timing.state === "missing" ? 1 : 2;
    const groupDifference = group(left.timing) - group(right.timing);
    if (groupDifference !== 0) return groupDifference;
    if (left.timing.state === "recorded" && left.timing.started_at !== right.timing.started_at)
      return left.timing.started_at > right.timing.started_at ? -1 : 1;
    const leftSource = left.source ?? "\uffff";
    const rightSource = right.source ?? "\uffff";
    if (leftSource !== rightSource) return leftSource < rightSource ? -1 : 1;
    return left.execution_id < right.execution_id ? -1 : left.execution_id > right.execution_id ? 1 : 0;
  }

  const el = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  };
  const format = value => value.toLocaleString("ja-JP");
  const renderFact = (target, value) => window.LocalMonitorV1FactState.renderSessionCollection(target,
    { state: value.state, count: value.state === "recorded" ? BigInt(value.count ?? value.value) : null });

  function namedFact(title, value, dataName) {
    const card = tokenMetric(title, value);
    card.dataset[`sessionFixed${dataName}`] = "";
    return card;
  }

  function tokenMetric(title, value, valueLabel = format) {
    const card = el("div", "local-monitor-session-summary-card");
    card.append(el("h2", null, title));
    if (value.state === "recorded") card.append(el("strong", null, valueLabel(value.value)));
    else renderFact(card.appendChild(el("div")), value);
    return card;
  }

  function renderBars(card, first, second, className, firstLabel, secondLabel) {
    if (first.state !== "recorded" || second.state !== "recorded") return;
    const total = first.value + second.value;
    if (total <= 0) return;
    const bar = el("div", `local-monitor-session-bar ${className}`);
    const a = el("span"); a.style.width = `${first.value / total * 100}%`; a.setAttribute("role", "img"); a.setAttribute("aria-label", `${firstLabel} ${format(first.value)}`);
    const b = el("span"); b.style.width = `${second.value / total * 100}%`; b.setAttribute("role", "img"); b.setAttribute("aria-label", `${secondLabel} ${format(second.value)}`);
    bar.append(a, b); card.append(bar);
  }

  const requestUrl = (path, parameters) => `${path}?${new URLSearchParams(parameters)}`;

  const CONTENT_LABELS = { instruction: "指示", tool_input: "ツール入力", tool_result: "ツール結果", error_message: "エラーメッセージ", subagent_input: "サブエージェント入力", event_content: "イベント内容" };
  const CONTENT_STATE_LABELS = { not_captured: "内容は記録されていません", expired: "保存期間を過ぎたため表示できません", deleted: "内容は保存されていません", read_denied: "内容を読み取れません", oversized: "内容が大きすぎるため表示できません", invalid: "記録が一部欠けています" };
  const HTTP_CONTENT_LABELS = { 403: "内容を読み取れません", 404: "内容は記録されていません", 410: "保存期間を過ぎたため表示できません", 413: "内容が大きすぎるため表示できません", 409: "記録内容が更新されました", 503: "記録内容を一時的に表示できません" };
  const KIND_LABELS = Object.freeze({ execution: "実行", agent: "エージェント", skill: "スキル", tool: "ツール", subagent: "サブエージェント", event: "イベント", error: "エラー", retry: "再試行", permission: "権限", unknown_relation_group: "親子関係不明" });
  const STATUS_LABELS = Object.freeze({ active: "実行中", completed: "完了", failed: "失敗", unknown: "確認できません", selected: "選択", started: "開始", deselected: "選択解除", current: "有効", stale: "更新あり", invalid: "無効", certification_pending: "安定して取得できるか未確認です", unavailable: "利用できません", allowed: "許可", denied: "拒否", asked: "確認待ち" });
  const SIGNAL_LABELS = Object.freeze({ instruction: "指示", source: "取得元", model: "モデル", version: "バージョン", timing: "時刻", tokens: "トークン", cache: "キャッシュ", skill: "スキル", tool: "ツール", subagent: "サブエージェント", error: "エラー", retry: "再試行" });
  const stateLabel = value => ({ recorded: "記録あり", complete_zero: "今回の記録にはありません", not_observed: "今回の記録にはありません", source_unsupported: "この取得元では記録できません", capture_gap: "記録が一部欠けています", malformed: "記録が一部欠けています", oversized: "記録が一部欠けています", projection_invalid: "記録が一部欠けています", certification_pending: "安定して取得できるか未確認です", not_captured: "内容は記録されていません", redacted: "内容は記録されていません", expired: "保存期間を過ぎたため表示できません", inconsistent: "内訳を表示できません" })[value] ?? "記録が一部欠けています";
  const sourceLabel = value => SOURCES.has(value) ? window.LocalMonitorV1FactState.sessionSourceLabel(value) : value;

  function closeRawDialog() {
    if (!rawDialog?.open) return;
    rawDialog.close();
    const trigger = rawDialogTrigger; rawDialogTrigger = null;
    trigger?.focus();
  }

  function showRawDialog(trigger, title) {
    rawDialogTrigger = trigger;
    rawDialog.querySelector("[data-raw-content-title]").textContent = title;
    rawDialog.querySelector("[data-raw-content-status]").textContent = "読み込んでいます";
    rawDialog.querySelector("[data-raw-content-text]").textContent = "";
    rawDialog.showModal();
    rawDialog.querySelector("[data-raw-content-close]").focus();
  }

  function publishRawText(text, status) {
    rawDialog.querySelector("[data-raw-content-status]").textContent = status;
    rawDialog.querySelector("[data-raw-content-text]").textContent = text;
  }

  function validateContent(document, nodeId, part) {
    const text = document?.text;
    if (!exact(document, ["schema_version", "workspace_revision", "session_id", "node_id", "part", "state", "source_reference", "text", "utf8_byte_length", "unicode_scalar_length", "truncation"])
        || document.schema_version !== "local-monitor-node-content.response.v2" || document.workspace_revision !== state.revision
        || document.session_id !== root.dataset.sessionId || document.node_id !== nodeId || document.part !== part
        || document.state !== "available" || document.truncation !== false || typeof text !== "string"
        || document.utf8_byte_length !== new TextEncoder().encode(text).length || document.unicode_scalar_length !== [...text].length
        || document.utf8_byte_length > 1048576 || document.unicode_scalar_length > 1048576
        || !exact(document.source_reference, ["store_kind", "source_item_id", "revision"])
        || !["session_event_content", "raw_record", "analysis_run_raw", "sensitive_bundle", "analysis_sdk_directory"].includes(document.source_reference.store_kind)
        || typeof document.source_reference.source_item_id !== "string" || !document.source_reference.source_item_id.length
        || !nonnegative(document.source_reference.revision)) throw new TypeError("invalid content document");
    return document;
  }

  async function readGenericContent(trigger, nodeId, part) {
    const generation = routeGeneration;
    showRawDialog(trigger, CONTENT_LABELS[part]);
    try {
      const urlFactory = () => requestUrl(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/nodes/${nodeId}/content`, { workspace_revision: state.revision, part });
      const document = validateContent(await requestJson(urlFactory, false, () => selectNode(state.selectedExecutionId, nodeId, false, true, generation), generation), nodeId, part);
      throwIfRouteSuperseded(generation);
      publishRawText(document.text, `${format(document.utf8_byte_length)}バイト · ${format(document.unicode_scalar_length)} Unicodeスカラー · ${document.source_reference.store_kind} · ${document.source_reference.source_item_id} · リビジョン ${format(document.source_reference.revision)}`);
    } catch (error) { if (!(error instanceof RouteSuperseded)) publishRawText("", HTTP_CONTENT_LABELS[error?.status] ?? "記録内容を読み取れませんでした"); }
  }

  async function readSkillContent(trigger, snapshotId, current) {
    const title = current ? "現在のファイル" : "履歴スナップショット";
    showRawDialog(trigger, title);
    const path = `/api/local-monitor/v1/sessions/${root.dataset.sessionId}/skill-invocations/${snapshotId}/${current ? "current-file-read" : "content"}`;
    try {
      const response = await fetch(path, current ? { method: "POST", headers: { Accept: "application/json", "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" }, body: '{"schema_version":"local-skill-current-file-read.request.v1"}' } : { headers: { Accept: "application/json" } });
      if (!response.ok) { publishRawText("", HTTP_CONTENT_LABELS[response.status] ?? "スキルの内容を読み取れませんでした"); return; }
      const document = JSON.parse(await response.text());
      const validHistorical = !current && exact(document, ["schema_version", "snapshot_id", "content_kind", "body", "definition_path", "body_sha256", "definition_path_sha256", "captured_at"]) && document.schema_version === "local-skill-invocation-snapshot.content.v1" && document.content_kind === "historical_snapshot";
      const validCurrent = current && exact(document, ["schema_version", "snapshot_id", "content_kind", "comparison", "historical_body_sha256", "current_body_sha256", "current_body_utf8_bytes", "body", "read_at"]) && document.schema_version === "local-skill-current-file-read.response.v1" && document.content_kind === "current_file" && ["same", "changed"].includes(document.comparison);
      if ((!validHistorical && !validCurrent) || document.snapshot_id !== snapshotId || typeof document.body !== "string") throw new TypeError("invalid Skill document");
      const comparisonLabel = current ? { same: "変更なし", changed: "変更あり" }[document.comparison] : null;
      publishRawText(document.body, current ? `${title} · ${comparisonLabel}` : `${title} · ${document.captured_at} · 定義パス: ${document.definition_path}`);
    } catch { publishRawText("", "スキルの内容を読み取れませんでした"); }
  }

  const currentRouteGeneration = generation => generation === null || generation === routeGeneration;
  const throwIfRouteSuperseded = generation => { if (!currentRouteGeneration(generation)) throw new RouteSuperseded(); };

  async function requestJson(urlFactory, attempted = false, reestablish = null, generation = null) {
    let response;
    try { response = await fetch(urlFactory(), { headers: { Accept: "application/json" } }); }
    catch (error) { throwIfRouteSuperseded(generation); throw error; }
    throwIfRouteSuperseded(generation);
    if (response.status === 409 && !attempted) {
      const error = await response.json().catch(() => null);
      throwIfRouteSuperseded(generation);
      if (error?.error === "workspace_snapshot_stale") {
        const previous = state.revision;
        if (!await refreshSummary(generation)) throw new RouteSuperseded();
        if (state.revision === previous) throw new Error("Session revision did not advance");
        if (reestablish) await reestablish();
        throwIfRouteSuperseded(generation);
        return requestJson(urlFactory, true, reestablish, generation);
      }
    }
    if (!response.ok) { const failure = new Error("Session detail unavailable"); failure.status = response.status; throw failure; }
    const body = await response.text();
    throwIfRouteSuperseded(generation);
    return JSON.parse(body);
  }

  async function refreshSummary(generation = null) {
    let response;
    try { response = await fetch(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/summary`, { headers: { Accept: "application/json" } }); }
    catch (error) { throwIfRouteSuperseded(generation); throw error; }
    throwIfRouteSuperseded(generation);
    if (!response.ok) throw new Error("Session summary unavailable");
    const body = await response.text();
    throwIfRouteSuperseded(generation);
    const summary = validate(JSON.parse(body));
    state.executionState.clear(); state.summary = summary; state.revision = summary.workspace_revision;
    render(summary, false);
    return true;
  }

  function executionMemory(executionId) {
    if (!state.executionState.has(executionId)) state.executionState.set(executionId, {
      open: false, pages: new Map(), expanded: new Set(), scrollTop: 0,
    });
    return state.executionState.get(executionId);
  }

  async function loadTimeline(executionId, parentNodeId = null, after = null, attempted = false, generation = null) {
    const parameters = { workspace_revision: state.revision, execution_id: executionId };
    if (parentNodeId) parameters.parent_node_id = parentNodeId;
    if (after) parameters.after = after;
    parameters.limit = "100";
    const urlFactory = () => { parameters.workspace_revision = state.revision; return requestUrl(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/timeline`, parameters); };
    const page = validateTimeline(await requestJson(urlFactory, attempted, null, generation), executionId, parentNodeId);
    if (generation !== null && generation !== routeGeneration) return false;
    const memory = executionMemory(executionId);
    memory.open = true;
    const key = parentNodeId ?? "root";
    const existing = after ? memory.pages.get(key)?.items ?? [] : [];
    memory.pages.set(key, { items: [...existing, ...page.items], nextCursor: page.next_cursor });
    renderExecutions();
    return true;
  }

  function timingLabel(node) {
    if (node.timing?.state !== "recorded") return node.timing?.state === "invalid" ? "時刻が無効" : "時刻なし";
    return node.timing.duration_ms === null ? node.timing.started_at : `${format(node.timing.duration_ms)} ms`;
  }

  function renderNodes(execution, memory, parentNodeId, depth, authoritativeSetSize) {
    const fragment = document.createDocumentFragment();
    const page = memory.pages.get(parentNodeId ?? "root");
    if (!page) return fragment;
    const known = page.items.filter(item => ["exact", "explicit"].includes(item.relationship_authority));
    const unknown = page.items.filter(item => !["exact", "explicit"].includes(item.relationship_authority));
    const siblings = [...known, ...unknown];
    const appendRow = (node, target = fragment) => {
      const wrapper = el("div", "local-monitor-session-timeline-entry");
      const row = el("button", "local-monitor-session-timeline-node");
      row.type = "button"; row.dataset.timelineNode = node.node_id; row.style.setProperty("--timeline-depth", depth);
      row.setAttribute("role", "treeitem"); row.setAttribute("aria-level", String(depth + 1));
      row.setAttribute("aria-setsize", String(authoritativeSetSize ?? siblings.length)); row.setAttribute("aria-posinset", String(siblings.indexOf(node) + 1));
      row.setAttribute("aria-selected", String(state.selectedNodeId === node.node_id)); row.tabIndex = -1;
      if (node.child_count > 0) row.setAttribute("aria-expanded", String(memory.expanded.has(node.node_id)));
      const label = node.name?.state === "recorded" ? node.name.text : KIND_LABELS[node.kind];
      row.append(el("strong", null, label), el("span", null, `${KIND_LABELS[node.kind]} · ${STATUS_LABELS[node.status]} · ${timingLabel(node)}`));
      if (node.timing.state === "recorded" && node.timing.started_at && node.timing.duration_ms !== null
          && execution.timing.state === "recorded" && execution.timing.started_at && execution.timing.duration_ms !== null) {
        const start = Date.parse(node.timing.started_at) - Date.parse(execution.timing.started_at);
        const duration = node.timing.duration_ms;
        const extent = Math.max(1, execution.timing.duration_ms, start + duration);
        const geometry = duration === 0 ? el("span", "local-monitor-session-time-instant") : el("span", "local-monitor-session-time-bar");
        if (duration === 0) { geometry.dataset.timelineInstant = ""; geometry.setAttribute("aria-label", "記録された瞬時イベント"); }
        else geometry.dataset.timelineTimeBar = "";
        geometry.style.marginLeft = `${Math.max(0, start) / extent * 100}%`; if (duration > 0) geometry.style.width = `${duration / extent * 100}%`;
        wrapper.append(geometry);
      }
      row.addEventListener("click", async () => {
        const generation = ++routeGeneration;
        if (node.child_count > 0 && !await setExpanded(execution.execution_id, node.node_id, !memory.expanded.has(node.node_id), generation)) return;
        await selectNodeFromUser(execution.execution_id, node.node_id, true, generation);
      });
      wrapper.append(row);
      if (memory.expanded.has(node.node_id)) wrapper.append(renderNodes(execution, memory, node.node_id, depth + 1, node.child_count));
      target.append(wrapper);
    };
    known.forEach(node => appendRow(node));
    if (unknown.length) {
      const group = el("div", "local-monitor-session-unknown-group");
      group.append(el("strong", null, "親子関係不明"));
      const holder = el("div"); unknown.forEach(node => appendRow(node, holder));
      group.append(holder); fragment.append(group);
    }
    if (page.nextCursor) {
      const more = el("button", "local-monitor-session-load-more", "さらに読み込む");
      more.type = "button"; more.dataset.timelineLoadMore = parentNodeId ?? "root";
      more.addEventListener("click", () => loadTimeline(execution.execution_id, parentNodeId, page.nextCursor));
      fragment.append(more);
    }
    return fragment;
  }

  async function setExpanded(executionId, nodeId, expanded, generation = null) {
    generation ??= ++routeGeneration;
    const memory = executionMemory(executionId);
    try { if (expanded && !memory.pages.has(nodeId) && !await loadTimeline(executionId, nodeId, null, false, generation)) return false; }
    catch (error) { if (error instanceof RouteSuperseded) return false; throw error; }
    if (!currentRouteGeneration(generation)) return false;
    if (expanded) memory.expanded.add(nodeId);
    else memory.expanded.delete(nodeId);
    renderExecutions();
    return true;
  }

  function renderExecutions() {
    const executions = root.querySelector("[data-session-executions]");
    const focusedNodeId = document.activeElement?.dataset?.timelineNode;
    for (const section of executions.querySelectorAll("[data-execution-id]")) {
      const scroll = section.querySelector("[data-execution-scroll]");
      if (scroll) executionMemory(section.dataset.executionId).scrollTop = scroll.scrollTop;
    }
    executions.replaceChildren();
    for (const execution of state.summary.executions) {
      const memory = executionMemory(execution.execution_id);
      const section = el("section", "local-monitor-session-execution"); section.dataset.executionId = execution.execution_id;
      const toggle = el("button", "local-monitor-session-execution-toggle"); toggle.type = "button"; toggle.dataset.executionToggle = "";
      toggle.setAttribute("aria-expanded", String(memory.open));
      const summaryFacts = [`活動 ${format(execution.child_count)}件`, timingLabel(execution), `トークン ${execution.tokens.total.state === "recorded" ? format(execution.tokens.total.value) : stateLabel(execution.tokens.total.state)}`];
      for (const [key, label] of [["skill", "スキル"], ["tool", "ツール"], ["subagent", "サブエージェント"], ["error", "エラー"], ["retry", "再試行"]]) summaryFacts.push(`${label} ${execution.activity[key].state === "recorded" ? `${format(execution.activity[key].count)}件` : stateLabel(execution.activity[key].state)}`);
      toggle.append(el("strong", null, `実行 ${execution.source === null ? "確認できません" : sourceLabel(execution.source)} · ${STATUS_LABELS[execution.status]}`), el("span", null, summaryFacts.join(" · ")));
      toggle.addEventListener("click", async () => {
        memory.open = !memory.open;
        if (memory.open && !memory.pages.has("root")) await loadTimeline(execution.execution_id);
        renderExecutions();
      });
      section.append(toggle);
      if (memory.open) {
        const scroll = el("div", "local-monitor-session-execution-scroll"); scroll.dataset.executionScroll = ""; scroll.setAttribute("role", "tree"); scroll.setAttribute("aria-label", "実行タイムライン");
        scroll.append(renderNodes(execution, memory, null, 0, execution.child_count)); scroll.addEventListener("scroll", () => { memory.scrollTop = scroll.scrollTop; }); section.append(scroll);
        requestAnimationFrame(() => { scroll.scrollTop = memory.scrollTop; });
      }
      executions.append(section);
    }
    const rows = [...executions.querySelectorAll("[role='treeitem']")];
    const focusRow = rows.find(row => row.dataset.timelineNode === focusedNodeId) ?? rows.find(row => row.dataset.timelineNode === state.selectedNodeId) ?? rows[0];
    if (focusRow) focusRow.tabIndex = 0;
    if (focusedNodeId) focusRow?.focus();
  }

  function visibleTreeRows(row) { return [...row.closest("[role='tree']").querySelectorAll("[role='treeitem']")]; }

  async function handleTreeKey(event) {
    const row = event.target.closest("[role='treeitem']"); if (!row) return;
    const rows = visibleTreeRows(row); const index = rows.indexOf(row); const expanded = row.getAttribute("aria-expanded") === "true";
    let target = null;
    if (event.key === "ArrowDown") target = rows[index + 1] ?? row;
    else if (event.key === "ArrowUp") target = rows[index - 1] ?? row;
    else if (event.key === "Home") target = rows[0];
    else if (event.key === "End") target = rows.at(-1);
    else if (event.key === "ArrowRight" && expanded) target = Number(rows[index + 1]?.getAttribute("aria-level")) > Number(row.getAttribute("aria-level")) ? rows[index + 1] : row;
    else if (event.key === "ArrowRight" && row.hasAttribute("aria-expanded")) { event.preventDefault(); await setExpanded(row.closest("[data-execution-id]").dataset.executionId, row.dataset.timelineNode, true); return; }
    else if (event.key === "ArrowLeft" && expanded) { event.preventDefault(); await setExpanded(row.closest("[data-execution-id]").dataset.executionId, row.dataset.timelineNode, false); return; }
    else if (event.key === "ArrowLeft") target = rows.slice(0, index).reverse().find(candidate => Number(candidate.getAttribute("aria-level")) < Number(row.getAttribute("aria-level"))) ?? row;
    else if (event.key === "Enter" || event.key === " ") { event.preventDefault(); await selectNodeFromUser(row.closest("[data-execution-id]").dataset.executionId, row.dataset.timelineNode, true); return; }
    else return;
    event.preventDefault(); rows.forEach(item => item.tabIndex = -1); target.tabIndex = 0; target.focus();
  }

  function appendInspectorFact(section, label, fact, key = "value", valueLabel = value => value) {
    const row = el("p"); row.append(el("strong", null, `${label}: `));
    if (fact?.state === "recorded") {
      const value = Object.hasOwn(fact, key) ? fact[key] : "記録あり";
      row.append(document.createTextNode(String(valueLabel(value))));
    } else renderFact(row.appendChild(el("span")), fact ?? { state: "not_observed" });
    section.append(row);
  }

  function appendContentAction(section, detail, part) {
    const content = detail.content[part]; const row = el("p"); row.append(el("strong", null, `${CONTENT_LABELS[part]}: `));
    if (content.available) { const button = el("button", null, `${CONTENT_LABELS[part]}を表示`); button.type = "button"; button.addEventListener("click", () => readGenericContent(button, detail.node.node_id, part)); row.append(button); }
    else row.append(document.createTextNode(CONTENT_STATE_LABELS[content.state] ?? content.state)); section.append(row);
  }

  function appendRelated(section, title, items) {
    if (!items.length) return; section.append(el("h3", null, title)); const list = el("ul");
    for (const item of items) { const button = el("button", null, item.name.state === "recorded" ? item.name.text : KIND_LABELS[item.kind]); button.type = "button"; button.addEventListener("click", () => selectNodeFromUser(item.execution_id, item.node_id, true)); const li = el("li"); li.append(button); list.append(li); }
    section.append(list);
  }

  const AI_STATE_LABELS = Object.freeze({
    queued: "分析を待っています", running: "分析しています", succeeded: "分析が完了しました",
    zero_findings: "指摘はありませんでした", provider_failed: "AIで分析できませんでした",
    provider_partial: "不完全な結果のため表示できません", timed_out: "分析がタイムアウトしました",
    canceled: "分析をキャンセルしました", stale_snapshot: "セッションが更新されたため分析を完了できませんでした",
    scope_too_large: "分析対象が上限を超えています", invalid_result: "AI結果を安全に確認できません",
    invalid_evidence: "証拠を確認できないため結果を表示できません",
  });

  function aiPost(path, body) {
    return fetch(path, { method: "POST", credentials: "same-origin", headers: { Accept: "application/json", "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" }, body: JSON.stringify(body) });
  }

  function evidenceAction(reference) {
    if (!NODE.test(reference)) return el("span", "local-monitor-ai-evidence-unavailable", "この証拠は現在のタイムラインでは表示できません");
    const button = el("button", null, "証拠を表示"); button.type = "button";
    button.addEventListener("click", async () => {
      try {
        const dialog = document.querySelector("[data-session-ai-dialog]");
        if (dialog?.open) closeSessionAi(false);
        if (!await selectNodeFromUser(null, reference, true)) return;
        root.querySelector(`[data-timeline-node='${reference}']`)?.focus();
      }
      catch { button.replaceWith(el("span", "local-monitor-ai-evidence-unavailable", "この証拠は現在のタイムラインでは表示できません")); }
    });
    return button;
  }

  function appendAiField(target, label, value) {
    if (value === null || value === undefined || value === "") return;
    const row = el("p"); row.append(el("strong", null, `${label}: `), document.createTextNode(String(value))); target.append(row);
  }

  const AI_EVIDENCE_STATE_LABELS = { supported: "根拠あり", limited: "根拠に制約あり" };
  const AI_TARGET_KIND_LABELS = { instructions: "指示", skill: "スキル", agent: "エージェント", subagent_input: "サブエージェント入力", tool_configuration: "ツール設定" };

  function renderAiResult(target, result, focus = false) {
    target.replaceChildren();
    if (!result || typeof result !== "object" || typeof result.summary !== "string" || !Array.isArray(result.findings)
        || !Array.isArray(result.improvement_suggestions) || !Array.isArray(result.limitations)) {
      target.append(el("p", null, AI_STATE_LABELS.invalid_result)); return;
    }
    const heading = el("h3", null, "AIによる解釈"); heading.tabIndex = -1; target.append(heading);
    if (result.scope && typeof result.scope === "object") {
      const scope = el("section"); scope.append(el("h4", null, "分析対象の技術情報"));
      for (const [key, label] of [["kind", "種類"], ["session_id", "セッションID"], ["node_id", "ノードID"], ["anchor_id", "基準ID"]]) appendAiField(scope, label, result.scope[key]);
      target.append(scope);
    }
    if (result.snapshot && typeof result.snapshot === "object") {
      const snapshot = el("section"); snapshot.append(el("h4", null, "記録時点の技術情報")); appendAiField(snapshot, "スナップショットID", result.snapshot.snapshot_id); appendAiField(snapshot, "内容のSHA-256", result.snapshot.payload_sha256); target.append(snapshot);
    }
    target.append(el("h4", null, "要約"), el("p", null, result.summary));
    if (result.findings.length) target.append(el("h4", null, "指摘"));
    for (const finding of result.findings) {
      const article = el("article", "local-monitor-ai-finding"); article.append(el("h5", null, String(finding.title ?? "指摘")));
      appendAiField(article, "指摘ID", finding.finding_id); appendAiField(article, "解釈", finding.explanation); appendAiField(article, "根拠の状態", AI_EVIDENCE_STATE_LABELS[finding.evidence_state]); appendAiField(article, "制約", finding.limitation);
      for (const reference of Array.isArray(finding.evidence_refs) ? finding.evidence_refs : []) article.append(evidenceAction(reference));
      target.append(article);
    }
    if (result.improvement_suggestions.length) target.append(el("h4", null, "改善案"));
    for (const suggestion of result.improvement_suggestions) {
      const article = el("article", "local-monitor-ai-suggestion");
      appendAiField(article, "提案ID", suggestion.suggestion_id); appendAiField(article, "対象の種類", AI_TARGET_KIND_LABELS[suggestion.target_kind]); appendAiField(article, "対象", suggestion.target_label); appendAiField(article, "理由", suggestion.rationale); appendAiField(article, "変更案", suggestion.concrete_change); appendAiField(article, "期待される効果（AIによる提案）", suggestion.expected_effect); appendAiField(article, "リスク・制約", suggestion.risks_or_limitations);
      for (const reference of Array.isArray(suggestion.evidence_refs) ? suggestion.evidence_refs : []) article.append(evidenceAction(reference));
      target.append(article);
    }
    if (result.limitations.length) { target.append(el("h4", null, "制約")); for (const limitation of result.limitations) target.append(el("p", null, String(limitation))); }
    if (result.provenance && typeof result.provenance === "object") {
      const provenance = el("section"); provenance.append(el("h4", null, "分析の技術情報"));
      for (const [key, label] of [["provider", "プロバイダー"], ["model", "モデル"], ["configuration_sha256", "設定のSHA-256"], ["prompt_template_version", "テンプレート"], ["requested_at", "依頼日時"], ["started_at", "開始日時"], ["completed_at", "完了日時"], ["snapshot_id", "スナップショットID"], ["snapshot_sha256", "内容のSHA-256"]]) appendAiField(provenance, label, result.provenance[key]);
      if (result.provenance.coverage && typeof result.provenance.coverage === "object") {
        appendAiField(provenance, "対象件数", result.provenance.coverage.included); appendAiField(provenance, "除外件数", result.provenance.coverage.excluded); appendAiField(provenance, "記録内容", result.provenance.coverage.content_available ? "利用できます" : "利用できません");
      }
      target.append(provenance);
    }
    if (focus) heading.focus();
  }

  async function pollAiRun(runId, scope, generation = null, routeGenerationValue = null) {
    const status = document.querySelector(scope === "session" ? "[data-session-ai-status]" : "[data-node-ai-status]");
    const deadline = Date.now() + 610000;
    while (Date.now() < deadline && currentRouteGeneration(routeGenerationValue) && (scope === "session" ? generation === sessionPollGeneration && activeSessionRun === runId : generation === nodePollGeneration)) {
      try {
        const response = await fetch(`/api/local-monitor/v1/ai/${scope}-runs/${runId}`, { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } });
        if (!response.ok) throw new Error("poll_failed");
        const value = await response.json(); if (!currentRouteGeneration(routeGenerationValue)) return null; if (status) status.textContent = AI_STATE_LABELS[value.state] ?? "AI分析を確認できません";
        if (!["queued", "running"].includes(value.state)) return value;
      } catch { if (!currentRouteGeneration(routeGenerationValue)) return null; if (status) status.textContent = "AI分析の状態を一時的に確認できません。再試行しています"; }
      await new Promise(resolve => setTimeout(resolve, 250));
    }
    return null;
  }

  function showSessionReport(item, updateHistory = true, focus = false) {
    const status = document.querySelector("[data-session-ai-status]"); const target = document.querySelector("[data-session-ai-report]");
    status.textContent = item.snapshot_changed ? "前回の分析後に記録が更新されています" : AI_STATE_LABELS[item.state] ?? "";
    if (item.content_state === "expired") target.replaceChildren(el("p", null, "保存期間を過ぎたため分析内容を表示できません"));
    else if (item.content_state === "status_only" && ["succeeded", "zero_findings"].includes(item.state)) target.replaceChildren(el("p", null, "この分析レポートを確認できません"));
    else if (["succeeded", "zero_findings"].includes(item.state)) renderAiResult(target, item.result, focus);
    else {
      const heading = el("h3", null, AI_STATE_LABELS[item.state] ?? "AI分析を表示できません"); heading.tabIndex = -1; target.replaceChildren(heading); if (focus) heading.focus();
    }
    if (updateHistory && UUID_V7.test(item.run_id)) { state.ignoreRouteEvent = true; window.LocalMonitorV1History.push({ analysis: item.run_id }); }
  }

  async function readSessionReports(cursor = null, open = false, generation = null) {
    const url = new URL(`/api/local-monitor/v1/ai/sessions/${root.dataset.sessionId}/reports`, location.origin); url.searchParams.set("limit", "20"); if (cursor) url.searchParams.set("cursor", cursor);
    const response = await fetch(url, { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } }); if (!response.ok) return;
    const page = await response.json(); if (!currentRouteGeneration(generation)) return false;
    sessionReports = cursor ? [...sessionReports, ...(page.reports ?? [])] : page.reports ?? []; sessionReportCursor = page.next_cursor ?? null;
    const history = document.querySelector("[data-session-ai-history]"); history.replaceChildren();
    for (const item of sessionReports) { const button = el("button", null, item.run_id); button.type = "button"; button.addEventListener("click", () => showSessionReport(item)); history.append(button); }
    document.querySelector("[data-session-ai-more]").hidden = !sessionReportCursor;
    if (open && sessionReports[0]) showSessionReport(sessionReports[0]);
    return true;
  }

  async function readExactSessionReport(runId, generation = null) {
    let cursor = null;
    do {
      const url = new URL(`/api/local-monitor/v1/ai/sessions/${root.dataset.sessionId}/reports`, location.origin); url.searchParams.set("limit", "100"); if (cursor) url.searchParams.set("cursor", cursor);
      const response = await fetch(url, { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } }); if (!currentRouteGeneration(generation)) return { state: "canceled", report: null }; if (!response.ok) return { state: "unavailable", report: null };
      const page = await response.json(); if (!currentRouteGeneration(generation)) return { state: "canceled", report: null };
      const exact = (page.reports ?? []).find(item => item.run_id === runId); if (exact) return { state: "found", report: exact }; cursor = page.next_cursor ?? null;
    } while (cursor);
    return { state: "missing", report: null };
  }

  async function findExactSessionReport(runId) { return (await readExactSessionReport(runId)).report; }

  function showSessionDialog(invoker) {
    sessionAiInvoker = invoker; const dialog = document.querySelector("[data-session-ai-dialog]"); if (!dialog.open) dialog.showModal(); dialog.querySelector("[data-session-ai-close]").focus();
  }

  async function restoreExactSessionAnalysis(run, routeGenerationValue = null) {
    if (!currentRouteGeneration(routeGenerationValue)) return "canceled";
    const runId = run.run_id;
    showSessionDialog(document.querySelector("[data-session-ai-open]"));
    if (["queued", "running"].includes(run.state)) {
      activeSessionRun = runId; const pollGeneration = ++sessionPollGeneration; document.querySelector("[data-session-ai-cancel]").hidden = false;
      showSessionReport({ ...run, content_state: "status_only", snapshot_changed: false }, false);
      const terminal = await pollAiRun(runId, "session", pollGeneration, routeGenerationValue); if (pollGeneration !== sessionPollGeneration || !currentRouteGeneration(routeGenerationValue)) return "canceled";
      activeSessionRun = null; document.querySelector("[data-session-ai-cancel]").hidden = true;
      if (terminal && ["succeeded", "zero_findings"].includes(terminal.state)) {
        const exact = await readExactSessionReport(runId, routeGenerationValue); if (exact.state === "canceled") return "canceled"; if (exact.state === "unavailable") return closeExactAnalysisUnavailable(503, routeGenerationValue); showSessionReport(exact.report ?? { ...terminal, result: null, content_state: "status_only", snapshot_changed: false }, false, true);
      } else if (terminal) showSessionReport({ ...terminal, content_state: "status_only", snapshot_changed: false }, false, true);
      await readSessionReports(null, false, routeGenerationValue);
    } else if (["succeeded", "zero_findings"].includes(run.state)) {
      const exact = await readExactSessionReport(runId, routeGenerationValue); if (exact.state === "canceled") return "canceled"; if (exact.state === "unavailable") return closeExactAnalysisUnavailable(503, routeGenerationValue); if (exact.report) showSessionReport(exact.report, false); else showSessionReport({ ...run, result: null, content_state: "status_only", snapshot_changed: false }, false);
    } else showSessionReport({ ...run, content_state: "status_only", snapshot_changed: false }, false);
    return "restored";
  }

  async function restoreExactNodeAnalysis(run, route, routeGenerationValue = null) {
    if (!await selectNode(route.execution ?? null, run.node_id, false, false, routeGenerationValue) || !currentRouteGeneration(routeGenerationValue)) return "canceled";
    const section = inspector.querySelector("[data-inspector-kind]"); if (!section) return;
    const action = section.querySelector("[data-node-ai-start] button"); if (action) action.disabled = true;
    const surface = createNodeAiSurface(section, run.node_id); nodeTranscript = []; nodeAiContext = run.node_id;
    if (route.execution !== state.selectedExecutionId || route.node !== run.node_id) {
      state.ignoreRouteEvent = true; window.LocalMonitorV1History.replace({ execution: state.selectedExecutionId, node: run.node_id, analysis: run.run_id });
    }
    const status = surface.querySelector("[data-node-ai-status]"); status.textContent = AI_STATE_LABELS[run.state] ?? "";
    if (["queued", "running"].includes(run.state)) {
      const generation = ++nodePollGeneration; const terminal = await pollAiRun(run.run_id, "node", generation, routeGenerationValue); if (generation !== nodePollGeneration || !terminal || !currentRouteGeneration(routeGenerationValue)) return "canceled";
      if (["succeeded", "zero_findings"].includes(terminal.state) && terminal.result) renderAiResult(surface.querySelector("[data-node-ai-result]"), terminal.result, true);
      else focusNodeAiFailure(surface, terminal.state);
    } else if (["succeeded", "zero_findings"].includes(run.state) && run.result) renderAiResult(surface.querySelector("[data-node-ai-result]"), run.result);
    else focusNodeAiFailure(surface, run.state);
    return "restored";
  }

  async function restoreExactAnalysis(runId, route, generation = null) {
    try {
      const response = await fetch(`/api/local-monitor/v1/ai/runs/${runId}`, { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } });
      if (!currentRouteGeneration(generation)) return "canceled";
      if (response.status === 404) { location.reload(); return "closed"; }
      if (!response.ok) return closeExactAnalysisUnavailable(response.status, generation);
      const run = await response.json();
      if (!currentRouteGeneration(generation)) return "canceled";
      if (run.run_id !== runId || run.session_id !== root.dataset.sessionId) return closeExactAnalysisUnavailable(503, generation);
      if (run.scope_kind === "node" && NODE.test(run.node_id)) return await restoreExactNodeAnalysis(run, route, generation);
      else if (run.scope_kind === "session") return await restoreExactSessionAnalysis(run, generation);
      else return closeExactAnalysisUnavailable(503, generation);
    } catch { return closeExactAnalysisUnavailable(503, generation); }
  }

  function closeExactAnalysisUnavailable(status = 503, generation = null) {
    if (!currentRouteGeneration(generation)) return "canceled";
    nodePollGeneration++; nodeTranscript = []; nodeAiContext = null;
    const dialog = document.querySelector("[data-session-ai-dialog]"); if (dialog?.open) closeSessionAi(false); else { activeSessionRun = null; sessionPollGeneration++; document.querySelector("[data-session-ai-cancel]").hidden = true; }
    document.querySelector("[data-session-ai-report]")?.replaceChildren(); document.querySelector("[data-session-ai-history]")?.replaceChildren();
    renderRouteRecovery({ status }); return "closed";
  }

  async function openSessionAi(invoker) {
    showSessionDialog(invoker);
    if (!sessionReports.length) await readSessionReports(null, false);
    if (sessionReports[0]) showSessionReport(sessionReports[0]); else document.querySelector("[data-session-ai-status]").textContent = "まだ分析はありません";
  }

  async function startSessionAi() {
    const response = await aiPost("/api/local-monitor/v1/ai/session-runs", { session_id: root.dataset.sessionId });
    if (!response.ok) { document.querySelector("[data-session-ai-status]").textContent = "AI分析を開始できませんでした"; return; }
    const started = await response.json(); activeSessionRun = started.run_id; const generation = ++sessionPollGeneration; document.querySelector("[data-session-ai-cancel]").hidden = false; state.ignoreRouteEvent = true; window.LocalMonitorV1History.push({ analysis: started.run_id }); const run = await pollAiRun(started.run_id, "session", generation);
    activeSessionRun = null; document.querySelector("[data-session-ai-cancel]").hidden = true;
    if (run && ["succeeded", "zero_findings"].includes(run.state)) {
      const report = await findExactSessionReport(run.run_id); showSessionReport(report ?? { ...run, result: null, content_state: "status_only", snapshot_changed: false }, false, true);
    } else if (run) showSessionReport({ ...run, content_state: "status_only", snapshot_changed: false }, false, true);
    await readSessionReports(null, false);
  }

  function closeNodeAi(section) { nodePollGeneration++; nodeTranscript = []; nodeAiContext = null; section.querySelector("[data-node-ai-surface]")?.remove(); }

  async function startNodeAi(section, nodeId, question = null) {
    if (nodeAiContext !== nodeId || question === null) { nodeTranscript = []; nodeAiContext = nodeId; }
    const body = { session_id: root.dataset.sessionId, node_id: nodeId }; if (question !== null) { body.question = question; body.prior_turns = nodeTranscript; }
    if (new TextEncoder().encode(JSON.stringify(body)).length > 262144 || question !== null && new TextEncoder().encode(question).length > 4096 || nodeTranscript.length > 16) {
      section.querySelector("[data-node-ai-status]").textContent = "質問が送信可能な上限を超えています"; return;
    }
    const response = await aiPost("/api/local-monitor/v1/ai/node-runs", body); if (!response.ok) { section.querySelector("[data-node-ai-status]").textContent = "AI分析を開始できませんでした"; return; }
    const started = await response.json(); state.ignoreRouteEvent = true; window.LocalMonitorV1History.push({ execution: state.selectedExecutionId, node: nodeId, analysis: started.run_id }); const generation = ++nodePollGeneration; const run = await pollAiRun(started.run_id, "node", generation); if (!run) return;
    if (["succeeded", "zero_findings"].includes(run.state) && run.result) {
      renderAiResult(section.querySelector("[data-node-ai-result]"), run.result, true); const answer = run.result.summary;
      if (new TextEncoder().encode(answer).length <= 32768) { nodeTranscript.push({ question: question ?? "", answer }); if (nodeTranscript.length > 16) nodeTranscript.shift(); }
    } else focusNodeAiFailure(section, run.state);
  }

  function focusNodeAiFailure(section, stateName) {
    const result = section.querySelector("[data-node-ai-result]"); const heading = el("h3", null, AI_STATE_LABELS[stateName] ?? "AI分析を表示できません"); heading.tabIndex = -1; result.replaceChildren(heading); heading.focus();
  }

  function createNodeAiSurface(section, nodeId) {
    section.querySelector("[data-node-ai-surface]")?.remove();
    const surface = el("section", "local-monitor-node-ai"); surface.dataset.nodeAiSurface = "";
    surface.append(el("h3", null, "この項目のAI分析"));
    const status = el("div"); status.dataset.nodeAiStatus = ""; status.setAttribute("role", "status"); status.setAttribute("aria-live", "polite");
    const result = el("div"); result.dataset.nodeAiResult = ""; const question = el("textarea"); question.setAttribute("aria-label", "追加の質問"); question.maxLength = 4096;
    const ask = el("button", null, "質問する"); ask.type = "button"; ask.addEventListener("click", () => startNodeAi(surface, nodeId, question.value));
    const close = el("button", null, "AI分析を閉じる"); close.type = "button"; close.addEventListener("click", () => { closeNodeAi(section); const action = section.querySelector("[data-node-ai-start] button"); if (action) { action.disabled = false; action.focus(); } });
    surface.append(status, result, question, ask, close); const anchor = section.querySelector("[data-node-ai-start]"); if (anchor) anchor.after(surface); else section.append(surface); return surface;
  }

  function appendNodeAi(section, nodeId) {
    if (section.querySelector("[data-node-ai-start]")) return;
    const start = el("section", "local-monitor-node-ai-start"); start.dataset.nodeAiStart = "";
    start.append(el("p", null, "選択した項目と利用可能な記録内容は GitHub Copilot へ送信される場合があります。"));
    const action = el("button", null, "この項目をAIで分析"); action.type = "button";
    action.addEventListener("click", async () => {
      action.disabled = true;
      await startNodeAi(createNodeAiSurface(section, nodeId), nodeId);
    });
    start.append(action); section.append(start);
  }

  function ensureSelectedNodeAiStart() { const section = inspector.querySelector("[data-inspector-kind]"); if (aiReady && state.selectedNodeId && section) appendNodeAi(section, state.selectedNodeId); }

  function renderInspector(detail) {
    const node = detail.node; const metadata = node.metadata; inspector.replaceChildren();
    inspector.setAttribute("aria-label", node.name.state === "recorded" ? node.name.text : KIND_LABELS[node.kind]);
    if (narrowInspector.matches) {
      inspectorReturnFocus = { executionId: detail.execution.execution_id, nodeId: node.node_id };
      inspector.append(createInspectorClose());
      inspector.setAttribute("role", "dialog"); inspector.setAttribute("aria-modal", "true"); inspector.setAttribute("aria-hidden", "false");
      setBackgroundInert(true);
    }
    const section = el("section", "local-monitor-contextual-inspector"); section.dataset.inspectorKind = node.kind;
    const overview = el("button", null, "セッションの概要に戻る"); overview.type = "button"; overview.addEventListener("click", () => { routeGeneration++; state.ignoreRouteEvent = true; window.LocalMonitorV1History.push({ execution: null, node: null }); fallbackSelection(false); });
    section.append(overview, el("h2", null, node.name.state === "recorded" ? node.name.text : KIND_LABELS[node.kind]), el("p", null, `${KIND_LABELS[node.kind]} · ${STATUS_LABELS[node.status]} · ${timingLabel(node)}`));
    if (aiReady) appendNodeAi(section, node.node_id);
    if (node.kind === "tool") {
      appendInspectorFact(section, "開始", { state: node.timing.state, value: node.timing.started_at }); appendInspectorFact(section, "終了", node.timing.ended_at === null ? { state: "not_observed" } : { state: node.timing.state, value: node.timing.ended_at }); appendInspectorFact(section, "所要時間", node.timing.duration_ms === null ? { state: "not_observed" } : { state: node.timing.state, value: `${node.timing.duration_ms} ms` });
      appendInspectorFact(section, "呼び出し元", metadata.caller, "node_id"); appendInspectorFact(section, "ライフサイクル", metadata.lifecycle, "value", value => STATUS_LABELS[value]); appendInspectorFact(section, "状態", metadata.status, "value", value => STATUS_LABELS[value]); appendInspectorFact(section, "終了状態", metadata.exit);
      if (metadata.mcp_server_identity.state === "recorded") appendInspectorFact(section, "MCPサーバーID", metadata.mcp_server_identity);
      appendInspectorFact(section, "ツール", metadata.mcp_tool_name);
    } else if (node.kind === "skill") {
      section.append(el("p", null, `現在の状態: ${STATUS_LABELS[metadata.current_valid_state]}`)); appendInspectorFact(section, "取得元", metadata.source, "value", sourceLabel); appendInspectorFact(section, "起動条件", metadata.trigger); appendInspectorFact(section, "一覧の参照先", metadata.inventory_reference);
      if (metadata.historical_snapshot_reference.state === "recorded") {
        const snapshotId = metadata.historical_snapshot_reference.value; const historical = el("button", null, "履歴スナップショットを表示"); historical.type = "button"; historical.addEventListener("click", () => readSkillContent(historical, snapshotId, false));
        const current = el("button", null, "現在のファイルを読み取る"); current.type = "button"; current.addEventListener("click", () => readSkillContent(current, snapshotId, true)); section.append(historical, current);
      } else section.append(el("p", null, `履歴スナップショット: ${stateLabel(metadata.historical_snapshot_reference.state)}`));
    } else if (node.kind === "subagent") {
      for (const [key, label] of [["selected", "選択"], ["started", "開始"], ["completed", "完了"], ["failed", "失敗"], ["deselected", "選択解除"]]) appendInspectorFact(section, label, metadata.lifecycle[key]);
      for (const [key, label] of [["skill", "スキル活動"], ["tool", "ツール活動"], ["subagent", "サブエージェント活動"], ["error", "エラー活動"], ["retry", "再試行活動"]]) appendInspectorFact(section, label, metadata.activity[key], "count");
      for (const [key, label] of [["input", "入力トークン"], ["output", "出力トークン"], ["total", "トークン合計"], ["reasoning", "推論トークン"], ["cache_read", "キャッシュから読み込み"], ["cache_creation", "キャッシュ書き込み"], ["new_input", "新規入力"]]) appendInspectorFact(section, label, metadata.tokens[key]);
      appendInspectorFact(section, "子項目", metadata.children, "count");
    } else if (node.kind === "error") {
      appendInspectorFact(section, "エラーコード", metadata.error_code); appendInspectorFact(section, "状態", metadata.status, "value", value => STATUS_LABELS[value]);
    } else if (node.kind === "permission") {
      appendInspectorFact(section, "判断", metadata.decision, "value", value => STATUS_LABELS[value]); appendInspectorFact(section, "待機", metadata.wait);
    } else if (node.kind === "event") {
      appendInspectorFact(section, "イベント", metadata.event_name); appendInspectorFact(section, "取得元の時刻", metadata.source_time);
    } else if (node.kind === "retry") {
      appendInspectorFact(section, "試行回数", metadata.attempt); appendInspectorFact(section, "対象", metadata.target, "node_id"); appendInspectorFact(section, "復旧", metadata.recovered, "value", value => value ? "はい" : "いいえ");
    }
    if (node.kind !== "skill") for (const part of CONTENT_PARTS) appendContentAction(section, detail, part);
    if (detail.parent_path.length) { section.append(el("h3", null, "親項目の経路")); const path = el("ol"); for (const item of detail.parent_path) path.append(el("li", null, item.name.state === "recorded" ? item.name.text : KIND_LABELS[item.kind])); section.append(path); }
    appendRelated(section, "再試行", detail.related.retry); appendRelated(section, "復旧", detail.related.recovery); appendRelated(section, "子項目", detail.related.children);
    const refs = node.technical_references; const technical = el("details"); const referenceLabels = { source_kind: "取得元の種類", source_identity: "取得元ID", trace_id: "トレースID", span_id: "スパンID", event_id: "イベントID" }; technical.append(el("summary", null, "技術情報")); for (const key of ["source_kind", "source_identity", "trace_id", "span_id", "event_id"]) if (refs[key] !== null) technical.append(el("p", null, `${referenceLabels[key]}: ${refs[key]}`)); section.append(technical);
    inspector.append(section);
    if (narrowInspector.matches) requestAnimationFrame(() => inspector.querySelector("[data-inspector-close]")?.focus());
  }

  async function selectNode(executionId, nodeId, push, attempted = false, generation = null) {
    generation ??= ++routeGeneration;
    if (!attempted && currentRouteGeneration(generation)) closeRawDialog();
    if (state.selectedNodeId !== nodeId) { nodePollGeneration++; nodeTranscript = []; nodeAiContext = null; }
    const urlFactory = () => requestUrl(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/nodes/${nodeId}`, { workspace_revision: state.revision });
    const rawDetail = await requestJson(urlFactory, attempted, null, generation);
    const detail = validateNode(rawDetail, nodeId, executionId);
    if (generation !== routeGeneration) return false;
    executionId = detail.execution.execution_id;
    const memory = executionMemory(executionId);
    const path = (detail.parent_path ?? []).filter(parent => ["exact", "explicit"].includes(parent.relationship_authority));
    if (!memory.pages.has("root") && !await loadTimeline(executionId, null, null, false, generation)) return false;
    for (const parent of path.slice(1)) if (!memory.pages.has(parent.node_id) && !await loadTimeline(executionId, parent.node_id, null, false, generation)) return false;
    if (generation !== routeGeneration) return false;
    state.selectedExecutionId = executionId; state.selectedNodeId = nodeId; memory.open = true;
    for (const parent of path) memory.expanded.add(parent.node_id);
    renderInspector(detail);
    if (push) { state.ignoreRouteEvent = true; window.LocalMonitorV1History.push({ execution: executionId, node: nodeId }); }
    else if (!window.LocalMonitorV1History.current().execution) window.LocalMonitorV1History.replace({ execution: executionId, node: nodeId });
    renderExecutions();
    return true;
  }

  async function selectNodeFromUser(executionId, nodeId, push, generation = null) {
    generation ??= ++routeGeneration;
    try { return await selectNode(executionId, nodeId, push, false, generation); }
    catch (error) { if (error instanceof RouteSuperseded) return false; throw error; }
  }

  function fallbackSelection(replace) {
    state.selectedExecutionId = null; state.selectedNodeId = null;
    renderOverview(state.summary);
    if (replace) window.LocalMonitorV1History.replace({ execution: null, node: null });
  }

  async function applyRoute(route) {
    const generation = ++routeGeneration;
    try {
      if (!state.summary) return;
      const closedAnalysis = !route.analysis && document.querySelector("[data-session-ai-dialog]")?.open;
      if (closedAnalysis) closeSessionAi(!route.execution && !route.node);
      if (route.analysis) { const outcome = await restoreExactAnalysis(route.analysis, route, generation); if (generation !== routeGeneration || outcome === "closed" || outcome === "canceled" || document.querySelector("[data-node-ai-surface]")) return; }
      if (route.node) {
        if (document.querySelector("[data-session-ai-dialog]")?.open) closeSessionAi(false);
        try { const selected = await selectNode(route.execution ?? null, route.node, false, false, generation); if (selected && generation === routeGeneration && closedAnalysis) root.querySelector("[data-timeline-node][aria-selected=true]")?.focus(); }
        catch (error) { if (error instanceof RouteSuperseded) return; if (error?.status === 404) fallbackSelection(true); else renderRouteRecovery(error); }
      } else if (route.execution) {
        const execution = state.summary.executions.find(item => item.execution_id === route.execution);
        if (!execution) { fallbackSelection(true); return; }
        if (!executionMemory(execution.execution_id).pages.has("root") && !await loadTimeline(execution.execution_id, null, null, false, generation)) return;
        if (generation !== routeGeneration) return;
        state.selectedExecutionId = execution.execution_id; state.selectedNodeId = null;
        for (const item of state.summary.executions) executionMemory(item.execution_id).open = item.execution_id === execution.execution_id;
        renderOverview(state.summary); renderExecutions(); if (closedAnalysis) root.querySelector(`[data-execution-id='${route.execution}'] [data-execution-toggle]`)?.focus();
      } else fallbackSelection(false);
    } catch (error) {
      if (error instanceof RouteSuperseded) return;
      renderRouteRecovery(error);
    }
  }

  function renderRouteRecovery(error) {
    state.selectedNodeId = null;
    const action = error?.status === 409 ? "セッション一覧を開いてください" : "もう一度お試しください";
    inspector.setAttribute("aria-label", "セッション詳細を表示できません");
    inspector.replaceChildren(el("h2", null, "セッション詳細を表示できません"), el("p", null, action));
    normalizeInspectorBreakpoint(narrowInspector);
  }

  function renderOverview(summary) {
    const session = summary.session;
    const overview = root.querySelector("[data-session-overview]"); overview.setAttribute("aria-label", "セッションの概要"); overview.replaceChildren(el("h2", null, "セッションの概要"));
    overview.append(el("h3", null, "最初の指示"), el("p", null, session.instruction.state === "recorded" ? session.instruction.label : stateLabel(session.instruction.state)));
    overview.append(el("p", null, session.instruction.additional_count === null
      ? "追加の指示 今回の記録にはありません" : `追加の指示 ${format(session.instruction.additional_count)}件`));
    overview.append(el("p", null, `状態 ${STATUS_LABELS[session.status]} · 実行 ${format(summary.executions.length)}件`));
    const sourceRow = el("p"); sourceRow.append(document.createTextNode("取得元 ")); const source = sourceRow.appendChild(el("span")); source.dataset.sessionOverviewSource = "";
    if (session.source.state === "recorded") source.textContent = session.source.values.map(window.LocalMonitorV1FactState.sessionSourceLabel).join(" / ");
    else renderFact(source, { state: session.source.state, count: null });
    const timeRow = el("p"); timeRow.append(document.createTextNode("時刻 ")); const time = timeRow.appendChild(el("span")); time.dataset.sessionOverviewTime = "";
    if (session.timing.state === "recorded") time.textContent = timingLabel(session);
    else renderFact(time, { state: session.timing.state, count: null });
    overview.append(sourceRow, timeRow);
    const coverage = el("ul"); for (const item of session.capture.coverage) coverage.append(el("li", null, `${SIGNAL_LABELS[item.signal_family]}: ${stateLabel(item.state)}`));
    overview.append(el("h3", null, "取得範囲"), coverage);
    const technical = el("details"); technical.append(el("summary", null, "技術情報"), el("p", null, `リビジョン ${summary.workspace_revision}`));
    const referenceLabels = { native_session_ids: "取得元のセッションID", trace_ids: "トレースID" }; for (const [key, values] of Object.entries(summary.technical_references)) for (const value of values) technical.append(el("p", null, `${referenceLabels[key]}: ${value}`)); overview.append(technical);
    normalizeInspectorBreakpoint(narrowInspector);
  }

  function closeInspector() {
    if (!narrowInspector.matches) return;
    inspector.setAttribute("aria-hidden", "true");
    setBackgroundInert(false);
    const target = inspectorReturnFocus; inspectorReturnFocus = null;
    if (target instanceof Element && target.isConnected && target !== document.body) target.focus();
    else if (!(target instanceof Element)) root.querySelector(`[data-execution-id='${target?.executionId}'] [data-timeline-node='${target?.nodeId}']`)?.focus();
    if (document.activeElement === document.body) root.querySelector("[data-execution-toggle]")?.focus();
  }

  function setBackgroundInert(value) {
    for (const target of root.querySelectorAll("[data-session-context], [data-session-summary], [data-session-executions]")) target.inert = value;
    document.querySelector(".monitor-shell-header").inert = value;
  }

  function createInspectorClose() {
    const close = el("button", "local-monitor-session-inspector-close", "閉じる"); close.type = "button"; close.dataset.inspectorClose = ""; close.setAttribute("aria-label", "インスペクターを閉じる"); close.addEventListener("click", closeInspector); return close;
  }

  function normalizeInspectorBreakpoint(event) {
    if (event.matches) {
      inspectorReturnFocus = state.selectedNodeId ? { executionId: state.selectedExecutionId, nodeId: state.selectedNodeId } : document.activeElement;
      if (!inspector.querySelector("[data-inspector-close]")) inspector.prepend(createInspectorClose());
      inspector.setAttribute("role", "dialog"); inspector.setAttribute("aria-modal", "true"); inspector.setAttribute("aria-hidden", "false");
      setBackgroundInert(true); requestAnimationFrame(() => inspector.querySelector("[data-inspector-close]")?.focus()); return;
    }
    inspector.removeAttribute("role"); inspector.removeAttribute("aria-modal"); inspector.removeAttribute("aria-hidden");
    inspector.querySelector("[data-inspector-close]")?.remove();
    setBackgroundInert(false); inspectorReturnFocus = null;
  }

  function render(summary, openLatest = true) {
    const session = summary.session;
    const safeInstant = session.timing.started_at ?? session.timing.last_seen_at;
    const sessionLabel = session.instruction.label !== null ? session.instruction.label : safeInstant === null ? "日時不明のセッション" : `${new Intl.DateTimeFormat("ja-JP", { year: "numeric", month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit", hour12: false }).format(new Date(safeInstant))} のセッション`;
    root.querySelector("[data-session-breadcrumb]").textContent = sessionLabel;
    root.querySelector("[data-session-title]").textContent = sessionLabel;
    const context = root.querySelector("[data-session-context-content]");
    context.replaceChildren(el("strong", null, sessionLabel), el("span", null, ` ${STATUS_LABELS[session.status]}`));
    const source = el("span"); source.dataset.sessionSource = "";
    if (session.source.state === "recorded") source.textContent = session.source.values.map(window.LocalMonitorV1FactState.sessionSourceLabel).join(" / ");
    else renderFact(source, { state: session.source.state, count: null });
    const time = el("span"); time.dataset.sessionTime = "";
    if (session.timing.state === "recorded") time.textContent = session.timing.ended_at === null
      ? `${session.timing.started_at} から実行中` : `${session.timing.started_at} – ${session.timing.ended_at} · ${format(session.timing.duration_ms)} ms`;
    else renderFact(time, { state: session.timing.state, count: null });
    context.append(source, time);
    if (session.archive.state !== "active") context.append(el("span", null, " アーカイブ済み"));
    if (session.capture.state !== "complete") context.append(el("span", null, " 記録に制限があります"));

    const summaryRoot = root.querySelector("[data-session-summary]");
    summaryRoot.replaceChildren();
    const total = tokenMetric("トークン合計", session.tokens.total);
    if (session.tokens.total.state === "recorded"
        && session.tokens.input.state === "recorded" && session.tokens.output.state === "recorded"
        && session.tokens.total.value === session.tokens.input.value + session.tokens.output.value) {
      renderBars(total, session.tokens.input, session.tokens.output, "local-monitor-token-bar", "入力", "出力");
    }
    const cache = tokenMetric("入力トークンの内訳", session.tokens.cache_read_ratio_basis_points, value => `${format(value / 100)}%`);
    if (session.tokens.input.state === "recorded" && session.tokens.cache_read.state === "recorded"
        && session.tokens.new_input.state === "recorded" && session.tokens.cache_read_ratio_basis_points.state === "recorded"
        && session.tokens.input.value === session.tokens.cache_read.value + session.tokens.new_input.value
        && session.tokens.cache_read_ratio_basis_points.value === Math.round(session.tokens.cache_read.value * 10000 / session.tokens.input.value)) {
      renderBars(cache, session.tokens.cache_read, session.tokens.new_input, "local-monitor-cache-bar", "キャッシュから読み込み", "新規入力");
    }
    if (session.tokens.cache_creation.state === "recorded") cache.append(el("small", null, `キャッシュ書き込み ${format(session.tokens.cache_creation.value)}`));
    const input = namedFact("入力", session.tokens.input, "Input");
    const output = namedFact("出力", session.tokens.output, "Output");
    const cacheRead = namedFact("キャッシュから読み込み", session.tokens.cache_read, "CacheRead");
    const newInput = namedFact("新規入力", session.tokens.new_input, "NewInput");
    const coverageCard = el("div", "local-monitor-session-summary-card"); coverageCard.dataset.sessionFixedCoverage = "";
    coverageCard.append(el("h2", null, "取得範囲"));
    const coverageList = el("ul", "local-monitor-session-coverage");
    for (const item of session.capture.coverage) {
      const row = el("li"); row.append(el("span", null, `${SIGNAL_LABELS[item.signal_family]}: `));
      const factTarget = row.appendChild(el("span"));
      if (item.state === "recorded") factTarget.textContent = "記録済み";
      else if (item.state === "complete_zero") factTarget.textContent = "0件（完全）";
      else renderFact(factTarget, { state: item.state, count: null });
      coverageList.append(row);
    }
    coverageCard.append(coverageList);
    summaryRoot.append(total, cache, input, output, cacheRead, newInput, coverageCard);
    for (const [key, label] of [["skill", "スキル"], ["tool", "ツール"], ["subagent", "サブエージェント"], ["error", "エラー"], ["retry", "再試行"]]) {
      const card = el("div", "local-monitor-session-summary-card"); card.append(el("h2", null, label));
      renderFact(card.appendChild(el("div")), session.activity[key]); summaryRoot.append(card);
    }

    for (const execution of summary.executions) if (openLatest) executionMemory(execution.execution_id).open = execution.latest;
    renderExecutions();
    renderOverview(summary);
  }

  async function load(refresh = false, applyCurrentRoute = true) {
    try {
      const response = await fetch(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/summary`, { headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error("セッションの概要を表示できません");
      const summary = validate(JSON.parse(await response.text()));
      if (refresh && state.revision !== summary.workspace_revision) state.executionState.clear();
      state.summary = summary; state.revision = summary.workspace_revision; render(summary);
      const route = window.LocalMonitorV1History.current();
      if (applyCurrentRoute && (route.node || route.execution || route.analysis)) await applyRoute(route);
      else if (applyCurrentRoute) {
        const latest = summary.executions.find(execution => execution.latest);
        if (latest && !executionMemory(latest.execution_id).pages.has("root")) await loadTimeline(latest.execution_id);
      }
    } catch {
      root.querySelector("[data-session-context-content]").textContent = "セッションを読み込めませんでした";
    }
  }

  async function checkAiReadiness() {
    try {
      const response = await fetch("/api/local-monitor/v1/settings/ai-readiness", { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } }); const value = response.ok ? await response.json() : null;
      aiReady = value?.readiness_state === "ready"; const action = document.querySelector("[data-session-ai-open]"); action.hidden = !aiReady; if (aiReady) { ensureSelectedNodeAiStart(); await readSessionReports(null, false); }
    } catch { aiReady = false; }
  }

  document.addEventListener("cao-route-state", event => {
    if (state.ignoreRouteEvent) { state.ignoreRouteEvent = false; return; }
    applyRoute(event.detail);
  });
  root.querySelector("[data-session-executions]").addEventListener("keydown", handleTreeKey);
  document.addEventListener("keydown", event => { if (event.key === "Escape" && narrowInspector.matches && inspector.getAttribute("aria-hidden") === "false") { event.preventDefault(); closeInspector(); } });
  inspector.addEventListener("keydown", event => {
    if (event.key !== "Tab" || !narrowInspector.matches || inspector.getAttribute("aria-hidden") === "true") return;
    const focusable = [...inspector.querySelectorAll("button, a[href], summary, [tabindex]:not([tabindex='-1'])")].filter(item => !item.disabled);
    if (!focusable.length) return; const first = focusable[0]; const last = focusable.at(-1);
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  });
  narrowInspector.addEventListener("change", normalizeInspectorBreakpoint);
  rawDialog?.querySelector("[data-raw-content-close]")?.addEventListener("click", closeRawDialog);
  rawDialog?.addEventListener("cancel", event => { event.preventDefault(); closeRawDialog(); });
  rawDialog?.addEventListener("keydown", event => {
    if (event.key !== "Tab") return; const focusable = [...rawDialog.querySelectorAll("button, [tabindex]:not([tabindex='-1'])")]; if (!focusable.length) return;
    const first = focusable[0]; const last = focusable.at(-1); if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); } else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  });
  const sessionAiDialog = document.querySelector("[data-session-ai-dialog]");
  function closeSessionAi(restoreFocus = true) { if (sessionAiDialog.open) sessionAiDialog.close(); activeSessionRun = null; sessionPollGeneration++; document.querySelector("[data-session-ai-cancel]").hidden = true; if (restoreFocus) sessionAiInvoker?.focus(); }
  document.querySelector("[data-session-ai-open]")?.addEventListener("click", event => openSessionAi(event.currentTarget));
  document.querySelector("[data-session-ai-close]")?.addEventListener("click", () => closeSessionAi());
  document.querySelector("[data-session-ai-regenerate]")?.addEventListener("click", startSessionAi);
  document.querySelector("[data-session-ai-cancel]")?.addEventListener("click", async () => { if (activeSessionRun) await aiPost(`/api/local-monitor/v1/ai/runs/${activeSessionRun}/cancel`, {}); });
  document.querySelector("[data-session-ai-more]")?.addEventListener("click", () => readSessionReports(sessionReportCursor, false));
  sessionAiDialog?.addEventListener("cancel", event => { event.preventDefault(); closeSessionAi(); });
  window.addEventListener("pagehide", () => { nodePollGeneration++; sessionPollGeneration++; nodeTranscript = []; nodeAiContext = null; activeSessionRun = null; });
  load().then(checkAiReadiness);
})();
