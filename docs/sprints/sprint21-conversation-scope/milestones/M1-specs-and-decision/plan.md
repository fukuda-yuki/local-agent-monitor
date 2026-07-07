# Sprint21 M1: Specs And Decision Record

M1 defines the bounded conversation-scope contract in source-of-truth docs
before code changes.

## Target Files

- `docs/decisions.md`
- `docs/specifications/interfaces/instruction-diagnosis-analysis.md`
- `docs/specifications/layers/telemetry-ingestion.md`
- `docs/requirements.md` (review; modify only if needed)
- `docs/spec.md` (review; modify only if needed)
- `docs/sprints/sprint21-conversation-scope/README.md` (status update only)

## Requirements To Pin

- The selected trace is the anchor.
- The bounded window is up to two previous and two following traces in the same
  `conversation_id`, plus the analyzed trace itself.
- The emitted context contains at most five trace entries.
- Ordering follows `ListConversationTraces(conversationId)`: earliest span
  start time, tie-break trace id.
- Sibling entries are summaries, not transcripts.
- Sibling raw-derived instruction descriptors are local raw-analysis data only.
- Prompt v4 must label evidence as analyzed-trace or sibling-trace evidence.
- No out-of-window trace may be cited.

## Tasks

- [ ] Append D048 to `docs/decisions.md`.
  - Status: Accepted.
  - Motivation: Sprint20 exposed sibling ids but not sibling evidence, leaving
    instruction diagnosis effectively trace-scoped.
  - Contract: bounded window, maximum five emitted traces, additive internal
    behavior.
  - Safety: no public API, no projection migration, no Canvas focus change, no
    repository-safe raw content.
  - Validation: M5 gate adds bounded-window compliance.

- [ ] Update `docs/specifications/interfaces/instruction-diagnosis-analysis.md`.
  - Add a `conversation_context` contract under the extractor output section.
  - Define fields:

```text
conversation_context:
  conversation_id
  trace_count
  analyzed_trace_index
  window_start_index
  window_end_index
  truncated_before
  truncated_after
  traces[]

conversation_context.traces[]:
  trace_id
  relative_position
  is_analyzed_trace
  first_start_time
  user_instruction_descriptor
  turn_count
  input_tokens
  output_tokens
  total_tokens
  error_span_count
  retry_chain_count
  error_span_ids[]       # capped, span ids only
  retry_tool_names[]     # capped, names only
```

  - Keep existing `conversation` metadata or explicitly supersede it only if
    the spec says so. Prefer additive: keep `conversation`, add
    `conversation_context`.
  - Define the descriptor cap. Reuse Sprint20's first-line, 160-character rule
    unless the spec update explicitly changes it.

- [ ] Update prompt v4 rules in the same interface spec.
  - The model must call `get_instruction_evidence` first.
  - Sibling evidence must cite `trace_id` and a trace-relative descriptor
    (previous/current/next).
  - Findings that rely on sibling evidence must explain the relationship to
    the analyzed trace.
  - Evidence outside `conversation_context.traces[]` is forbidden.
  - If only sibling evidence exists and the analyzed trace has no relevant
    support, the model must mark the category insufficient instead of inventing
    a finding.

- [ ] Update `docs/specifications/layers/telemetry-ingestion.md`.
  - Keep the process-internal tool list stable unless D048 chooses a new tool.
  - If `get_instruction_evidence` is extended, note that it returns bounded
    conversation context for `instruction-diagnosis`.

- [ ] Review `docs/requirements.md` and `docs/spec.md`.
  - Update only shipped-behavior wording that says evidence is trace-internal
    without allowing bounded same-conversation context.
  - Preserve non-goals: no GitHub issue/commit/test correlation, no public
    export, no Canvas focus change.

- [ ] Update the Sprint21 README milestone table from Planned to M1 Done after
  review.

## Verification

Documentation-only change:

```powershell
rg -n "D048|conversation_context|prompt template v4|bounded conversation" docs
```

Expected:
- D048 exists.
- The interface spec contains the output contract.
- No sprint-local document is the only place where the product contract lives.

## Commit

```powershell
git add docs/decisions.md docs/specifications/interfaces/instruction-diagnosis-analysis.md docs/specifications/layers/telemetry-ingestion.md docs/requirements.md docs/spec.md docs/sprints/sprint21-conversation-scope
git commit -m "Instruction Diagnosis Conversation Scope: docs: define bounded conversation contract (Sprint21 M1)"
```
