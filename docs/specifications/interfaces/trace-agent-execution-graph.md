# Trace Agent Execution Graph Interface

## Scope

This specification pins the Issue #49 agent execution graph: a derived,
sanitized ownership model over the existing span projection, one sanitized
read API, and the trace-detail UI obligations (header summary, flow,
waterfall, inspector). It is the single Agent ownership model; Issue #53
Canvas Evidence integration consumes this same API and adds no separate
interpretation.

The Canvas consumer contract is `canvas-session-evidence.md`. It consumes the
graph verbatim and does not re-run or supplement ancestry/time inference.

No new telemetry input, span schema change, or projection migration is
introduced. The graph is derived on read from already-projected spans.

## Graph Derivation

Agent nodes are spans with `category == agent_invocation` or
`operation == invoke_agent`. Every span in the trace derives exactly these
fields:

| Field | Contract |
| --- | --- |
| `owning_agent_span_id` | Nullable span id of the nearest ancestor Agent node (an Agent node owns itself is false: its owner is its nearest ancestor Agent or null). |
| `parent_agent_span_id` | Nullable span id of the owning Agent's own owning Agent. |
| `agent_depth` | 0 for root Agents, +1 per nested Agent; non-Agent spans inherit their owner's depth. |
| `agent_role` | `main`, `sub`, `root`, or `unknown`. |
| `relationship_source` | `parent_span`, `time_inferred`, or `unresolved`. |
| `relationship_confidence` | `exact`, `inferred`, or `unknown`. |

Rules (deterministic, in order):

1. Ownership follows `parent_span_id` ancestry; the first Agent node found
   toward the root is the owner (`relationship_source: parent_span`,
   confidence `exact`).
2. Only when the parent chain is broken (missing parent span) may time-range
   containment within an Agent's start/end assign ownership
   (`time_inferred`, confidence `inferred`). Ambiguous containment (zero or
   multiple candidates) resolves to no owner (`unresolved`, `unknown`).
3. A single root Agent is `main`; every Agent with an owning Agent is `sub`;
   when multiple root Agents exist they are all `root` and no `main` is
   fabricated; spans whose ownership cannot be determined are `unknown`.
4. Cycles, duplicate span ids, and unknown parents must not break the API or
   UI: affected spans resolve to `unresolved` / `unknown` and the response
   carries `graph_warnings` (bounded string codes, no span payload).
5. "No Agent spans present" and "cannot determine Agent usage" are distinct
   outcomes (`agent_presence`: `detected` / `none_detected` /
   `undeterminable`).

## Sanitized Read API

```text
GET /api/monitor/traces/{traceId}/agent-graph
```

Loopback + Host validation as existing `/api/monitor/*` routes. The response
is sanitized (names, ids, numbers, statuses only — no prompt bodies, no
attribute payloads):

- `summary`: `main_agent_name` (nullable), `root_agent_count`,
  `subagent_invocation_count` (derived from the graph, not
  `AgentInvocationCount`), `unique_subagent_count`, `max_agent_depth`,
  `parallel_agent_group_count`, `relationship_quality`
  (`exact` / `partially_inferred` / `undeterminable`), `agent_presence`.
- `agents[]`: per Agent node — `span_id`, `agent_name`, `agent_role`,
  `caller_agent_span_id`, `model` (nullable), `started_at`, `ended_at`,
  `duration_ms`, `input_tokens`, `output_tokens`, `total_tokens`, `status`,
  `child_agent_count`, `agent_depth`, `relationship_source`,
  `relationship_confidence`.
- `span_ownership[]`: per non-Agent span — `span_id`,
  `owning_agent_span_id`, `relationship_source`, `relationship_confidence`.
- `parallel_groups[]`: sets of Agent `span_id`s whose execution time ranges
  overlap under the same owner.
- `graph_warnings[]`: bounded codes (`cycle_detected`, `duplicate_span_id`,
  `unknown_parent`, `time_range_inconsistent`).

Unknown trace: `404` with the existing `/api/monitor/*` failure shape
`{ "accepted": false, "error": "trace_not_found", "message": "..." }`; the
`message` is fixed text and never echoes request content.

## Trace Detail UI

- **Header agent summary**: shows one of `Sub-agent N回検出` /
  `Sub-agentは検出されませんでした` / `Sub-agent利用を判定できません`, plus
  main/root Agent naming, subagent invocation and unique counts, max depth,
  parallel group count, and relationship quality. Absence of OTel evidence
  is never rendered as "not used".
- **Flow view**: Agents render as independent execution containers
  (start/complete markers, owned turns/tools nested, collapsible); Agent
  cards show role, name, caller, model, start/end/duration, tokens,
  status/error, child Agent count. Parallel Sub-agents render as an Agent
  parallel group distinct from tool parallelism. The legend adds `agent`.
  The current single-turn-sequence model is replaced by per-Agent
  turn/tool ownership from `span_ownership`.
- **Waterfall view**: Agent duration bars with indented owned spans,
  collapsible per Agent, parallel overlap visible, child spans outside the
  parent Agent's time range flagged as telemetry inconsistency, and no flat
  all-agents-first block.
- **Agent span inspector**: selecting an Agent span shows Agent-specific
  sanitized detail (the `agents[]` fields plus owned turn/tool counts). On
  the raw-default monitor, Sub-agent instruction/response render
  best-effort through the existing raw-detail span route and boundary
  (`--sanitized-only` hides them; the sanitized graph stays visible).
- Inferred relationships are always visually marked 「推定」; unresolved as
  「判定不能」. Exact and inferred are never conflated.
- Existing URL span selection, error-only filter, tool parallel groups, and
  the cache panel must not regress.

## Tests And Evidence

Fixtures and tests cover at minimum the Issue #49 matrix: main-agent-only;
one Sub-agent; nested Sub-agent; serial Sub-agents; parallel Sub-agents;
correct LLM/tool attribution under Sub-agents; missing-parent time
inference; unknown parent; cycle/duplicate span ids; multiple root Agents;
no Agent spans; sanitized-only hierarchy visibility without raw content;
raw-default best-effort instruction/response; and non-regression of span
selection, error filter, tool parallel groups, and cache panel. E2E evidence
captures header summary, nested flow, parallel waterfall, Agent inspector,
and inferred/undeterminable rendering.

## Non-Goals

No AI judgement of Sub-agent usage quality, no automatic workflow
optimization, no new input sources (VS Code internal logs,
`workspaceStorage`, `chatSessions`), and no assertive completion of facts
OTel cannot observe.
