# Human-review Record Interfaces

Human-review records are the legacy compatibility path for diagnosis validation, proposal generation, proposal evaluation, and explicit human decisions.
They remain supported so existing review loops can consume candidate output through `adapt-diagnosis-candidates`.

## Diagnosis Row

Produced or consumed by:

```text
config-cli adapt-diagnosis-candidates <diagnosis-candidates.csv|diagnosis-candidates.json> <measurements.csv|measurements.json> [--csv <output.csv>] [--json <output.json>]
config-cli validate-diagnoses <input.csv|input.json> [--csv <output.csv>] [--json <output.json>]
```

CSV column order and JSON property names:

```text
trace_id,task_id,task_category,client_kind,comparison_id,experiment_id,experiment_condition,prompt_version,agent_variant,task_run_index,failure_category_id,anti_pattern_id,severity,evidence_summary,recommended_improvement_target,review_status
```

## Improvement Proposal Row

Produced by:

```text
config-cli generate-improvement-proposals <diagnoses.csv|diagnoses.json> [--csv <output.csv>] [--json <output.json>]
```

CSV column order and JSON property names:

```text
proposal_id,source_diagnosis_index,trace_id,task_id,task_category,client_kind,comparison_id,experiment_id,experiment_condition,prompt_version,agent_variant,task_run_index,failure_category_id,anti_pattern_id,severity,improvement_target,evidence_summary,proposal_title,proposal_summary,proposed_change,acceptance_check,human_review_status
```

## Proposal Evaluation Row

Produced by:

```text
config-cli evaluate-improvement-proposals <proposals.csv|proposals.json> [--csv <output.csv>] [--json <output.json>]
```

CSV column order and JSON property names:

```text
proposal_id,source_diagnosis_index,trace_id,task_id,task_category,client_kind,comparison_id,experiment_id,experiment_condition,prompt_version,agent_variant,task_run_index,failure_category_id,anti_pattern_id,severity,improvement_target,proposal_title,proposal_evaluation_status,evaluator_findings,required_human_checks,evaluator_notes
```

## Human Decision Row

Produced or consumed by:

```text
config-cli generate-decision-template <evaluations.csv|evaluations.json> [--csv <output.csv>] [--json <output.json>]
config-cli record-human-decisions <evaluations.csv|evaluations.json> <decisions.csv|decisions.json> [--csv <output.csv>] [--json <output.json>]
```

CSV column order and JSON property names:

```text
proposal_id,human_decision,decision_rationale,approver_id,approved_at,conditions_or_notes
```

## Safety Boundary

Human-review rows are repository-safe only when every free-text field is sanitized.
The following fields need particular review before committing or publishing:

- `evidence_summary`
- `proposal_summary`
- `proposed_change`
- `acceptance_check`
- `evaluator_findings`
- `required_human_checks`
- `evaluator_notes`
- `decision_rationale`
- `conditions_or_notes`

These fields must not contain raw prompt, raw response, tool arguments, tool results, observed source-code fragments, credentials, or sensitive bundle local paths.

## Related Specifications

- [Candidate records](candidate-records.md)
- [Candidate pipeline](../layers/candidate-pipeline.md)
- [Security and data boundaries](../security-data-boundaries.md)
