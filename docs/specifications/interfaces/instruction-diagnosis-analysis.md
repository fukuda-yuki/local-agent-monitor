# Instruction Diagnosis Analysis Interface

This document defines the `instruction-diagnosis` focus of the Local Monitor
Copilot raw analysis (decision D046, Issue #46 Phase 1). The analysis routes,
runner, and run lifecycle are defined in
[telemetry ingestion](../layers/telemetry-ingestion.md). The focus diagnoses
the implementation instructions the user gave the agent, using trace-internal
evidence only, and reports findings in a fixed per-finding format.

## Scope And Non-Goals

- Diagnosis display only: the result appears as a normal raw analysis result
  in the Local Monitor analysis drawer.
- Evidence is trace-internal only. No GitHub issue / commit / test-evidence
  correlation (traces stay manually selected, D037).
- No memory candidate generation, no adoption workflow, and no new
  repository-safe export.
- Prompt-only in v1: the raw trace is fed through the existing analysis
  runner and the prompt requires span/turn citations. Migration of proven
  evidence patterns to deterministic pre-extractors is a later phase.
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
defined trace-internal evidence pattern. Adding a category requires adding
its evidence pattern in the same change.

| Category id | Name (JA) | Definition | Required trace-internal evidence pattern |
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

## Per-Finding Output Contract

Each finding must contain exactly these four parts:

1. Category: exactly one taxonomy category id.
2. Evidence citation: span id(s) and/or turn number(s) that exist in the
   analyzed trace, with a short factual descriptor. Long raw bodies must not
   be copied into the finding.
3. Gap explanation: what the instruction lacked, tied to the cited evidence.
4. Improved next-time instruction: a concrete rewrite the user could give
   next time.

Rules:

- A finding without a citable evidence reference is forbidden.
- Citations must refer to spans/turns present in the analyzed trace. The
  Sprint19 M5 live-validation gate checks citation existence and
  trace-specificity.
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

## Related Specifications

- [Telemetry ingestion](../layers/telemetry-ingestion.md) — analysis routes,
  runner, and accepted focus values.
- [Security and data boundaries](../security-data-boundaries.md) — raw
  analysis boundary, `--sanitized-only` behavior.
