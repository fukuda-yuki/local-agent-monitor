# M4 Review: raw normalization

## Scope

- Sprint2 M4 raw normalization.
- `config-cli normalize-raw <raw-store.db|raw.json> [--csv <output.csv>] [--json <output.json>]` command.
- Raw OTLP payload conversion into the existing M12 measurement schema.

## Changed Files

- `src/CopilotAgentObservability.ConfigCli/Program.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/RawNormalizationTests.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/raw-otlp.synthetic.json`
- `docs/sprints/sprint2-raw-data-loop/milestones/M4-raw-normalization/task.md`
- `docs/sprints/sprint2-raw-data-loop/README.md`
- `docs/task.md`

## Review Findings

- Spec compliance: `normalize-raw` accepts saved raw OTLP JSON or SQLite raw store input only. It does not add an HTTP receiver, daemon, custom OTLP receiver, masking / redaction workflow, or trace-driven diagnosis.
- Functional correctness: The normalizer maps OTLP Resource Attributes to M12 measurement columns, aggregates GenAI token attributes with `total_tokens` fallback, calculates duration from span start / end bounds, classifies turn / tool counts using the M16-style rules, and counts span status / event errors.
- Responsibility boundary: `aggregate-measurements` remains the Langfuse export adapter. `normalize-raw` uses a separate raw OTLP adapter and shares only `MeasurementRow`, `MeasurementOutputWriter`, and sanitizer behavior.
- Data handling: Unknown spans keep only minimal identifiers. Unknown Resource Attributes are sanitized to exclude prompt / response / content / arguments / results / credentials / secrets / authorization / token and identity-bearing keys.
- Maintainability: The change follows the existing single-file CLI style and adds no runtime or development dependency.

## Sub-Agent Review Follow-up

2026-06-07 post-commit review of `b8004e4` used parallel read-only sub-agents for spec / behavior, test coverage, and data handling.

- Spec / behavior review: no actionable M4 spec violation was found. The reviewer noted that cross-record merge of the same trace is not currently specified.
- Test coverage review: accepted as valid findings that JSON fixed schema / null behavior, CSV missing-value assertions, safe unknown Resource Attribute retention, and multi-record raw store normalization needed stronger regression coverage.
- Data handling review: accepted as valid findings that normalized auxiliary JSON could leak nested unsafe Resource Attribute values and unsafe unknown span names.

Main-Agent response:

- Added recursive sanitization for unknown Resource Attribute values before writing `unknown_attributes_json`.
- Extended identity-bearing unknown key filtering for `user.*` and `enduser.*`.
- Suppressed unsafe unknown span `name` values while preserving safe span names.
- Strengthened raw normalization tests for exact JSON property set, JSON null missing values, parsed CSV column values, safe / unsafe unknown attributes, unsafe unknown span names, and multi-record raw store input using `payload_json` as the source of truth.

Re-review result:

- Data handling re-review found the previous leak findings resolved and no new actionable regression.
- Test re-review found the previous coverage findings resolved. A low suggestion to assert exact JSON property set was applied before final validation.

## Tests

- `RawNormalizationTests` covers raw JSON output, raw store output, CSV fixed columns / blank missing values, JSON null missing values, Resource Attribute mapping, token / duration / count mapping, sanitizer behavior, and deterministic CLI errors.
- Existing `MeasurementAggregationTests` remain unchanged and pass, covering the Langfuse export adapter regression boundary.
- `dotnet build CopilotAgentObservability.slnx`: passed, warning 0 / error 0.
- `dotnet test CopilotAgentObservability.slnx`: passed, 156 tests.

## Notes

- The raw store `resource_attributes_json` projection is not treated as the complete source of truth; normalization reads each record's `payload_json`.
- M5 remains responsible for wiring the normalized dataset into the Langfuse-independent diagnosis / proposal / evaluation / human decision loop.
