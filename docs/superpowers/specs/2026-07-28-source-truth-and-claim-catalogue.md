# Source Truth and Claim Catalogue

Status: empirical source catalogue for Issue #129 (C1), measured 2026-07-28.

## Scope, method, and captures

This catalogue covers exactly two telemetry sources:

- `github-copilot-cli`;
- `github-copilot-vscode`.

It inventories what arrived in the supplied purpose-built captures, the coverage
of each field, the source version to which the observation applies, and the
strongest of the five permitted claim layers that the field can support. It is
an empirical catalogue, not a source schema, population estimate, information
architecture, or proposal for changing storage or code.

The evidence base is fixed:

| Source | Capture | Source version | Retained records | Raw evidence scanned |
| --- | --- | --- | --- | --- |
| `github-copilot-cli` | `data/issue-129-cli-capture.db` | Per trace: `service.version` included two versions, 1.0.74 and 1.0.75; the distribution was not supplied | 4 monitor traces, 14 monitor spans | 14 raw OTLP spans |
| `github-copilot-vscode` | `data/issue-127-vscode-capture.db` | The extraction did not supply a producer version | 24 monitor traces, 55 monitor spans | 55 raw OTLP spans |
| `github-copilot-vscode` | `data/monitor-live-validation-vscode.db` | 0.54.0 | 0 monitor traces, 0 monitor spans | 18 raw OTLP spans |

The CLI auto-updated during the capture. Consequently source version is a
per-trace property; assigning one version to the whole CLI capture would be
false. The older VS Code capture reached the raw OTLP payload but did not reach
the monitor tables. Its raw observations remain evidence of what version 0.54.0
emitted, but not evidence that the product could display those records.

Counts below are spans carrying the named key over raw spans scanned, unless the
row explicitly says that it is a trace count or a projected monitor-column
count. `Distinct` is cardinality only. No captured value, content, repository
identifier, path, prompt, response, tool payload, or skill-file content is
reproduced.

## How to use this catalogue to check an IA

An IA reviewer must reduce every datum shown, grouped, filtered, counted, or
used to word an empty state to a field row in this document:

1. Match the exact source and field.
2. Confirm that the screen's data path is available. A raw field does not make
   a NULL projection usable.
3. Keep the wording at or below the row's claim layer.
4. If the field is absent, use the row's unavailability state; never substitute
   zero.
5. If the field is raw-bearing, do not use it as ordinary display data.
6. For a negative statement, require a row explicitly marked as closed-world
   capable. Otherwise say only that no value or event was observed.

A screen dependency with no matching row is not evidenced by these captures and
may not be a v1 dependency. Vendor field names are listed separately from
normalized product projections so that “the source sent it” cannot be confused
with “the product can currently render it.”

## Claim and unavailability vocabulary

| Layer | Permitted use in this catalogue |
| --- | --- |
| exact identity | “This record is, or refers to, this Session, Trace, Span, or receipt.” |
| positively observed | “This was observed.” It never establishes that the observed set is complete. |
| conditionally complete | “Within captured coverage, this is the complete set.” The stated coverage conditions are mandatory. |
| exploratory | “These may be related; review them.” It is never evidence, priority, or cohort membership. |
| unsupported | “This source does not provide this.” |

The four unavailability states are:

| State | Meaning |
| --- | --- |
| unsupported by source | The required semantic signal is not supplied by this source. |
| not observed on this record | The source may support the field, but this capture or record did not carry it. |
| not captured: configuration or ingest gap | The source-side fact is not safely available through the current product path because capture, attribution, or projection lost it. |
| observed but uncertified | The value arrived, but the capability declaration or capture-control evidence does not certify it. |

These states are independent of the claim layers. For example, a raw token field
can be positively observed and still be “observed but uncertified” because the
source capability manifest declares its category `unknown`.

The existing capability manifests declare trace and hook signals, native
session identity, trace/span identity and parentage, timing, and the content
capture gate as available. They declare the source-version detector unavailable
and declare model/token, tool, and agent categories `unknown` for both sources.
The captures below nevertheless contain model, token, tool, and agent-related
fields. Populated data and a declared capability are therefore recorded
separately; this catalogue does not promote or edit either manifest.

## `github-copilot-cli` field inventory

All ratios in this table are from 14 raw spans or 14 projected monitor spans,
except the explicitly trace-scoped rows. The observations span CLI versions
1.0.74 and 1.0.75; the extraction did not provide per-version field
distributions.

“Raw-bearing” means the field may carry user content, tool payloads, system or
skill content, commands, or paths and is not ordinary display data.

