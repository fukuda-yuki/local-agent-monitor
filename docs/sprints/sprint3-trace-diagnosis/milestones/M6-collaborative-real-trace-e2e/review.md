# M6 Review: Collaborative real-trace E2E

## 2026-06-18 implementation review

### Scope reviewed

- GitHub Copilot CLI OTel file exporter output from a read-only local prompt.
- VS Code Copilot Chat file exporter output from a user-sent read-only Chat prompt.
- Conversion from Copilot CLI JSON-lines span output to an OTLP JSON envelope accepted by `ingest-raw`.
- Conversion from VS Code Chat JSON-lines log record output to an OTLP JSON envelope accepted by `ingest-raw`.
- Redaction and anonymization before preserving evidence.
- Sprint3 candidate pipeline execution through auto-decision generation.
- Sensitive bundle manifest handling and deletion status.

### Findings

- Spec compliance: no mismatch found. The work used ignored `tmp\` artifacts, did not add product behavior, did not add dependencies, did not generate repository patches from auto-decisions, and did not push or create a pull request.
- Functional correctness: both CLI-derived and VS Code Chat-derived redacted real-trace input passed `ingest-raw`, `normalize-raw`, `generate-diagnosis-candidates`, `generate-improvement-candidates`, and `generate-auto-decisions`.
- Data handling: raw content-capturing JSONL files and Copilot session output were deleted after redaction and pipeline execution. Sensitive bundle manifests were checked and local sensitive bundle directories were deleted. Preserved repository evidence contains only sanitized summaries, relative ignored paths, anonymous trace identifiers, commands, counts, and completion status.
- Candidate behavior: both client-kind pipelines detected one sensitive-content candidate from redacted sensitive-key evidence plus metadata-missing candidates from exporter entries without trace context. Blocked sensitive candidates did not flow into improvement generation; metadata candidates produced Sprint3-local auto-decision records only.

### Residual risk

- The generated local candidate outputs under `tmp\` may contain ignored absolute local paths in machine-local fields. They are not repository evidence and were not committed.
- The VS Code Chat conversion path is evidence-only for M6; it does not add a product command or parser for VS Code's file exporter JSONL log-record shape.

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
- VS Code Chat `ingest-raw` succeeded with 1 raw telemetry record.
- VS Code Chat `normalize-raw` succeeded with 27 raw measurement rows.
- VS Code Chat `generate-diagnosis-candidates ... --include-sensitive-content ...` succeeded and generated 27 candidate records.
- VS Code Chat `generate-improvement-candidates` succeeded and generated 26 improvement candidate records.
- VS Code Chat `generate-auto-decisions` succeeded and generated 26 auto-decision records.
- `dotnet build CopilotAgentObservability.slnx` succeeded.
- `dotnet test CopilotAgentObservability.slnx` succeeded with 199 tests passed.
- Tracked documentation secret-oriented scan found no checked local identity, auth header, auth scheme credential, GitHub token marker, private-key marker, synthetic email, or raw prompt string.

### Review note

Subagent review was not used because this is documentation and local evidence recording for a partial live check. This review records spec compliance, functional correctness, data handling, candidate behavior, validation, and residual risk for the completed CLI-side work.
