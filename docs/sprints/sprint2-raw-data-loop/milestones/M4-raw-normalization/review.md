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

## Tests

- `RawNormalizationTests` covers raw JSON output, raw store output, CSV fixed columns / blank missing values, JSON null missing values, Resource Attribute mapping, token / duration / count mapping, sanitizer behavior, and deterministic CLI errors.
- Existing `MeasurementAggregationTests` remain unchanged and pass, covering the Langfuse export adapter regression boundary.
- `dotnet build CopilotAgentObservability.slnx`: passed, warning 0 / error 0.
- `dotnet test CopilotAgentObservability.slnx`: passed, 154 tests.

## Notes

- The raw store `resource_attributes_json` projection is not treated as the complete source of truth; normalization reads each record's `payload_json`.
- M5 remains responsible for wiring the normalized dataset into the Langfuse-independent diagnosis / proposal / evaluation / human decision loop.
