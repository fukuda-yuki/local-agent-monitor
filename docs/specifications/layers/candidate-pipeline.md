# Candidate Pipeline Specification

## Scope

The candidate pipeline generates deterministic records from normalized measurements and optional raw evidence.
It does not modify repository files and does not create patches, commits, pushes, or pull requests.

## Outputs

The pipeline may produce:

- diagnosis candidate defined in [../interfaces/candidate-records.md](../interfaces/candidate-records.md)。
- improvement candidate defined in [../interfaces/candidate-records.md](../interfaces/candidate-records.md)。
- auto-decision record defined in [../interfaces/candidate-records.md](../interfaces/candidate-records.md)。
- adapter output for existing [human-review records](../interfaces/human-review-records.md)。
- sensitive bundle metadata。

## Commands

```text
config-cli generate-diagnosis-candidates <measurements.csv|measurements.json> [--raw <raw-store.db|raw-otlp.json>] [--include-sensitive-content] [--sensitive-output-dir <dir>] [--csv <output.csv>] [--json <output.json>]
config-cli generate-improvement-candidates <diagnosis-candidates.csv|diagnosis-candidates.json> [--csv <output.csv>] [--json <output.json>]
config-cli generate-auto-decisions <improvement-candidates.csv|improvement-candidates.json> [--csv <output.csv>] [--json <output.json>]
config-cli adapt-diagnosis-candidates <diagnosis-candidates.csv|diagnosis-candidates.json> <measurements.csv|measurements.json> [--csv <output.csv>] [--json <output.json>]
```

Legacy human review commands remain supported:

```text
config-cli validate-diagnoses <input.csv|input.json> [--csv <output.csv>] [--json <output.json>]
config-cli generate-improvement-proposals <diagnoses.csv|diagnoses.json> [--csv <output.csv>] [--json <output.json>]
config-cli evaluate-improvement-proposals <proposals.csv|proposals.json> [--csv <output.csv>] [--json <output.json>]
config-cli generate-decision-template <evaluations.csv|evaluations.json> [--csv <output.csv>] [--json <output.json>]
config-cli record-human-decisions <evaluations.csv|evaluations.json> <decisions.csv|decisions.json> [--csv <output.csv>] [--json <output.json>]
```

## Sensitive Evidence

Sensitive content extraction requires both:

- `--include-sensitive-content`
- `--sensitive-output-dir <dir>`

Sensitive bundles must not be committed.
Candidate CSV / JSON files with `sensitive_bundle_path` are local runtime artifacts.
Repository-safe dashboard outputs may contain only sanitized references and `sensitive_bundle_present`.

## Auto-decision Boundary

Auto-decision records are recommendation records.
They do not apply changes and do not prove improvement effects.
Human review remains required before any product or repository change.