| Field | Observed evidence and coverage | Acquisition or declaration state | Raw-bearing | Strongest claim layer and absence rule |
| --- | --- | --- | --- | --- |
| `service.name` | Present as the source discriminator for the 4/4 captured traces; value `github-copilot` | observed | no | positively observed; supports source attribution for these records, not population completeness |
| `service.version` | Present per trace; two versions occurred within the 4-trace capture | observed but the manifest's version detector is unavailable | no | positively observed; version must remain attached per trace |
| `client.kind` | No source value reached `monitor_traces.client_kind`: NULL on 4/4 traces | not captured: ingest/attribution gap, issue #151; `service.name` was captured instead | no | positively observed only through the `service.name` substitute; absence of `client.kind` is not evidence that source identity is unsupported |
| span operation name | 14/14 raw spans had an operation name in the extraction: 7 chat, 4 agent invocation, and 3 tool execution spans, including one dedicated Skill execution span | observed | no | positively observed; only those spans may be said to have been observed |
| `enduser.pseudo.id` | 4/14 spans; 1 distinct value | observed | no | positively observed; not Session identity and no absence claim |
| `gen_ai.agent.id` | 4/14; 1 distinct producer value, `github.copilot.default` | observed but agent capability is uncertified | no | positively observed as a producer identifier; unsupported for “which user-configured agent ran” |
| `gen_ai.agent.version` | 4/14; 2 distinct values | observed but agent capability is uncertified | no | positively observed as an agent-component version; no agent-ownership claim |
| `gen_ai.conversation.id` | 14/14; 4 distinct values | observed | no | exact identity for the source-native conversation reference; a conversation is not automatically a user turn |
| `gen_ai.operation.name` | 14/14; 3 distinct values | observed | no | positively observed; no completeness claim |
| `gen_ai.provider.name` | 14/14; 1 distinct value | observed | no | positively observed |
| `gen_ai.request.model` | 11/14; 1 distinct value | observed but model capability is uncertified | no | positively observed for the carrying spans; absence means not observed on that span |
| `gen_ai.request.stream` | 7/14; 1 distinct value | observed but model/request capability is uncertified | no | positively observed |
| `gen_ai.response.finish_reasons` | 11/14; 1 distinct value | observed but the monitor projection lost it | no | positively observed in raw OTLP only; projection absence is an ingest gap, not a source limitation |
| `gen_ai.response.id` | 7/14; 7 distinct values | observed | no | exact identity for the emitted response receipt |
| `gen_ai.response.model` | 7/14; 1 distinct value | observed but model capability is uncertified | no | positively observed for carrying spans |
| `gen_ai.response.time_to_first_chunk` | 1/14; 1 distinct value | observed but timing-field coverage is sparse | no | positively observed for that span; no general latency claim |
| `gen_ai.tool.call.id` | 3/14; 3 distinct values | observed but tool capability is uncertified | no | exact identity for the emitted tool-call receipt |
| `gen_ai.tool.definitions` | 11/14; 1 distinct value | observed but tool capability is uncertified | yes | positively observed as an available-definition payload; it does not prove tool use and is not ordinary display data |
| `gen_ai.tool.name` | 3/14; 3 distinct values | observed but tool capability is uncertified | no | positively observed tool executions only; absence means “no tool name observed” |
| `gen_ai.tool.type` | 3/14; 1 distinct value | observed but tool capability is uncertified | no | positively observed |
| `gen_ai.usage.cache_read.input_tokens` | 11/14; 7 distinct values | observed but token capability is uncertified | no | positively observed captured token values; never an uncaptured total |
| `gen_ai.usage.input_tokens` | 11/14; 10 distinct values | observed but token capability is uncertified | no | positively observed captured token values |
| `gen_ai.usage.output_tokens` | 11/14; 10 distinct values | observed but token capability is uncertified | no | positively observed captured token values |
| `gen_ai.usage.reasoning.output_tokens` | 0/14; absent from the exhaustive CLI key list | not observed on this record | no | positively observed at most if later present; current wording is “no reasoning-output token value was observed,” never zero |
| `gen_ai.usage.reasoning_tokens` | 0/14; absent from the exhaustive CLI key list | not observed on this record | no | positively observed at most if later present; no negative event claim |
| `github.copilot.agent.type` | 4/14; 1 distinct value | observed but agent capability is uncertified | no | positively observed as producer classification, not user-configured agent identity |
| `github.copilot.context.custom_agent_names` | 4/14; 1 distinct value | observed but agent capability is uncertified | no | positively observed as an at-run-time available-name inventory; it does not identify which agent ran |
| `github.copilot.context.mcp_server_names` | 4/14; 1 distinct value | observed but tool/configuration capability is uncertified | no | positively observed as an at-run-time configuration inventory; no invocation or completeness claim beyond the carrying trace |
| `github.copilot.context.skills` | 4/14, corresponding to every one of the 4 traces; 1 distinct array value | observed but the manifest has no Skill capability category | no | conditionally complete for the available Skill-name set at run time; it can support an absence claim only together with version-bound healthy invocation capture |
| `github.copilot.cost` | 11/14; 2 distinct values | observed but no certified product claim is established here | no | positively observed only |
| `github.copilot.initiator` | 7/14; 2 distinct values | observed | no | positively observed |
| `github.copilot.interaction_id` | 7/14; 4 distinct values | observed | no | exact identity for the emitted interaction reference; not proof that an interaction equals a user turn |
| `github.copilot.nano_aiu` | 11/14; 10 distinct values | observed but no certified product claim is established here | no | positively observed only |
| `github.copilot.server_duration` | 7/14; 7 distinct values | observed | no | positively observed for carrying spans |
| `github.copilot.service_request_id` | 7/14; 7 distinct values | observed | no | exact identity for the emitted service-request receipt |
| `github.copilot.skill.name` | Structurally observed on the dedicated Skill execution path; the supplied extraction did not include a separate per-key count | observed but Skill capability is undeclared | no | positively observed Skill identity; do not infer unobserved invocations from this field alone |
| `github.copilot.skill.source` | Structurally observed on the dedicated Skill execution path; no separate per-key count was supplied | observed but Skill capability is undeclared | no | positively observed Skill-source metadata |
| `github.copilot.skill.invocation_trigger` | Structurally observed on the dedicated Skill execution path; no separate per-key count was supplied | observed but Skill capability is undeclared | no | positively observed invocation-trigger metadata |
| `github.copilot.tool.parameters.skill_name` | 1/14; 1 distinct value | observed but Skill capability is undeclared | no | positively observed Skill identity on that tool span |
| dedicated Skill execution span | 1/14 raw spans | observed but Skill capability is undeclared | no | positively observed Skill invocation; with the per-trace available inventory and healthy/version-bound coverage, the invoked set can become conditionally complete |
| `github.copilot.turn_count` | 4/14; 2 distinct values | observed | no | positively observed producer count; not a count of monitor traces or necessarily user turns |
| `github.copilot.turn_id` | 7/14; 2 distinct values | observed | no | exact identity for the emitted turn reference; it does not make a trace a turn |
| `monitor_spans.tool_name` | 3/14 non-null, matching the three raw tool-name spans | observed but the source category remains uncertified | no | positively observed tool activity |
| `monitor_spans.agent_name` | 0/14 non-null while `gen_ai.agent.id` was present on 4/14; CLI never sent `gen_ai.agent.name` | not captured: key/projection mismatch | no | unsupported for “which user-configured agent ran”; the NULL projection must not be displayed as “no agent” |
| `monitor_spans.conversation_id` | 14/14 non-null | observed | no | exact identity for the projected conversation reference |
| `monitor_spans.input_tokens` | 11/14 non-null | observed but token capability is uncertified | no | positively observed captured input tokens |
| `monitor_spans.output_tokens` | 11/14 non-null | observed but token capability is uncertified | no | positively observed captured output tokens |
| `monitor_spans.total_tokens` | 11/14 non-null | observed but token capability is uncertified | no | positively observed captured total-token values; not necessarily all tokens in a run |
| `monitor_spans.cache_read_tokens` | 11/14 non-null | observed but token capability is uncertified | no | positively observed captured cache-read tokens |
| `monitor_spans.reasoning_tokens` | 0/14 non-null and no CLI reasoning-token raw key was observed | not observed on this record | no | positively observed at most; current absence must not be rendered as zero |
| `monitor_spans.request_model` | 11/14 non-null | observed but model capability is uncertified | no | positively observed |
| `monitor_spans.response_model` | 7/14 non-null | observed but model capability is uncertified | no | positively observed |
| `monitor_spans.error_type` | 0/14 non-null; no corresponding raw error key occurred in the exhaustive key list | not observed on this record | no | positively observed at most; only “no error type was observed,” not “no error happened” |
| `monitor_spans.finish_reasons` | 0/14 non-null despite raw finish reasons on 11/14 spans | not captured: ingest/projection gap | no | positively observed in raw OTLP only; a screen may not depend on the projection |
| `vcs.repository.name` / product `repository_name` | 0/14; the expected key was not emitted and no CLI repository alternative appeared in the exhaustive key list | not captured: key mismatch for the product field; underlying repository identity was not observed in this capture | no | no repository claim; current evidence supports only “repository identity was not observed” |
| user-configured agent identity | No dedicated field; the 4/14 `gen_ai.agent.id` values identified the producer's default component, not a user-configured agent | unsupported by source for this semantic claim | no | unsupported for “which agent ran” |
| delegated sub-agent task text | No dedicated field in 14/14 spans | unsupported by source as a structured field | yes if guessed from tool payloads | unsupported as evidence; extraction from raw tool content would be exploratory and raw-bearing |
| raw prompt or response content | No dedicated prompt/response-content key occurred in the exhaustive CLI attribute list | not observed on this record | yes | positively observed at most if later present; no display dependency and no claim that content was absent from the interaction |
| tool arguments or results | No dedicated argument/result key occurred in the exhaustive CLI attribute list | not observed on this record | yes | positively observed at most if later present; no display dependency |
| system instructions | No dedicated system-instruction key occurred in the exhaustive CLI attribute list | not observed on this record | yes | positively observed at most if later present; no display dependency |
| file path | No dedicated file-path key occurred in the exhaustive CLI attribute list | not observed on this record | yes | positively observed at most if later present; no display dependency |
| skill file content | 0/14 structured skill-file-content fields; structural Skill identity did arrive | unsupported by source as a structured Skill claim field | yes | unsupported as evidence of Skill identity or invocation; raw content must not be substituted |

