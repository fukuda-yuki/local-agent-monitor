# M5 Review: Langfuse 非依存 loop

## Scope

- Sprint2 M5 Langfuse 非依存 loop.
- Existing CLI chain from raw OTLP ingest through normalized dataset, human-classified diagnosis validation, proposal generation, proposal evaluation, decision template generation, and human decision recording.

## Changed Files

- `tests/CopilotAgentObservability.ConfigCli.Tests/LangfuseIndependentLoopTests.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/TestData/m5-diagnoses.synthetic.json`
- `docs/sprints/sprint2-raw-data-loop/milestones/M5-langfuse-independent-loop/task.md`
- `docs/sprints/sprint2-raw-data-loop/milestones/M5-langfuse-independent-loop/review.md`

## Review Findings

- Spec compliance: M5 adds E2E coverage only. It does not add a CLI command, public schema, dependency, HTTP receiver, daemon, Langfuse API dependency, or live Copilot dependency.
- Functional correctness: The E2E test ingests `raw-otlp.synthetic.json` into a temp SQLite DB, normalizes from the DB, asserts the normalized row matches the M5 human diagnosis fixture on `trace_id`, `task_id`, `client_kind`, and `task_run_index`, then runs the existing diagnosis / proposal / evaluation / human decision workflow to completion.
- Diagnosis boundary: The diagnosis input is a human-classified synthetic fixture. No trace-to-diagnosis extraction, failure category inference, or anti-pattern inference was added.
- Data handling: The E2E test asserts known unsafe synthetic prompt, token, identity, and unknown span content from the raw fixture does not appear in workflow outputs.
- Maintainability: The change follows existing xUnit and temp-directory patterns and keeps the CLI implementation unchanged.

## Tests

- `LangfuseIndependentLoopTests.EndToEnd_RawStoreThroughHumanDecision_UsesSyntheticFixturesOnly` covers the M5 synthetic loop.
- `dotnet build CopilotAgentObservability.slnx`: passed, warning 0 / error 0.
- `dotnet test CopilotAgentObservability.slnx`: passed, 159 tests.

## Residual Risk

- M5 proves deterministic synthetic wiring only. It does not prove live Copilot emission shape, live Langfuse availability, or real data masking.

## 2026-06-11 Follow-up Review

Parallel read-only Sub-Agent review rechecked Sprint2 M5 after M5/M6 completion.

Accepted finding:

- `generate-decision-template` could be used directly with a hand-written or corrupted evaluation file and copy an unsafe `proposal_id` into the human decision template, because the command did not run `ProposalEvaluationSafetyValidator` before template generation. Main-Agent accepted this as a valid safety finding on a public command included in the M5 loop.

Not adopted:

- Adding an integrated CSV E2E leg was treated as a coverage suggestion, not a required M5 fix. Individual command tests already cover CSV behavior, and the M5 acceptance criterion is the synthetic workflow connection rather than every output format combination.

Applied fix:

- `generate-decision-template` now validates evaluation input with `ProposalEvaluationSafetyValidator` before generating or writing a template.
- Added `HumanApprovalWorkflowTests.GenerateDecisionTemplate_RejectsUnsafeEvaluationInput`.

Verification:

- Targeted `RawNormalizationTests` / `HumanApprovalWorkflowTests`: passed, 35 tests.
- `dotnet build CopilotAgentObservability.slnx`: passed, warning 0 / error 0. NETSDK1057 appeared as the existing preview .NET SDK informational message.
- `dotnet test CopilotAgentObservability.slnx`: passed, 161 tests.

Re-review result:

- M5 Sub-Agent re-review found the decision-template safety finding resolved and no new actionable issue.
- The validator applies to all evaluation rows, including non-ready rows that would not appear in the template. Main-Agent accepted this as a safe-side behavior consistent with the M27 requirement that output must not contain unsafe content.
