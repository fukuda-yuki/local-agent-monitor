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
config-cli generate-diagnosis-candidates <measurements.csv|measurements.json> [--raw <raw-store.db|raw-otlp.json>] [--include-sensitive-content --retention-database <local-monitor.db> [--sensitive-output-dir <dir>]] [--csv <output.csv>] [--json <output.json>]
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

Sensitive content extraction requires all of:

- `--include-sensitive-content`
- `--raw <raw-store.db|raw-otlp.json>`
- `--retention-database <local-monitor.db>`

`--retention-database` is the sole explicit catalog binding and is validated
before measurement or raw input is opened. `--sensitive-output-dir <dir>` is an
optional parent directory; when omitted, the command uses its specified local
temporary parent. The option remains inert when sensitive mode is not enabled.

Sensitive bundles must not be committed.
Candidate CSV / JSON files with `sensitive_bundle_path` are local runtime artifacts.
Repository-safe dashboard outputs may contain only sanitized references and `sensitive_bundle_present`.

Before sensitive input, measurement input, raw input, bundle output, or
candidate output is opened or created, the command validates the explicitly
supplied retention catalog. If no sensitive fragment is extracted, it creates
neither a capture reservation nor the optional bundle parent. A sensitive
bundle is completed independently before candidate CSV/JSON publication; a
candidate-output publication failure never deletes or rolls back a completed
bundle.

When CSV and JSON are both requested, the command stages both bytes and
publishes them as one all-or-cleaned-up operation: existing targets are never
overwritten, and a failure after one new target is published removes only the
new target after verifying it is still the content this invocation published.
If that rollback cannot be proven or completed, the command returns a distinct
sanitized cleanup failure. No failure message, log, manifest, repository-safe
output, or candidate-output rollback may disclose raw content, source or
delete locations, or local paths.

## Auto-decision Boundary

Auto-decision records are recommendation records.
They do not apply changes and do not prove improvement effects.
Human review remains required before any product or repository change.