### CLI absence boundary

The only measured field with the shape needed for a closed-world control
inventory is `github.copilot.context.skills`, present on every captured trace.
The structural Skill identity fields and dedicated Skill execution span identify
positive invocations. Together they can support “configured Skill X was not
invoked within captured coverage” only when the same trace has:

- the at-run-time available Skill inventory;
- proof that the structural invocation path was healthy for the interval;
- trace-bound source-version coverage.

The four traces show that the necessary fields exist; they do not by themselves
certify all future CLI versions or configurations. Until capture health and
version coverage are certified, the safe record wording remains “no invocation
of Skill X was observed.” No other CLI row in this catalogue supports “X did
not happen.”

## `github-copilot-vscode` field inventory

Each row gives the 2026-07-28 capture first (`55` raw spans) and, where the field
was part of the older extraction, the 0.54.0 capture second (`18` raw spans).
The 2026-07-28 producer version was not supplied. The older raw spans had no
retained monitor trace or span rows, so they establish producer emission only.

| Field | Observed evidence and coverage | Acquisition or declaration state | Raw-bearing | Strongest claim layer and absence rule |
| --- | --- | --- | --- | --- |
| `service.name` | Present as the source discriminator for 24/24 current monitor traces; value `copilot-chat` | observed | no | positively observed; supports source attribution for these records |
| `service.version` | Current capture: no version was supplied in the extraction. Older capture: version 0.54.0 is known at capture level | current: not observed on this record; older: observed, while manifest detector is unavailable | no | positively observed only where attached; no version may be invented for the current records |
| `client.kind` | `monitor_traces.client_kind` was NULL on 24/24 current traces; older capture retained no trace rows | not captured: ingest/attribution gap, issue #151; `service.name` was captured instead | no | positively observed only through `service.name`; not an unsupported-source claim |
| span operation name | Current 55/55 spans: 15 chat, 4 agent invocation, 8 tool execution, and 28 hook execution spans. Older 18/18: 9 chat, 4 embeddings, 4 tool execution, 1 agent invocation | observed | no | positively observed; a trace is not a user turn |
| hook span types | Current capture included SessionStart 8, Stop 8, UserPromptSubmit 4, PreToolUse 4, and PostToolUse 4; older capture observed none of these in 18 spans | current: observed; older: not observed on this record | no | positively observed; this disproves the assumption that hooks are CLI-only but does not certify a complete hook set |
| `copilot_chat.chat_session_id` | Current 47/55, 4 distinct; older 7/18, 1 distinct | observed | no | exact identity for the source-native chat-session reference |
| `copilot_chat.copilot_usage_nano_aiu` | Current 15/55, 8 distinct; older 9/18, 3 distinct | observed but no certified product claim is established here | no | positively observed only |
| `copilot_chat.debug_log_label` | Current 4/55, 1 distinct; older 1/18, 1 distinct | observed | no | positively observed; not Session identity |
| `copilot_chat.hook_command` | Current 28/55, 3 distinct; older 0/18 | current: observed; older: not observed on this record | yes | positively observed hook payload only; raw-bearing and not ordinary display data |
| `copilot_chat.hook_input` | Current 28/55, 20 distinct; older 0/18 | current: observed; older: not observed on this record | yes | positively observed hook payload only; never evidence of all inputs |
| `copilot_chat.hook_output` | Current 20/55, 1 distinct; older 0/18 | current: observed; older: not observed on this record | yes | positively observed hook payload only; never evidence of all outputs |
| `copilot_chat.hook_result_kind` | Current 28/55, 1 distinct; older 0/18 | current: observed; older: not observed on this record | no | positively observed |
| `copilot_chat.hook_type` | Current 28/55, 5 distinct; older 0/18 | current: observed; older: not observed on this record | no | positively observed hook types; no closed-world hook absence claim |
| `copilot_chat.parent_chat_session_id` | Current 4/55, 4 distinct; older 1/18, 1 distinct | observed | no | exact identity for the emitted parent-session reference |
| `copilot_chat.reasoning_content` | Current 1/55, 1 distinct; older 2/18, 2 distinct | observed; content gate behavior is uncertified | yes | positively observed raw content only; not display data and absence is not zero reasoning |
| `copilot_chat.repo.head_branch_name` | Current 4/55, 1 distinct; older 1/18, 1 distinct | observed but product repository projection does not consume it | no | positively observed branch metadata; no repository grouping through the current projection |
| `copilot_chat.repo.head_commit_hash` | Current 4/55, 1 distinct; older 1/18, 1 distinct | observed but product repository projection does not consume it | no | positively observed commit metadata |
| `copilot_chat.repo.remote_url` | Current 4/55, 1 distinct; older 1/18, 1 distinct | observed but product repository projection does not consume it | no | positively observed repository metadata; keep local and do not reproduce the value |
| `copilot_chat.request.max_prompt_tokens` | Current 15/55, 3 distinct; older 9/18, 2 distinct | observed but model/token capability is uncertified | no | positively observed request limit, not consumed tokens |
| `copilot_chat.request.options` | Current 15/55, 2 distinct; older 9/18, 2 distinct | observed but request capability is uncertified | yes | positively observed opaque request payload only; not ordinary display data |
| `copilot_chat.request.shape` | Current 15/55, 4 distinct; older 9/18, 3 distinct | observed but request capability is uncertified | no | positively observed producer shape classification |
| `copilot_chat.server_request_id` | Current 15/55, 12 distinct; older 9/18, 8 distinct | observed | no | exact identity for the emitted server-request receipt |
| `copilot_chat.session_id` | Current 47/55, 4 distinct; older 3/18, 1 distinct | observed | no | exact identity for the source-native session reference |
| `copilot_chat.time_to_first_token` | Current 15/55, 15 distinct; older 9/18, 9 distinct | observed | no | positively observed for carrying spans; not an all-run latency |
| `copilot_chat.turn_count` | Current 4/55, 2 distinct; older 1/18, 1 distinct | observed | no | positively observed producer count; a trace is not a turn |
| `copilot_chat.user_request` | Current 18/55, 13 distinct; older 9/18, 7 distinct | observed even though `captureContent` was not enabled; content gate unverified | yes | positively observed raw prompt-bearing content only; not display data |
| `gen_ai.agent.name` | Current 19/55, 5 distinct; older 10/18, 5 distinct; all observed values were internal component labels, never a user-configured agent | observed but uncertified and semantically mismatched | no | exploratory for internal-component grouping only; unsupported for “which user-configured agent ran” |
| `gen_ai.conversation.id` | Current 47/55, 4 distinct; older 10/18, 9 distinct | observed | no | exact identity for the emitted conversation reference; not a user-turn identity |
| `gen_ai.embeddings.input_count` | Current 0/55; older 4/18, 1 distinct | current: not observed on this record; version 0.54.0: observed | no | positively observed only for the older carrying spans |
| `gen_ai.input.messages` | Current 19/55, 14 distinct; older 10/18, 8 distinct | observed; content gate behavior is unverified | yes | positively observed raw prompt/context content only; not display data |
| `gen_ai.operation.name` | Current 55/55, 4 distinct; older 18/18, 4 distinct | observed | no | positively observed |
| `gen_ai.output.messages` | Current 19/55, 14 distinct; older 10/18, 9 distinct | observed; content gate behavior is unverified | yes | positively observed raw response content only; not display data |
| `gen_ai.provider.name` | Current 19/55, 1 distinct; older 14/18, 2 distinct | observed | no | positively observed |
| `gen_ai.request.max_tokens` | Current 15/55, 2 distinct; older 9/18, 2 distinct | observed but model/token capability is uncertified | no | positively observed request limit, not usage |
| `gen_ai.request.model` | Current 19/55, 3 distinct; older 14/18, 4 distinct | observed but model capability is uncertified | no | positively observed for carrying spans |
| `gen_ai.request.stream` | Current 15/55, 1 distinct; older 9/18, 1 distinct | observed but request capability is uncertified | no | positively observed |
| `gen_ai.request.temperature` | Current 8/55, 1 distinct; older 7/18, 1 distinct | observed but request capability is uncertified | no | positively observed |
| `gen_ai.request.top_p` | Current 8/55, 1 distinct; older 7/18, 1 distinct | observed but request capability is uncertified | no | positively observed |
| `gen_ai.response.finish_reasons` | Current 15/55, 1 distinct; older 9/18, 1 distinct | observed but current monitor projection lost it | no | positively observed in raw OTLP only; projection absence is an ingest gap |
| `gen_ai.response.id` | Current 15/55, 12 distinct; older 9/18, 8 distinct | observed | no | exact identity for the emitted response receipt |
| `gen_ai.response.model` | Current 19/55, 3 distinct; older 10/18, 2 distinct | observed but model capability is uncertified | no | positively observed |
| `gen_ai.response.time_to_first_chunk` | Current 15/55, 15 distinct; older 9/18, 8 distinct | observed | no | positively observed for carrying spans |
| `gen_ai.system_instructions` | Current 14/55, 5 distinct; older 8/18, 4 distinct | observed; content gate behavior is unverified | yes | positively observed raw system content only; not display data |
| `gen_ai.tool.call.arguments` | Current 8/55, 8 distinct; older 4/18, 4 distinct | observed but tool capability is uncertified | yes | positively observed raw tool arguments only; not display data or structured task identity |
| `gen_ai.tool.call.id` | Current 8/55, 5 distinct; older 4/18, 4 distinct | observed but tool capability is uncertified | no | exact identity for the emitted tool-call receipt |
| `gen_ai.tool.call.result` | Current 8/55, 4 distinct; older 4/18, 2 distinct | observed but tool capability is uncertified | yes | positively observed raw tool results only; not display data |
| `gen_ai.tool.definitions` | Current 11/55, 2 distinct; older 3/18, 1 distinct | observed but tool capability is uncertified | yes | positively observed available-definition payload only; it does not prove invocation |
| `gen_ai.tool.description` | Current 8/55, 3 distinct; older 4/18, 2 distinct | observed but tool capability is uncertified | yes | positively observed tool-description content only; not proof of use |
| `gen_ai.tool.name` | Current 8/55, 3 distinct; older 4/18, 2 distinct | observed but tool capability is uncertified | no | positively observed tool calls only; absence means “no tool name observed” |
| `gen_ai.tool.type` | Current 8/55, 1 distinct; older 4/18, 1 distinct | observed but tool capability is uncertified | no | positively observed |
| `gen_ai.usage.cache_read.input_tokens` | Current 19/55, 9 distinct; older 10/18, 5 distinct | observed but token capability is uncertified; current projection lost it | no | positively observed raw captured token values; no projected-screen dependency |
| `gen_ai.usage.input_tokens` | Current 19/55, 17 distinct; older 10/18, 9 distinct | observed but token capability is uncertified; current projection lost it, issue #150 | no | positively observed raw captured token values |
| `gen_ai.usage.output_tokens` | Current 19/55, 16 distinct; older 10/18, 10 distinct | observed but token capability is uncertified; current projection lost it, issue #150 | no | positively observed raw captured token values |
| `gen_ai.usage.reasoning.output_tokens` | Current 2/55, 1 distinct; older 3/18, 3 distinct | observed but token capability is uncertified; current projection lost it | no | positively observed raw captured token values |
| `gen_ai.usage.reasoning_tokens` | Current 1/55, 1 distinct; older 2/18, 2 distinct | observed but token capability is uncertified; current projection lost it | no | positively observed raw captured token values |
| `github.copilot.agent.type` | Current 4/55, 1 distinct; older 1/18, 1 distinct | observed but agent capability is uncertified | no | positively observed producer classification; not user-configured agent identity |
| `github.copilot.git.branch` | Current 4/55, 1 distinct; older 1/18, 1 distinct | observed but product repository projection does not consume it | no | positively observed branch metadata |
| `github.copilot.git.commit_sha` | Current 4/55, 1 distinct; older 1/18, 1 distinct | observed but product repository projection does not consume it | no | positively observed commit metadata |
| `github.copilot.git.repository` | Current 4/55, 1 distinct; older 1/18, 1 distinct | observed but product repository projection does not consume it | no | positively observed repository metadata |
| `github.copilot.github.org` | Current 4/55, 1 distinct; older 1/18, 1 distinct | observed | no | positively observed organization metadata; keep local and do not infer ownership beyond the recorded value |
| `github.copilot.hook.decision` | Current 28/55, 1 distinct; older 0/18 | current: observed; older: not observed on this record | no | positively observed hook decision values; no closed-world claim |
| `github.copilot.hook.duration` | Current 28/55, 28 distinct; older 0/18 | current: observed; older: not observed on this record | no | positively observed per-hook duration |
| `github.copilot.hook.tool_names` | Current 8/55, 2 distinct; older 0/18 | current: observed; older: not observed on this record | no | positively observed hook tool-name metadata; no proof that it lists every tool use |
| `github.copilot.tool.parameters.command` | Current 2/55, 2 distinct; older 0/18 | current: observed; older: not observed on this record | yes | positively observed raw command content only; not display data |
| `github.copilot.tool.parameters.file_path` | Current 2/55, 2 distinct; older 0/18 | current: observed; older: not observed on this record | yes | positively observed raw path content only; not display data |
| `monitor_spans.tool_name` | Current 8/55 non-null, matching raw tool-name coverage; older capture had no monitor span rows | current: observed but uncertified; older: not captured due ingest gap | no | positively observed tool activity only |
| `monitor_spans.agent_name` | Current 19/55 non-null; values came from `gen_ai.agent.name` and were internal component names | observed but uncertified and semantically mismatched | no | exploratory for internal components; unsupported for “which user-configured agent ran” |
| `monitor_spans.conversation_id` | Current 47/55 non-null; older capture had no monitor span rows | current: observed; older: not captured due ingest gap | no | exact identity for the projected conversation reference |
| `monitor_spans.input_tokens` | Current 0/55 non-null despite raw input usage on 19/55 | not captured: ingest/projection defect, issue #150 | no | positively observed in raw OTLP only; the current screen path may not depend on this projection |
| `monitor_spans.output_tokens` | Current 0/55 non-null despite raw output usage on 19/55 | not captured: ingest/projection defect, issue #150 | no | positively observed in raw OTLP only; the current screen path may not depend on this projection |
| `monitor_spans.total_tokens` | Current 0/55 non-null while component token values arrived | not captured: ingest/projection defect, issue #150 | no | no projected total claim; raw components are positively observed but not proof of an all-run total |
| `monitor_spans.cache_read_tokens` | Current 0/55 non-null despite raw cache-read usage on 19/55 | not captured: ingest/projection defect, issue #150 | no | positively observed in raw OTLP only |
| `monitor_spans.reasoning_tokens` | Current 0/55 non-null despite raw reasoning usage on 1/55 and reasoning-output usage on 2/55 | not captured: ingest/projection defect, issue #150 | no | positively observed in raw OTLP only; NULL must not be rendered as zero |
| `monitor_spans.request_model` | Current 19/55 non-null | observed but model capability is uncertified | no | positively observed |
| `monitor_spans.response_model` | Current 19/55 non-null | observed but model capability is uncertified | no | positively observed |
| `monitor_spans.error_type` | Current 0/55 non-null; no raw error-type key occurred in the exhaustive current list | not observed on this record | no | only “no error type was observed,” never “no error happened” |
| `monitor_spans.finish_reasons` | Current 0/55 non-null despite raw finish reasons on 15/55 | not captured: ingest/projection gap | no | positively observed in raw OTLP only |
| `vcs.repository.name` / product `repository_name` | Current 0/55 and older 0/18 for the expected key, while six alternate repository/branch/commit fields were present on 4/55 current and 1/18 older spans as listed above | not captured: key/projection mismatch | no | positively observed repository metadata exists, but the current product repository-name dependency is unusable |
| Skill identity | No structural Skill attribute or Skill span in 55/55 current or 18/18 older spans; Skill loading appeared only as an ordinary file-read tool call and names could occur only inside raw-bearing content | unsupported by source | raw substitution would be yes | unsupported; raw text may not be promoted to Skill evidence |
| available Skill inventory | No Skill configuration-inventory field in 55/55 current or 18/18 older spans | unsupported by source | no | unsupported; no Skill absence claim is possible |
| user-configured agent identity | `gen_ai.agent.name` occurred on 19/55 current and 10/18 older spans, but all observed values were internal component labels | unsupported by source for this semantic claim | no | unsupported for “which agent ran”; internal labels are exploratory only |
| delegated sub-agent task text | No dedicated field in 55/55 current or 18/18 older spans | unsupported by source as a structured field | yes if guessed from arguments | unsupported as evidence; any inference from raw arguments is exploratory and raw-bearing |
| skill file content | 0/55 current and 0/18 older structured Skill fields; a file read can carry content without identifying it as a Skill | unsupported by source as Skill evidence | yes | unsupported for Skill identity, invocation, priority, or cohort membership |

