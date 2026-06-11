# Review

レビュー実施時に、レビュー範囲、変更ファイル、指摘、妥当性判断、対応方針、適用した修正、残リスクを記録する。

## 2026-06-08 Sprint2 completion review

### Scope

- Sprint2 M1-M6 の完了状態、user-facing docs、Sprint2 sprint index、M6 release check 記録。
- `docs/spec.md` 5.17 と実装済み Config CLI behavior の整合。
- `docs/requirements.md` 8.3 と Sprint2 README の整合。
- M5 synthetic E2E test による Langfuse 非依存 loop の evidence。

### Changed files

- `README.md`
- `docs/getting-started.md`
- `docs/task.md`
- `docs/sprints/sprint2-raw-data-loop/README.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M6-docs-and-release-check/task.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M6-docs-and-release-check/review.md`

### Findings and disposition

- Spec compliance: no actionable mismatch found. `ingest-raw` remains saved raw OTLP JSON file ingest into SQLite raw store, `normalize-raw` remains raw store / raw JSON to measurement schema, and `aggregate-measurements` remains the Langfuse export adapter.
- Requirements alignment: no actionable mismatch found. Sprint2 README matches `docs/requirements.md` 8.3 on file-based raw OTLP ingest, SQLite local raw store, PostgreSQL as future candidate, existing measurement / improvement CLI connection, and trace-driven automatic diagnosis being out of scope.
- Test / regression risk: no new code behavior was changed. Existing M5 E2E coverage exercises the full synthetic loop from raw store through human decision output, and full solution test passed.
- Data handling: user-facing docs now call out synthetic-only inputs, `data/raw-store.db`, temp output cleanup, raw payload cleanup, and prohibition on repository storage of real credentials, secrets, Base64 headers, real user identity, and real prompt / response content.
- Maintainability / docs consistency: Sprint2 status is now completion-oriented in README and `docs/task.md`; Sprint3 remains a candidate for trace diagnosis and is not implied as implemented.

### Verification

- `dotnet build CopilotAgentObservability.slnx`: passed with 0 warnings and 0 errors. NETSDK1057 appeared as the existing preview .NET SDK informational message.
- `dotnet test CopilotAgentObservability.slnx`: passed with 159 passed, 0 failed, 0 skipped. NETSDK1057 appeared as the existing preview .NET SDK informational message.

### Residual risk

- Live Copilot / live Langfuse collection was not rerun in M6 because this milestone documents and releases the synthetic Sprint2 loop, not Phase 1 live telemetry behavior.
- Shared environment use, real data validation, retention, access control, masking / redaction, and user notice remain separate specification work before any non-local or non-synthetic validation.
- Trace-driven automatic failure category / anti-pattern extraction remains a Sprint3 candidate and is not part of Sprint2 MVP.

## 2026-06-11 Post-completion Sub-Agent Review Loop

### Scope

- Parallel read-only Sub-Agent review of Sprint2 M4, M5, and M6 after M4-M6 implementation.
- Main-Agent disposition of findings, focused fixes, targeted and full validation, and re-review.

### Findings and disposition

- M4 sanitizer gap: accepted. Unknown auxiliary output did not cover additional identity-bearing key variants and content-marker unknown span names. Fixed in `MeasurementSanitizer` and covered by `RawNormalizationTests.NormalizeRaw_RemovesAdditionalIdentityAndContentMarkersFromAuxiliaryJson`.
- M5 decision-template safety gap: accepted. Direct `generate-decision-template` input could copy unsafe evaluation fields into template output. Fixed by running `ProposalEvaluationSafetyValidator` before template generation and covered by `HumanApprovalWorkflowTests.GenerateDecisionTemplate_RejectsUnsafeEvaluationInput`.
- Raw store same-`trace_id` cross-record merge: not adopted. This remains an unspecified behavior decision and is not treated as a Sprint2 M4-M6 bug.
- M5 integrated CSV E2E leg: not adopted. Existing per-command CSV tests cover CSV behavior; M5's synthetic workflow proof remains JSON-based.
- M6 docs/release-check review: no actionable mismatch found.

### Re-review result

- M4 focused Sub-Agent re-review accepted the sanitizer fix with no new actionable finding.
- M5 focused Sub-Agent re-review accepted the decision-template safety fix with no new actionable finding.

### Verification

- Targeted `RawNormalizationTests` / `HumanApprovalWorkflowTests`: passed, 35 tests.
- `dotnet build CopilotAgentObservability.slnx`: passed, warning 0 / error 0. NETSDK1057 appeared as the existing preview .NET SDK informational message.
- `dotnet test CopilotAgentObservability.slnx`: passed, 161 tests.
