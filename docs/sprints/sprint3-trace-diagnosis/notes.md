# Sprint3 Notes

## 2026-06-12: scope alignment

- Sprint3 is named Content-aware Trace Diagnosis and Auto-decision Foundation.
- Sprint3 includes trace-driven diagnosis candidate generation, deterministic content-aware evidence extraction, improvement proposal candidates, and auto-decision records.
- Sprint3 allows explicit opt-in sensitive local output to include real prompt / response content, tool arguments / results, credential, secret, Base64 header, and real user identity.
- Sensitive local output is not repository material and must not be committed.
- Sprint3 does not modify repository files, generate patch / diff, commit, push, create pull requests, or decide experiment winners.
- Repository-changing auto-improvement implementation is deferred to Sprint4 or later, after allowlist, dry-run, diff preview, rollback, test execution, commit boundary, and stop conditions are specified.

## 2026-06-12: source-of-truth correction

- `docs/spec.md` now keeps only the high-level Sprint3 scope and safety boundary.
- Candidate command contracts and schemas remain sprint-local until the open questions are resolved.
- Diagnosis candidate generation will use a candidate-specific command and candidate-specific schema first, then map into M24 records after review.
- Auto-decisions will use a separate schema rather than extending the existing M27 human decision record.
- Startup checks passed for Config CLI help, solution build, Aspire AppHost start, and Aspire AppHost stop.
- `dotnet test CopilotAgentObservability.slnx` is now recorded as passing with 173 tests.

## 2026-06-12: M1 candidate schema and command boundary

- Created M1 as `candidate-schema-and-command-boundary`.
- Selected three candidate commands: `generate-diagnosis-candidates`, `generate-improvement-candidates`, and `generate-auto-decisions`.
- Defined output columns for diagnosis candidates, improvement candidates, and auto-decision records in M1 `command-boundary.md`.
- Set sensitive output default path to `tmp/sprint3-sensitive/<run_id>/`.
- Kept repository fixtures synthetic-only.

## 2026-06-12: Claude review follow-up

- Rejected the overcorrection that removed `auto-approved`; Sprint3 scope includes auto-decision records, including `auto-approved`, while still excluding repository modification.
- Added M2-M5 milestones for rule/evidence contract, diagnosis candidate implementation, improvement/auto-decision implementation, and M24-M27 human-review pipeline connection. M6 was added later for collaborative real-trace E2E.
- Added initial `rule_id` and `decision_rule_id` tables to M1 `command-boundary.md` as the basis for M2.
- Added sensitive bundle read contract with manifest fields, evidence file fields, fragment granularity, reverse lookup by `evidence_ref`, and 7-day expiry metadata.
- Synchronized `docs/requirements.md`, `docs/spec.md`, and `docs/task.md` so Sprint3 includes auto-decision records but still excludes repository modification.
- Ran `dotnet test CopilotAgentObservability.slnx`; 173 tests passed.

## 2026-06-12: auto-decision scope correction

- Reconfirmed Sprint3 scope from the user decision: implement diagnosis -> improvement proposal -> auto decision in Sprint3.
- Restored `auto-approved` as a Sprint3 `generate-auto-decisions` output state.
- Kept repository file modification, patch / diff generation, commit, push, and pull request creation out of Sprint3.
- Renamed M4 to improvement and auto-decision implementation.

## 2026-06-12: Claude review follow-up 2

- Accepted the concern that M24-M27 connection cannot wait until after M3/M4 implementation.
- Kept M24-M27 as compatibility-maintained commands and schemas; Sprint3 does not replace them.
- M2 now blocks implementation until the candidate-to-human-review adapter / mapping contract is defined.
- Accepted the concern that `auto-approved` needs a Sprint3-local exit. It now means `record-for-sprint4-planning`, with no Sprint3 repository-modifying consumer command.
- Accepted the concern that content-aware patterns were underspecified. M2 must define literal / regex / field predicate patterns before M3.
- Added M6 collaborative real-trace E2E so Sprint3 validates redacted real Copilot trace input, with agent handling CLI work and user handling lower-cost VS Code Copilot Chat prompt operations.
- Kept automatic sensitive bundle deletion out of scope per user direction, but documented manual deletion via `manifest.json` `delete_target_paths`.
- Reduced candidate output schemas by removing measurement context carry-through columns and relying on `trace_id` plus `source_record_ref` joins.

## 2026-06-17: M1/M2 spec promotion

- Promoted the Sprint3 M1/M2 command contracts, candidate schemas, initial rule set, sensitive bundle schema, auto-decision boundary, and M5 adapter command requirement into `docs/spec.md`.
- Updated Sprint3 state from requirements definition to implementation preparation.

## 2026-06-17: spec promotion review

- Reviewed the promoted Sprint3 candidate pipeline spec against the M1 / M2 sprint-local contracts.
- Fixed the auto-decision sensitive-content rule so it no longer references `DIAG-CONTENT-SENSITIVE-LEAK-V1`; that diagnosis rule produces `candidate_status=blocked` and does not flow into improvement candidates.

## 2026-06-17: M3 diagnosis candidate implementation

- Added `generate-diagnosis-candidates` as a candidate-specific Config CLI command without changing the existing M24-M27 human-review commands.
- Implemented the five M2 diagnosis rules only: metric error count, metric tool loop, content error message, content sensitive leak, and missing trace context.
- Added raw OTLP and raw store evidence lookup for deterministic content-aware candidates.
- Added opt-in sensitive bundle schema v1 generation with `manifest.json`, `evidence/*.json`, 7-day expiry metadata, source input hashes, and manual deletion paths.
- Added synthetic xUnit coverage for JSON / CSV measurements, raw OTLP, raw store input, no-leak standard output, opt-in bundle shape, and option validation.
- Validation passed on 2026-06-17: `dotnet build CopilotAgentObservability.slnx`; `dotnet test CopilotAgentObservability.slnx` with 181 tests passed.

## 2026-06-18: M5 human-review pipeline connection

- Added `adapt-diagnosis-candidates` to convert Sprint3 diagnosis candidate CSV / JSON plus normalized measurement CSV / JSON into existing M24 diagnosis record CSV / JSON.
- Kept existing M24-M27 commands and schemas unchanged; `validate-diagnoses`, proposal generation, evaluation, and human decision recording remain the downstream human-review workflow.
- Mapped `candidate_status` to M24 `review_status`: `auto-eligible` to `accepted-for-proposal`, `candidate` to `needs-human-review`, and `blocked` to `rejected`.
- Joined measurement context by `trace_id`, using exact `source_record_ref` only as a tie-breaker; ambiguous multi-row trace matches leave context columns blank.
- Mapped blank candidate `trace_id` to `missing-trace-<diagnosis_candidate_id>` so metadata-missing candidates remain consumable by existing M24 validation.
- Added sanitized `rule_id` and `evidence_ref` to M24 `evidence_summary` without copying `sensitive_bundle_path` or raw fragment values. Measurement refs in summaries are reduced to file name plus row marker.
- Validation passed on 2026-06-18: `dotnet build CopilotAgentObservability.slnx`; `dotnet test CopilotAgentObservability.slnx` with 199 tests passed.
