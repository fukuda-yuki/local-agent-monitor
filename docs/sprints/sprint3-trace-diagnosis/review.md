# Sprint3 Review

## 2026-06-12: requirements alignment review

Scope reviewed:

- `docs/requirements.md`
- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint3-trace-diagnosis/README.md`

Findings:

- No implementation code was changed.
- Sprint3 is now defined as requirements/planning work for deterministic trace diagnosis candidates and auto-decision records.
- M11-M22 and M23-M27 historical boundaries remain documented as earlier phases, while Sprint3 adds deterministic candidate generation and auto-decision before any repository modification.
- Sensitive content is allowed only in explicit opt-in local output and remains disallowed in repository documents, fixtures, and review records.
- Repository-changing auto-improvement remains deferred to Sprint4 or later.

Residual risk:

- The command names, candidate schema, content evidence schema, and auto-decision schema still need to be finalized before implementation.
- The Sprint4 repository modification safety model is intentionally unresolved.

## 2026-06-12: source-of-truth follow-up review

Finding addressed:

- Concern: `docs/spec.md` contained Sprint3 candidate / auto-decision schema details while Sprint3 README still listed unresolved open questions.
- Resolution: `docs/spec.md` was reduced to high-level confirmed scope and safety boundary. Candidate command and schema details remain sprint-local until finalized.

Implementation-start decisions recorded:

- Use a candidate-specific diagnosis command and candidate-specific schema before mapping to M24 diagnosis records.
- Use a separate auto-decision schema instead of extending M27 human decision records.

Validation:

- `dotnet run --project src\CopilotAgentObservability.ConfigCli -- --help` succeeded.
- `dotnet build CopilotAgentObservability.slnx` succeeded.
- `aspire start --non-interactive --format Json` succeeded.
- `aspire ps --format Json` showed the AppHost running.
- `aspire describe --format Json` returned an empty resource graph.
- `aspire stop --non-interactive` stopped the AppHost successfully.

## 2026-06-12: M1 review

Finding:

- M1 command and schema details are now sprint-local under `milestones/M1-candidate-schema-and-command-boundary/`.

Decision summary:

- Candidate generation uses dedicated commands and schemas before any M24 / M25 / M27 mapping.
- Sensitive full content is stored only in opt-in local bundles under `tmp/sprint3-sensitive/<run_id>/`.
- Automated verification remains synthetic-only.

Residual risk:

- M2 must finalize deterministic rule ids, rule behavior, auto-decision rule behavior, and sensitive bundle read contract before implementation.

## 2026-06-12: Claude finding follow-up review

Findings accepted:

- The previous Sprint3 name and later readiness-only correction understated the agreed Sprint3 auto-decision scope.
- `--include-sensitive-content` had a write shape but no read contract.
- `auto-approved` must remain a Sprint3 auto-decision output state, but it must not trigger repository modification in Sprint3.
- Candidate pipeline to M24-M27 connection was deferred without a milestone.
- Initial `rule_id` and `decision_rule_id` sets were not defined.
- `requirements.md` must distinguish Sprint3 auto-decision from Sprint4-or-later repository modification.
- Startup check did not include `dotnet test`.

Resolution:

- Restored Sprint3 as Content-aware Trace Diagnosis and Auto-decision Foundation.
- Added M2-M5 milestones. M6 was added later for collaborative real-trace E2E.
- Added initial deterministic rule tables and sensitive bundle read contract to M1 command boundary.
- Kept `auto-approved` in Sprint3 output states while keeping repository modification out of Sprint3.
- Updated `requirements.md`, `spec.md`, and `task.md` to stop Sprint3 at auto-decision records, not repository modification.
- Ran and recorded `dotnet test CopilotAgentObservability.slnx`; 173 tests passed.

## 2026-06-12: auto-decision scope correction review

Finding:

- The readiness-only correction contradicted the agreed Sprint3 scope: diagnosis -> improvement proposal -> auto decision, while repository modification remains out of scope.

Resolution:

- Restored Sprint3 naming to Content-aware Trace Diagnosis and Auto-decision Foundation.
- Restored `auto-approved` / `needs-human-review` / `blocked` as `generate-auto-decisions` output states.
- Kept repository file modification, patch / diff generation, commit, push, and pull request creation out of Sprint3.
- Renamed M4 path to `M4-improvement-and-auto-decision-implementation`.

Validation:

- `rg` found no remaining readiness-only scope statement or `auto-approved` removal instruction.

## 2026-06-12: Claude review follow-up 2

Findings accepted:

- M24-M27 coexistence vs replacement was a blocking design decision, not something to discover after M3/M4 implementation.
- `auto-approved` needed a Sprint3-local, verifiable exit that does not imply repository modification.
- Content-aware rule patterns were not yet defined at deterministic matching level.
- Sprint3 should include redacted real-trace E2E, not only synthetic fixtures.
- Sensitive bundle expiry needed an operational deletion procedure even without an auto-delete command.
- Diagnosis and improvement candidate schemas carried too many measurement context columns.

Resolution:

- M24-M27 are explicitly compatibility-maintained; Sprint3 is an upstream candidate pipeline and must define adapter / mapping in M2.
- `next_action` for auto-approved is now `record-for-sprint4-planning`.
- M2 blocks implementation until literal / regex / field predicate patterns are defined.
- Added M6 collaborative real-trace E2E with agent/user work split and repository-safe evidence rules.
- Documented manual deletion through `manifest.json` `delete_target_paths`; no Sprint3 auto-delete command is introduced.
- Simplified candidate schemas to avoid carry-through metadata and use `trace_id` plus `source_record_ref` joins.

## 2026-06-17: M3 implementation review

Scope reviewed:

- `src/CopilotAgentObservability.ConfigCli/DiagnosisCandidates/`
- `src/CopilotAgentObservability.ConfigCli/Cli/`
- `tests/CopilotAgentObservability.ConfigCli.Tests/DiagnosisCandidateGenerationTests.cs`
- `docs/sprints/sprint3-trace-diagnosis/milestones/M3-diagnosis-candidate-implementation/task.md`

Findings:

- No blocking issues found in the M3 implementation review.
- The new command is candidate-specific and does not modify M24-M27 diagnosis / proposal / human decision schemas.
- Standard candidate CSV / JSON output uses sanitized summaries and refs; raw fragment values are written only to the opt-in sensitive bundle.
- The implementation is intentionally limited to the five M2 `DIAG-*` rules; additional heuristic rules remain out of scope.

Residual risk:

- Content-aware detection is deterministic and intentionally shallow; redacted real-trace compatibility still needs M6 coverage.
- Sensitive bundle expiry is metadata only; deletion remains a manual procedure through `manifest.json` `delete_target_paths`.

Validation:

- `dotnet build CopilotAgentObservability.slnx` succeeded.
- `dotnet test CopilotAgentObservability.slnx` succeeded with 181 tests passed.

## 2026-06-17: M4 implementation review

Scope reviewed:

- `src/CopilotAgentObservability.ConfigCli/ImprovementCandidates/`
- `src/CopilotAgentObservability.ConfigCli/Cli/`
- `tests/CopilotAgentObservability.ConfigCli.Tests/ImprovementCandidatePipelineTests.cs`
- `docs/sprints/sprint3-trace-diagnosis/milestones/M4-improvement-and-auto-decision-implementation/task.md`

Findings:

- No blocking issues found in the M4 implementation review.
- The new `generate-improvement-candidates` and `generate-auto-decisions` commands use Sprint3 candidate schemas and do not modify existing M24-M27 human-review commands.
- `auto-approved` exits only through `next_action=record-for-sprint4-planning`; no repository-modifying consumer, patch / diff generation, commit, push, or pull request behavior was added.
- Sensitive bundle references are routed to `needs-human-review`, while scope-overreach patterns and unsupported implementation targets are blocked.
- Review follow-up widened scope-overreach detection to include repository modification variants already covered by the older M26 evaluator, such as `modify repositories` and `make repository changes`.

Residual risk:

- Scope-overreach detection is deterministic substring / regex matching and intentionally limited to the M2 rule inventory.
- M6 redacted real-trace E2E remains an open follow-up milestone.

Validation:

- `dotnet build CopilotAgentObservability.slnx` succeeded.
- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --no-build --filter FullyQualifiedName~ImprovementCandidatePipelineTests` succeeded with 11 tests passed before review follow-up and remained covered after adding repository modification variants.
- `dotnet test CopilotAgentObservability.slnx` succeeded with 194 tests passed after review follow-up.

