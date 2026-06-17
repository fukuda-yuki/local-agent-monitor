# M6 Review: Collaborative real-trace E2E

## 2026-06-18 partial implementation review

### Scope reviewed

- GitHub Copilot CLI OTel file exporter output from a read-only local prompt.
- Conversion from Copilot CLI JSON-lines span output to an OTLP JSON envelope accepted by `ingest-raw`.
- Redaction and anonymization before preserving evidence.
- Sprint3 candidate pipeline execution through auto-decision generation.
- Sensitive bundle manifest handling and deletion status.

### Findings

- Spec compliance: no mismatch found for the CLI-side M6 work. The work used ignored `tmp\` artifacts, did not add product behavior, did not add dependencies, did not generate repository patches from auto-decisions, and did not push or create a pull request.
- Functional correctness: CLI-derived redacted real-trace input passed `ingest-raw`, `normalize-raw`, `generate-diagnosis-candidates`, `generate-improvement-candidates`, and `generate-auto-decisions`.
- Data handling: raw content-capturing Copilot JSONL and Copilot session output were deleted after redaction and pipeline execution. The sensitive bundle manifest was checked and the local sensitive bundle directory was deleted. Preserved repository evidence contains only sanitized summaries, relative ignored paths, anonymous trace identifiers, commands, counts, and pending items.
- Candidate behavior: the pipeline detected one sensitive-content candidate from redacted sensitive-key evidence and 10 metadata-missing candidates from file exporter entries without trace context. Blocked sensitive candidates did not flow into improvement generation; metadata candidates produced Sprint3-local auto-decision records only.

### Residual risk

- VS Code Copilot Chat real-trace execution remains pending because it requires user-side Chat UI operation.
- M6 remains partial until a redacted `vscode-copilot-chat` payload passes at least `generate-diagnosis-candidates`.
- The generated local candidate outputs under `tmp\` may contain ignored absolute local paths in machine-local fields. They are not repository evidence and were not committed.

### Validation

- `copilot --version` reported GitHub Copilot CLI 1.0.63.
- `copilot help monitoring` confirmed `COPILOT_OTEL_FILE_EXPORTER_PATH` activates file exporter OTel output.
- Copilot CLI non-interactive read-only run completed with exit code 0.
- Redacted raw input scan found no checked forbidden identity, auth, secret, raw prompt, raw command, local path, GitHub URL, or synthetic email strings.
- `ingest-raw tmp\sprint3-m6-real-trace-e2e\20260618-cli\redacted-raw.json --db tmp\sprint3-m6-real-trace-e2e\20260618-cli\raw-store.db` succeeded.
- `normalize-raw tmp\sprint3-m6-real-trace-e2e\20260618-cli\raw-store.db --csv tmp\sprint3-m6-real-trace-e2e\20260618-cli\measurements.csv --json tmp\sprint3-m6-real-trace-e2e\20260618-cli\measurements.json` succeeded.
- `generate-diagnosis-candidates ... --include-sensitive-content ...` succeeded and generated 11 candidate records.
- `generate-improvement-candidates` succeeded and generated 10 improvement candidate records.
- `generate-auto-decisions` succeeded and generated 10 auto-decision records.
- `dotnet build CopilotAgentObservability.slnx` succeeded.
- `dotnet test CopilotAgentObservability.slnx` succeeded with 199 tests passed.
- Tracked documentation secret-oriented scan found no checked local identity, auth header, auth scheme credential, GitHub token marker, private-key marker, synthetic email, or raw prompt string.

### Review note

Subagent review was not used because this is documentation and local evidence recording for a partial live check. This review records spec compliance, functional correctness, data handling, candidate behavior, validation, and residual risk for the completed CLI-side work.
