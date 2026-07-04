# Dashboard Dataset Contract

## Purpose

This contract defines the Sprint4 M2 dashboard dataset shape for a Grafana-first prototype and a static report.
M2 defines schema only.
It does not implement a dataset generator, Grafana JSON dashboard, synthetic fixture, live Langfuse export, external ETL, or repository-local preview.

The dataset is split into four logical tables instead of one wide table:

| Logical table | Purpose |
| --- | --- |
| `dashboard_run_summary` | Trace / run level trend, latency, token, cost, status, and drilldown reference |
| `dashboard_operation_summary` | Tool / LLM / permission / subagent operation counts, duration, error, retry, and wait metrics |
| `dashboard_candidate_summary` | Diagnosis / improvement / auto-decision / human-review backlog status distribution |
| `dashboard_collection_health` | Required attribute gaps, unknown span / attribute counts, normalization / mapping / candidate generation failures |

CSV output uses one file per logical table.
JSON output uses one object with one array per logical table:

```json
{
  "schema_version": "sprint4-m2-v1",
  "generated_at_utc": "2026-06-19T00:00:00Z",
  "time_bucket_granularity": "day",
  "parameters": {
    "long_running_trace_threshold_ms": 600000,
    "long_running_turn_threshold_ms": 300000,
    "long_running_tool_threshold_ms": 120000,
    "stuck_session_threshold_ms": 900000
  },
  "dashboard_run_summary": [],
  "dashboard_operation_summary": [],
  "dashboard_candidate_summary": [],
  "dashboard_collection_health": []
}
```

## Defaults

| Field | Default |
| --- | --- |
| `time_bucket_granularity` | `day`; prototype validation may also use `hour` or `week` |
| `time_bucket_start_utc` | ISO-8601 UTC bucket start |
| `long_running_trace_threshold_ms` | 600000 |
| `long_running_turn_threshold_ms` | 300000 |
| `long_running_tool_threshold_ms` | 120000 |
| `stuck_session_threshold_ms` | 900000 |
| `client_kind` | Reserved values: `vscode-copilot-chat`, `copilot-cli`, `codex-app`, `unknown` |
| `ttft_ms` | Nullable; use direct attribute when available, otherwise derived first generation event / span when available |
| `ttft_source` | `direct-attribute`, `derived-first-generation-event`, `derived-first-generation-span`, or `unavailable` |
| `estimated_cost` | Nullable decimal; observability estimate only, not actual Copilot billing |
| `cost_source` | `unit-price-table`, `unavailable-unit-price`, or `not-calculated` |

`codex-app` is reserved for optional Codex App / app-server telemetry.
Sprint4 M2 / M3 does not require Codex App fixture coverage.

## Security Boundary

Dashboard datasets are aggregate / sanitized datasets.
They must not contain:

- Raw prompt, response, system prompt, tool arguments, or tool results.
- Source code fragments, file contents, or sensitive evidence fragments.
- Credential, secret, token, API key, password, or Base64 authorization header.
- Real `user.id`, `user.email`, or personal identity mapping.
- Sensitive bundle content or direct local sensitive bundle paths.

`team.id` and `department` are collected Resource Attributes in the project requirements, but they are not part of the M2 required dashboard schema.
If a future shared dashboard displays team or department dimensions, retention, access control, masking / redaction, and user communication must be specified first.

Allowed drilldown references are:

- `trace_id`, `langfuse_trace_id`, `measurement_record_ref`.
- `diagnosis_candidate_id`, `improvement_candidate_id`, `auto_decision_id`, `proposal_id`.
- Sanitized `evidence_ref`.
- Boolean `sensitive_bundle_present`.

If sensitive bundle material exists, the dashboard dataset records only `sensitive_bundle_present=true` and a sanitized `evidence_ref`.

## Source Mapping

