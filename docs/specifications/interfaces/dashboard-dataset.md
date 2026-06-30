# Dashboard Dataset Interface

Dashboard dataset output is the contract between normalized telemetry / candidate records and the static dashboard.

## Root Object

JSON output uses one object with four logical table arrays:

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

CSV output uses one file per logical table.

`schema_version` currently remains `sprint4-m2-v1` for wire compatibility with the existing CLI and static dashboard.
Treat this as a schema identifier, not product positioning or active sprint planning.
Do not rename it without a schema migration, fixture update, and dashboard compatibility check.

## Logical Tables

| Logical table | Purpose |
| --- | --- |
| `dashboard_run_summary` | Trace / run level trend, latency, token, cost, status, and drilldown reference |
| `dashboard_operation_summary` | Tool / LLM / permission / subagent operation counts, duration, error, retry, and wait metrics |
| `dashboard_candidate_summary` | Diagnosis / improvement / auto-decision / human-review backlog status distribution |
| `dashboard_collection_health` | Required attribute gaps, unknown span / attribute counts, normalization / mapping / candidate generation failures |

## Source Mapping

| Source | Directly usable fields |
| --- | --- |
| [Normalized measurement](measurement-dataset.md) | `trace_id`, `experiment_id`, `client_kind`, `task_id`, `task_category`, `task_run_index`, `experiment_condition`, `prompt_version`, `agent_variant`, `repo_snapshot`, token counts, `turn_count`, `tool_call_count`, `duration_ms`, `error_count`, `success_status`, `unknown_spans_json`, `unknown_attributes_json` |
| [Diagnosis candidate output](candidate-records.md) | `diagnosis_candidate_id`, `trace_id`, `rule_id`, `failure_category_id`, `anti_pattern_id`, `severity`, `recommended_improvement_target`, `evidence_ref`, `content_included`, `confidence`, `candidate_status` |
| [Improvement candidate output](candidate-records.md) | `improvement_candidate_id`, `source_diagnosis_candidate_id`, `trace_id`, `failure_category_id`, `anti_pattern_id`, `severity`, `improvement_target`, `proposed_change_kind`, `evidence_ref`, `candidate_status` |
| [Auto-decision output](candidate-records.md) | `auto_decision_id`, `source_improvement_candidate_id`, `source_diagnosis_candidate_id`, `trace_id`, `decision_status`, `decision_rule_id`, `confidence`, `implementation_target`, `next_action` |
| [Human-review records](human-review-records.md) | `proposal_id`, `proposal_evaluation_status`, `human_review_status`, `human_decision`, `approved_at`, context carried from proposal / evaluation records |

## Defaults

| Field | Default |
| --- | --- |
| `time_bucket_granularity` | `day`; validation may also use `hour` or `week` |
| `time_bucket_start_utc` | ISO-8601 UTC bucket start |
| `long_running_trace_threshold_ms` | 600000 |
| `long_running_turn_threshold_ms` | 300000 |
| `long_running_tool_threshold_ms` | 120000 |
| `stuck_session_threshold_ms` | 900000 |
| `client_kind` | `vscode-copilot-chat`, `copilot-cli`, `codex-app`, `unknown` |
| `ttft_source` | `direct-attribute`, `derived-first-generation-event`, `derived-first-generation-span`, or `unavailable` |
| `cost_source` | `unit-price-table`, `unavailable-unit-price`, or `not-calculated` |

## Table Columns

### `dashboard_run_summary`

```text
schema_version,time_bucket_start_utc,time_bucket_granularity,trace_id,langfuse_trace_id,measurement_record_ref,user_id,user_email,client_kind,experiment_id,experiment_condition,task_id,task_category,task_run_index,prompt_version,agent_variant,skill_version,mcp_profile,repo_snapshot,model,status,success_status,duration_ms,ttft_ms,ttft_source,input_tokens,output_tokens,total_tokens,turn_count,llm_call_count,tool_call_count,error_count,estimated_cost,cost_source,long_running_trace,stuck_session,sensitive_bundle_present,drilldown_ref
```