## 2026-06-18: M5 implementation review

Scope reviewed:

- `src/CopilotAgentObservability.ConfigCli/DiagnosisCandidates/`
- `src/CopilotAgentObservability.ConfigCli/Cli/`
- `tests/CopilotAgentObservability.ConfigCli.Tests/DiagnosisCandidateAdapterTests.cs`
- `docs/sprints/sprint3-trace-diagnosis/milestones/M5-human-review-pipeline-connection/task.md`

Findings:

- No blocking issues found in the M5 implementation review.
- The new `adapt-diagnosis-candidates` command connects Sprint3 diagnosis candidates to the existing M24 diagnosis schema without modifying M24-M27 downstream commands.
- Candidate statuses map to the M2-defined M24 review statuses, so `accepted-for-proposal` rows can flow into existing proposal generation while human-review and rejected rows remain explicit.
- Blank candidate trace ids are mapped to `missing-trace-<diagnosis_candidate_id>` so metadata-missing candidates can still be validated as M24 diagnosis records.
- The adapter does not copy `sensitive_bundle_path` or raw fragment values into M24 output. Measurement evidence refs included in summaries are reduced to basename plus row marker to avoid path-like false positives in the existing M24 safety validator.

Residual risk:

- Context join remains conservative: if multiple measurement rows share a trace id and `source_record_ref` does not identify one row, context columns are blank.
- Redacted real-trace compatibility still needs M6 coverage.