| Source | Directly usable fields |
| --- | --- |
| Normalized measurement | `trace_id`, `experiment_id`, `client_kind`, `task_id`, `task_category`, `task_run_index`, `experiment_condition`, `prompt_version`, `agent_variant`, `repo_snapshot`, token counts, `turn_count`, `tool_call_count`, `duration_ms`, `error_count`, `success_status`, `unknown_spans_json`, `unknown_attributes_json` |
| Diagnosis candidate output | `diagnosis_candidate_id`, `trace_id`, `source_record_ref`, `rule_id`, `failure_category_id`, `anti_pattern_id`, `severity`, `recommended_improvement_target`, `evidence_ref`, `content_included`, `sensitive_bundle_path`, `confidence`, `candidate_status` |
| Improvement candidate output | `improvement_candidate_id`, `source_diagnosis_candidate_id`, `trace_id`, `failure_category_id`, `anti_pattern_id`, `severity`, `improvement_target`, `proposed_change_kind`, `evidence_ref`, `sensitive_bundle_path`, `candidate_status` |
| Auto-decision output | `auto_decision_id`, `source_improvement_candidate_id`, `source_diagnosis_candidate_id`, `trace_id`, `decision_status`, `decision_rule_id`, `confidence`, `sensitive_content_included`, `sensitive_bundle_path`, `implementation_target`, `next_action` |
| M24-M27 human-review records | `proposal_id`, `proposal_evaluation_status`, `human_review_status`, `human_decision`, `approved_at`, context fields carried from proposal / evaluation records |

M3 or later generator / fixture work must add or derive:

- TTFT fallback fields.
- Tool total duration, tool error count, retry count.
- Approval wait, permission result, subagent / nested agent wait.
- Long-running / stuck flags.
- Estimated cost from a unit price table.
- Candidate backlog age.

Future-only fields include GitHub / Notion / issue / PR outcome linkage.
Outcome Linkage priority will be tiered in M5 handoff and is not part of the required M2 schema.

## `dashboard_run_summary`

CSV header:

```text
schema_version,time_bucket_start_utc,time_bucket_granularity,trace_id,langfuse_trace_id,measurement_record_ref,client_kind,experiment_id,experiment_condition,task_id,task_category,task_run_index,prompt_version,agent_variant,skill_version,mcp_profile,repo_snapshot,model,status,success_status,duration_ms,ttft_ms,ttft_source,input_tokens,output_tokens,total_tokens,turn_count,llm_call_count,tool_call_count,error_count,estimated_cost,cost_source,long_running_trace,stuck_session,sensitive_bundle_present,drilldown_ref
```

JSON object shape:

```json
{
  "schema_version": "sprint4-m2-v1",
  "time_bucket_start_utc": "2026-06-19T00:00:00Z",
  "time_bucket_granularity": "day",
  "trace_id": "trace-001",
  "langfuse_trace_id": "trace-001",
  "measurement_record_ref": "measurement:trace-001",
  "client_kind": "copilot-cli",
  "experiment_id": "baseline",
  "status": "success",
  "duration_ms": 120000,
  "ttft_ms": null,
  "ttft_source": "unavailable",
  "estimated_cost": null,
  "cost_source": "unavailable-unit-price",
  "drilldown_ref": "trace:trace-001"
}
```

