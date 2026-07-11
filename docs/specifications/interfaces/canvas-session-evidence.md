# Canvas Session Evidence Interface

## Scope

This specification defines Issue #53: the Evidence tab in the Canvas Session
Workspace. It composes the Issue #51 sanitized Session detail with the Issue
#49 sanitized trace Agent execution graph and paged spans API. It adds no Local
Monitor endpoint, schema, projection, ownership model, raw proxy, dependency,
or Canvas action.

The approved Issue #52 two-column Evidence prototype is the visual contract.
Review remains unchanged; Improve and Compare remain placeholders; `/analysis`
and the Issue #45 `session.send()` boundary remain unchanged.

## Exact Trace Composition

For the selected Session detail, the Canvas helper reads `runs[]` in stored
order, selects each non-null `trace_id`, and removes later duplicates while
preserving the first occurrence. These are the only linked traces. Canvas must
not select a latest trace or correlate by repository, workspace, timestamp,
conversation, run proximity, names, or payload content.

For every selected trace, the token-gated extension server fetches the graph
and every spans page (`limit=200`) in order until `next_cursor` is JSON `null`.
Numeric Monitor cursors and nonblank string cursors are canonicalized to a
trimmed query value. Repeated or non-progressing cursors fail honestly instead
of looping. A separate forest/section
is retained per trace; roots are never merged between or within traces.

The extension server exposes only these sanitized, token-gated proxy routes:

```text
GET /api/session-evidence/traces/{traceId}/agent-graph
GET /api/session-evidence/traces/{traceId}/spans?limit=200[&after=<cursor>]
```

Trace IDs use the existing 1..128 allowlist. `limit` must be exactly `200`.
`after`, when present, is a trimmed nonblank string of at most 512 characters.
Invalid input returns a fixed `400` error. Only upstream `400`, `404`, and `503`
statuses with sanitized JSON failure bodies are preserved. Empty, non-JSON, or
invalid successful responses and unexpected statuses return fixed `502`
`monitor_unavailable`; no alternate source or payload is logged. Graph and
spans are fetched independently, so one source's error does not erase valid
evidence from the other.

## Ownership And Agent Forest

The Issue #49 graph is the sole source for Agent hierarchy, caller, ownership,
parallel grouping, presence, relationship source, and confidence. Canvas
performs no parent, ancestry, timing, containment, or naming inference. It
attaches a non-Agent span only through `span_ownership[]`.

- `parent_span` / `exact` renders exact.
- `time_inferred` / `inferred` renders `推定`.
- `unresolved` / `unknown`, missing ownership, or an absent named owner renders
  `判定不能` outside Agent ownership.
- `none_detected` and `undeterminable` remain distinct.
- Multiple roots stay separate; `parallel_groups[]` is displayed verbatim.
- Tree edges use only `caller_agent_span_id`; API array order and
  `agent_depth` never establish parentage. Missing or unresolved callers remain
  explicit orphans rather than fabricated children.

## Linked Timeline

The timeline combines sanitized OTel spans for each exact-linked trace and the
selected Session's sanitized `events[]`. It sorts by timestamp ascending, then
stable source order. Missing/invalid times sort after valid times while
retaining source order. Session events are always `Session / unowned`; Canvas
must not infer ownership through `event.run_id`, a run's `trace_id`, time, or
neighboring spans.

With no exact-linked trace, the Session event timeline remains usable and the
graph renders an honest unavailable state. A graph/spans error is shown for
that trace; no fallback trace or source is selected.

## Inspector And Review Links

The inspector uses only sanitized Agent fields from the graph, sanitized
Tool/LLM span fields, and Session event metadata (`event_id`, `run_id`,
`source_surface`, `type`, `occurred_at`, `parent_event_id`, `status`, and
`content_state`). Missing sanitized Skill name/path/version or typed test/review
result renders `利用不可`; it is not derived from names, tool output, or content.

The Review terminal gate links only to a matching canonical terminal Session
event (`session.shutdown`, `session.task_complete`, `SessionEnd`, or `Stop`).
The error-event gate links to every exact `status == error` event counted by
that gate. Otherwise no link is rendered and evidence is honestly unavailable.
Issue #53 adds no persistent human-verdict reference.

## Safety, Tests, And Non-Goals

Evidence remains available with `--sanitized-only`. It adds no raw/event-content
proxy and reconstructs no raw body. Existing token, loopback, Host, same-origin,
action DTO, and no-CORS boundaries remain unchanged. No payload content is
logged or copied to actions, `session.send()` prompts, or repository artifacts.

Pure Node and Canvas contract tests cover trace dedupe/order, all-page cursor
pagination, exact joins/no inference, multi-trace forests, deterministic
timeline order, relationship/presence distinctions, inspector unavailable
states, gate links, no trace, graph errors, route/token validation, upstream
`400`/`404`/`503`, and sanitized-only/raw-boundary regressions.

Non-goals: Session-to-Agent ownership; test/review verdict or Skill identity
inference; raw preview; Local Monitor changes; new actions; Improve/Compare;
workflow optimization.