### VS Code absence boundary

No VS Code field in these captures supports a closed-world absence statement.
Hook spans, tool spans, model usage, and content were positively observed, but
the capture contains no active configuration inventory that proves every
possible event would have been emitted and captured. Therefore:

- “no hook/tool/error/sub-agent event was observed” is permitted when accurate;
- “no hook/tool/error/sub-agent event happened” is not permitted;
- all Skill claims, including positive Skill identity and negative Skill
  absence, are unsupported.

VS Code emitted raw conversation content even though `captureContent` was not
enabled. The content-capture gate is therefore unverified for this source. The
arrival of content cannot be used as proof that the gate works, and the absence
of content cannot be attributed confidently to that setting.

## Normalized source-truth summary

This table is the shortest mechanical check for a screen that uses product-level
concepts rather than vendor keys.

| Product-level dependency | `github-copilot-cli` | `github-copilot-vscode` |
| --- | --- | --- |
| Source identity | positively observed from `service.name` on 4/4 traces; `client.kind` NULL on 4/4 due issue #151 | positively observed from `service.name` on 24/24 traces; `client.kind` NULL on 24/24 due issue #151 |
| Source version | positively observed per trace, with versions 1.0.74 and 1.0.75 inside one capture | current version not observed; older capture known as 0.54.0 |
| Native conversation/session reference | exact identity: conversation ID 14/14, 4 distinct | exact identity: conversation/chat session IDs 47/55, 4 distinct in the current capture |
| User turn | unsupported as a trace equivalence; producer turn fields are only positively observed | unsupported as a trace equivalence; one question produced 7 traces |
| Request model | positively observed on 11/14; uncertified | positively observed on 19/55 current and 14/18 older; uncertified |
| Response model | positively observed on 7/14; uncertified | positively observed on 19/55 current and 10/18 older; uncertified |
| Captured token values | positively observed and projected on 11/14 for input, output, total, and cache-read | positively observed raw on 19/55 for input, output, and cache-read, but current projections are 0/55 due issue #150 |
| Tool activity | positively observed on 3/14 tool-name spans | positively observed on 8/55 current and 4/18 older tool-name spans |
| Hook activity | no hook-specific row was observed in this CLI extraction | positively observed: 28/55 current hook spans across five hook types; hooks are not CLI-only |
| Skill identity/invocation | positively observed structurally; one dedicated invocation span and one tool Skill-name parameter | unsupported; ordinary file reads and raw text are not Skill signals |
| Available Skill inventory | conditionally complete for the 4 captured traces: inventory present on every trace | unsupported |
| User-configured agent identity | unsupported; agent ID is a producer default component and product `agent_name` is 0/14 | unsupported; 19/55 projected names are internal components, not user-configured agents |
| Repository identity through current product field | unavailable due key mismatch; no repository alternative observed in 14 spans | unavailable due key mismatch despite alternate repository metadata on 4/55 current and 1/18 older spans |
| Error absence | only “no error type observed” | only “no error type observed” |
| Raw prompt/response/system content | not observed in the enumerated CLI keys | observed on multiple VS Code fields, but raw-bearing and content gate unverified |