| Column | Type | Nullable | Source | PII / sensitive | Views / panels |
| --- | --- | --- | --- | --- | --- |
| `schema_version` | string | no | generator constant | no | all |
| `time_bucket_start_utc` | datetime | no | derived from trace timestamp | no | Run Overview, Baseline vs Variant |
| `time_bucket_granularity` | enum | no | generation parameter | no | all time series |
| `trace_id` | string | yes | normalized measurement | sanitized ref | drilldown |
| `langfuse_trace_id` | string | yes | normalized measurement / Langfuse export | sanitized ref | drilldown |
| `measurement_record_ref` | string | yes | derived source ref | sanitized ref | drilldown |
| `client_kind` | enum | yes | normalized measurement | no | all |
| `experiment_id` | string | yes | normalized measurement | no | Run Overview, Baseline vs Variant |
| `experiment_condition` | string | yes | normalized measurement | no | Baseline vs Variant |
| `task_id` | string | yes | normalized measurement | no | Baseline vs Variant |
| `task_category` | string | yes | normalized measurement | no | Run Overview |
| `task_run_index` | integer | yes | normalized measurement | no | Baseline vs Variant |
| `prompt_version` | string | yes | normalized measurement | no | Prompt / Skill / Instructions |
| `agent_variant` | string | yes | normalized measurement | no | Prompt / Skill / Instructions |
| `skill_version` | string | yes | generation context or blank | no | Prompt / Skill / Instructions |
| `mcp_profile` | string | yes | generation context or blank | no | Agent / Tool Behavior |
| `repo_snapshot` | string | yes | normalized measurement | no | Baseline vs Variant |
| `model` | string | yes | raw span / Langfuse observation when available | no | Token and cost trend |
| `status` | enum | no | derived from error / excluded / success | no | Run volume and status |
| `success_status` | enum | no | normalized measurement | no | Baseline vs Variant |
| `duration_ms` | integer | yes | normalized measurement | no | Latency distribution |
| `ttft_ms` | integer | yes | raw span attribute or derived fallback | no | Latency distribution |
| `ttft_source` | enum | no | derived | no | Collection Health |
| `input_tokens` | integer | yes | normalized measurement | no | Token and cost trend |
| `output_tokens` | integer | yes | normalized measurement | no | Token and cost trend |
| `total_tokens` | integer | yes | normalized measurement | no | Token and cost trend |
| `turn_count` | integer | yes | normalized measurement | no | Prompt / Skill / Instructions |
| `llm_call_count` | integer | yes | raw span / M3 fixture | no | Run Overview |
| `tool_call_count` | integer | yes | normalized measurement | no | Run Overview |
| `error_count` | integer | yes | normalized measurement | no | Run volume and status |
| `estimated_cost` | decimal | yes | derived unit price table | no | Token and cost trend |
| `cost_source` | enum | no | derived | no | Collection Health |
| `long_running_trace` | boolean | no | derived threshold | no | Stuck and long-running runs |
| `stuck_session` | boolean | no | derived threshold | no | Stuck and long-running runs |
| `sensitive_bundle_present` | boolean | no | candidate outputs | no | drilldown |
| `drilldown_ref` | string | yes | derived sanitized ref | sanitized ref | drilldown |

## `dashboard_operation_summary`

CSV header:

```text
schema_version,time_bucket_start_utc,time_bucket_granularity,trace_id,client_kind,experiment_id,experiment_condition,task_id,repo_snapshot,operation_kind,tool_name,model,status,call_count,error_count,timeout_count,retry_count,total_duration_ms,p50_duration_ms,p95_duration_ms,approval_wait_ms,permission_result,subagent_call_count,nested_agent_call_count,long_running_tool,sensitive_bundle_present,drilldown_ref
```

