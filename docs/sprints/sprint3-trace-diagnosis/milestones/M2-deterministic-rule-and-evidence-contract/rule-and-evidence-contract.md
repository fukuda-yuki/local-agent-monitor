# Rule and Evidence Contract

This document finalizes the Sprint3 M2 deterministic rule, content evidence, sensitive bundle, and M24-M27 adapter contract.

M2 does not change code behavior. M3-M5 must treat this document as the implementation contract unless `docs/requirements.md` or `docs/spec.md` is updated with a different product decision.

## Source Documents

- Sprint3 overview: [../../README.md](../../README.md)
- M1 command boundary: [../M1-candidate-schema-and-command-boundary/command-boundary.md](../M1-candidate-schema-and-command-boundary/command-boundary.md)
- M2 task: [task.md](task.md)

## Inputs

Diagnosis candidate generation may read these input layers:

| Layer | Required | Purpose |
| --- | --- | --- |
| normalized measurement CSV / JSON | yes | trace metadata, aggregate metrics, and `success_status` |
| raw store SQLite DB | no | raw OTLP payload lookup by trace id |
| raw OTLP JSON | no | raw span, event, attribute, and content lookup by trace id |
| sensitive bundle | no | opt-in content evidence created by Sprint3 commands |

Raw content is never written to standard CSV / JSON output. When raw content is included by explicit opt-in, it is written only to the sensitive bundle.

## Diagnosis Rule Set

M3 must implement only the rules listed here unless M2 is amended.
These five rules are the Sprint3 initial implementation set, not the final long-term rule inventory.
Additional rules such as duration thresholds, token-volume thresholds, unknown-span counts, repeated same-tool calls, or `needs-review`-specific rules are future candidates, likely Sprint4 or later.

| rule_id | Inputs | Deterministic condition | Output |
| --- | --- | --- | --- |
| `DIAG-METRIC-ERROR-COUNT-V1` | normalized measurement | `error_count` is present and `error_count > 0` | `failure_category_id=F-ERROR`, empty `anti_pattern_id`, `severity=major`, `recommended_improvement_target=workflow`, `confidence=high`, `candidate_status=auto-eligible` |
| `DIAG-METRIC-TOOL-LOOP-V1` | normalized measurement | `tool_call_count` is present and `tool_call_count >= 10`, and `success_status` is not exactly `pass` | `failure_category_id=F-TOOL`, `anti_pattern_id=AP-TOOL-LOOP`, `severity=major`, `recommended_improvement_target=workflow`, `confidence=medium`, `candidate_status=candidate` |
| `DIAG-CONTENT-ERROR-MESSAGE-V1` | raw span / raw event | at least one error field predicate or error text pattern in this document matches | `failure_category_id=F-ERROR`, `anti_pattern_id=AP-ERROR-BLIND`, `severity=major`, `recommended_improvement_target=workflow`, `confidence=medium`, `candidate_status=candidate` |
| `DIAG-CONTENT-SENSITIVE-LEAK-V1` | raw attribute / raw event / raw content fragment | at least one sensitive key predicate, sensitive text regex, or Base64 credential predicate in this document matches | `failure_category_id=F-DATA`, `anti_pattern_id=AP-RAW-CONTENT`, `severity=blocking`, `recommended_improvement_target=workflow`, `confidence=high`, `candidate_status=blocked` |
| `DIAG-METADATA-MISSING-TRACE-CONTEXT-V1` | normalized measurement | `trace_id` is blank, or `client_kind` is blank, or `experiment_id` is blank | `failure_category_id=F-MEASURE`, `anti_pattern_id=AP-SCHEMA-DRIFT`, `severity=major`, `recommended_improvement_target=eval`, `confidence=high`, `candidate_status=auto-eligible` |

If more than one rule matches the same trace, each rule produces its own diagnosis candidate. Candidate ids are assigned in output order after stable ordering by input row order, then rule table order.

## Content-Aware Read Scope

Content-aware rules may inspect only records associated with the candidate trace id.

For raw OTLP JSON, allowed source paths are:

| Path | Use |
| --- | --- |
| `resourceSpans[*].resource.attributes[*]` | resource metadata and sensitive key / value detection |
| `resourceSpans[*].scopeSpans[*].spans[*].traceId` | trace association |
| `resourceSpans[*].scopeSpans[*].spans[*].spanId` | `evidence_ref` and source locator |
| `resourceSpans[*].scopeSpans[*].spans[*].name` | span classification and error text detection |
| `resourceSpans[*].scopeSpans[*].spans[*].status.code` | error status detection |
| `resourceSpans[*].scopeSpans[*].spans[*].status.message` | error text detection |
| `resourceSpans[*].scopeSpans[*].spans[*].attributes[*]` | tool, prompt, response, argument, result, error, and sensitive predicate detection |
| `resourceSpans[*].scopeSpans[*].spans[*].events[*].name` | error / exception event detection |
| `resourceSpans[*].scopeSpans[*].spans[*].events[*].attributes[*]` | error and sensitive predicate detection |