## The owner's four wants

| Want | `github-copilot-cli` | `github-copilot-vscode` | Permitted cross-source statement |
| --- | --- | --- | --- |
| Which Skills fired | Structural Skill name, source, trigger, tool parameter, and a dedicated invocation span were positively observed. The available Skill-name inventory was present on all 4 traces. With capture-health and per-trace version certification, this source can support a conditionally complete invoked set. | Unsupported. There was no Skill signal in 55 current or 18 older spans. An ordinary file read and raw content cannot be reclassified as a Skill invocation. | “Observed Skill invocations” may be shown for CLI only. Do not offer a cross-source Skill count. Negative CLI wording requires the stated closed-world conditions; VS Code gets “source does not provide Skill identity.” |
| How many sub-agents, and what they were asked | Four agent-invocation spans and `github.copilot.agent.type` on 4/14 spans were observed, but there is no dedicated delegated-task field and no certified complete sub-agent identity set. | Four current and one older agent-invocation spans were observed; agent type occurred on 4/55 current and 1/18 older spans. Names are internal components and delegated task text has no dedicated field. | At most “observed agent/sub-agent starts: N” when the producer semantics establish that the counted spans are starts. Never “the number of sub-agents.” What they were asked is unsupported as structured evidence; raw-argument guesses are exploratory and raw-bearing. |
| This run used abnormally more tokens than usual | Captured per-span token values are positively observed on 11/14 and projected, but the 4-trace sample supplies no valid usual-work cohort. | Raw token values are positively observed on 19/55, but projections are lost by issue #150. One question producing 7 traces also prevents treating trace count as turn count. No valid usual-work cohort exists. | v1 may show this run's captured figures only where the screen path has them. “Abnormal,” “more than usual,” cohort membership, and same-task comparison are unsupported by this catalogue; a reviewer-selected comparison would be exploratory until a declared population exists. |
| An AI that frankly says what to change | No field supplies a causal diagnosis or change recommendation. | No field supplies a causal diagnosis or change recommendation. | Any AI suggestion is exploratory: a reviewable hypothesis linked to exact evidence, never evidence itself, never a priority, and never proof that a configuration caused an outcome. |