Purpose:

- run volume and status。
- latency and TTFT。
- token and estimated cost trend。
- baseline / variant comparison。
- sanitized drilldown reference。

### `dashboard_operation_summary`

```text
schema_version,time_bucket_start_utc,time_bucket_granularity,trace_id,user_id,user_email,client_kind,experiment_id,experiment_condition,task_id,repo_snapshot,operation_kind,tool_name,model,status,call_count,error_count,timeout_count,retry_count,total_duration_ms,p50_duration_ms,p95_duration_ms,approval_wait_ms,permission_result,subagent_call_count,nested_agent_call_count,long_running_tool,sensitive_bundle_present,drilldown_ref
```

Purpose:

- top tools by count and duration。
- tool / LLM / permission reliability。
- retry、timeout、approval wait、subagent wait。

### `dashboard_candidate_summary`

```text
schema_version,time_bucket_start_utc,time_bucket_granularity,trace_id,user_id,user_email,client_kind,experiment_id,experiment_condition,task_id,repo_snapshot,candidate_kind,diagnosis_candidate_id,improvement_candidate_id,auto_decision_id,proposal_id,candidate_rule,failure_category_id,anti_pattern_id,candidate_severity,improvement_target,proposed_change_kind,candidate_status,decision_status,review_status,human_decision,backlog_age_hours,evidence_ref,sensitive_bundle_present,drilldown_ref
```

Purpose:

- diagnosis / improvement / auto-decision distribution。
- human review queue。
- candidate severity and rule trend。
- sanitized evidence reference。

`evidence_ref` is sanitized when candidate rows are imported into the dashboard dataset. Obvious local filesystem paths (drive-letter absolute paths, UNC paths, Unix absolute paths, and `file:` URIs) are rejected and stored as empty, so a sensitive bundle path (or any other local path) placed in `evidence_ref` cannot leak into the repository-safe dataset. Scheme-bearing refs (`measurement:`, `raw:`, `bundle:`) and relative row refs are preserved. Local-only sensitive bundle paths belong in `sensitive_bundle_path` (local-only, never committed), not `evidence_ref`.

### `dashboard_collection_health`

```text
schema_version,time_bucket_start_utc,time_bucket_granularity,input_ref,trace_id,user_id,user_email,client_kind,experiment_id,health_check_kind,health_status,missing_attribute_name,unknown_span_count,unknown_attribute_count,normalization_failure_count,mapping_failure_count,candidate_generation_failure_count,affected_record_count,details_ref
```

Purpose:

- required attribute gaps。
- unknown span / attribute counts。
- normalization, mapping, and candidate generation failures。

Required-attribute gaps in this repository-safe table are reported only for `trace_id`, `client_kind`, and `experiment_id`. `user_id` / `user_email` columns are display / filter context (see Allowed Identity Fields) and are not validated as required attributes here; `team.id` / `department` are not columns in this table and are not validated as required in repository-safe outputs. PII / organization attribute collection health is observable only on the local monitor side.

## Allowed Identity Fields

Static dashboard may include:

- `user_id`
- `user_email`

These are display and filter fields.
Before shared publishing, confirm access control and user communication.

## Forbidden Fields And Values

Dashboard dataset and static dashboard artifacts must not contain:

- raw prompt / response / system prompt。
- tool arguments / tool results。
- source code fragments or file contents from observed sessions。
- credentials, secrets, tokens, API keys, passwords。
- Base64 authorization headers。
- sensitive bundle content。
- sensitive bundle local paths。

## View Mapping

| View | Tables |
| --- | --- |
| Run Overview | `dashboard_run_summary` |
| Agent / Tool Behavior | `dashboard_operation_summary` |
| Prompt / Skill / Instructions | `dashboard_run_summary`, `dashboard_candidate_summary` |
| Baseline vs Variant | `dashboard_run_summary`, `dashboard_candidate_summary` |
| Diagnosis / Improvement Loop | `dashboard_candidate_summary` |
| Collection Health | `dashboard_collection_health` |
| Outcome Linkage Candidate | future-only placeholder |
