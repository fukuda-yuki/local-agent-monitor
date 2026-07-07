# Sprint21 - Conversation Scope For Instruction Diagnosis (Issue #46 Phase 2, step 2)

Status: Planned. This sprint is the next Issue #46 Phase 2 step after
Sprint20's deterministic instruction-evidence extractor.

Sprint20 added conversation metadata to `get_instruction_evidence`, but the
analysis remains effectively trace-scoped: the model sees the analyzed trace's
evidence plus sibling trace ids and ordering, not bounded evidence from the
neighboring traces. Sprint21 makes `instruction-diagnosis` able to use nearby
traces from the same `conversation_id` as bounded context.

## Goal

When a user analyzes one trace, instruction diagnosis can refer to the
immediately relevant previous and following traces in the same conversation, so
it can detect instruction clarification, rephrasing, follow-up acceptance
criteria, and repeated context failures that occur across Copilot CLI
invocations.

The selected trace remains the anchor. Sibling traces are supporting evidence,
not an unbounded conversation transcript.

## Requirements

- Resolve sibling traces by the existing projected `conversation_id`.
- Build a deterministic bounded window around the analyzed trace:
  - default window: up to two traces before and two traces after the analyzed
    trace, plus the analyzed trace itself;
  - maximum emitted trace entries: five;
  - order: earliest span start time, tie-break by trace id, matching
    `ListConversationTraces(conversationId)`.
- Emit bounded sibling evidence through the raw analysis surface only. No new
  route, no public API field, no projection migration, and no Canvas focus
  change.
- Include only short, diagnostic summaries for sibling traces:
  - trace id, relative position, first start time, and whether the row is the
    analyzed trace;
  - capped first-line user instruction descriptor when resolvable;
  - turn count and token totals;
  - error and retry summary counts, with capped span id/tool references.
- Keep raw content bounded:
  - no prompt/response/tool body copying from sibling traces;
  - no long raw bodies in extractor output;
  - instruction descriptors use the same first-line, capped descriptor posture
    as Sprint20 unless M1 updates the source-of-truth contract first.
- Prompt template v4 must distinguish analyzed-trace evidence from sibling
  evidence. A finding may cite sibling evidence, but it must name the sibling
  trace id and state how it relates to the analyzed trace.
- No evidence outside the bounded window may be cited. If the needed proof sits
  outside the emitted window, the model must say the bounded evidence is
  insufficient instead of inferring from missing context.
- Existing Sprint20 gates remain: citation existence, trace specificity,
  no-evidence-no-finding, and extractor/raw-verified grounding.

## Boundary

- Raw analysis only: `--sanitized-only` continues to remove the whole raw
  analysis surface, including this conversation scope.
- Additive internal behavior only: no new Local Monitor route, no
  `/api/monitor/*` shape change, no SSE change, no dashboard/static export,
  no Canvas bounded DTO change, no memory candidate generation, and no
  adoption workflow.
- Repository evidence remains sanitized. Sprint records may include trace ids,
  span ids, counts, categories, and verdicts, but not raw prompts, raw
  responses, tool arguments/results, PII, credentials, provider URLs, or full
  analysis markdown.
- `conversation_id` is grouping metadata, not identity or session ownership.
  It must not be used for cross-repository correlation or external outcome
  linkage in this sprint.

## Non-Goals

- Full conversation transcript reconstruction.
- Searching all historical traces for similar instructions.
- Changing the diagnosis taxonomy unless M5 proves the current taxonomy cannot
  express cross-trace evidence.
- GitHub issue, commit, test-evidence, or PR correlation.
- Automatic prompt memory, adoption, or repository-safe export.
- Expanding the Canvas helper focus set.

## Milestones

| Milestone | Summary | Status |
| --- | --- | --- |
| M1 | Source-of-truth specs and D048 decision for bounded conversation context. | Done |
| M2 | Deterministic bounded conversation-context extraction and tests. | Planned |
| M3 | Tool data wiring and prompt template v4 with sibling-evidence rules. | Planned |
| M4 | Regression validation and self-review. | Planned |
| M5 | Live validation against preserved Sprint19/Sprint20 traces plus conversation-window cases; repository-safe Issue #46 update draft. | Planned |

## Recommended Implementation Order

M1 -> M2 -> M3 -> M4 -> M5.

M1 must update the current source-of-truth specifications before code changes.
M2 should prove the bounded context as a pure deterministic computation before
prompt changes. M3 should then expose the new context through the existing
`get_instruction_evidence` tool and revise only the instruction-diagnosis
prompt block. M4 verifies the additive boundary. M5 decides whether Phase 2
step 2 is ready to proceed.

## Acceptance Criteria

- The conversation-scope requirement is explicit and bounded.
- The analysis can cite previous/following sibling traces by trace id without
  reading or emitting unbounded raw bodies.
- Existing trace-scoped behavior still works when there is no `conversation_id`,
  when the conversation has only one trace, and when sibling raw descriptors are
  unavailable.
- Prompt v4 prevents out-of-window or uncited sibling claims.
- Full validation records the automated result and the live-validation caveats.

## Related

- Sprint19 instruction diagnosis:
  `docs/sprints/sprint19-instruction-diagnosis-analysis/`
- Sprint20 deterministic extractor:
  `docs/sprints/sprint20-instruction-evidence-extractor/`
- Source-of-truth interface:
  `docs/specifications/interfaces/instruction-diagnosis-analysis.md`
- Decisions D046 and D047:
  `docs/decisions.md`