## Coverage limits

This is thin, purpose-built evidence:

- CLI: 4 traces and 14 spans, under one capture configuration, at one point in
  time, with an auto-update from 1.0.74 to 1.0.75 during measurement.
- Current VS Code: 24 traces and 55 spans, under one capture configuration, at
  one point in time, with producer version not supplied.
- Older VS Code 0.54.0: 18 raw spans, but zero retained monitor traces and spans.
- One VS Code question produced 7 traces. Trace count is therefore not user
  turn count.
- Coverage ratios describe these spans only. They are not rates, reliability
  estimates, or prevalence estimates for either product.
- Distinct-value counts establish only observed cardinality. They do not
  establish a closed enumeration of possible values.
- One configuration does not test capture-off, capture-on, permissions,
  failures, retries, alternate repositories, alternate agents, or all model and
  tool paths.
- CLI per-version field distributions were not supplied, so a field observed in
  the combined capture cannot be attributed separately to 1.0.74 or 1.0.75.
- The current VS Code source version was not supplied, so current/0.54.0
  differences are observations across captures, not a version-transition claim.
- Raw VS Code content arriving without `captureContent` enabled leaves the
  content gate unverified. It is not evidence of a functioning privacy control.
- Except for the conditional CLI Skill inventory/invocation combination, this
  evidence is open-world. Missing fields mean “not observed” or “not captured,”
  not that the underlying action did not happen.