Validation:

- `dotnet build CopilotAgentObservability.slnx` succeeded.
- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --no-build --filter FullyQualifiedName~DiagnosisCandidateAdapterTests` succeeded with 5 tests passed.
- `dotnet test CopilotAgentObservability.slnx` succeeded with 199 tests passed.

## 2026-06-18: M3 follow-up review after Sprint3 closeout

Scope reviewed:

- `docs/sprints/sprint3-trace-diagnosis/milestones/M2-deterministic-rule-and-evidence-contract/rule-and-evidence-contract.md`
- `src/CopilotAgentObservability.ConfigCli/DiagnosisCandidates/RawEvidenceReader.cs`
- `src/CopilotAgentObservability.ConfigCli/DiagnosisCandidates/SensitiveBundleWriter.cs`
- `tests/CopilotAgentObservability.ConfigCli.Tests/DiagnosisCandidateGenerationTests.cs`

Findings:

- Accepted: M3 stored only the first raw fragment for each content-aware diagnosis candidate. The standard `evidence_ref` correctly used the first stable source locator, but M2 requires the opt-in sensitive bundle to retain the full supporting fragment list when multiple raw fragments support the same candidate.
- Rejected as non-issues: limiting M3 to the five M2 `DIAG-*` rules, keeping raw values out of standard CSV / JSON output, and requiring `--raw` for `--include-sensitive-content` all match the M2 contract.

Resolution:

- Updated `RawEvidenceReader` so error and sensitive matches preserve the first `evidence_ref` / `source_locator` while accumulating all matching fragments for the sensitive bundle.
- Updated the M3 generation test to assert that a sensitive candidate bundle evidence file contains both matching fragments from the synthetic raw OTLP fixture.

Validation:

- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~DiagnosisCandidateGenerationTests` succeeded with 8 tests passed.
- `dotnet build CopilotAgentObservability.slnx` succeeded.
- `dotnet test CopilotAgentObservability.slnx` succeeded with 199 tests passed.
