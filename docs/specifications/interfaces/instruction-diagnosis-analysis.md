# Instruction Diagnosis Analysis Interface

This document defines the `instruction-diagnosis` focus of the Local Monitor
Copilot raw analysis (decision D046, Issue #46 Phase 1). The analysis routes,
runner, and run lifecycle are defined in
[telemetry ingestion](../layers/telemetry-ingestion.md). The focus diagnoses
the implementation instructions the user gave the agent, using the analyzed
trace as the anchor plus bounded same-conversation sibling evidence when
available, and reports findings in a fixed per-finding format.

## Scope And Non-Goals

- Diagnosis display only: the result appears as a normal raw analysis result
  in the Local Monitor analysis drawer.
- Evidence is the analyzed trace plus the bounded `conversation_context`
  window from the same `conversation_id`. No GitHub issue / commit /
  test-evidence correlation (traces stay manually selected, D037).
- No memory candidate generation, no adoption workflow, and no new
  repository-safe export.
- Prompt-only in v1; deterministic pre-extraction since prompt template v3
  (D047, Issue #46 Phase 2 step 1): a code extractor runs at analysis start
  and its output is exposed through the process-internal tool
  `get_instruction_evidence`. Prompt template v4 (D048, Issue #46 Phase 2
  step 2) extends that output with bounded conversation context and requires
  per-category citations of extractor output or explicitly raw-verified
  evidence inside the emitted bounded window (see Evidence Extractor Output
  Contract and Per-Category Required Evidence below). The raw trace is still
  fed through the existing analysis runner, and the existing six raw tools
  stay available unchanged as the verification path.
- The focus value rides the existing `POST /traces/{traceId}/analysis`
  payload. No new routes, no schema change, and no new API fields.

## Focus Value

- Wire value: `instruction-diagnosis`.
- Japanese drawer label: 指示診断.
- Exposed in the Local Monitor analysis drawer only. The Canvas helper focus
  set (`latency` / `tokens` / `cache` / `errors`, D036) is not extended.
- The D045 history-resend follow-up chat works with this focus like any
  other focus.

## Taxonomy v1

Coupling rule: a category exists in this taxonomy only together with a
defined analyzed-trace or bounded same-conversation evidence pattern. Adding a
category requires adding its evidence pattern in the same change.

| Category id | Name (JA) | Definition | Required trace / bounded-conversation evidence pattern |
| --- | --- | --- | --- |
| `goal-clarity` | 目標の明確さ | The instruction does not state the intended outcome. | User follow-up turns that redirect or redefine the goal after work started; discarded or redone agent output (rework turns, tokens spent on abandoned work). |
| `ambiguity` | 曖昧さ | The instruction admits multiple readings. | A rephrased or clarified instruction in a later user turn; agent clarifying-question turns; divergent exploration before the user disambiguates. |
| `missing-acceptance-criteria` | 受け入れ基準の欠如 | The instruction has no verifiable done-condition. | User correction turns after the agent declares completion; extra user-initiated verification turns. |
| `task-size-split` | タスク粒度・分割 | The instruction bundles too much work for one run. | A long multi-goal trace with mid-trace error spans or retries; token totals concentrated in retried segments; follow-up turns re-scoping the work to a subset. |
| `missing-context-constraints` | 前提・制約の欠如 | The instruction omits environment facts or constraints the agent needed. | Failed or retried tool calls, or error spans, that resolve only after a user turn supplies the missing information. |

Disambiguation note: `missing-context-constraints` is distinguished from
`ambiguity` by evidence type — execution failure resolved by supplied
information versus instruction rephrasing.

Category ids are prompt-template and output vocabulary. They are not API
fields and not schema.

## Evidence Extractor Output Contract

A deterministic extractor (D047 / D048) computes the following structured
evidence from the projection rows and raw records already loaded for the
analysis run, plus sibling-trace metadata resolved through the read-only
projection-store query `ListConversationTraces(conversationId)` over the
existing `monitor_spans.conversation_id` column. The serialized output is
returned by the process-internal tool `get_instruction_evidence` (tool list:
[telemetry ingestion](../layers/telemetry-ingestion.md)).

Sources are allowlist projection columns only, with one exception:
`user_instruction` and `conversation_context.traces[].user_instruction_descriptor`
read the `gen_ai.prompt` attribute out of a chat span's raw record payload.
No field contains long raw bodies; the only raw-derived content is the capped
first-line instruction descriptor, which is reachable only through the raw
analysis surface and is removed with it under `--sanitized-only`.

- `error_spans[]`: spans with `status == "error"`, ordered by span ordinal.
  Entry: span id, tool name, error kind (`error_type`, `"unknown"` when
  null), and a short factual descriptor built only from allowlist columns
  (operation / tool / error kind — never payload text).
- `retry_chains[]`: per tool name, spans ordered by span ordinal; a chain
  starts at an error span of that tool and extends through subsequent spans
  of the same tool; emitted only when the chain length is >= 2; final
  outcome is `recovered` (last span ok) or `unrecovered` (last span error).
  Chains are ordered by first span ordinal.
- `turn_tokens[]`: spans with `operation == "chat"` or
  `category == "llm_call"` (the same filter as the existing cache summary),
  ordered by span ordinal. Entry: 1-based turn index, span id, input tokens
  and output tokens (null → 0).
- `user_instruction`: the first chat-operation span by span ordinal. Entry:
  span id, raw record id, and a descriptor derived from the `gen_ai.prompt`
  attribute of that span's raw record — the first line of the prompt text,
  truncated to 160 characters with a `"..."` marker when truncated. Omitted
  when there is no chat span or no resolvable prompt text (missing
  attribute, empty text, or malformed payload JSON; malformed payloads must
  not throw).
- `conversation`: the analyzed trace's `conversation_id` (from its spans),
  sibling trace ids ordered by earliest span start time (tie-break: trace
  id), the trace count, and the analyzed trace's 1-based position. Metadata
  only, no bodies. Omitted when the trace has no conversation id.
- `conversation_context`: bounded supporting evidence for the analyzed trace's
  same `conversation_id`. Omitted when the trace has no conversation id. The
  selected trace remains the anchor. The window contains up to two previous
  traces and two following traces plus the analyzed trace itself, with at most
  five emitted trace entries. Ordering matches `ListConversationTraces`:
  earliest span start time, tie-break trace id. Entry indexes are 1-based over
  the full ordered conversation.

  `conversation_context` fields:

  - `conversation_id`.
  - `trace_count`: total traces in the ordered conversation metadata.
  - `analyzed_trace_index`: 1-based index of the analyzed trace.
  - `window_start_index`: 1-based index of the first emitted trace.
  - `window_end_index`: 1-based index of the last emitted trace.
  - `truncated_before`: true when traces before the emitted window exist.
  - `truncated_after`: true when traces after the emitted window exist.
  - `traces[]`: emitted trace summaries.

  `conversation_context.traces[]` fields:

  - `trace_id`.
  - `relative_position`: analyzed trace is `0`, previous traces are negative,
    following traces are positive.
  - `is_analyzed_trace`.
  - `first_start_time`: first projected span start time for the trace, when
    known.
  - `user_instruction_descriptor`: first-line descriptor from that trace's
    first chat-operation prompt, truncated to 160 characters with a `"..."`
    marker when truncated. Omitted when no descriptor is resolvable; malformed
    sibling raw payloads must not throw.
  - `turn_count`, `input_tokens`, `output_tokens`, `total_tokens`: computed
    with the same turn filter as `turn_tokens[]` (`operation == "chat"` or
    `category == "llm_call"`).
  - `error_span_count`: count of spans with `status == "error"`.
  - `retry_chain_count`: count of retry chains using the same chain rule as
    `retry_chains[]`.
  - `error_span_ids[]`: span ids for error spans, capped at five entries,
    span ids only.
  - `retry_tool_names[]`: retry-chain tool names, capped at five entries,
    names only.

Determinism rule: the same inputs produce byte-identical serialized output;
every ordering above is explicit.

## Per-Category Required Evidence (Prompt Template v4)

Prompt template v4 requires the model to call `get_instruction_evidence`
first. Each finding must ground its category in extractor output as follows:

| Category id | Required extractor evidence |
| --- | --- |
| `goal-clarity` | Turn-level evidence of the analyzed trace: `turn_tokens` entries and/or spans verified through the raw tools. |
| `ambiguity` | User rephrase evidence: `conversation_context` sibling metadata plus corrective wording inside the analyzed trace or a bounded sibling trace. |
| `missing-acceptance-criteria` | Turn-level evidence of the analyzed trace: `turn_tokens` entries and/or spans verified through the raw tools. |
| `task-size-split` | Both a multi-goal `user_instruction` descriptor and a `turn_tokens` concentration (the concentrated turns must be named). |
| `missing-context-constraints` | At least one `error_spans` or `retry_chains` entry cited by span id. |

Conversation-scope rules:

- The analyzed trace is the anchor. `conversation_context.traces[]` is bounded
  supporting evidence, not a full transcript.
- Evidence from the analyzed trace must be labeled as analyzed-trace evidence.
- Evidence from a sibling trace must cite the sibling `trace_id`, its
  `relative_position`, and a short trace-relative descriptor such as previous
  or following trace; the finding must explain how that sibling evidence
  relates to the analyzed trace.
- Evidence outside `conversation_context.traces[]` is forbidden. If the needed
  proof sits outside the emitted bounded window, the model must state that the
  bounded evidence is insufficient instead of inferring from missing context.
- If only sibling evidence exists and the analyzed trace has no relevant
  support for the category, the model must mark the category insufficient
  rather than inventing a finding tied only to an unrelated sibling.
- Sibling instruction descriptors must not be copied verbatim into the final
  report beyond short factual references.

Escape hatch: a finding grounded outside the extractor output is allowed only
with a span id citation explicitly verified through the raw tools in the same
session, and the finding must state that verification. For sibling evidence,
the verified span must still belong to a trace emitted in
`conversation_context.traces[]`. Discovery of evidence the extractor cannot see
stays possible through this hatch.

## Per-Finding Output Contract

Each finding must contain exactly these four parts:

1. Category: exactly one taxonomy category id.
2. Evidence citation: span id(s), turn number(s), and/or sibling trace id(s)
   that exist in the analyzed trace or emitted `conversation_context.traces[]`,
   with a short factual descriptor. Long raw bodies must not be copied into the
   finding.
3. Gap explanation: what the instruction lacked, tied to the cited evidence.
4. Improved next-time instruction: a concrete rewrite the user could give
   next time.

Rules:

- A finding without a citable evidence reference is forbidden.
- Citations must refer to spans/turns present in the analyzed trace or trace
  ids present in the emitted bounded conversation window. The Sprint21 M5
  live-validation gate checks citation existence, trace-specificity, bounded
  window compliance, and sibling relationship clarity.
- Zero findings is a valid result and must be stated explicitly.
- Findings, including the improved next-time instruction, are written in
  Japanese, so the rewrite is usable verbatim as the user's next
  instruction. Other focuses stay language-unpinned.
- The result is markdown inside the existing analysis run result and remains
  local runtime data. No new schema.

## Safety Boundary

This focus inherits the
[Local Monitor Copilot Raw Analysis Boundary](../security-data-boundaries.md)
unchanged:

- Analysis results are raw-bearing local runtime data and must not be
  committed.
- Repository-safe summaries must not copy instruction text or other raw
  bodies.
- `--sanitized-only` removes the whole raw analysis surface, including this
  focus.

## Change Rules

- Taxonomy changes must preserve the category-evidence coupling rule; a
  category addition or removal updates this specification and the analysis
  prompt template together.
- Wire-value changes must update
  [telemetry ingestion](../layers/telemetry-ingestion.md) and the focus
  parse tests together.
- Output-contract changes must update this specification, the prompt
  template, and `docs/requirements.md` together.
- Extractor-field or per-category coupling-rule changes must update this
  specification, the relevant decision (D047 / D048), and the prompt template
  together.

## Related Specifications

- [Telemetry ingestion](../layers/telemetry-ingestion.md) — analysis routes,
  runner, and accepted focus values.
- [Security and data boundaries](../security-data-boundaries.md) — raw
  analysis boundary, `--sanitized-only` behavior.