For raw store SQLite input, the command reads the stored raw payload for matching `trace_id` and applies the same raw OTLP source-path rules after parsing the payload as JSON.

Content-aware rules must not call an LLM, must not query live Langfuse, and must not infer meaning beyond the literal predicates below.

## Error Detection Contract

`DIAG-CONTENT-ERROR-MESSAGE-V1` matches when any of the following predicates is true.

### Error Field Predicates

| Predicate | Match |
| --- | --- |
| span `status.code` | equals `2`, `ERROR`, or `STATUS_CODE_ERROR`, case-insensitive for string values |
| span attribute key `level` | string value equals `error`, case-insensitive |
| span attribute key `status` | string value equals `error`, case-insensitive |
| span attribute key `error` | key exists and value is non-empty |
| event name | contains `exception` or `error`, case-insensitive |
| event attribute key `exception.type` | key exists and value is non-empty |
| event attribute key `error` | key exists and value is non-empty |
| event attribute key `level` | string value equals `error`, case-insensitive |

### Error Text Patterns

These regexes are applied to string values from span names, span status messages, span attributes, event names, and event attributes:

| Pattern id | Regex |
| --- | --- |
| `ERR-TIMEOUT` | `(?i)\b(timeout|timed out|deadline exceeded)\b` |
| `ERR-PERMISSION` | `(?i)\b(permission denied|unauthorized|forbidden|access denied)\b` |
| `ERR-EXCEPTION` | `(?i)\b(exception|stack trace|traceback)\b` |
| `ERR-FAILED` | `(?i)\b(failed|failure|exit code [1-9][0-9]*)\b` |

The word `error` by itself is not a text-pattern match unless it appears in one of the field predicates above. This avoids turning every safe error-count summary into content-aware evidence.

## Sensitive Content Detection Contract

`DIAG-CONTENT-SENSITIVE-LEAK-V1` matches when any of the following predicates is true.

### Sensitive Key Predicates

Normalize keys by lowercasing them and replacing `_` with `.`. A key is sensitive when it matches any predicate below:

| Predicate id | Match |
| --- | --- |
| `KEY-CREDENTIAL` | contains `secret`, `password`, `credential`, `authorization`, `api.key`, `access.token`, `refresh.token`, or `auth.token` |
| `KEY-TOKEN` | contains `.token`, starts with `token`, or ends with `.token` |
| `KEY-IDENTITY` | equals `email`, ends with `.email`, starts with `user.`, starts with `enduser.`, contains `user.id`, contains `user.email`, contains `userid`, contains `user_id`, or contains `username` |
| `KEY-RAW-CONTENT` | contains `prompt.content`, `response.content`, `tool.arguments`, `tool.results`, `tool.input`, or `tool.output` |

`prompt.version` is not sensitive by key predicate. It is a version identifier, not raw prompt content.

### Sensitive Text Patterns

These regexes are applied to string values under allowed raw source paths:

| Pattern id | Regex |
| --- | --- |
| `SENS-EMAIL` | `(?i)\b[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}\b` |
| `SENS-GITHUB-TOKEN` | `\b(ghp|github_pat)_[A-Za-z0-9_]{20,}\b` |
| `SENS-AUTH-SCHEME` | `(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=\-]{16,}\b` |
| `SENS-AUTH-HEADER` | `(?i)\bauthorization\s*[:=]\s*(bearer|basic)\s+[A-Za-z0-9._~+/=\-]{16,}\b` |
| `SENS-ASSIGNMENT` | `(?i)\b(api[_\-.]?key|access[_\-.]?token|refresh[_\-.]?token|secret|password|credential)\b\s*[:=]\s*["']?[^"',\s]{8,}` |

### Base64 Credential Predicate

A string value matches the Base64 credential predicate when:

1. It contains a standalone Base64-looking token matching `(?<![A-Za-z0-9+/=])[A-Za-z0-9+/]{16,}={0,2}(?![A-Za-z0-9+/=])`.
2. The token length is divisible by 4.
3. UTF-8 decoding succeeds.
4. The decoded value contains `:`, `secret`, `key`, `token`, `password`, or `authorization`, case-insensitive.

The command records only the pattern id and source locator in standard output.

## Evidence Reference Contract

Standard candidate output uses `evidence_ref` as a stable reference and never embeds raw content.

Recommended `evidence_ref` format:

```text
raw:<trace_id>:<span_id>:<source_path>
bundle:<bundle_id>:<diagnosis_candidate_id>
measurement:<input_file>#row=<row_number>
```

