# M4 Review: Redacted real-trace E2E

## 2026-06-11 implementation review

### Scope reviewed

- GitHub Copilot CLI OTel file exporter output from a read-only local prompt.
- Conversion from Copilot CLI JSON-lines span output to an OTLP JSON envelope accepted by `ingest-raw`.
- Redaction of content capture fields before preserving evidence.
- Raw data loop and downstream workflow execution using ignored local artifacts.

### Findings

- Spec compliance: no mismatch found. M4 remained a compatibility evidence exercise and did not add product behavior, CLI commands, dependencies, AppHost resources, or trace-driven automatic diagnosis.
- Functional correctness: `ingest-raw` and `normalize-raw` accepted the redacted real Copilot CLI shaped input. Measurement output included trace id, client kind, token counts, duration, error count, and tool call count.
- Data handling: raw content-capturing Copilot JSONL and session output were deleted after redaction. Preserved repository evidence contains only sanitized summaries and commands. Local generated artifacts are under ignored `tmp\`.
- Downstream compatibility: a sanitized human diagnosis record was created from the measurement row, then proposal generation, proposal evaluation, decision template generation, and human decision recording all completed.
- Residual risk: VS Code Copilot Chat UI execution was not performed by the agent. VS Code was launched and a read-only prompt was put on the clipboard for user-side UI execution if that additional client-kind evidence is desired.

### Validation

- `copilot --version` reported GitHub Copilot CLI 1.0.57.
- Copilot CLI non-interactive run completed with exit code 0.
- `ingest-raw tmp\m4-redacted-real-trace-e2e\redacted-raw.json --db tmp\m4-redacted-real-trace-e2e\raw-store.db` succeeded.
- `normalize-raw tmp\m4-redacted-real-trace-e2e\raw-store.db --csv tmp\m4-redacted-real-trace-e2e\measurements.csv --json tmp\m4-redacted-real-trace-e2e\measurements.json` succeeded.
- Measurement row confirmed `trace_id=0e0c15ad877bcb21b5ba78795b3774d3`, `client_kind=copilot-cli`, `input_tokens=71468`, `output_tokens=724`, `total_tokens=72192`, `duration_ms=8241`, `error_count=0`, and `tool_call_count=2`.
- Downstream commands succeeded through `record-human-decisions`.
- Redacted input scan found no forbidden local path, auth header, credential, secret, private key marker, real prompt string, or tool command string.

### Review note

Subagent review was not used because this M4 change is documentation and local evidence only. This review records the required spec compliance, functional correctness, data handling, tests, edge cases, and residual risk perspectives in the sprint-local milestone.
