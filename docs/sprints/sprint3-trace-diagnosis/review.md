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