| Column | Type | Nullable | Source | PII / sensitive | Views / panels |
| --- | --- | --- | --- | --- | --- |
| Common context columns | mixed | varies | same as `dashboard_run_summary` | no / sanitized ref | all |
| `operation_kind` | enum | no | normalized / raw span classification | no | Agent / Tool Behavior |
| `tool_name` | string | yes | normalized / raw span classification | no | Top tools |
| `model` | string | yes | raw span / Langfuse observation | no | LLM operation panels |
| `status` | enum | no | derived | no | Tool reliability |
| `call_count` | integer | no | normalized measurement / raw span | no | Top tools by count |
| `error_count` | integer | no | normalized measurement / raw span | no | Tool reliability |
| `timeout_count` | integer | no | M3 fixture / raw span | no | Tool reliability |
| `retry_count` | integer | yes | M3 fixture / diagnosis candidate | no | Tool reliability |
| `total_duration_ms` | integer | yes | M3 fixture / raw span | no | Top tools by total duration |
| `p50_duration_ms` | integer | yes | M3 fixture / raw span | no | Top tools by total duration |
| `p95_duration_ms` | integer | yes | M3 fixture / raw span | no | Top tools by total duration |
| `approval_wait_ms` | integer | yes | M3 fixture / raw span | no | Subagent and approval waits |
| `permission_result` | enum | yes | raw span / event | no | Tool reliability |
| `subagent_call_count` | integer | yes | M3 fixture / raw span | no | Subagent and approval waits |
| `nested_agent_call_count` | integer | yes | M3 fixture / raw span | no | Subagent and approval waits |
| `long_running_tool` | boolean | no | derived threshold | no | Stuck and long-running runs |
| `sensitive_bundle_present` | boolean | no | candidate outputs | no | drilldown |
| `drilldown_ref` | string | yes | derived sanitized ref | sanitized ref | drilldown |

JSON rows use the same property names as the CSV header.

## `dashboard_candidate_summary`

CSV header:

```text
schema_version,time_bucket_start_utc,time_bucket_granularity,trace_id,client_kind,experiment_id,experiment_condition,task_id,repo_snapshot,candidate_kind,diagnosis_candidate_id,improvement_candidate_id,auto_decision_id,proposal_id,candidate_rule,failure_category_id,anti_pattern_id,candidate_severity,improvement_target,proposed_change_kind,candidate_status,decision_status,review_status,human_decision,backlog_age_hours,evidence_ref,sensitive_bundle_present,drilldown_ref
```

| Column | Type | Nullable | Source | PII / sensitive | Views / panels |
| --- | --- | --- | --- | --- | --- |
| Common context columns | mixed | varies | normalized measurement join | no / sanitized ref | all |
| `candidate_kind` | enum | no | derived source type | no | Candidate distribution |
| `diagnosis_candidate_id` | string | yes | diagnosis candidate | sanitized ref | drilldown |
| `improvement_candidate_id` | string | yes | improvement candidate | sanitized ref | drilldown |
| `auto_decision_id` | string | yes | auto-decision output | sanitized ref | drilldown |
| `proposal_id` | string | yes | M24-M27 records | sanitized ref | Human review queue |
| `candidate_rule` | string | yes | candidate / decision rule | no | Candidate distribution |
| `failure_category_id` | string | yes | candidate / diagnosis record | no | Candidate distribution |
| `anti_pattern_id` | string | yes | candidate / diagnosis record | no | Candidate distribution |
| `candidate_severity` | enum | yes | candidate / diagnosis record | no | Candidate distribution |
| `improvement_target` | string | yes | candidate / proposal | no | Human review queue |
| `proposed_change_kind` | string | yes | improvement candidate | no | Human review queue |
| `candidate_status` | enum | yes | candidate output | no | Candidate distribution |
| `decision_status` | enum | yes | auto-decision output | no | Candidate distribution |
| `review_status` | enum | yes | M24-M27 / adapter output | no | Human review queue |
| `human_decision` | enum | yes | M27 decision record | no | Human review queue |
| `backlog_age_hours` | decimal | yes | M3+ derived from generated / approved timestamps | no | Human review queue |
| `evidence_ref` | string | yes | candidate output | sanitized ref | drilldown |
| `sensitive_bundle_present` | boolean | no | candidate outputs | no | drilldown |
| `drilldown_ref` | string | yes | derived sanitized ref | sanitized ref | drilldown |

JSON rows use the same property names as the CSV header.

## `dashboard_collection_health`

CSV header:

