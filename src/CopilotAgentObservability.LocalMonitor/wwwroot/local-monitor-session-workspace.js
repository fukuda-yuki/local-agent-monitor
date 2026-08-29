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
  let rawDialogTrigger = null;
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
        || detail.node.node_id !== nodeId || expectedExecutionId && detail.execution.execution_id !== expectedExecutionId
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

  function tokenMetric(title, value) {
    const card = el("div", "local-monitor-session-summary-card");
    card.append(el("h2", null, title));
    if (value.state === "recorded") card.append(el("strong", null, format(value.value)));
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

  const CONTENT_LABELS = { instruction: "Instruction", tool_input: "Tool input", tool_result: "Tool result", error_message: "Error message", subagent_input: "Sub-agent input", event_content: "Event content" };
  const CONTENT_STATE_LABELS = { not_captured: "Raw content was not captured", expired: "Raw content has expired", deleted: "Raw content was deleted", read_denied: "Raw content read was denied", oversized: "Raw content is too large", invalid: "Raw content is invalid" };
  const HTTP_CONTENT_LABELS = { 403: "Raw content read was denied", 404: "Raw content was not captured", 410: "Raw content is no longer retained", 413: "Raw content is too large", 409: "Raw content changed while it was being read", 503: "Raw content is temporarily unavailable" };

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
    showRawDialog(trigger, `${CONTENT_LABELS[part]} raw content`);
    try {
      const response = await fetch(requestUrl(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/nodes/${nodeId}/content`, { workspace_revision: state.revision, part }), { headers: { Accept: "application/json" } });
      if (!response.ok) { publishRawText("", HTTP_CONTENT_LABELS[response.status] ?? "Raw content could not be read"); return; }
      const document = validateContent(JSON.parse(await response.text()), nodeId, part);
      publishRawText(document.text, `${format(document.utf8_byte_length)} bytes · ${format(document.unicode_scalar_length)} scalars`);
    } catch { publishRawText("", "Raw content could not be read"); }
  }

  async function readSkillContent(trigger, snapshotId, current) {
    const title = current ? "現在のファイル" : "履歴スナップショット";
    showRawDialog(trigger, title);
    const path = `/api/local-monitor/v1/sessions/${root.dataset.sessionId}/skill-invocations/${snapshotId}/${current ? "current-file-read" : "content"}`;
    try {
      const response = await fetch(path, current ? { method: "POST", headers: { Accept: "application/json", "Content-Type": "application/json", "x-monitor-csrf": "local-monitor" }, body: '{"schema_version":"local-skill-current-file-read.request.v1"}' } : { headers: { Accept: "application/json" } });
      if (!response.ok) { publishRawText("", HTTP_CONTENT_LABELS[response.status] ?? "Skill content could not be read"); return; }
      const document = JSON.parse(await response.text());
      const validHistorical = !current && exact(document, ["schema_version", "snapshot_id", "content_kind", "body", "definition_path", "body_sha256", "definition_path_sha256", "captured_at"]) && document.schema_version === "local-skill-invocation-snapshot.content.v1" && document.content_kind === "historical_snapshot";
      const validCurrent = current && exact(document, ["schema_version", "snapshot_id", "content_kind", "comparison", "historical_body_sha256", "current_body_sha256", "current_body_utf8_bytes", "body", "read_at"]) && document.schema_version === "local-skill-current-file-read.response.v1" && document.content_kind === "current_file" && ["same", "changed"].includes(document.comparison);
      if ((!validHistorical && !validCurrent) || document.snapshot_id !== snapshotId || typeof document.body !== "string") throw new TypeError("invalid Skill document");
      publishRawText(document.body, current ? `${title} · ${document.comparison}` : `${title} · ${document.captured_at}`);
    } catch { publishRawText("", "Skill content could not be read"); }
  }

  async function requestJson(urlFactory, attempted = false) {
    const response = await fetch(urlFactory(), { headers: { Accept: "application/json" } });
    if (response.status === 409 && !attempted) {
      const error = await response.json().catch(() => null);
      if (error?.error === "workspace_snapshot_stale") {
        const previous = state.revision;
        await refreshSummary();
        if (state.revision === previous) throw new Error("Session revision did not advance");
        return requestJson(urlFactory, true);
      }
    }
    if (!response.ok) throw new Error("Session detail unavailable");
    return JSON.parse(await response.text());
  }

  async function refreshSummary() {
    const response = await fetch(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/summary`, { headers: { Accept: "application/json" } });
    if (!response.ok) throw new Error("Session summary unavailable");
    const summary = validate(JSON.parse(await response.text()));
    state.executionState.clear(); state.summary = summary; state.revision = summary.workspace_revision;
    render(summary, false);
  }

  function executionMemory(executionId) {
    if (!state.executionState.has(executionId)) state.executionState.set(executionId, {
      open: false, pages: new Map(), expanded: new Set(), scrollTop: 0,
    });
    return state.executionState.get(executionId);
  }

  async function loadTimeline(executionId, parentNodeId = null, after = null, attempted = false) {
    const parameters = { workspace_revision: state.revision, execution_id: executionId };
    if (parentNodeId) parameters.parent_node_id = parentNodeId;
    if (after) parameters.after = after;
    parameters.limit = "100";
    const urlFactory = () => { parameters.workspace_revision = state.revision; return requestUrl(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/timeline`, parameters); };
    const page = validateTimeline(await requestJson(urlFactory, attempted), executionId, parentNodeId);
    const memory = executionMemory(executionId);
    memory.open = true;
    const key = parentNodeId ?? "root";
    const existing = after ? memory.pages.get(key)?.items ?? [] : [];
    memory.pages.set(key, { items: [...existing, ...page.items], nextCursor: page.next_cursor });
    renderExecutions();
  }

  function timingLabel(node) {
    if (node.timing?.state !== "recorded") return node.timing?.state === "invalid" ? "時刻が無効" : "時刻なし";
    return node.timing.duration_ms === null ? node.timing.started_at : `${format(node.timing.duration_ms)} ms`;
  }

  function renderNodes(execution, memory, parentNodeId, depth) {
    const fragment = document.createDocumentFragment();
    const page = memory.pages.get(parentNodeId ?? "root");
    if (!page) return fragment;
    const known = page.items.filter(item => ["exact", "explicit"].includes(item.relationship_authority));
    const unknown = page.items.filter(item => !["exact", "explicit"].includes(item.relationship_authority));
    const appendRow = (node, target = fragment) => {
      const wrapper = el("div", "local-monitor-session-timeline-entry");
      const row = el("button", "local-monitor-session-timeline-node");
      row.type = "button"; row.dataset.timelineNode = node.node_id; row.style.setProperty("--timeline-depth", depth);
      row.setAttribute("aria-expanded", node.child_count > 0 ? String(memory.expanded.has(node.node_id)) : "false");
      const label = node.name?.state === "recorded" ? node.name.text : node.kind;
      row.append(el("strong", null, label), el("span", null, `${node.kind} · ${node.status} · ${timingLabel(node)}`));
      if (node.timing.state === "recorded" && node.timing.started_at && node.timing.duration_ms !== null
          && execution.timing.state === "recorded" && execution.timing.started_at && execution.timing.duration_ms !== null) {
        const start = Date.parse(node.timing.started_at) - Date.parse(execution.timing.started_at);
        const duration = node.timing.duration_ms;
        const extent = Math.max(1, execution.timing.duration_ms, start + duration);
        const bar = el("span", "local-monitor-session-time-bar"); bar.dataset.timelineTimeBar = "";
        bar.style.marginLeft = `${Math.max(0, start) / extent * 100}%`; bar.style.width = `${Math.max(1, duration / extent * 100)}%`;
        wrapper.append(bar);
      }
      row.addEventListener("click", async () => {
        if (node.child_count > 0) {
          if (memory.expanded.has(node.node_id)) memory.expanded.delete(node.node_id);
          else { memory.expanded.add(node.node_id); if (!memory.pages.has(node.node_id)) await loadTimeline(execution.execution_id, node.node_id); }
        }
        await selectNode(execution.execution_id, node.node_id, true);
        renderExecutions();
      });
      wrapper.append(row);
      if (memory.expanded.has(node.node_id)) wrapper.append(renderNodes(execution, memory, node.node_id, depth + 1));
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

  function renderExecutions() {
    const executions = root.querySelector("[data-session-executions]");
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
      toggle.append(el("strong", null, `実行 ${execution.source ?? "不明"} · ${execution.status}`), el("span", null, `${format(execution.child_count)} activity · ${timingLabel(execution)}`));
      toggle.addEventListener("click", async () => {
        memory.open = !memory.open;
        if (memory.open && !memory.pages.has("root")) await loadTimeline(execution.execution_id);
        renderExecutions();
      });
      section.append(toggle);
      if (memory.open) {
        const scroll = el("div", "local-monitor-session-execution-scroll"); scroll.dataset.executionScroll = "";
        scroll.append(renderNodes(execution, memory, null, 0)); scroll.addEventListener("scroll", () => { memory.scrollTop = scroll.scrollTop; }); section.append(scroll);
        requestAnimationFrame(() => { scroll.scrollTop = memory.scrollTop; });
      }
      executions.append(section);
    }
  }

  function appendInspectorFact(section, label, fact, key = "value") {
    const row = el("p"); row.append(el("strong", null, `${label}: `));
    row.append(document.createTextNode(fact?.state === "recorded" ? String(fact[key]) : fact?.state ?? "not_observed")); section.append(row);
  }

  function appendContentAction(section, detail, part) {
    const content = detail.content[part]; const row = el("p"); row.append(el("strong", null, `${CONTENT_LABELS[part]}: `));
    if (content.available) { const button = el("button", null, `${CONTENT_LABELS[part]} を表示`); button.type = "button"; button.addEventListener("click", () => readGenericContent(button, detail.node.node_id, part)); row.append(button); }
    else row.append(document.createTextNode(CONTENT_STATE_LABELS[content.state] ?? content.state)); section.append(row);
  }

  function appendRelated(section, title, items) {
    if (!items.length) return; section.append(el("h3", null, title)); const list = el("ul");
    for (const item of items) { const button = el("button", null, item.name.state === "recorded" ? item.name.text : item.kind); button.type = "button"; button.addEventListener("click", () => selectNode(item.execution_id, item.node_id, true)); const li = el("li"); li.append(button); list.append(li); }
    section.append(list);
  }

  function renderInspector(detail) {
    const node = detail.node; const metadata = node.metadata; const inspector = root.querySelector("[data-session-overview]"); inspector.replaceChildren();
    const section = el("section", "local-monitor-contextual-inspector"); section.dataset.inspectorKind = node.kind;
    section.append(el("h2", null, node.name.state === "recorded" ? node.name.text : node.kind), el("p", null, `${node.kind} · ${node.status} · ${timingLabel(node)}`));
    if (node.kind === "tool") {
      appendInspectorFact(section, "MCP server", metadata.mcp_server_name); appendInspectorFact(section, "Tool", metadata.mcp_tool_name); appendInspectorFact(section, "Caller", metadata.caller, "node_id");
    } else if (node.kind === "skill") {
      section.append(el("p", null, `Current valid state: ${metadata.current_valid_state}`)); appendInspectorFact(section, "Source", metadata.source); appendInspectorFact(section, "Trigger", metadata.trigger); appendInspectorFact(section, "Inventory reference", metadata.inventory_reference);
      if (metadata.historical_snapshot_reference.state === "recorded") {
        const snapshotId = metadata.historical_snapshot_reference.value; const historical = el("button", null, "履歴スナップショットを表示"); historical.type = "button"; historical.addEventListener("click", () => readSkillContent(historical, snapshotId, false));
        const current = el("button", null, "現在のファイルを読み取る"); current.type = "button"; current.addEventListener("click", () => readSkillContent(current, snapshotId, true)); section.append(historical, current);
      } else section.append(el("p", null, "履歴スナップショットはありません"));
    } else if (node.kind === "subagent") {
      for (const key of ["selected", "started", "completed", "failed", "deselected"]) appendInspectorFact(section, key, metadata.lifecycle[key]);
      appendInspectorFact(section, "Children", metadata.children, "count");
    } else if (node.kind === "error") {
      appendInspectorFact(section, "Error code", metadata.error_code); appendInspectorFact(section, "Status", metadata.status);
    } else if (node.kind === "permission") {
      appendInspectorFact(section, "Decision", metadata.decision); appendInspectorFact(section, "Wait", metadata.wait);
    } else if (node.kind === "event") {
      appendInspectorFact(section, "Event", metadata.event_name); appendInspectorFact(section, "Source time", metadata.source_time);
    } else if (node.kind === "retry") {
      appendInspectorFact(section, "Attempt", metadata.attempt); appendInspectorFact(section, "Target", metadata.target, "node_id"); appendInspectorFact(section, "Recovered", metadata.recovered);
    }
    if (node.kind !== "skill") for (const part of CONTENT_PARTS) appendContentAction(section, detail, part);
    if (detail.parent_path.length) { section.append(el("h3", null, "Parent path")); const path = el("ol"); for (const item of detail.parent_path) path.append(el("li", null, item.name.state === "recorded" ? item.name.text : item.kind)); section.append(path); }
    appendRelated(section, "Retry", detail.related.retry); appendRelated(section, "Recovery", detail.related.recovery); appendRelated(section, "Children", detail.related.children);
    const refs = node.technical_references; const technical = el("details"); technical.append(el("summary", null, "Technical references")); for (const key of ["source_kind", "source_identity", "trace_id", "span_id", "event_id"]) if (refs[key] !== null) technical.append(el("p", null, `${key}: ${refs[key]}`)); section.append(technical);
    inspector.append(section);
  }

  async function selectNode(executionId, nodeId, push, attempted = false) {
    const urlFactory = () => requestUrl(`/api/local-monitor/v1/sessions/${root.dataset.sessionId}/nodes/${nodeId}`, { workspace_revision: state.revision });
    const rawDetail = await requestJson(urlFactory, attempted);
    const detail = validateNode(rawDetail, nodeId, executionId);
    executionId = detail.execution.execution_id;
    state.selectedExecutionId = executionId; state.selectedNodeId = nodeId;
    const memory = executionMemory(executionId); memory.open = true;
    const path = (detail.parent_path ?? []).filter(parent => ["exact", "explicit"].includes(parent.relationship_authority));
    for (const parent of path) memory.expanded.add(parent.node_id);
    if (!memory.pages.has("root")) await loadTimeline(executionId);
    for (const parent of path.slice(1)) if (!memory.pages.has(parent.node_id)) await loadTimeline(executionId, parent.node_id);
    renderInspector(detail);
    if (push) { state.ignoreRouteEvent = true; window.LocalMonitorV1History.push({ execution: executionId, node: nodeId }); }
    else if (!window.LocalMonitorV1History.current().execution) window.LocalMonitorV1History.replace({ execution: executionId, node: nodeId });
    renderExecutions();
  }

  function fallbackSelection(replace) {
    state.selectedExecutionId = null; state.selectedNodeId = null;
    renderOverview(state.summary);
    if (replace) window.LocalMonitorV1History.replace({ execution: null, node: null });
  }

  async function applyRoute(route) {
    if (!state.summary) return;
    if (route.node) {
      try { await selectNode(route.execution ?? null, route.node, false); }
      catch { fallbackSelection(true); }
    } else fallbackSelection(false);
  }

  function renderOverview(summary) {
    const session = summary.session;
    const overview = root.querySelector("[data-session-overview]"); overview.replaceChildren(el("h2", null, "Session overview"));
    overview.append(el("h3", null, "最初の指示"), el("p", null, session.instruction.label || "記録されていません"));
    overview.append(el("p", null, session.instruction.additional_count === null
      ? "追加の指示 今回の記録にはありません" : `追加の指示 ${format(session.instruction.additional_count)}件`));
    overview.append(el("p", null, `状態 ${session.status} · 実行 ${format(summary.executions.length)}件`));
    const coverage = el("ul"); for (const item of session.capture.coverage) coverage.append(el("li", null, `${item.signal_family}: ${item.state}`));
    overview.append(el("h3", null, "取得範囲"), coverage);
    const technical = el("details"); technical.append(el("summary", null, "技術情報"), el("p", null, `revision ${summary.workspace_revision}`)); overview.append(technical);
  }

  function render(summary, openLatest = true) {
    const session = summary.session;
    root.querySelector("[data-session-breadcrumb]").textContent = session.instruction.label || "Session";
    const context = root.querySelector("[data-session-context-content]");
    context.replaceChildren(el("strong", null, session.instruction.label || "Session"), el("span", null, ` ${session.status}`));
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
      renderBars(total, session.tokens.input, session.tokens.output, "local-monitor-token-bar", "入力トークン", "出力トークン");
    }
    const cache = tokenMetric("入力トークンの内訳", session.tokens.cache_read_ratio_basis_points);
    if (session.tokens.input.state === "recorded" && session.tokens.cache_read.state === "recorded"
        && session.tokens.new_input.state === "recorded" && session.tokens.cache_read_ratio_basis_points.state === "recorded"
        && session.tokens.input.value === session.tokens.cache_read.value + session.tokens.new_input.value
        && session.tokens.cache_read_ratio_basis_points.value === Math.round(session.tokens.cache_read.value * 10000 / session.tokens.input.value)) {
      renderBars(cache, session.tokens.cache_read, session.tokens.new_input, "local-monitor-cache-bar", "cache read", "new input");
    }
    if (session.tokens.cache_creation.state === "recorded") cache.append(el("small", null, `cache write ${format(session.tokens.cache_creation.value)}`));
    const input = namedFact("入力", session.tokens.input, "Input");
    const output = namedFact("出力", session.tokens.output, "Output");
    const cacheRead = namedFact("cache read", session.tokens.cache_read, "CacheRead");
    const newInput = namedFact("new input", session.tokens.new_input, "NewInput");
    const coverageCard = el("div", "local-monitor-session-summary-card"); coverageCard.dataset.sessionFixedCoverage = "";
    coverageCard.append(el("h2", null, "取得範囲"));
    const coverageList = el("ul", "local-monitor-session-coverage");
    for (const item of session.capture.coverage) {
      const row = el("li"); row.append(el("span", null, `${item.signal_family}: `));
      const factTarget = row.appendChild(el("span"));
      if (item.state === "recorded") factTarget.textContent = "記録済み";
      else if (item.state === "complete_zero") factTarget.textContent = "0件（完全）";
      else renderFact(factTarget, { state: item.state, count: null });
      coverageList.append(row);
    }
    coverageCard.append(coverageList);
    summaryRoot.append(total, cache, input, output, cacheRead, newInput, coverageCard);
    for (const [key, label] of [["skill", "Skill"], ["tool", "Tool"], ["subagent", "Sub-agent"], ["error", "Error"], ["retry", "Retry"]]) {
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
      if (!response.ok) throw new Error("Session summary unavailable");
      const summary = validate(JSON.parse(await response.text()));
      if (refresh && state.revision !== summary.workspace_revision) state.executionState.clear();
      state.summary = summary; state.revision = summary.workspace_revision; render(summary);
      const route = window.LocalMonitorV1History.current();
      if (applyCurrentRoute && route.node) await applyRoute(route);
      else if (applyCurrentRoute) {
        const latest = summary.executions.find(execution => execution.latest);
        if (latest && !executionMemory(latest.execution_id).pages.has("root")) await loadTimeline(latest.execution_id);
      }
    } catch {
      root.querySelector("[data-session-context-content]").textContent = "Session を読み込めませんでした";
    }
  }

  document.addEventListener("cao-route-state", event => {
    if (state.ignoreRouteEvent) { state.ignoreRouteEvent = false; return; }
    applyRoute(event.detail);
  });
  rawDialog?.querySelector("[data-raw-content-close]")?.addEventListener("click", closeRawDialog);
  rawDialog?.addEventListener("cancel", event => { event.preventDefault(); closeRawDialog(); });
  rawDialog?.addEventListener("keydown", event => {
    if (event.key !== "Tab") return; const focusable = [...rawDialog.querySelectorAll("button, [tabindex]:not([tabindex='-1'])")]; if (!focusable.length) return;
    const first = focusable[0]; const last = focusable.at(-1); if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); } else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  });
  load();
})();
