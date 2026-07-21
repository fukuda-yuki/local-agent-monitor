# Instruction Diagnosis Analysis Interface

This document defines the `instruction-diagnosis` focus of the Local Monitor
Copilot raw analysis (decisions D046-D048, Issue #46) and the versioned,
repository-safe finding and instruction-rule handoff introduced by Issue #59.
The analysis routes, runner, and run lifecycle are defined in
[telemetry ingestion](../layers/telemetry-ingestion.md). The focus diagnoses
the implementation instructions the user gave the agent, using the analyzed
trace as the anchor plus bounded same-conversation sibling evidence when
available, and reports findings in a fixed per-finding format.

## Scope And Non-Goals

- The raw result appears as a normal raw analysis result in the Local Monitor
  analysis drawer. Issue #59 additionally persists an internal repository-safe
  finding handoff for downstream consumers.
- Evidence is the analyzed trace plus the bounded `conversation_context`
  window from the same `conversation_id`. No GitHub issue / commit /
  test-evidence correlation (traces stay manually selected, D037).
- Issue #59 generates deterministic instruction-rule candidates only. It does
  not promote, approve, apply, display, or measure them, and it does not add a
  repository-safe export route.
- Deterministic pre-extraction has run since prompt template v3 (D047, Issue
  #46 Phase 2 step 1): a code extractor runs at analysis start
  and its output is exposed through the process-internal tool
  `get_instruction_evidence`. Prompt template v4 (D048, Issue #46 Phase 2
  step 2) extends that output with bounded conversation context. Issue #59
  prompt template v5 requires exact extractor-index references for the
  machine-readable receipt and adds `submit_instruction_finding`; other raw
  tools remain available for the local markdown analysis but cannot authorize
  receipt evidence outside that index.
- The focus value rides the existing `POST /traces/{traceId}/analysis`
  payload. There is no new public route or API field. Additive local SQLite
  storage holds the repository-safe handoff; raw analysis storage and the
  existing public route shapes remain unchanged.

## Focus Value

- Wire value: `instruction-diagnosis`.
- Japanese drawer label: 指示診断.
- Exposed in the Local Monitor analysis drawer only. The Canvas helper focus
  set (`latency` / `tokens` / `cache` / `errors`, D036) is not extended.
- The D045 history-resend follow-up chat works with this focus like any
  other focus.

## Finding Taxonomy v1

Coupling rule: a category exists in this taxonomy only together with a
defined analyzed-trace or bounded same-conversation evidence pattern. Adding a
category requires adding its evidence pattern in the same change.

| Category id | Name (JA) | Definition | Deterministically checkable evidence | Permitted interpretation | Minimum `supported` pattern |
| --- | --- | --- | --- | --- | --- |
| `goal_clarity` | 目標の明確さ | The intended outcome is not sufficiently pinned. | Two distinct exact turn references. | Whether the later turn redirects or redefines the goal. | Two distinct turns and a producer-assessed `supported` verdict. |
| `ambiguity` | 曖昧さ | The instruction admits multiple materially different readings. | Two distinct turns, or anchor plus bounded sibling trace references. | Whether the later evidence is a clarification or disambiguation. | Either two distinct turns or two distinct traces including the anchor, plus a producer-assessed `supported` verdict. |
| `acceptance_criteria_missing` | 受け入れ基準の欠如 | No verifiable done-condition was stated. | Two distinct exact turn references. | Whether a later turn corrects completion or adds verification. | Two distinct turns and a producer-assessed `supported` verdict. |
| `scope_boundary_missing` | スコープ境界の欠如 | Included and excluded work was not bounded. | At least one exact turn plus one exact error/retry span. | Whether the failure or rework resulted from an unstated boundary. | A turn and an error/retry span, plus a producer-assessed `supported` verdict. |
| `task_too_large` | タスク過大 | The instruction bundles too much work for one bounded run. | The exact instruction span plus two distinct turn references. | Whether the instruction is multi-goal and the later turns show concentration or re-scoping. | The instruction span and two distinct turns, plus a producer-assessed `supported` verdict. |
| `test_requirement_missing` | テスト要件の欠如 | Required test scope was not stated before completion. | Two distinct exact turn references. | Whether a later turn adds or corrects test requirements. | Two distinct turns and a producer-assessed `supported` verdict. |
| `evidence_requirement_missing` | 証跡要件の欠如 | Required completion evidence or artifact checks were not stated. | Two distinct exact turn references. | Whether a later turn asks for missing proof or artifact existence. | Two distinct turns and a producer-assessed `supported` verdict. |
| `environment_assumption_missing` | 環境前提の欠如 | An environment fact or constraint needed for execution was omitted. | At least one exact turn plus one exact error/retry span. | Whether the later turn supplies the missing environment fact. | A turn and an error/retry span, plus a producer-assessed `supported` verdict. |

Disambiguation note: `environment_assumption_missing` is distinguished from
`ambiguity` by evidence type: execution failure or retry resolved by supplied
environment information versus instruction rephrasing.

Category ids are closed receipt values. Adding a category requires the evidence
pattern, deterministic fact boundary, permitted interpretation, positive
fixture, negative fixture, and eligibility rule in the same versioned change.

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
| `goal_clarity` | Two exact `turn_tokens` entries from the analyzed trace. |
| `ambiguity` | Two exact turns, or the anchor plus one emitted `conversation_context` sibling trace. |
| `acceptance_criteria_missing` | Two exact `turn_tokens` entries from the analyzed trace. |
| `scope_boundary_missing` | One exact analyzed-trace turn plus one analyzed-trace span in `error_spans` or `retry_chains`. |
| `task_too_large` | The analyzed-trace `user_instruction` span plus two exact analyzed-trace `turn_tokens` entries. |
| `test_requirement_missing` | Two exact `turn_tokens` entries from the analyzed trace. |
| `evidence_requirement_missing` | Two exact `turn_tokens` entries from the analyzed trace. |
| `environment_assumption_missing` | One exact analyzed-trace turn plus one analyzed-trace span in `error_spans` or `retry_chains`. |

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

There is no raw-tool escape hatch for the machine-readable receipt. Evidence
that does not resolve exactly in this deterministic index cannot be submitted
as a receipt, even if the model inspected it through another process-internal
tool.

## Per-Finding Output Contract

Each accepted finding in the local markdown result contains these four parts:

1. Category: exactly one taxonomy category id.
2. Evidence citation: span id(s), turn number(s), and/or sibling trace id(s)
   that exist in the analyzed trace or emitted `conversation_context.traces[]`,
   with a short factual descriptor. Long raw bodies must not be copied into the
   finding.
3. Gap explanation: the fixed taxonomy `gap_summary`.
4. Improved next-time instruction: the fixed taxonomy
   `suggested_instruction`.

Rules:

- A finding without an accepted `submit_instruction_finding` call is
  forbidden.
- Citations must refer to spans/turns present in the analyzed trace or trace
  ids present in the emitted bounded conversation window. The Sprint21 M5
  live-validation gate checks citation existence, trace-specificity, bounded
  window compliance, and sibling relationship clarity.
- Zero findings is a valid result and must be stated explicitly.
- Findings, including the improved next-time instruction, are written in
  Japanese, so the rewrite is usable verbatim as the user's next
  instruction. Other focuses stay language-unpinned.
- The result markdown remains local raw-derived runtime data. The separate
  machine-readable handoff below is the only new versioned schema; it is not
  embedded as model-authored JSON in the markdown result.

## Process-Internal Finding Submission

`instruction-diagnosis` adds the process-internal SDK tool
`submit_instruction_finding`. It is not an HTTP route and is not available to
other focuses. The model calls it once for each proposed finding before
writing the final markdown. Its exact string parameters are:

| Parameter | Contract |
| --- | --- |
| `category` | One closed v1 category value. |
| `verdict` | `supported`, `weak`, or `incomplete`. |
| `extractor_source` | `deterministic_prepass` or `prompt_only`. |
| `evidence_refs_json` | A raw-local JSON array containing only exact evidence-reference objects returned by the extractor. |

No gap, instruction, title, rule, or other free-text field is accepted. The
bounded response is exactly `{"status":"accepted"}` or
`{"status":"rejected","code":"<closed-code>"}`. A rejected submission is
not retained and may not appear in the final finding report. No submission is
the valid zero-finding path. Submission rejection codes are closed to
`invalid_contract`, `invalid_serialization`,
`unresolved_evidence_reference`, and `invalid_derived_identity`. Persistence
uses the separate internal codes `conflicting_persistence` and
`invalid_persistence`.

Submission references are raw-local inputs used only to resolve the exact
extractor index. After validation, each non-null Session, trace, and span ID is
replaced by a deterministic domain-separated opaque reference before any
receipt, candidate, JSON carrier, or persistence write is constructed. Raw IDs
never cross into the repository-safe handoff.

## Machine-Readable Finding Handoff v1

The Issue #59 handoff is an internal bounded DTO and a canonical JSON carrier.
Its structural source of truth is
[`instruction-finding-handoff.schema.json`](../contracts/instruction-findings/v1/instruction-finding-handoff.schema.json).
It is not a new HTTP route. Exact .NET type names are:

- `InstructionRawEvidenceReferenceV1` (raw-local submission/index only)
- `InstructionEvidenceReferenceV1`
- `InstructionFindingReceiptV1`
- `InstructionRuleProvenanceV1`
- `InstructionRuleCandidateV1`
- `InstructionFindingHandoffV1`

The three exact schema-version values are:

- finding: `instruction-finding.v1`
- candidate: `instruction-rule-candidate.v1`
- handoff: `instruction-finding-handoff.v1`

The closed wire vocabularies are:

| Field | Values |
| --- | --- |
| `category` | `goal_clarity`, `ambiguity`, `acceptance_criteria_missing`, `scope_boundary_missing`, `task_too_large`, `test_requirement_missing`, `evidence_requirement_missing`, `environment_assumption_missing` |
| `verdict` | `supported`, `weak`, `incomplete` |
| `extractor_source` | `deterministic_prepass`, `prompt_only` |
| `relative_position` | `anchor`, `previous`, `following` |
| `evidence_quote_state` | `raw_local_only` |
| `candidate_eligibility` | `eligible`, `ineligible` |
| `target_kind` | `prompt_instruction` |
| `scope_hint` | `task`, `repository` |

`InstructionFindingReceiptV1` has this exact JSON shape and property order:

```json
{
  "schema_version": "instruction-finding.v1",
  "finding_id": "instruction-finding-<24-lowercase-hex>",
  "analysis_run_id": 123,
  "category": "acceptance_criteria_missing",
  "verdict": "supported",
  "extractor_source": "deterministic_prepass",
  "anchor_trace_id": "trace-ref-f5ae5df5128a218007b6681270f7ff01",
  "evidence_refs": [
    {
      "session_id": null,
      "trace_id": "trace-ref-f5ae5df5128a218007b6681270f7ff01",
      "span_id": "span-ref-349edfe8b40889b23c198307e6a28602",
      "turn_index": 1,
      "relative_position": "anchor"
    },
    {
      "session_id": null,
      "trace_id": "trace-ref-f5ae5df5128a218007b6681270f7ff01",
      "span_id": "span-ref-4fd8fb963e88a87d6a94c68bd275ef96",
      "turn_index": 2,
      "relative_position": "anchor"
    }
  ],
  "evidence_quote_state": "raw_local_only",
  "gap_summary": "完了条件を確認できる証拠が不足している。",
  "suggested_instruction": "実装前に完了条件と、それを確認する必須テストを明記する。",
  "candidate_eligibility": "eligible"
}
```

Rules:

- `analysis_run_id` is a positive signed 64-bit integer.
- `finding_id` is unique within the run. Duplicate canonical findings collapse
  to one receipt. If duplicate submissions assess different verdicts, the
  least-strong verdict wins (`incomplete` before `weak` before `supported`),
  independent of submission order.
- Repository-safe references have exact forms
  `session-ref-<32-lowercase-hex>`, `trace-ref-<32-lowercase-hex>`, and
  `span-ref-<32-lowercase-hex>`. They are derived opaque tokens, never source
  IDs, bodies, labels, or paths.
- `evidence_refs` is nonempty. Its canonical order is `session_id` (null first),
  `trace_id`, `span_id` (null first), `turn_index` (null first), then
  `relative_position` (`anchor`, `previous`, `following` order). String fields
  use ordinal comparison. Exact duplicate references collapse.
- `span_id` and `turn_index` may not both be null. `turn_index`, when present,
  is positive.
- `anchor` requires the derived anchor trace reference. `previous` and
  `following` require a derived reference for an emitted bounded sibling trace
  on the corresponding side.
- Each raw-local submission reference must resolve before tokenization: a span
  reference resolves only when that exact span is present in the deterministic
  evidence index for the referenced trace, and a turn reference resolves only
  when that exact 1-based turn exists for the referenced trace. Receipt
  references are the kind-specific tokens of those already-resolved raw-local
  inputs; consumers do not re-resolve them as source IDs. Trace proximity,
  repository, timestamp, text similarity, and guessed session linkage never
  resolve evidence.
- An unresolved raw-local reference rejects the finding before a receipt is
  constructed. It is never persisted as a weaker receipt and never becomes a
  candidate.
- Unsupported category, schema, or enum values are rejected. There is no
  `unsupported` verdict in v1.
- `gap_summary` and `suggested_instruction` come only from the fixed taxonomy
  templates below. Arbitrary model text is never copied into this carrier.

### Verdict and eligibility

The producer supplies an assessed verdict, but the pipeline deterministically
enforces the category minimum:

1. Validate every raw-local submission reference exactly. Any unresolved
   reference rejects the finding before repository-safe tokenization.
2. `incomplete` stays `incomplete`.
3. `weak` stays `weak`.
4. An assessed `supported` becomes `supported` only when the category's minimum
   evidence pattern is satisfied; otherwise it is downgraded to `weak`.
5. Only final `supported` is `eligible`. `weak` and `incomplete` are
   `ineligible`.

`extractor_source` records how the finding itself was produced. It does not
upgrade the verdict. A `prompt_only` finding can be supported only when its
exact deterministic evidence references meet the same minimum. A
`deterministic_prepass` finding does not authorize semantic claims outside the
taxonomy's deterministically checkable facts.

The fixed safe text and targeting templates are:

| Category | `gap_summary` | `suggested_instruction` / candidate `rule_text` | `title` | `target_hint` | `scope_hint` |
| --- | --- | --- | --- | --- | --- |
| `goal_clarity` | `達成する成果の定義が不足している。` | `作業開始前に、達成する成果と利用者が判断できる終了状態を明記する。` | `Goal clarity` | `task_prompt` | `task` |
| `ambiguity` | `複数の解釈を防ぐ指定が不足している。` | `複数の解釈が可能な語は、期待する解釈と除外する解釈を明記する。` | `Disambiguate instructions` | `task_prompt` | `task` |
| `acceptance_criteria_missing` | `完了条件を確認できる証拠が不足している。` | `実装前に完了条件と、それを確認する必須テストを明記する。` | `Acceptance criteria` | `task_prompt` | `task` |
| `scope_boundary_missing` | `実施範囲と非対象範囲の境界が不足している。` | `着手前に実施範囲、非対象範囲、変更してよい対象を明記する。` | `Scope boundaries` | `repository_guidance` | `repository` |
| `task_too_large` | `一回の実行に対して作業範囲が大きすぎる。` | `独立して検証できる単位に作業を分割し、各単位の完了条件を明記する。` | `Bound task size` | `task_prompt` | `task` |
| `test_requirement_missing` | `必要なテスト範囲の指定が不足している。` | `変更前に実行する対象テストと、完了前に実行する回帰テストを明記する。` | `Test requirements` | `repository_guidance` | `repository` |
| `evidence_requirement_missing` | `完了時に残す証跡の指定が不足している。` | `完了を宣言する前に、必須テスト結果と必要な証跡の存在を確認する。` | `Evidence requirements` | `repository_guidance` | `repository` |
| `environment_assumption_missing` | `実行に必要な環境前提の指定が不足している。` | `着手前に、必要な実行環境、利用可能なツール、禁止された代替経路を明記する。` | `Environment assumptions` | `repository_guidance` | `repository` |

## Instruction Rule Candidate v1

Only eligible receipts generate candidates. The exact JSON shape and property
order are:

```json
{
  "schema_version": "instruction-rule-candidate.v1",
  "candidate_id": "instruction-rule-<24-lowercase-hex>",
  "deduplication_key": "instruction-rule-dedup-<24-lowercase-hex>",
  "source_finding_ids": ["instruction-finding-<24-lowercase-hex>"],
  "title": "Evidence requirements",
  "rule_text": "完了を宣言する前に、必須テスト結果と必要な証跡の存在を確認する。",
  "target_kind": "prompt_instruction",
  "target_hint": "repository_guidance",
  "scope_hint": "repository",
  "provenance": {
    "analysis_run_id": 123,
    "trace_refs": ["trace-ref-f5ae5df5128a218007b6681270f7ff01"]
  }
}
```

- `deduplication_key` depends only on schema major, category, target kind,
  target hint, scope hint, and the fixed rule-template version. It does not
  depend on input order, run ID, finding ID, or trace ID.
- Equal deduplication keys collapse to one candidate. Source finding IDs and
  trace refs are distinct and ordinally sorted.
- `candidate_id` is derived from the deduplication key using a separate hash
  domain and is therefore stable across analysis runs for the same rule.
- Target values are hints only. They grant no file, proposal, apply, or git
  authority.

## Deterministic Identity

Finding IDs, deduplication keys, and candidate IDs use SHA-256 over a fixed UTF-8
domain followed by length-framed canonical fields. Each field is encoded as a
four-byte unsigned big-endian byte length followed by its UTF-8 bytes; null is
encoded with length `0xffffffff`; integers use invariant decimal text. Only the
first 12 digest bytes are rendered as 24 lowercase hexadecimal characters.

- Finding domain: `copilot-agent-observability/instruction-finding/v1`.
  Fields are analysis run ID, category, extractor source, and the canonical
  evidence-reference tuples. The assessed/final verdict and generated text do
  not change identity.
- Dedup domain: `copilot-agent-observability/instruction-rule-dedup/v1`.
  Fields are category, target kind, target hint, scope hint, and template
  version `instruction-rule-template.v1`.
- Candidate domain: `copilot-agent-observability/instruction-rule/v1`.
  The sole field is the complete deduplication key.

Repository-safe reference tokens use the same four-byte big-endian
length-framing rule and the first 16 SHA-256 digest bytes (32 lowercase hex).
The domains are
`copilot-agent-observability/instruction-session-reference/v1`,
`copilot-agent-observability/instruction-trace-reference/v1`, and
`copilot-agent-observability/instruction-span-reference/v1`; the sole framed
field is the corresponding exact raw-local ID. Domain separation prevents a
value reused across identifier kinds from producing the same token.

## Handoff, Serialization, And Persistence

`InstructionFindingHandoffV1` has exact fields
`schema_version`, `analysis_run_id`, `findings`, and `candidates`, in that
order. Findings and candidates are ordinally ordered by their IDs. Canonical
JSON is UTF-8 without BOM, indentation, or trailing newline. It includes null
evidence-reference fields, uses the exact snake_case names above, and rejects
unknown properties and non-string enum values on read.

Local persistence is additive table `instruction_finding_handoffs`:

```text
analysis_run_id INTEGER PRIMARY KEY
schema_version TEXT NOT NULL
payload_json TEXT NOT NULL
payload_sha256 TEXT NOT NULL
created_at TEXT NOT NULL
```

`payload_sha256` is the lowercase SHA-256 of the exact UTF-8 `payload_json`
bytes. A write validates the typed handoff, serializes it canonically, and uses
insert-or-identical semantics: a second write for the same run succeeds only
when schema, payload bytes, and checksum are identical. A conflicting rewrite
fails. A read verifies schema, checksum, strict deserialization, all derived
ids, ordering, and candidate/source-finding consistency before returning the
DTO. The successful analysis-result update and handoff insert occur in the
same SQLite transaction; neither may commit without the other.

Consumer handoff:

- Issue #72 may retain only existing `finding_id` references and, when needed,
  the already-tokenized receipt references in its bounded evidence dataset. It
  never retains the raw-local submission IDs and does not copy or redefine the
  receipt schema.
- Issue #73 constructs assessed finding drafts from the frozen #72 dataset,
  supplies the exact evidence index, and consumes `InstructionFindingHandoffV1`.
  It may add no category or field locally. Invalid citations are rejected;
  zero findings is a valid handoff with empty arrays.
- Issue #85 may export only the canonical handoff JSON after its own bundle
  repository-safe scanner succeeds. It must preserve IDs, provenance, schema
  versions, nulls, and byte ordering.

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
- The finding handoff remains usable in sanitized consumers because it contains
  only derived opaque reference tokens, closed classifications, numeric run
  identity, and fixed templates. It never accepts model-supplied summary,
  instruction, title, rule text, or a raw Session/trace/span ID.
- Generated text may not contain a raw prompt/response/tool body, source-code
  or file fragment, credential, PII, or local path. The fixed-template-only
  construction is the v1 enforcement mechanism; a permissive free-text scanner
  is not a substitute.

## Change Rules

- Taxonomy or field changes require a named contract-version change and update
  this specification, the JSON Schema, the canonical fixture, the analysis
  prompt template, and positive/negative compatibility tests together.
- Wire-value changes must update
  [telemetry ingestion](../layers/telemetry-ingestion.md) and the focus
  parse tests together.
- Output-contract changes must update this specification, the prompt template,
  and the Wave integration-owner edits to `docs/requirements.md`,
  `docs/spec.md`, and `docs/decisions.md` together.
- Extractor-field or per-category coupling-rule changes must update this
  specification, the relevant decision (D047 / D048), and the prompt template
  together.

## Related Specifications

- [Telemetry ingestion](../layers/telemetry-ingestion.md) — analysis routes,
  runner, and accepted focus values.
- [Security and data boundaries](../security-data-boundaries.md) — raw
  analysis boundary, `--sanitized-only` behavior.