`source_record_ref` points to the normalized measurement or raw input row / record. `evidence_ref` points to the specific evidence source used by the rule.

When multiple raw fragments support the same candidate, standard output uses the first stable source locator and the sensitive bundle `evidence_index` records the full fragment list.

## Sensitive Bundle Schema Version 1

Sensitive bundles are created only when `--include-sensitive-content` is set. The default path remains:

```text
tmp/sprint3-sensitive/<run_id>/
```

### `manifest.json`

Top-level fields:

| Field | Type | Required | Value |
| --- | --- | --- | --- |
| `schema_version` | integer | yes | `1` |
| `bundle_id` | string | yes | same value as `run_id` |
| `created_at_utc` | string | yes | UTC timestamp in ISO 8601 format |
| `expires_at_utc` | string | yes | default is 7 days after `created_at_utc` |
| `generated_by_command` | string | yes | command name, for M3 usually `generate-diagnosis-candidates` |
| `source_inputs` | array | yes | input path and sha256 objects |
| `content_included` | boolean | yes | `true` |
| `delete_target_paths` | array | yes | paths the user should delete to remove the bundle |
| `evidence_index` | array | yes | evidence lookup entries |

`source_inputs` item fields:

| Field | Type | Required |
| --- | --- | --- |
| `path` | string | yes |
| `sha256` | string | yes |
| `kind` | string | yes: `measurement`, `raw-store`, or `raw-otlp` |

`evidence_index` item fields:

| Field | Type | Required |
| --- | --- | --- |
| `evidence_ref` | string | yes |
| `diagnosis_candidate_id` | string | yes |
| `trace_id` | string or null | yes |
| `source_locator` | string | yes |
| `evidence_file` | string | yes, relative to bundle root |
| `content_kinds` | array | yes |
| `fragment_count` | integer | yes |

Allowed `content_kinds` values are `prompt`, `response`, `tool_arguments`, `tool_results`, `identity`, `credential`, `secret`, and `base64_header`.

### `evidence/*.json`

Top-level fields:

| Field | Type | Required |
| --- | --- | --- |
| `schema_version` | integer | yes, `1` |
| `evidence_ref` | string | yes |
| `diagnosis_candidate_id` | string | yes |
| `trace_id` | string or null | yes |
| `source_locator` | string | yes |
| `fragments` | array | yes |

Fragment fields:

| Field | Type | Required |
| --- | --- | --- |
| `fragment_id` | string | yes |
| `content_kind` | string | yes |
| `source_path` | string | yes |
| `sequence` | integer | yes |
| `value` | string | yes |
| `sha256` | string | yes |

Fragment granularity is source span / event / field. A command must not write a full raw trace payload as one fragment.

Reverse lookup is fixed:

1. Read `evidence_ref` from standard candidate output.
2. Open `manifest.json`.
3. Find the matching `evidence_index[*].evidence_ref`.
4. Open `evidence_index[*].evidence_file`.
5. Read only the fragments needed for the candidate.

Expired bundle handling:

- Commands that read a bundle may warn when `expires_at_utc` is in the past.
- Sprint3 does not implement automatic deletion.
- Manual deletion uses `manifest.json` `delete_target_paths` after the user confirms the target path.

## Decision Rule Set

M4 must implement only the rules listed here unless M2 is amended.

Rules are evaluated in this order:

1. `DEC-BLOCK-SCOPE-OVERREACH-V1`
2. `DEC-HUMAN-REVIEW-SENSITIVE-CONTENT-V1`
3. `DEC-AUTO-APPROVE-SAFE-METADATA-V1`
4. `DEC-HUMAN-REVIEW-DEFAULT-V1`

| decision_rule_id | Deterministic condition | decision_status | next_action |
| --- | --- | --- | --- |
| `DEC-BLOCK-SCOPE-OVERREACH-V1` | proposal fields contain a scope-overreach pattern below, or implementation target is outside `prompt`, `instruction`, `skill`, `tool schema`, `workflow`, `eval` | `blocked` | `do-not-implement` |
| `DEC-HUMAN-REVIEW-SENSITIVE-CONTENT-V1` | `sensitive_content_included=true`, or `sensitive_bundle_path` is non-empty | `needs-human-review` | `request-human-review` |
| `DEC-AUTO-APPROVE-SAFE-METADATA-V1` | source candidate is not blocked, sensitive content is not included, severity is `minor` or `major`, and implementation target is one of the allowed Sprint3 targets | `auto-approved` | `record-for-sprint4-planning` |
| `DEC-HUMAN-REVIEW-DEFAULT-V1` | no earlier rule matches | `needs-human-review` | `request-human-review` |

