# Final Independent Review — Issue #128 Step B

## Blocking findings

- **Blocking — `src/CopilotAgentObservability.Persistence.Sqlite/RuntimeBackup/SqliteRuntimeBackupService.cs:1185`** — The schema-v9 shape check for `monitor_skill_invocations` omits the required `span_id` column; a database or archive stamped as monitor v9 but lacking that column can pass this check, `CREATE TABLE IF NOT EXISTS` will not repair the existing table, and the next Skill projection will fail when its insert references `span_id`.
- **Blocking — `docs/specifications/layers/telemetry-ingestion.md:587`** — The addition says the source-version gate is not re-evaluated after write, then at lines 590–592 requires a consumer to re-check the same resolution at read time; those normative statements contradict each other, and the read-time implementation prescription is mechanism for a not-yet-existing read surface that can drift from code.

## Other findings

- **Major — `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSkillProjectionTests.cs:129`** — Fix 3 is only partially pinned: the test removes `spanId` and therefore covers null/missing identity, but never supplies `"spanId":""`; a regression from `string.IsNullOrEmpty` to a null-only check would keep this test green while recording an empty exact identity.

## Clean checks

- **Fix 1 — clean:** `MonitorSkillProjectionBuilder.cs:79–101` requires a sanitized Skill identifier, non-empty span id, exact `execute_tool`, and exact `skill`; the negative theory would fail under the pre-fix any-Skill-attribute behavior, while `ResolvedCliTrace_ProjectsInvokedSkillAndAvailableInventory` proves one dedicated span still produces exactly one invocation.
- **Fix 2 — clean:** `MonitorSchemaMigrator.cs:147–148` has both `UNIQUE(raw_record_id, span_ordinal)` and `UNIQUE(trace_id, span_id)`, and `INSERT OR IGNORE` follows those identities; a redelivery with a changed ordinal is rejected by trace/span identity, while distinct spans in the same trace do not conflict, and the redelivery test would fail without the new constraint.
- **Fix 3 implementation — clean:** `MonitorSkillProjectionBuilder.cs:80–101` drops null and empty span ids before constructing the batch, and that unchanged value is bound at `RawTelemetryStore.cs:715–731`; the missing-id half of the regression test would fail against the pre-fix builder and the positive half confirms a valid id remains observable.
- **Skill edge cases — clean:** a dedicated Skill tool span without either allowlisted Skill-name attribute produces no invocation, an `execute_tool` span for another tool is suppressed, and the intentional no-span-id drop is consistent with exact-identity-only evidence.
- **Specification observation semantics — clean:** the addition does not weaken lines 557–562: stored rows remain positive observations only, inventory does not license absence, and “no Skill claim” is not an absence claim.
- **Frozen contracts — clean:** no `/api/monitor/*` or `/api/session-workspace/*` production route/DTO, `session_events.content_state`, or closed raw-bearing-surface definition is touched; the new test also byte-compares the existing monitor responses with and without Skill rows.
- **Raw leakage and bounds — clean:** Skill file-path input is ignored, projected identifiers pass through the existing sanitizer, retained inventory names are capped at 100 and 256 characters, and no changed log or error message includes raw Skill content or an absolute path.
- **Review scope — clean:** the two deferred behaviors were not re-reported; `docs/requirements.md`, `docs/spec.md`, untouched files, and `.claude/settings.local.json` were not read, as instructed.
- **Execution not checked:** build and test commands were not run because they can create or modify build artifacts, which would violate the instruction that this review file is the only file that may be created; test conclusions above are static red/green analyses, not execution claims.

**fix-first — The specification simultaneously forbids and requires post-write source-version re-evaluation, so the contract is internally contradictory.**