```text
schema_version,time_bucket_start_utc,time_bucket_granularity,input_ref,trace_id,client_kind,experiment_id,health_check_kind,health_status,missing_attribute_name,unknown_span_count,unknown_attribute_count,normalization_failure_count,mapping_failure_count,candidate_generation_failure_count,affected_record_count,details_ref
```

| Column | Type | Nullable | Source | PII / sensitive | Views / panels |
| --- | --- | --- | --- | --- | --- |
| `schema_version` | string | no | generator constant | no | Collection Health |
| `time_bucket_start_utc` | datetime | no | derived | no | Collection Health |
| `time_bucket_granularity` | enum | no | generation parameter | no | Collection Health |
| `input_ref` | string | yes | input file / batch ref | sanitized ref | Collection Health |
| `trace_id` | string | yes | normalized measurement | sanitized ref | drilldown |
| `client_kind` | enum | yes | normalized measurement | no | Collection Health |
| `experiment_id` | string | yes | normalized measurement | no | Collection Health |
| `health_check_kind` | enum | no | derived | no | Collection Health |
| `health_status` | enum | no | derived | no | Collection Health |
| `missing_attribute_name` | string | yes | validation result | no | Attribute completeness |
| `unknown_span_count` | integer | yes | normalized measurement `unknown_spans_json` | no | Normalization and mapping health |
| `unknown_attribute_count` | integer | yes | normalized measurement `unknown_attributes_json` | no | Normalization and mapping health |
| `normalization_failure_count` | integer | yes | M3+ generator | no | Normalization and mapping health |
| `mapping_failure_count` | integer | yes | M3+ generator | no | Normalization and mapping health |
| `candidate_generation_failure_count` | integer | yes | M3+ generator | no | Normalization and mapping health |
| `affected_record_count` | integer | no | derived | no | Collection Health |
| `details_ref` | string | yes | sanitized source ref | sanitized ref | drilldown |

JSON rows use the same property names as the CSV header.

## View / Panel Mapping

| View / panel | Logical table |
| --- | --- |
| Run Overview / Run volume and status | `dashboard_run_summary` |
| Run Overview / Latency distribution | `dashboard_run_summary` |
| Run Overview / Token and cost trend | `dashboard_run_summary` |
| Run Overview / Stuck and long-running runs | `dashboard_run_summary`, `dashboard_operation_summary` |
| Agent / Tool Behavior / Top tools by count | `dashboard_operation_summary` |
| Agent / Tool Behavior / Top tools by total duration | `dashboard_operation_summary` |
| Agent / Tool Behavior / Tool reliability | `dashboard_operation_summary` |
| Agent / Tool Behavior / Subagent and approval waits | `dashboard_operation_summary` |
| Prompt / Skill / Instructions / Variant cost and token impact | `dashboard_run_summary` |
| Prompt / Skill / Instructions / Variant failure and candidate impact | `dashboard_run_summary`, `dashboard_candidate_summary` |
| Baseline vs Variant / Matched task comparison | `dashboard_run_summary` |
| Baseline vs Variant / Regression candidate list | `dashboard_run_summary`, `dashboard_candidate_summary` |
| Diagnosis / Improvement Loop / Candidate distribution | `dashboard_candidate_summary` |
| Diagnosis / Improvement Loop / Human review queue | `dashboard_candidate_summary` |
| Collection Health / Attribute completeness | `dashboard_collection_health` |
| Collection Health / Normalization and mapping health | `dashboard_collection_health` |
| Outcome Linkage Candidate / External outcome placeholders | future-only; no M2 required table |

## M3 Handoff

M3 synthetic dashboard data should validate:

- The four logical tables can be populated from synthetic normalized measurement and candidate outputs.
- Nullable TTFT, cost, operation timing, and backlog age fields do not block dashboard generation.
- Required schema fields exclude raw content, credentials, Base64 authorization headers, and real identity values.
- `codex-app` remains a reserved optional `client_kind`, not a required fixture source.
- Outcome Linkage remains a placeholder until M5 handoff tiers future work.
