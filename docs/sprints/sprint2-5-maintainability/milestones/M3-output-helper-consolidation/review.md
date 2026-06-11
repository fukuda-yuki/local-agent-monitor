# M3 Review: Output helper consolidation

## 2026-06-11 implementation review

### Scope reviewed

- CSV cell escaping consolidation under `Shared/CsvEscaper.cs`.
- CSV line parsing consolidation under `Shared/CsvLineParser.cs`.
- Indented JSON output with trailing newline consolidation under `Shared/JsonOutput.cs`.
- Output compatibility for measurement, diagnosis, improvement proposal, proposal evaluation, and human decision writers.

### Findings

- Spec compliance: no mismatch found. M3 stayed within helper consolidation and did not add commands, schemas, dependencies, AppHost resources, or trace-driven diagnosis behavior.
- Functional correctness: the existing writer / reader column arrays and row mapping logic were preserved. Header order, blank CSV cells, JSON `null`, indentation, trailing newline, and invalid CSV quote errors are covered by existing tests plus `SharedOutputHelperTests`.
- Maintainability: identical production CSV escaping implementations were removed from five output writers, and identical production CSV line parsers were removed from four input readers. JSON output helper consolidation was limited to output writers that already used `WriteIndented = true` plus `Environment.NewLine`.
- Residual risk: `ConfigSamples` still serializes indented JSON directly because its helper returns a settings sample string without adding its own trailing newline. This is intentionally outside the M3 output-writer helper path.

### Validation

- `dotnet build CopilotAgentObservability.slnx` passed.
- `dotnet test CopilotAgentObservability.slnx` passed, 173 tests.
- `rg -n "EscapeCsv|ParseCsvLine|new JsonSerializerOptions \{ WriteIndented = true \}" src\CopilotAgentObservability.ConfigCli tests\CopilotAgentObservability.ConfigCli.Tests` confirmed no production `EscapeCsv` / `ParseCsvLine` duplication remains. Remaining `ConfigSamples` JSON serialization and test-local CSV parsing are outside the M3 production helper consolidation target.

### Review note

Subagent review was not used because the available delegation tool is restricted to explicit user-requested subagent work. This review records the required M3 self-review perspectives in the sprint-local milestone.