Four traces must never be presented as the CLI population, 24 traces must never
be presented as the VS Code population, and 55 spans must never be presented as
55 user turns.

## What this blocks and unblocks

### Issue #125 — source capability manifest promotion

This catalogue unblocks evidence-based review of manifest promotion by separating
source emission from product projection and by attaching measured coverage to
each observed field. It demonstrates that leaving all model/token/tool/agent
categories at a single undifferentiated `unknown` hides real source differences.

It does not itself promote any capability:

- model, token, and tool fields were observed but remain uncertified;
- CLI Skill identity and the available inventory were observed, but the current
  manifest schema has no Skill category;
- VS Code Skill capability is empirically unsupported in both supplied captures;
- neither source supports user-configured agent identity;
- source-version detection cannot be promoted globally from these captures,
  because CLI version is per trace and the current VS Code version is unknown;
- issue #150 and issue #151 are product ingestion/attribution defects, not source
  limitations and must not be encoded as source unavailability.

Promotion in #125 must therefore cite the applicable source, field, version
coverage, and claim layer from this document; it cannot copy one source's result
to the other.

### Issue #132 — information architecture

This catalogue unblocks an IA from using:

- exact native conversation/session and request/response/tool receipt references;
- positively observed model, tool, hook, timing, and token facts where the
  product path actually retains them;
