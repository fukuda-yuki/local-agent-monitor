# Issue #128 Step B — independent working-tree review

## Blocking

- **blocking — `src/CopilotAgentObservability.LocalMonitor/Projection/ProjectionWorker.cs:303`, `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs:619`, `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs:871` — The version gate is a one-time, non-transactional write-time check.** The worker reads `GetTraceSourceVersionResolution(traceId)` on the compatibility-store connection, then the projection store writes Skill rows in a separate transaction and permanently stamps `span_projected_at`. There is no read-time gate, trace-wide invalidation, deletion, or re-projection. If a later observation changes a previously `Resolved` trace to `Missing`, `Conflicting`, or `Unrecognised`, the old positive Skill rows remain. A concurrent ingest can also change the resolution between the read and insert. In the other direction, a null/unresolved result is stamped as completed with an empty batch, so a later resolution cannot recover the omitted projection. Thus a trace whose current Step A resolution fails closed can still have a Skill projection, and an eventually resolved trace can remain permanently empty.

- **blocking — `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorSkillProjectionBuilder.cs:78` — Any span carrying a Skill-name attribute is treated as the invoking span.** The builder does not require positive invocation evidence such as the `execute_tool` operation and `skill` tool identity used by the positive fixture; it adds an invocation as soon as either Skill-name key sanitizes successfully. A parent, context, or unrelated span that merely propagates `github.copilot.skill.name` or `github.copilot.tool.parameters.skill_name` therefore creates a false invoked-Skill observation.

- **blocking — `src/CopilotAgentObservability.Persistence.Sqlite/MonitorSchemaMigrator.cs:131`, `src/CopilotAgentObservability.Persistence.Sqlite/RawTelemetryStore.cs:587` — A v8 predecessor database is upgraded to empty Skill tables without backfilling already projected raw records.** The migration adds the v9 tables but preserves existing non-null `monitor_ingestions.span_projected_at` values, while the worker selects only rows where that marker is null. Consequently, retained v8 raw telemetry that already completed span projection is never considered for Skill projection after upgrade.

- **blocking — `src/CopilotAgentObservability.Telemetry/Monitoring/MonitorSkillProjectionBuilder.cs:42`, `src/CopilotAgentObservability.Persistence.Sqlite/MonitorSchemaMigrator.cs:147` — Invocation identity is not deduplicated by trace/span.** The builder emits one row per payload occurrence, and storage uniqueness is only `(raw_record_id, span_ordinal)`. Repeating the same `(trace_id, span_id)` in an OTLP retry, another raw record, or another ordinal stores the same invocation twice and makes downstream invocation counts wrong.

## Should-fix

- **should-fix — `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSkillProjectionTests.cs:92`, `tests/CopilotAgentObservability.LocalMonitor.Tests/MonitorSchemaMigrationFixtureTests.cs:12` — The tests do not exercise the failure modes above or the required predecessor fixture.** The three static failing-version cases are each covered (`missing`, `conflicting`, `unrecognised`), and the VS Code negative case exists. Untested rules are resolution transitions and the read/write race, a Skill attribute on a non-invoking span, duplicate delivery of the same trace/span, migration from a real v8 predecessor database, late exact-session availability, `/api/session-workspace/*` byte stability, and absence of Skill content/path values from logs and error messages. The historical fixture matrix still contains only v1–v5, so renaming the assertion to “v9” does not exercise v8→v9.

## Minor

- **minor — `.claude/settings.local.json:1` — An unrelated local permissions/hooks file is untracked in the reviewed working tree.** If swept into the Issue #128 commit, it would add machine-local agent permissions and hook behavior unrelated to Skill projection.

## Clean checks

- **Raw leakage — clean in the changed production path.** Skill names, sources, triggers, and inventory names use the existing `MeasurementSanitizer`; rejected identifiers are dropped, `github.copilot.tool.parameters.file_path` and Skill content are never read into a Skill batch, and no new log or value-bearing error interpolation was added.
- **Source scoping / cross-source view — clean apart from the stale-gate blocker.** The builder uses an ordinal exact match for `github-copilot-cli`, the VS Code negative test exists, and no Skill read API or cross-source view is introduced.
- **Inventory bound — clean.** Retained names are capped at 100, each retained name passes the existing 256-character sanitizer bound, and count/length truncation sets `names_truncated`.
- **Session binding — clean.** Binding is an exact `COLLATE BINARY` lookup of the native identity restricted to `source_surface = 'copilot-cli'`; no time, name, path, or proximity fallback exists.
- **Frozen contracts — clean by changed-file inspection.** No `/api/monitor/*` or `/api/session-workspace/*` implementation, `session_events.content_state`, or raw-bearing-surface enumeration was changed; the added monitor byte-regression test does not cover the session-workspace family, as noted above.
- **Additive schema/versioning — clean in structure, subject to the backfill blocker.** The migration adds three new tables and indexes without repurposing existing columns, and the monitor/runtime-backup version is consistently bumped from 8 to 9.
- **Absence claims / sanitized-only — clean.** No absence-claim surface is added, and a sanitized-only positive-path test confirms the sanitized Skill rows remain available.

## Verification limits

I followed the requested scope: read the working-tree diff first, then only changed/untracked files. I did not read `docs/requirements.md` or `docs/spec.md`. I did not reopen the untouched Step A SQL implementation or the sanitizer implementation; the new consumer was checked against the committed `ISourceCompatibilityStore.GetTraceSourceVersionResolution` seam and the touched Step A resolution tests. I did not run build or test commands because they can write generated artifacts and the task permits creation of only this review file. The read-only `git diff --check` completed with exit code 0.

**fix-first — The current one-shot version gate can leave positive Skill rows for a trace whose Step A resolution now fails closed.**
