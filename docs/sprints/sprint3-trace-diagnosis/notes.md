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
- Added M2-M5 milestones for rule/evidence contract, diagnosis candidate implementation, improvement/auto-decision implementation, and M24-M27 human-review pipeline connection.
- Added initial `rule_id` and `decision_rule_id` tables to M1 `command-boundary.md` as the basis for M2.
- Added sensitive bundle read contract with manifest fields, evidence file fields, fragment granularity, reverse lookup by `evidence_ref`, and 7-day expiry metadata.
- Synchronized `docs/requirements.md`, `docs/spec.md`, and `docs/task.md` so Sprint3 includes auto-decision records but still excludes repository modification.
- Ran `dotnet test CopilotAgentObservability.slnx`; 173 tests passed.

## 2026-06-12: auto-decision scope correction

- Reconfirmed Sprint3 scope from the user decision: implement diagnosis -> improvement proposal -> auto decision in Sprint3.
- Restored `auto-approved` as a Sprint3 `generate-auto-decisions` output state.
- Kept repository file modification, patch / diff generation, commit, push, and pull request creation out of Sprint3.
- Renamed M4 to improvement and auto-decision implementation.