- observed CLI Skill invocations, clearly source-scoped;
- the CLI available Skill inventory, with the closed-world conditions stated;
- distinct empty-state wording for unsupported, not observed, not captured, and
  observed-but-uncertified fields.

It blocks an IA from treating a source-side field as displayable when the
projection is NULL, from treating raw-bearing content as display data, from
equating traces with turns, or from wording open-world absence as “did not
happen.”

## v1 IA prohibitions

An IA reviewer must reject any v1 screen that depends on any of the following:

1. `client.kind` being populated for either source. It is NULL on 4/4 CLI traces
   and 24/24 VS Code traces; source attribution currently depends on
   `service.name`, and issue #151 remains an ingest/attribution defect.
2. one source version per capture. CLI 1.0.74 and 1.0.75 occurred in the same
   capture, and the current VS Code version is not supplied.
3. one trace equalling one user turn. One VS Code question produced 7 traces.
4. product `repository_name` being populated. Neither source emitted
   `vcs.repository.name`; VS Code's alternate repository fields are not consumed
   by that projection.
5. product `agent_name` answering “which user-configured agent ran.” CLI is
   0/14 because it sends `gen_ai.agent.id`, not `gen_ai.agent.name`; VS Code's
   19/55 names are internal components.
6. a cross-source Skill view. CLI carries structural Skill identity and an
   available inventory; VS Code carries no Skill signal and raw file-read content
   may not be substituted.
7. a VS Code Skill positive claim, Skill count, configured-Skill inventory, or
   Skill absence claim.
8. “Skill X did not run” for CLI unless the same run has its available inventory,
   healthy invocation capture, and per-trace version coverage. Otherwise the
   only permitted wording is “no invocation of Skill X was observed.”
9. “all tools,” “all hooks,” “all sub-agents,” “no errors,” or any other complete
   or negative set claim from the open-world rows.
10. a count labelled “number of sub-agents,” or structured text labelled “what
    the sub-agent was asked.” Only observed invocation spans exist; delegated
    task text has no dedicated field.
11. VS Code projected token fields. Raw token usage arrived on 19/55 spans, but
    input, output, total, cache-read, and reasoning projections are all 0/55
    because of issue #150.
12. a missing numeric field being rendered as zero. This includes token,
    reasoning, error, finish-reason, cost, and timing fields.
13. projected finish reasons for either source. Raw finish reasons arrived on
    11/14 CLI spans and 15/55 current VS Code spans, while both projections are
    0.
14. raw prompts, responses, input/output messages, reasoning content, system
    instructions, hook payloads, tool definitions/descriptions,
    tool arguments/results, commands, file paths, request payloads, or skill-file
    content as ordinary display data.
15. `captureContent` being a verified VS Code content gate. Raw conversation
    content arrived without it enabled.
16. “abnormally more tokens,” “more than usual,” or an inferred cohort from
    these captures. The samples establish no population or recurring-practice
    baseline.
17. an AI recommendation being evidence, priority, cohort membership, or a
    causal conclusion. It can only be an exploratory hypothesis for review.
18. any field or semantic dependency with no exact matching row in this
    catalogue.