Scope-overreach patterns are case-insensitive substring or regex checks over `proposal_title`, `proposal_summary`, `proposed_change_kind`, `improvement_target`, and any future proposed-change text field:

| Pattern id | Match |
| --- | --- |
| `SCOPE-REPO-MODIFY` | `repository file`, `modify files`, `edit files`, `apply patch`, `patch`, or `diff` |
| `SCOPE-GIT` | `commit`, `push`, `pull request`, `pr`, `merge`, or `auto-merge` |
| `SCOPE-WINNER` | `winner`, `win/loss`, `automatic winner`, or `auto decide experiment` |
| `SCOPE-LIVE` | `live service`, `network endpoint`, or `production data` as a required automated verification step |

`auto-approved` is a record state only. In Sprint3 it exits to M5 / M6 review evidence or Sprint4 planning handoff notes. It must not call a repository-modifying command and must not generate patch / diff output.

## M24-M27 Adapter Contract

Sprint3 candidate schemas do not replace M24-M27 schemas.

M5 must connect the pipeline by implementing an adapter command that maps diagnosis candidates to M24 diagnosis records, then using the existing M25-M27 commands where human review is needed.
The adapter may be small, but the Sprint3 connection should not remain a manual mapping only.

### Adapter Inputs

The M24 adapter needs:

| Input | Purpose |
| --- | --- |
| diagnosis candidate CSV / JSON | candidate classification fields |
| normalized measurement CSV / JSON | M24 context columns not carried by candidate output |
| optional sensitive bundle manifest | existence and review evidence only; raw fragment values are not copied |

The adapter joins by `trace_id` first. If multiple measurement rows share a trace id, `source_record_ref` row information is used as a tie-breaker. If no measurement row is found, context columns are blank.

### Diagnosis Candidate to M24 Mapping

| M24 diagnosis column | Source |
| --- | --- |
| `trace_id` | candidate `trace_id` |
| `task_id` | joined measurement `task_id`, blank if unavailable |
| `task_category` | joined measurement `task_category`, blank if unavailable |
| `client_kind` | joined measurement `client_kind`, blank if unavailable |
| `comparison_id` | blank in Sprint3 adapter v1 |
| `experiment_id` | joined measurement `experiment_id`, blank if unavailable |
| `experiment_condition` | joined measurement `experiment_condition`, blank if unavailable |
| `prompt_version` | joined measurement `prompt_version`, blank if unavailable |
| `agent_variant` | joined measurement `agent_variant`, blank if unavailable |
| `task_run_index` | joined measurement `task_run_index`, blank if unavailable |
| `failure_category_id` | candidate `failure_category_id` |
| `anti_pattern_id` | candidate `anti_pattern_id` |
| `severity` | candidate `severity` |
| `evidence_summary` | candidate `evidence_summary` plus sanitized `rule_id` and `evidence_ref` |
| `recommended_improvement_target` | candidate `recommended_improvement_target` |
| `review_status` | mapping below |

`evidence_summary` may include `rule_id=<rule_id>; evidence_ref=<evidence_ref>` because both values are stable references, not raw content. It must not include `sensitive_bundle_path` or fragment values.

Review status mapping:

| candidate_status | M24 `review_status` |
| --- | --- |
| `auto-eligible` | `accepted-for-proposal` |
| `candidate` | `needs-human-review` |
| `blocked` | `rejected` |

### Dropped Candidate Columns

The M24 adapter does not copy these columns into M24 output:

- `diagnosis_candidate_id`
- `source_record_ref`
- `content_included`
- `sensitive_bundle_path`
- `confidence`
- `required_human_checks`
- `candidate_status`

If these are needed for audit, M5 may write a separate adapter sidecar record that contains only ids, refs, paths, and statuses. The sidecar must not contain raw fragment values.

### M25-M27 Relationship

After M24 mapping:

1. Existing `validate-diagnoses` validates the mapped M24 records.
2. Existing `generate-improvement-proposals` produces M25 proposals only from `review_status=accepted-for-proposal`.
3. Existing `evaluate-improvement-proposals` performs M26 pre-review.
4. Existing `generate-decision-template` and `record-human-decisions` remain human decision workflow commands.

Sprint3 auto-decision records are not converted into M27 human decision records. A human decision must still be produced by M27 when human approval is required.

## Promotion to `docs/spec.md`

Before M3-M5 code behavior is treated as product behavior, the following should be promoted to `docs/spec.md`:

- command names and input / output contracts from M1,
- rule ids and decision rule ids from this document,
- sensitive bundle schema version 1,
- M24-M27 compatibility boundary,
- Sprint3 `auto-approved` exit boundary.

The exact regex inventory, bundle file examples, and implementation notes may remain sprint-local unless they become public CLI behavior.
