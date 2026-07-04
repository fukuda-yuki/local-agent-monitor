# Sprint9 M2 — Span Projection (sanitized) (Plan)

Status: **Planned** — to be challenge-reviewed via the Codex companion `review`
path before implementation (per `CLAUDE.md`).
Author role: Claude (orchestrator) per `CLAUDE.md`.

Sprint-local planning evidence, not product behavior. Source-of-truth order:
`docs/requirements.md` → `docs/spec.md` → `docs/specifications/`. Sprint context:
[../../README.md](../../README.md) (*Data classification*, *Per-field
sanitization policy*, *Token rollup rule*, *Dependencies & risks*).

## Objective

Build `MonitorSpanProjection` + its builder: turn each raw span into a per-span
**sanitized** projection row, reusing the existing classification and token
parsing in `RawMeasurementNormalizer`, with the per-field sanitization policy and
the no-double-count token rule baked in. Pure projection logic + tests;
persistence is M3.

## Scope

In scope:
1. `MonitorSpanProjection` record + builder: per-span operation
   (`invoke_agent` / `chat` / `execute_tool` / `execute_hook`), logical category,
   tool name, tool type, MCP tool name, MCP server hash, sub-agent name,
   request/response model, input/output/total/reasoning/cache tokens, status,
   `error.type`, finish reasons, duration, `trace_id` / `span_id` /
   `parent_span_id` / `conversation_id`.
2. **Per-field sanitization policy**: free-form name fields (`tool_name`,
   `mcp_tool_name`, `agent_name`, span `name`) pass the existing
   `MeasurementSanitizer` unsafe-value guard + max-length truncation; a failing
   value is dropped (row keeps other columns). `error.type` = class token only;
   `finish_reasons` = enum-like fixed set; `mcp_server_hash` = client hash only.
3. **No-double-count token rollup**: per-turn = the `chat` span's own
   `gen_ai.usage.*`; per-trace = `invoke_agent` usage when present, else the sum
   of `chat` spans; never add `invoke_agent` totals to `chat` per-call; sub-agent
   usage attributed to the sub-agent. Fix the inherited over-count in
   `RawMeasurementNormalizer`.
4. **`parent_span_id`-absent fallback**: flat grouping + unknown-hierarchy
   display, no failure; unrecognized spans routed to the existing unknown-span
   evidence path.
5. Tests: synthetic OTLP fixtures (sub-agent child `invoke_agent` under
   `execute_tool`, MCP tool, `error.type`, multi-turn chat tokens, multi-trace
   fan-out) **plus per-attribute negative tests** (email/path/secret injected
   into name fields are guarded out).

Out of scope (deferred):
- Persistence / schema / migration / backfill (M3).
- Read API (M4), UI (M5), security matrix + live validation (M6).

## Tasks
- [ ] Add `MonitorSpanProjection` + builder, reusing classification/token parsing.
- [ ] Apply the per-field sanitization policy to each free-form field.
- [ ] Implement the no-double-count token rollup; fix the normalizer over-count.
- [ ] Add the `parent_span_id`-absent flat-grouping fallback.
- [ ] Add synthetic fixtures + per-attribute negative tests.

## Acceptance criteria
- Per-attribute negative tests pass: email/path/secret in `tool_name` /
  `mcp_tool_name` / `agent_name` / `error.type` are guarded out of the projection.
- Multi-turn + sub-agent fixture asserts trace/turn totals match the agent-level
  numbers (no double count).
- `parent_span_id`-absent fixture yields flat grouping, not an error.
- All existing tests stay green.

## Validation
```powershell
dotnet build CopilotAgentObservability.slnx
dotnet test CopilotAgentObservability.slnx
```
Pass criteria: 0 build errors; existing tests stay green; new fixture + negative
tests present and passing.

## Dependencies
- Depends on **M1** (schema fields + sanitization policy pinned).
- Sub-agent child-span hierarchy is documented but only *confirmed* by a live run
  (human-gated, README risk C3/F4). M2 builds against synthetic fixtures shaped
  to the documented contract; the `parent_span_id`-absent fallback covers the
  unconfirmed shape.

## Review
- Challenge-reviewed via the Codex companion `review` / adversarial path
  (read-only) before implementation, per `CLAUDE.md`. Record the outcome here (or
  in a sibling `review.md`).
