# M5 Review: Regression review and closeout

## 2026-06-12 closeout review

### Scope reviewed

- Sprint2.5 M2-M4 implementation and evidence records.
- Full solution build and test status.
- Config CLI help and representative command stdout / stderr / exit code behavior.
- Config CLI dependency, public surface, and reference shape after responsibility split.
- Redacted real-trace E2E evidence and repository saved-content boundary.
- Sprint2.5 and roadmap closeout documentation.

### Findings

- Spec compliance: no mismatch found. Sprint2.5 remained a maintainability and compatibility sprint. It did not add product behavior, command contracts, AppHost resources, trace-driven automatic diagnosis, LLM-based generation, or automatic repository modification.
- Functional correctness: full build and test passed. CLI smoke checks confirmed help exits with code 0 on stdout, valid resource attributes exit with code 0 on stdout, unknown command exits with code 1 on stderr, and representative raw / diagnosis / proposal / evaluation commands keep their success messages on stdout.
- Tests and regression risk: `dotnet test` passed with 173 tests. The M5 smoke check used synthetic fixtures and does not replace M4's redacted real Copilot CLI shaped E2E evidence. VS Code Copilot Chat live capture remains unverified in M4 because it requires user-side Chat UI interaction.
- Maintainability: `Program.cs` remains a minimal entry point, production CSV / JSON helpers are centralized, no public production types were detected, Config CLI still has only the existing `Microsoft.Data.Sqlite` package reference, and it has no project references.
- Data handling: repository evidence remains limited to sanitized summary and procedure text. Forbidden-content scanning found only existing placeholder strings, policy text, test detection strings, and M4 redaction notes; no new raw prompt / response content, tool payload, credential, Base64 header, or real identity was added by M5.

### Validation

- `dotnet build CopilotAgentObservability.slnx` passed with 0 warnings and 0 errors.
- `dotnet test CopilotAgentObservability.slnx` passed, 173 tests.
- CLI smoke checks:
  - `--help`: exit code 0, stdout first line `Usage:`, stderr empty.
  - `validate-resource-attributes`: exit code 0, stdout `OTEL_RESOURCE_ATTRIBUTES is valid.`, stderr empty.
  - `unknown-command`: exit code 1, stdout empty, stderr starts with `error: unknown command`.
  - `ingest-raw`: exit code 0, stdout `Ingested 1 raw telemetry record(s).`, stderr empty.
  - `normalize-raw`: exit code 0, stdout `Normalized 1 raw measurement row(s).`, stderr empty.
  - `validate-diagnoses`: exit code 0, stdout `Validated 3 diagnosis record(s).`, stderr empty.
  - `generate-improvement-proposals`: exit code 0, stdout `Generated 1 improvement proposal record(s).`, stderr empty.
  - `evaluate-improvement-proposals`: exit code 0, stdout `Evaluated 1 improvement proposal record(s).`, stderr empty.
  - `generate-decision-template`: exit code 0, stdout `Generated decision template with 1 row(s).`, stderr empty.
- `rg -n "^public | public (class|record|struct|interface|enum)" src/CopilotAgentObservability.ConfigCli -g "*.cs"` returned no matches.
- `dotnet list src\CopilotAgentObservability.ConfigCli\CopilotAgentObservability.ConfigCli.csproj package` reported only `Microsoft.Data.Sqlite` 10.0.8.
- `dotnet list src\CopilotAgentObservability.ConfigCli\CopilotAgentObservability.ConfigCli.csproj reference` reported no project references.
- `rg -n "mwam0|Authorization|Basic |Bearer |secret|password|cookie|Set-Cookie|BEGIN |PRIVATE KEY|github\.com|Read-only telemetry check|dotnet --version" docs/sprints/sprint2-5-maintainability README.md docs/task.md src tests -g "!*bin*" -g "!*obj*"` found only existing placeholders, policy text, test detection strings, and M4 evidence notes.

### Residual risks

- M4 redacted real-trace E2E confirms GitHub Copilot CLI shaped input only; VS Code Copilot Chat live capture remains a manual/user-driven follow-up.
- Synthetic test coverage remains the automated regression baseline; redacted real-trace evidence is compatibility evidence, not a replacement for deterministic tests.
- AppHost local launcher behavior remains intentionally out of scope.
- Sprint3 trace diagnosis remains a candidate and is not implemented by Sprint2.5.

### Review note

Subagent review was not used because the current request did not ask to split work across reader and writer subagents. This review records the required M5 perspectives in the sprint-local milestone.

## 2026-06-12 Codex post-Claude review

### Scope reviewed

- Sprint2.5 implementation commits from planning through closeout.
- Config CLI responsibility split, shared CSV / JSON helpers, raw normalization boundary, and sprint-local evidence.
- Repository formatting hygiene and validation commands after the Claude implementation.

### Findings

- Functional / spec review: no blocking or major issue found. The implementation remains within Sprint2.5 scope and does not add new product behavior, AppHost resources, trace-driven diagnosis, LLM-based generation, or automatic repository modification.
- Minor hygiene issue: `git diff --check 441bfd1..HEAD` reported extra blank lines at EOF in split Config CLI `.cs` files. This was a valid review finding because it makes whitespace checks fail even though it has no runtime behavior impact.

### Fix applied

- Removed the extra EOF blank line from the affected split Config CLI source files. No logic, CLI contract, output schema, or documentation semantics were changed.

### Validation

- `git diff --check` passed.
- `dotnet build CopilotAgentObservability.slnx` passed with 0 warnings and 0 errors.
- `dotnet test CopilotAgentObservability.slnx --no-build` passed, 173 tests.

### Residual risks

- Same as the closeout review: VS Code Copilot Chat live capture remains a user-driven/manual follow-up, while the redacted real-trace E2E evidence covers GitHub Copilot CLI shaped input.
